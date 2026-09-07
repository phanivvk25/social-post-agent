using SocialPostAgent.Domain.Common;

namespace SocialPostAgent.Domain.Entities;

/// <summary>The outcome of one attempt to publish a <see cref="Post"/> to one platform.</summary>
public class PlatformResult : BaseEntity
{
    public Guid PostId { get; set; }

    public Post Post { get; set; } = null!;

    public PlatformType Platform { get; set; }

    /// <summary>The platform's own identifier for the published post, when successful.</summary>
    public string? RemotePostId { get; set; }

    /// <summary>A shareable link to the live post, when the platform provides one.</summary>
    public string? RemoteUrl { get; set; }

    public PublishStatus Status { get; set; }

    public string? ErrorMessage { get; set; }

    public DateTimeOffset AttemptedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
