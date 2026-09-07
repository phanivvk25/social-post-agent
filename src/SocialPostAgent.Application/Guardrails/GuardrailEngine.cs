using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SocialPostAgent.Domain;
using SocialPostAgent.Domain.Entities;
using SocialPostAgent.Domain.Repositories;

namespace SocialPostAgent.Application.Guardrails;

// Every guardrail rule the architecture calls for — format/mechanical, brand-safety,
// image, duplicate/spam and operational — lives in this one file. They are small,
// single-purpose classes; keeping them together (rather than one file each) makes the
// full set of checks a post goes through reviewable top-to-bottom in one place, and
// keeps GuardrailEngine's constructor list obvious at a glance.

#region Results and configuration

/// <summary>
/// The verdict of one guardrail rule against one post. <see cref="Message"/> is written
/// for a PR/marketing reviewer reading it in the dashboard, not an engineer reading logs.
/// </summary>
public sealed record GuardrailResult(string RuleName, GuardrailSeverity Severity, bool Passed, string Message)
{
    public static GuardrailResult Pass(string ruleName, GuardrailSeverity severity, string message) =>
        new(ruleName, severity, Passed: true, message);

    public static GuardrailResult Fail(string ruleName, GuardrailSeverity severity, string message) =>
        new(ruleName, severity, Passed: false, message);
}

/// <summary>Aggregate outcome of running every registered guardrail against one post.</summary>
public sealed record GuardrailReport(IReadOnlyList<GuardrailResult> Results)
{
    public bool HasBlockingFailure => Results.Any(r => !r.Passed && r.Severity == GuardrailSeverity.Block);

    public bool HasWarning => Results.Any(r => !r.Passed && r.Severity == GuardrailSeverity.Warn);
}

/// <summary>Tunable thresholds for every rule, bound from configuration ("Guardrails" section).</summary>
public sealed class GuardrailOptions
{
    public const string SectionName = "Guardrails";

    public int MaxHashtagsPerPost { get; set; } = 30;

    public string[] BannedWords { get; set; } = Array.Empty<string>();

    public string[] SensitiveTopics { get; set; } = Array.Empty<string>();

    public int DuplicateLookbackDays { get; set; } = 14;

    public int MaxPostsPerDay { get; set; } = 10;

    public int MaxConsecutiveFailuresBeforeCircuitBreak { get; set; } = 3;

    /// <summary>Global autonomous-mode kill switch. When true, every autonomous post is forced to Pending Review.</summary>
    public bool AutonomousModeSuspended { get; set; }
}

/// <summary>Everything a rule needs to evaluate a post beyond the post itself.</summary>
public interface IGuardrail
{
    string RuleName { get; }

    GuardrailSeverity Severity { get; }

    Task<GuardrailResult> EvaluateAsync(Post post, CancellationToken cancellationToken = default);
}

#endregion

#region Format / mechanical guardrails

/// <summary>Rejects captions that exceed the target platform's hard character limit.</summary>
public sealed class CaptionLengthGuardrail : IGuardrail
{
    private static readonly IReadOnlyDictionary<PlatformType, int> Limits = new Dictionary<PlatformType, int>
    {
        [PlatformType.Facebook] = 63_206,
        [PlatformType.Instagram] = 2200,
        [PlatformType.LinkedIn] = 3000
    };

    public string RuleName => "caption-length";

    public GuardrailSeverity Severity => GuardrailSeverity.Block;

    public Task<GuardrailResult> EvaluateAsync(Post post, CancellationToken cancellationToken = default)
    {
        foreach (var platformName in post.TargetPlatforms)
        {
            if (!Enum.TryParse<PlatformType>(platformName, ignoreCase: true, out var platform))
            {
                continue;
            }

            var caption = post.GetCaptionFor(platform);
            var limit = Limits[platform];

            if (!string.IsNullOrEmpty(caption) && caption.Length > limit)
            {
                return Task.FromResult(GuardrailResult.Fail(RuleName, Severity,
                    $"The {platform} caption is {caption.Length} characters, which exceeds {platform}'s {limit}-character limit."));
            }
        }

        return Task.FromResult(GuardrailResult.Pass(RuleName, Severity, "All captions are within their platform's character limit."));
    }
}

