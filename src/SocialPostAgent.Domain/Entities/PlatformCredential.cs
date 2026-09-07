using SocialPostAgent.Domain.Common;

namespace SocialPostAgent.Domain.Entities;

/// <summary>
/// Stores the long-lived access token (and, where the platform issues one, a refresh
/// token) needed to publish to one platform on behalf of the single configured brand.
/// Token values are encrypted at rest — see <c>ICredentialProtector</c> — never stored
/// in plain text, and never logged.
/// </summary>
public class PlatformCredential : BaseEntity
{
    public PlatformType Platform { get; set; }

    /// <summary>Data-Protection-encrypted access/page token.</summary>
    public string EncryptedAccessToken { get; set; } = string.Empty;

    /// <summary>Data-Protection-encrypted refresh token, when the platform issues one.</summary>
    public string? EncryptedRefreshToken { get; set; }

    public DateTimeOffset? ExpiresAtUtc { get; set; }

    /// <summary>Page ID (Facebook), IG Business Account ID (Instagram), or organization URN (LinkedIn).</summary>
    public string? AccountId { get; set; }

    public bool IsExpiringSoon(TimeSpan within) =>
        ExpiresAtUtc.HasValue && ExpiresAtUtc.Value <= DateTimeOffset.UtcNow.Add(within);
}
