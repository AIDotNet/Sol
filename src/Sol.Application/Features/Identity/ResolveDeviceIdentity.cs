using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sol.Application.Abstractions.Common;
using Sol.Application.Abstractions.Persistence;
using Sol.Application.Abstractions.Security;
using Sol.Application.Contracts.Device;
using Sol.Domain.Identity;

namespace Sol.Application.Features.Identity;

/// <summary>
/// Resolves an incoming request to a device and a visitor, creating them on first contact.
/// </summary>
/// <remarks>
/// <para>Two layers, in strict priority order:</para>
/// <list type="number">
/// <item>
/// Deterministic — an HttpOnly cookie, or the id the client mirrored into localStorage. Accurate
/// to roughly 99% for a returning browser, and the only layer business logic should trust.
/// </item>
/// <item>
/// Probabilistic — browser-independent signals plus IP prefix, used to guess that a new browser
/// is the same physical machine. Capped at 0.6 confidence. Cross-browser identification is not
/// reliably achievable: the high-entropy signals (canvas, audio, fonts) differ per engine, and
/// the ones that survive a browser switch carry only ~10-15 bits of correlated entropy.
/// </item>
/// </list>
/// </remarks>
public sealed class ResolveDeviceIdentity(
    IDeviceRepository devices,
    IVisitorRepository visitors,
    IFingerprintHasher hasher,
    IIpPrefixExtractor ipPrefixes,
    IClock clock,
    IOptions<DeviceIdentityOptions> options,
    ILogger<ResolveDeviceIdentity> logger)
{
    private readonly DeviceIdentityOptions _options = options.Value;

    public async Task<IdentityResolution> HandleAsync(
        DeviceSignalsPayload payload,
        DeviceId? cookieDeviceId,
        IPAddress? remoteAddress,
        CancellationToken ct)
    {
        var now = clock.UtcNow;

        // --- Layer 1a: the cookie ---
        if (cookieDeviceId is { } fromCookie)
        {
            var existing = await devices.FindByIdAsync(fromCookie, ct);
            if (existing is not null)
            {
                await devices.TouchAsync(fromCookie, now, ct);
                var visitorId = await visitors.FindVisitorForDeviceAsync(fromCookie, ct)
                                ?? await AttachToNewVisitorAsync(fromCookie, LinkMethod.Deterministic, now, ct);

                return new IdentityResolution(
                    fromCookie, visitorId, IsNewDevice: false, LinkConfidence.Certain, LinkMethod.Deterministic);
            }
        }

        // --- Layer 1b: the id the client kept in localStorage ---
        if (DeviceId.TryParse(payload.ClientStoredId, out var storedId))
        {
            var restored = await devices.FindByIdAsync(storedId, ct);
            if (restored is not null && MatchesStoredDevice(restored, payload, remoteAddress))
            {
                await devices.TouchAsync(storedId, now, ct);
                var visitorId = await visitors.FindVisitorForDeviceAsync(storedId, ct)
                                ?? await AttachToNewVisitorAsync(storedId, LinkMethod.Deterministic, now, ct);

                return new IdentityResolution(
                    storedId, visitorId, IsNewDevice: false, LinkConfidence.StorageRestore, LinkMethod.Deterministic);
            }
        }

        // --- Unknown browser: record it, then try (weakly) to place it on a known device ---
        var ipPrefix = ipPrefixes.Extract(remoteAddress);
        var coarse = hasher.ComputeCoarse(payload.Stable, ipPrefix, _options.SignalVersion);
        var exact = hasher.ComputeExact(payload.Stable, payload.Volatile, _options.SignalVersion);

        var newDeviceId = DeviceId.New();
        await devices.InsertAsync(
            new Device(
                newDeviceId,
                exact,
                coarse,
                ipPrefix,
                hasher.CanonicalizeForStorage(payload),
                _options.SignalVersion,
                payload.Volatile?.UserAgent,
                now,
                now),
            ct);

        var (visitor, method) = await ResolveVisitorForNewDeviceAsync(coarse, exact, ipPrefix, now, ct);
        await visitors.LinkAsync(
            new DeviceLink(newDeviceId, visitor, LinkConfidence.For(method), method, now), ct);

        return new IdentityResolution(
            newDeviceId, visitor, IsNewDevice: true, LinkConfidence.For(method), method);
    }

    /// <summary>
    /// Gates restoration of a localStorage-mirrored device id on the request still looking like
    /// the machine that earned the id.
    /// </summary>
    /// <remarks>
    /// The mirrored id reaches JavaScript by design, so on its own it is a bearer credential —
    /// anyone it leaks to could otherwise answer a handshake with it and take over the device's
    /// canvases, agent history, and stored provider keys. The raw signals behind a stored
    /// fingerprint never leave the server, so a remote holder of the bare id cannot reproduce
    /// them: requiring an exact match (same browser anywhere) or a coarse match (same stable
    /// signals on the same IP prefix) keeps the legitimate cookie-cleared-same-machine restore
    /// working while turning a leaked id alone into a fresh device.
    /// </remarks>
    private bool MatchesStoredDevice(
        Device device,
        DeviceSignalsPayload payload,
        IPAddress? remoteAddress)
    {
        var exact = hasher.ComputeExact(payload.Stable, payload.Volatile, device.SignalVersion);
        if (device.FingerprintExact.AsSpan().SequenceEqual(exact))
        {
            return true;
        }

        var coarse = hasher.ComputeCoarse(
            payload.Stable, ipPrefixes.Extract(remoteAddress), device.SignalVersion);
        return device.FingerprintCoarse.AsSpan().SequenceEqual(coarse);
    }

    /// <summary>
    /// Decides whether an unrecognised browser belongs to a visitor we have already seen.
    /// Ambiguity always resolves to a fresh visitor — a wrong merge is far more damaging than
    /// a duplicate, because it silently attributes one person's activity to another.
    /// </summary>
    private async Task<(VisitorId Visitor, LinkMethod Method)> ResolveVisitorForNewDeviceAsync(
        byte[] coarse,
        byte[] exact,
        string ipPrefix,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var since = now.AddHours(-_options.CoarseWindowHours);

        // Fetch one more than the cap so an over-fanout set is detectable rather than truncated.
        var candidates = await devices.FindCoarseCandidatesAsync(
            coarse, exact, ipPrefix, _options.SignalVersion, since, _options.MaxCoarseFanout + 1, ct);

        if (candidates.Count == 0)
        {
            return (await CreateVisitorAsync(now, ct), LinkMethod.Deterministic);
        }

        if (candidates.Count > _options.MaxCoarseFanout)
        {
            logger.LogInformation(
                "Coarse fingerprint too common ({Count} devices within {Hours}h on {IpPrefix}); not linking.",
                candidates.Count, _options.CoarseWindowHours, ipPrefix);

            return (await CreateVisitorAsync(now, ct), LinkMethod.Deterministic);
        }

        var distinctVisitors = candidates.Select(c => c.VisitorId).Distinct().ToArray();
        if (distinctVisitors.Length != 1)
        {
            logger.LogInformation(
                "Coarse fingerprint spans {VisitorCount} visitors; ambiguous, not linking.",
                distinctVisitors.Length);

            return (await CreateVisitorAsync(now, ct), LinkMethod.Deterministic);
        }

        logger.LogDebug(
            "Linking new device to visitor {VisitorId} on coarse fingerprint ({Candidates} candidates).",
            distinctVisitors[0], candidates.Count);

        return (new VisitorId(distinctVisitors[0]), LinkMethod.ProbabilisticCoarse);
    }

    private async Task<VisitorId> CreateVisitorAsync(DateTimeOffset now, CancellationToken ct)
    {
        var visitor = VisitorId.New();
        await visitors.CreateAsync(visitor, now, ct);
        return visitor;
    }

    private async Task<VisitorId> AttachToNewVisitorAsync(
        DeviceId deviceId, LinkMethod method, DateTimeOffset now, CancellationToken ct)
    {
        var visitor = await CreateVisitorAsync(now, ct);
        await visitors.LinkAsync(
            new DeviceLink(deviceId, visitor, LinkConfidence.For(method), method, now), ct);
        return visitor;
    }
}