/// <summary>Instagram cannot publish a text-only post — it always needs an image or video.</summary>
public sealed class RequiredMediaGuardrail : IGuardrail
{
    public string RuleName => "required-media";

    public GuardrailSeverity Severity => GuardrailSeverity.Block;

    public Task<GuardrailResult> EvaluateAsync(Post post, CancellationToken cancellationToken = default)
    {
        if (post.TargetsPlatform(PlatformType.Instagram) && string.IsNullOrWhiteSpace(post.ImagePath))
        {
            return Task.FromResult(GuardrailResult.Fail(RuleName, Severity,
                "Instagram requires an image or video — this post has no generated graphic yet."));
        }

        return Task.FromResult(GuardrailResult.Pass(RuleName, Severity, "Required media is present for every targeted platform that needs it."));
    }
}

/// <summary>Flags hashtag stuffing, which reads as spammy and can suppress reach on some platforms.</summary>
public sealed class HashtagCountGuardrail : IGuardrail
{
    private readonly GuardrailOptions _options;

    public HashtagCountGuardrail(IOptions<GuardrailOptions> options)
    {
        _options = options.Value;
    }

    public string RuleName => "hashtag-count";

    public GuardrailSeverity Severity => GuardrailSeverity.Warn;

    public Task<GuardrailResult> EvaluateAsync(Post post, CancellationToken cancellationToken = default)
    {
        foreach (var platformName in post.TargetPlatforms)
        {
            if (!Enum.TryParse<PlatformType>(platformName, ignoreCase: true, out var platform))
            {
                continue;
            }

            var caption = post.GetCaptionFor(platform) ?? string.Empty;
            var hashtagCount = caption.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Count(token => token.StartsWith('#'));

            if (hashtagCount > _options.MaxHashtagsPerPost)
            {
                return Task.FromResult(GuardrailResult.Fail(RuleName, Severity,
                    $"The {platform} caption has {hashtagCount} hashtags, more than the recommended {_options.MaxHashtagsPerPost}."));
            }
        }

        return Task.FromResult(GuardrailResult.Pass(RuleName, Severity, "Hashtag counts look reasonable."));
    }
}

#endregion

#region Brand-safety guardrails

/// <summary>Blocks captions containing a configured banned word or phrase (profanity, competitor names, off-brand terms).</summary>
public sealed class BannedWordsGuardrail : IGuardrail
{
    private readonly GuardrailOptions _options;

    public BannedWordsGuardrail(IOptions<GuardrailOptions> options)
    {
        _options = options.Value;
    }

    public string RuleName => "banned-words";

    public GuardrailSeverity Severity => GuardrailSeverity.Block;

    public Task<GuardrailResult> EvaluateAsync(Post post, CancellationToken cancellationToken = default)
    {
        var allText = string.Join(' ', post.FacebookText, post.InstagramText, post.LinkedInText);

        foreach (var banned in _options.BannedWords)
        {
            if (!string.IsNullOrWhiteSpace(banned) &&
                allText.Contains(banned, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(GuardrailResult.Fail(RuleName, Severity,
                    $"The generated copy contains the banned term \"{banned}\". Edit the caption before this can be published."));
            }
        }

        return Task.FromResult(GuardrailResult.Pass(RuleName, Severity, "No banned words or phrases were found."));
    }
}

/// <summary>Warns (does not block) when a caption touches a configured sensitive topic, for a human to judge.</summary>
public sealed class SensitiveTopicsGuardrail : IGuardrail
{
    private readonly GuardrailOptions _options;

    public SensitiveTopicsGuardrail(IOptions<GuardrailOptions> options)
    {
        _options = options.Value;
    }

    public string RuleName => "sensitive-topics";

    public GuardrailSeverity Severity => GuardrailSeverity.Warn;

