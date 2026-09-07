using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SocialPostAgent.Application.Orchestration;
using SocialPostAgent.Application.Security;
using SocialPostAgent.Domain.Repositories;
using SocialPostAgent.Infrastructure.Persistence;
using SocialPostAgent.Infrastructure.Scheduling;
using SocialPostAgent.Infrastructure.Security;

namespace SocialPostAgent.Infrastructure;

/// <summary>
/// Composition root for the Infrastructure layer. The API project calls
/// <see cref="AddInfrastructure"/> once from <c>Program.cs</c> and never needs to know
/// which database provider, repository implementation, or key-storage mechanism is
/// behind the abstractions it consumes.
/// </summary>
public static class DependencyInjection
{
    private const string ConnectionStringName = "Postgres";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException(
                $"Connection string '{ConnectionStringName}' is not configured. " +
                "Set ConnectionStrings:Postgres in appsettings.json or an environment variable.");

        // No EF Core Migrations assembly is configured on purpose — see EnsureDatabaseCreatedAsync below.
        services.AddDbContext<SocialPostAgentDbContext>(options => options.UseNpgsql(connectionString));

        // Generic repository: one open registration covers every entity that derives
        // from BaseEntity, so adding a new aggregate never requires a new DI registration.
        services.AddScoped(typeof(IRepository<>), typeof(Repository<>));
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        services.AddDataProtection()
            .SetApplicationName("SocialPostAgent");
        services.AddSingleton<ICredentialProtector, CredentialProtector>();

        // Hangfire's client/server registration (AddHangfire, AddHangfireServer, the
        // Postgres storage, and the dashboard middleware) lives in the API host's
        // Program.cs — this only registers the abstraction PostOrchestrator depends on.
        services.AddScoped<IPostScheduler, HangfirePostScheduler>();

        return services;
    }

    /// <summary>
    /// Creates the database schema directly from the current EF Core model.
    /// This project deliberately does not use EF Core Migrations while the schema is
    /// still moving during local development (see README) — <c>EnsureCreatedAsync</c>
    /// is safe to call on every startup because it is a no-op once the schema exists.
    /// Switch to a proper migrations workflow before this ever touches a shared or
    /// production database, since <c>EnsureCreated</c> cannot evolve an existing schema.
    /// </summary>
    public static async Task EnsureDatabaseCreatedAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<SocialPostAgentDbContext>();
        await context.Database.EnsureCreatedAsync(cancellationToken);
    }
}
