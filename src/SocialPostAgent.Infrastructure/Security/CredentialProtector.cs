using Microsoft.AspNetCore.DataProtection;
using SocialPostAgent.Application.Security;

namespace SocialPostAgent.Infrastructure.Security;

/// <summary>
/// Data-Protection-backed implementation of <see cref="ICredentialProtector"/>. The
/// purpose string is versioned ("v1") so a future key-rotation or algorithm change can
/// introduce a "v2" protector without breaking the ability to decrypt tokens written
/// under the old one, by keeping both registered during a migration window.
/// </summary>
public sealed class CredentialProtector : ICredentialProtector
{
    private const string Purpose = "SocialPostAgent.PlatformCredentials.v1";

    private readonly IDataProtector _protector;

    public CredentialProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector(Purpose);
    }

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string Unprotect(string ciphertext) => _protector.Unprotect(ciphertext);
}
