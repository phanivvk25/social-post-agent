namespace SocialPostAgent.Application.Security;

/// <summary>
/// Encrypts and decrypts platform access/refresh tokens before they touch the database
/// or leave it. The Application layer only depends on this abstraction — the concrete,
/// Data-Protection-backed implementation lives in Infrastructure, keeping the dependency
/// pointing inward as Clean Architecture requires.
/// </summary>
public interface ICredentialProtector
{
    string Protect(string plaintext);

    string Unprotect(string ciphertext);
}
