namespace Sol.Application.Abstractions.Security;

/// <summary>
/// Mints and validates time-limited public links to canvas assets.
/// </summary>
/// <remarks>
/// Upstream providers fetch reference images over plain http(s) and present no device cookie, so
/// an asset that leaves the building needs a URL that authorizes itself: signed with the
/// deployment's encryption key and expired, rather than made world-readable by id.
/// </remarks>
public interface IAssetLinkSigner
{
    /// <summary>
    /// Builds an absolute URL carrying a signed, expiring token for the asset.
    /// </summary>
    /// <returns>
    /// Null when the deployment has no public origin configured — there is no URL an outside
    /// service could fetch, and callers must fall back to inlining the bytes.
    /// </returns>
    string? CreatePublicUrl(Guid assetId);

    /// <summary>
    /// Checks a token presented with an asset request: right asset, intact signature, unexpired.
    /// </summary>
    bool TryValidateToken(Guid assetId, string token);
}