    public Task<GuardrailResult> EvaluateAsync(Post post, CancellationToken cancellationToken = default)
    {
        var allText = string.Join(' ', post.FacebookText, post.InstagramText, post.LinkedInText);

        var matched = _options.SensitiveTopics
            .Where(topic => !string.IsNullOrWhiteSpace(topic) && allText.Contains(topic, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matched.Count > 0)
        {
            return Task.FromResult(GuardrailResult.Fail(RuleName, Severity,
                $"This post touches on a configured sensitive topic ({string.Join(", ", matched)}) — please double-check tone before approving."));
        }

        return Task.FromResult(GuardrailResult.Pass(RuleName, Severity, "No configured sensitive topics detected."));
    }
}

/// <summary>
/// Flags near-identical content published to the same platform within the configured
/// lookback window — platforms suppress and viewers notice repetitive posting.
/// </summary>
public sealed class DuplicateContentGuardrail : IGuardrail
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly GuardrailOptions _options;

    public DuplicateContentGuardrail(IUnitOfWork unitOfWork, IOptions<GuardrailOptions> options)
    {
        _unitOfWork = unitOfWork;
        _options = options.Value;
    }

    public string RuleName => "duplicate-content";

    public GuardrailSeverity Severity => GuardrailSeverity.Warn;

    public async Task<GuardrailResult> EvaluateAsync(Post post, CancellationToken cancellationToken = default)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-_options.DuplicateLookbackDays);

        var recentPublished = await _unitOfWork.Posts.ListAsync(
            p => p.Status == PostStatus.Published && p.PublishedAtUtc >= since && p.Id != post.Id,
            cancellationToken);

        foreach (var candidate in recentPublished)
        {
            if (IsNearDuplicate(post.FacebookText, candidate.FacebookText) ||
                IsNearDuplicate(post.InstagramText, candidate.InstagramText) ||
                IsNearDuplicate(post.LinkedInText, candidate.LinkedInText))
            {
                return GuardrailResult.Fail(RuleName, Severity,
                    $"This looks very similar to a post published on {candidate.PublishedAtUtc:yyyy-MM-dd}. Consider varying the copy.");
            }
        }

        return GuardrailResult.Pass(RuleName, Severity, "No near-duplicate of a recently published post was found.");
    }

    private static bool IsNearDuplicate(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return false;
        }

        static string Normalize(string s) => string.Join(' ', s.ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return Normalize(a) == Normalize(b);
    }
}

#endregion

#region Operational guardrails

/// <summary>Caps the number of posts published per calendar day, regardless of how much content was generated.</summary>
public sealed class DailyPostCapGuardrail : IGuardrail
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly GuardrailOptions _options;

    public DailyPostCapGuardrail(IUnitOfWork unitOfWork, IOptions<GuardrailOptions> options)
    {
        _unitOfWork = unitOfWork;
        _options = options.Value;
    }

    public string RuleName => "daily-post-cap";

    public GuardrailSeverity Severity => GuardrailSeverity.Block;

    public async Task<GuardrailResult> EvaluateAsync(Post post, CancellationToken cancellationToken = default)
    {
        var startOfDayUtc = DateTimeOffset.UtcNow.Date;

        var publishedToday = await _unitOfWork.Posts.ListAsync(
            p => p.Status == PostStatus.Published && p.PublishedAtUtc >= startOfDayUtc,
            cancellationToken);

        if (publishedToday.Count >= _options.MaxPostsPerDay)
        {
            return GuardrailResult.Fail(RuleName, Severity,
                $"The daily posting cap of {_options.MaxPostsPerDay} has already been reached today.");
        }

        return GuardrailResult.Pass(RuleName, Severity, "Daily post cap has not been reached.");
    }
}

