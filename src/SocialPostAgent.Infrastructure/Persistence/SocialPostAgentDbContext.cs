using Microsoft.EntityFrameworkCore;
using SocialPostAgent.Domain.Common;
using SocialPostAgent.Domain.Entities;

namespace SocialPostAgent.Infrastructure.Persistence;

/// <summary>
/// The single EF Core context for the agent. Table shape lives entirely in the
/// <see cref="Configurations"/> namespace via <c>IEntityTypeConfiguration&lt;T&gt;</c>
/// classes — <see cref="OnModelCreating"/> only wires them up, it never configures a
/// property directly, so this class stays stable as entities evolve.
/// </summary>
public class SocialPostAgentDbContext : DbContext
{
    public SocialPostAgentDbContext(DbContextOptions<SocialPostAgentDbContext> options)
        : base(options)
    {
    }

    public DbSet<Post> Posts => Set<Post>();

    public DbSet<PlatformResult> PlatformResults => Set<PlatformResult>();

    public DbSet<GuardrailLog> GuardrailLogs => Set<GuardrailLog>();

    public DbSet<PlatformCredential> Credentials => Set<PlatformCredential>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SocialPostAgentDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }

    /// <summary>
    /// Stamps <see cref="Domain.Common.BaseEntity.UpdatedAtUtc"/> on every modified entity
    /// so callers never have to remember to do it themselves.
    /// </summary>
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampUpdatedTimestamps();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampUpdatedTimestamps();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void StampUpdatedTimestamps()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in ChangeTracker.Entries<BaseEntity>())
        {
            if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAtUtc = now;
            }
        }
    }
}
