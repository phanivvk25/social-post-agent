using SocialPostAgent.Domain.Entities;
using SocialPostAgent.Domain.Repositories;

namespace SocialPostAgent.Infrastructure.Persistence;

/// <summary>
/// Wires the generic <see cref="Repository{TEntity}"/> up for each aggregate and commits
/// them together against one <see cref="SocialPostAgentDbContext"/> instance, so a single
/// <see cref="SaveChangesAsync"/> call persists a post, its guardrail logs and its
/// platform results in one transaction.
/// </summary>
public class UnitOfWork : IUnitOfWork, IDisposable
{
    private readonly SocialPostAgentDbContext _context;

    public UnitOfWork(SocialPostAgentDbContext context)
    {
        _context = context;
        Posts = new Repository<Post>(context);
        PlatformResults = new Repository<PlatformResult>(context);
        GuardrailLogs = new Repository<GuardrailLog>(context);
        Credentials = new Repository<PlatformCredential>(context);
    }

    public IRepository<Post> Posts { get; }

    public IRepository<PlatformResult> PlatformResults { get; }

    public IRepository<GuardrailLog> GuardrailLogs { get; }

    public IRepository<PlatformCredential> Credentials { get; }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        _context.SaveChangesAsync(cancellationToken);

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }
}