/// <summary>
/// Trips after too many consecutive publish failures, so a broken integration (an
/// expired token, a platform outage) cannot keep failing silently in a loop.
/// </summary>
public sealed class CircuitBreakerGuardrail : IGuardrail
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly GuardrailOptions _options;

    public CircuitBreakerGuardrail(IUnitOfWork unitOfWork, IOptions<GuardrailOptions> options)
    {
        _unitOfWork = unitOfWork;
        _options = options.Value;
    }

    public string RuleName => "circuit-breaker";

    public GuardrailSeverity Severity => GuardrailSeverity.Block;

    public async Task<GuardrailResult> EvaluateAsync(Post post, CancellationToken cancellationToken = default)
    {
        // Query() is the escape hatch on IRepository<T> for ordering/paging; .ToList() executes
        // synchronously against the provider (EF Core included) without pulling an EF Core
        // package reference into this persistence-ignorant Application layer.
        var recentResults = _unitOfWork.PlatformResults.Query()
            .OrderByDescending(r => r.AttemptedAtUtc)
            .Take(_options.MaxConsecutiveFailuresBeforeCircuitBreak)
            .ToList();

        var enoughHistory = recentResults.Count == _options.MaxConsecutiveFailuresBeforeCircuitBreak;
        var allFailed = recentResults.All(r => r.Status == PublishStatus.Failed);

        if (enoughHistory && allFailed)
        {
            return GuardrailResult.Fail(RuleName, Severity,
                $"The last {_options.MaxConsecutiveFailuresBeforeCircuitBreak} publish attempts all failed — " +
                "autonomous publishing is paused until this is reviewed.");
        }

        return GuardrailResult.Pass(RuleName, Severity, "No repeated publish failures detected.");
    }
}

/// <summary>The manual, always-available override: when tripped, every autonomous post is forced to human review.</summary>
public sealed class KillSwitchGuardrail : IGuardrail
{
    private readonly GuardrailOptions _options;

    public KillSwitchGuardrail(IOptions<GuardrailOptions> options)
    {
        _options = options.Value;
    }

    public string RuleName => "kill-switch";

    public GuardrailSeverity Severity => GuardrailSeverity.Block;

    public Task<GuardrailResult> EvaluateAsync(Post post, CancellationToken cancellationToken = default)
    {
        if (post.Mode == PostMode.Autonomous && _options.AutonomousModeSuspended)
        {
            return Task.FromResult(GuardrailResult.Fail(RuleName, Severity,
                "Autonomous publishing has been manually paused. This post has been routed to Pending Review."));
        }

        return Task.FromResult(GuardrailResult.Pass(RuleName, Severity, "Autonomous publishing is enabled."));
    }
}

#endregion

#region Engine

/// <summary>
/// Runs every registered <see cref="IGuardrail"/> against a post, persists a
/// <see cref="GuardrailLog"/> row per rule for the dashboard's "why was this flagged"
/// view, and returns the aggregate verdict. Rules never short-circuit each other —
/// a reviewer (or the orchestrator) sees every finding at once, not just the first one.
/// </summary>
public sealed class GuardrailEngine
{
    private readonly IReadOnlyList<IGuardrail> _guardrails;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<GuardrailEngine> _logger;

    public GuardrailEngine(IEnumerable<IGuardrail> guardrails, IUnitOfWork unitOfWork, ILogger<GuardrailEngine> logger)
    {
        _guardrails = guardrails.ToList();
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task<GuardrailReport> EvaluateAsync(Post post, CancellationToken cancellationToken = default)
    {
        var results = new List<GuardrailResult>(_guardrails.Count);

        foreach (var guardrail in _guardrails)
        {
            GuardrailResult result;
            try
            {
                result = await guardrail.EvaluateAsync(post, cancellationToken);
            }
            catch (Exception ex)
            {
                // A misbehaving rule must never silently let a post through — treat it as a block.
                _logger.LogError(ex, "Guardrail {RuleName} threw while evaluating post {PostId}", guardrail.RuleName, post.Id);
                result = GuardrailResult.Fail(guardrail.RuleName, GuardrailSeverity.Block,
                    "This check could not be completed and was treated as a failure. Please review manually.");
            }

            results.Add(result);

            await _unitOfWork.GuardrailLogs.AddAsync(new GuardrailLog
            {
                PostId = post.Id,
                RuleName = result.RuleName,
                Severity = result.Severity,
                Passed = result.Passed,
                Message = result.Message
            }, cancellationToken);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return new GuardrailReport(results);
    }
}

#endregion
