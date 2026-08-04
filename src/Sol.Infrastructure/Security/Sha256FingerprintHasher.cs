using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Sol.Application.Abstractions.Security;
using Sol.Application.Contracts.Device;
using Sol.Application.Features.Identity;

namespace Sol.Infrastructure.Security;

/// <summary>
/// Normalizes browser signals and hashes them into the coarse and exact fingerprints.
/// </summary>
/// <remarks>
/// Quantization happens before hashing. Without it any jitter — a window resize changing
/// reported screen metrics, a fractional devicePixelRatio, a driver version in the GPU string —
/// produces a different hash and the device looks brand new on every visit. Quantizing trades a
/// little entropy for stability, which is the right trade when the goal is recognition.
/// </remarks>
public sealed class Sha256FingerprintHasher(IOptions<DeviceIdentityOptions> options) : IFingerprintHasher
{
    private const string Missing = "?";

    private static readonly double[] MemoryBuckets = [0.25, 0.5, 1, 2, 4, 8, 16, 32, 64];
    private static readonly int[] CoreBuckets = [1, 2, 4, 8, 12, 16, 24, 32, 64];

    private readonly DeviceIdentityOptions _options = options.Value;

    public byte[] ComputeCoarse(StableSignals? stable, string ipPrefix, int signalVersion)
    {
        var canonical = new StringBuilder();
        canonical.Append('v').Append(signalVersion).Append('|');
        AppendStable(canonical, stable);
        canonical.Append("|ip=").Append(ipPrefix);
        return Hash(canonical.ToString());
    }

    public byte[] ComputeExact(StableSignals? stable, VolatileSignals? volatileSignals, int signalVersion)
    {
        var canonical = new StringBuilder();
        canonical.Append('v').Append(signalVersion).Append('|');
        AppendStable(canonical, stable);
        canonical.Append('|');
        AppendVolatile(canonical, volatileSignals);
        return Hash(canonical.ToString());
    }

    public string CanonicalizeForStorage(DeviceSignalsPayload payload)
    {
        // Components are stored individually rather than only as a hash, so a future signal-set
        // version can re-derive fingerprints from history instead of losing every existing device.
        var stable = new StringBuilder();
        AppendStable(stable, payload.Stable);

        var vol = new StringBuilder();
        AppendVolatile(vol, payload.Volatile);

        return $$"""
                 {"v":{{payload.V}},"stable":"{{Escape(stable.ToString())}}","volatile":"{{Escape(vol.ToString())}}"}
                 """;
    }

    /// <summary>
    /// Browser-independent signals, in a fixed order with quantized values.
    /// </summary>
    private static void AppendStable(StringBuilder sb, StableSignals? s)
    {
        // Ordinal, fixed key order — a canonical form, so equal inputs always hash equally.
        sb.Append("tz=").Append(Normalize(s?.TimeZone));
        sb.Append("|pf=").Append(Normalize(s?.Platform));
        sb.Append("|hc=").Append(Bucket(s?.HardwareConcurrency, CoreBuckets));
        sb.Append("|dm=").Append(Bucket(s?.DeviceMemoryGb, MemoryBuckets));
        sb.Append("|sc=").Append(NormalizeScreen(s?.Screen));
        sb.Append("|gv=").Append(Normalize(s?.Gpu?.Vendor));
        sb.Append("|gr=").Append(NormalizeRenderer(s?.Gpu?.Renderer));
        sb.Append("|ln=").Append(PrimaryLanguage(s?.PrimaryLanguage));
    }

    private static void AppendVolatile(StringBuilder sb, VolatileSignals? v)
    {
        sb.Append("ua=").Append(Normalize(v?.UserAgent));
        sb.Append("|cv=").Append(Normalize(v?.CanvasHash));
        sb.Append("|au=").Append(Normalize(v?.AudioHash));
        sb.Append("|fn=").Append(Normalize(v?.FontsHash));
        sb.Append("|lg=").Append(v?.Languages is { Count: > 0 } langs
            ? string.Join(',', langs.Take(8).Select(l => l.Trim().ToLowerInvariant()))
            : Missing);
    }

    /// <summary>
    /// Orientation-invariant screen signature: a phone rotating must not read as a new device,
    /// so the larger edge is always emitted first and rotation is discarded entirely.
    /// </summary>
    private static string NormalizeScreen(ScreenSignals? screen)
    {
        if (screen?.W is not { } w || screen.H is not { } h)
        {
            return Missing;
        }

        var major = Math.Max(w, h);
        var minor = Math.Min(w, h);
        var depth = screen.ColorDepth ?? 0;
        // One decimal place: browsers report devicePixelRatio with float noise at zoom levels.
        var dpr = screen.Dpr is { } d ? Math.Round(d, 1).ToString("0.0") : Missing;

        return $"{major}x{minor}x{depth}@{dpr}";
    }

    /// <summary>
    /// Strips driver and version numbers from the GPU renderer string, keeping the model tokens.
    /// The same card reports differently per browser and per driver update, so the raw string is
    /// too brittle to hash directly.
    /// </summary>
    private static string NormalizeRenderer(string? renderer)
    {
        if (string.IsNullOrWhiteSpace(renderer))
        {
            return Missing;
        }

        var tokens = renderer
            .ToLowerInvariant()
            .Split([' ', ',', '(', ')', '/', '-'], StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !t.Any(char.IsDigit) || t.Length <= 4)
            .Take(6);

        var result = string.Join('_', tokens);
        return result.Length == 0 ? Missing : result;
    }

    /// <summary>Keeps only the primary subtag: zh-CN and zh-TW should not split one device in two.</summary>
    private static string PrimaryLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return Missing;
        }

        var trimmed = language.Trim().ToLowerInvariant();
        var dash = trimmed.IndexOf('-');
        return dash > 0 ? trimmed[..dash] : trimmed;
    }

    /// <summary>Snaps to the nearest bucket, so a reported 6 cores and 8 cores do not diverge.</summary>
    private static string Bucket(int? value, int[] buckets)
    {
        if (value is not { } v || v <= 0)
        {
            return Missing;
        }

        foreach (var bucket in buckets)
        {
            if (v <= bucket)
            {
                return bucket.ToString();
            }
        }

        return "max";
    }

    private static string Bucket(double? value, double[] buckets)
    {
        if (value is not { } v || v <= 0)
        {
            return Missing;
        }

        foreach (var bucket in buckets)
        {
            if (v <= bucket)
            {
                return bucket.ToString("0.##");
            }
        }

        return "max";
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? Missing : value.Trim().ToLowerInvariant();

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("\"", "\\\"", StringComparison.Ordinal);

    /// <summary>
    /// Hashes with a server-side pepper appended, so stored fingerprints cannot be recomputed
    /// from publicly observable signal values or correlated against another system's database.
    /// </summary>
    private byte[] Hash(string canonical) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(canonical + "|" + _options.Pepper));
}
