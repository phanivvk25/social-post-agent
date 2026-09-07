namespace SocialPostAgent.Domain.Common;

/// <summary>
/// Base class for every persisted aggregate in the system. Centralizes the identity
/// and audit-timestamp columns so entity classes only declare their own state.
/// </summary>
public abstract class BaseEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? UpdatedAtUtc { get; set; }
}
