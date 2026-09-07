using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SocialPostAgent.Application.Security;
using SocialPostAgent.Domain;
using SocialPostAgent.Domain.Entities;

namespace SocialPostAgent.Infrastructure.Persistence;

/// <summary>One platform's raw credential, as supplied via configuration (user-secrets/appsettings).</summary>
public sealed class PlatformCredentialSeedEntry
{
    public string? AccessToken { get; set; }

    public string? AccountId { get; set; }
}

/// <summary>Bound from the "PlatformCredentials" configuration section.</summary>
public sealed class PlatformCredentialsSeedOptions
{
    public const string SectionName = "PlatformCredentials";

    public PlatformCredentialSeedEntry? Facebook { get; set; }

    public PlatformCredentialSeedEntry? Instagram { get; set; }

    public PlatformCredentialSeedEntry? LinkedIn { get; set; }
}

/// <summary>
/// Dev-convenience path for getting a platform token into the database without a
/// credentials management UI: put the raw token in user-secrets (never in
/// appsettings.json, never committed) under "PlatformCredentials:{Platform}", and this
/// runs on every startup, encrypting it via <see cref="ICredentialProtector"/> and
/// upserting the corresponding <see cref="PlatformCredential"/> row. A platform with no
/// configured token is left untouched. This is intentionally not how credentials would
/// be managed in production — see README.
/// </summary>
public static class PlatformCredentialSeeder
{
    public static async Task SeedPlatformCredentialsFromConfigurationAsync(
        this IServiceProvider services,
        IConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<SocialPostAgentDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<ICredentialProtector>();

        var seed = configuration.GetSection(PlatformCredentialsSeedOptions.SectionName).Get<PlatformCredentialsSeedOptions>()
            ?? new PlatformCredentialsSeedOptions();

        await UpsertAsync(context, protector, PlatformType.Facebook, seed.Facebook, cancellationToken);
        await UpsertAsync(context, protector, PlatformType.Instagram, seed.Instagram, cancellationToken);
        await UpsertAsync(context, protector, PlatformType.LinkedIn, seed.LinkedIn, cancellationToken);

        await context.SaveChangesAsync(cancellationToken);
    }

    private static async Task UpsertAsync(
        SocialPostAgentDbContext context,
        ICredentialProtector protector,
        PlatformType platform,
        PlatformCredentialSeedEntry? entry,
        CancellationToken cancellationToken)
    {
        if (entry is null || string.IsNullOrWhiteSpace(entry.AccessToken))
        {
            return;
        }

        var existing = await context.Credentials.FirstOrDefaultAsync(c => c.Platform == platform, cancellationToken);
        var encryptedToken = protector.Protect(entry.AccessToken);

        if (existing is null)
        {
            context.Credentials.Add(new PlatformCredential
            {
                Platform = platform,
                EncryptedAccessToken = encryptedToken,
                AccountId = entry.AccountId
            });
        }
        else
        {
            existing.EncryptedAccessToken = encryptedToken;
            existing.AccountId = entry.AccountId ?? existing.AccountId;
        }
    }
}
