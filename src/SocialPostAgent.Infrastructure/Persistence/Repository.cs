using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using SocialPostAgent.Domain.Common;
using SocialPostAgent.Domain.Repositories;

namespace SocialPostAgent.Infrastructure.Persistence;

/// <summary>
/// The one and only <see cref="IRepository{TEntity}"/> implementation in the system.
/// Every entity gets CRUD support by virtue of deriving from <see cref="BaseEntity"/> —
/// nothing here is entity-specific, so a new aggregate never needs a new repository class.
/// </summary>
public class Repository<TEntity> : IRepository<TEntity> where TEntity : BaseEntity
{
    private readonly DbSet<TEntity> _set;

    public Repository(SocialPostAgentDbContext context)
    {
        _set = context.Set<TEntity>();
    }

    public async Task<TEntity?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        await _set.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);

    public async Task<IReadOnlyList<TEntity>> ListAsync(
        Expression<Func<TEntity, bool>>? predicate = null,
        CancellationToken cancellationToken = default)
    {
        IQueryable<TEntity> query = _set.AsNoTracking();

        if (predicate is not null)
        {
            query = query.Where(predicate);
        }

        return await query.ToListAsync(cancellationToken);
    }

    public IQueryable<TEntity> Query() => _set.AsQueryable();

    public async Task AddAsync(TEntity entity, CancellationToken cancellationToken = default) =>
        await _set.AddAsync(entity, cancellationToken);

    public void Update(TEntity entity) => _set.Update(entity);

    public void Remove(TEntity entity) => _set.Remove(entity);
}
