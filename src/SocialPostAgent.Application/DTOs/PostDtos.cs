using SocialPostAgent.Domain;
using SocialPostAgent.Domain.Entities;

namespace SocialPostAgent.Application.DTOs;

/// <summary>Request to generate a new post from a plain-language brief.</summary>
public sealed record ComposePostRequest(
    string Brief,
    IReadOnlyCollection<PlatformType> Platforms,
    PostMode Mode,
    DateTimeOffset? ScheduledAtUtc);

/// <summary>Approves a post that is pending human review, optionally scheduling it.</summary>
public sealed record ApprovePostRequest(string? ReviewNotes, DateTimeOffset? ScheduledAtUtc);

/// <summary>Rejects a post that is pending human review.</summary>
public sealed record RejectPostRequest(string ReviewNotes);

/// <summary>One guardrail finding, shaped for a PR/marketing reviewer rather than an engineer.</summary>
public sealed record GuardrailFindingDto(string RuleName, string Severity, bool Passed, string Message);

/// <summary>One platform's publish outcome for a post.</summary>
public sealed record PlatformResultDto(string Platform, bool Success, string? RemoteUrl, string? ErrorMessage, DateTimeOffset AttemptedAtUtc);

/// <summary>Full detail view of a post, as shown in the dashboard's review/detail screen.</summary>
public sealed record PostDetailDto(
    Guid Id,
    string Brief,
    string Status,
    string Mode,
    IReadOnlyCollection<string> TargetPlatforms,
    string? FacebookText,
    string? InstagramText,
    string? LinkedInText,
    string? ImageUrl,
    string? ReviewNotes,
    DateTimeOffset? ScheduledAtUtc,
    DateTimeOffset? PublishedAtUtc,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyCollection<GuardrailFindingDto> GuardrailFindings,
    IReadOnlyCollection<PlatformResultDto> PlatformResults)
{
    public static PostDetailDto FromEntity(Post post) => new(
        post.Id,
        post.Brief,
        post.Status.ToString(),
        post.Mode.ToString(),
        post.TargetPlatforms,
        post.FacebookText,
        post.InstagramText,
        post.LinkedInText,
        post.ImagePath,
        post.ReviewNotes,
        post.ScheduledAtUtc,
        post.PublishedAtUtc,
        post.CreatedAtUtc,
        post.GuardrailLogs
            .Select(g => new GuardrailFindingDto(g.RuleName, g.Severity.ToString(), g.Passed, g.Message))
            .ToList(),
        post.PlatformResults
            .Select(r => new PlatformResultDto(
                r.Platform.ToString(),
                r.Status == PublishStatus.Success,
                r.RemoteUrl,
                r.ErrorMessage,
                r.AttemptedAtUtc))
            .ToList());
}

/// <summary>Lightweight row for the review queue / post history list views.</summary>
public sealed record PostSummaryDto(
    Guid Id,
    string Brief,
    string Status,
    string Mode,
    IReadOnlyCollection<string> TargetPlatforms,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ScheduledAtUtc)
{
    public static PostSummaryDto FromEntity(Post post) => new(
        post.Id,
        post.Brief,
        post.Status.ToString(),
        post.Mode.ToString(),
        post.TargetPlatforms,
        post.CreatedAtUtc,
        post.ScheduledAtUtc);
}
