using SocialPostAgent.Domain.Common;

namespace SocialPostAgent.Domain.Entities;

/// <summary>
/// A single piece of content moving through the pipeline: generated brief and copy,
/// an optional generated image, its target platforms, and where it currently sits
/// in the HITL / Autonomous workflow.
/// </summary>
public class Post : BaseEntity
{
    /// <summary>The plain-language brief the content was generated from (what the caller asked for).</summary>
    public string Brief { get; set; } = string.Empty;

    public string? FacebookText { get; set; }

    public string? InstagramText { get; set; }

    public string? LinkedInText { get; set; }

    /// <summary>Absolute or relative path/URL to the generated image, if any.</summary>
    public string? ImagePath { get; set; }

    /// <summary>The prompt used to generate <see cref="ImagePath"/>, kept for regeneration and auditing.</summary>
    public string? ImagePrompt { get; set; }

    public PostMode Mode { get; set; } = PostMode.Hitl;

    public PostStatus Status { get; set; } = PostStatus.Draft;

    /// <summary>
    /// Platform names this post should be published to (e.g. "Facebook", "Instagram").
    /// Stored as a native Postgres text array — see <c>PostConfiguration</c>.
    /// </summary>
    public List<string> TargetPlatforms { get; set; } = new();

    /// <summary>Free-text notes left by a reviewer (approval comment or rejection reason).</summary>
    public string? ReviewNotes { get; set; }

    /// <summary>When set, the post should be published at this time rather than immediately.</summary>
    public DateTimeOffset? ScheduledAtUtc { get; set; }

    public DateTimeOffset? PublishedAtUtc { get; set; }

    public ICollection<PlatformResult> PlatformResults { get; set; } = new List<PlatformResult>();

    public ICollection<GuardrailLog> GuardrailLogs { get; set; } = new List<GuardrailLog>();

    /// <summary>Returns the per-platform caption for the given platform, or null if not generated yet.</summary>
    public string? GetCaptionFor(PlatformType platform) => platform switch
    {
        PlatformType.Facebook => FacebookText,
        PlatformType.Instagram => InstagramText,
        PlatformType.LinkedIn => LinkedInText,
        _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, "Unknown platform.")
    };

    public void SetCaptionFor(PlatformType platform, string text)
    {
        switch (platform)
        {
            case PlatformType.Facebook:
                FacebookText = text;
                break;
            case PlatformType.Instagram:
                InstagramText = text;
                break;
            case PlatformType.LinkedIn:
                LinkedInText = text;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(platform), platform, "Unknown platform.");
        }
    }

    public bool TargetsPlatform(PlatformType platform) =>
        TargetPlatforms.Contains(platform.ToString(), StringComparer.OrdinalIgnoreCase);
}
