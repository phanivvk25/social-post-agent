using System.Linq.Expressions;
using SocialPostAgent.Domain.Common;
using SocialPostAgent.Domain.Entities;

namespace SocialPostAgent.Domain.Repositories;

/// <summary>
/// Generic, entity-agnostic data access contract. Every aggregate in the system goes
/// through this same interface rather than a bespoke repository per entity, so adding
/// a new entity never requires new CRUD plumbing — only a new <see cref="IUnitOfWork"/>
/// accessor and an EF Core configuration.
/// </summary>
/// <typeparam name="TEntity">A persisted entity deriving from <see cref="BaseEntity"/>.</typeparam>
public interface IRepository<TEntity> where TEntity : BaseEntity
{
    Task<TEntity?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Returns tracked-free results; pass a predicate to filter server-side.</summary>
    Task<IReadOnlyList<TEntity>> ListAsync(
        Expression<Func<TEntity, bool>>? predicate = null,
        CancellationToken cancellationToken = default);

    /// <summary>Escape hatch for callers that need ordering, paging, includes, etc.</summary>
    IQueryable<TEntity> Query();

    Task AddAsync(TEntity entity, CancellationToken cancellationToken = default);

    void Update(TEntity entity);

    void Remove(TEntity entity);
}

/// <summary>
/// Groups the repositories that share one EF Core change-tracking context and
/// commits them together in a single transaction via <see cref="SaveChangesAsync"/>.
/// </summary>
public interface IUnitOfWork
{
    IRepository<Post> Posts { get; }

    IRepository<PlatformResult> PlatformResults { get; }

    IRepository<GuardrailLog> GuardrailLogs { get; }

    IRepository<PlatformCredential> Credentials { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
