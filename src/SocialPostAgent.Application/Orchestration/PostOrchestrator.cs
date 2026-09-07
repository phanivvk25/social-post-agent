using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using SocialPostAgent.Application.DTOs;
using SocialPostAgent.Application.Generation;
using SocialPostAgent.Application.Guardrails;
using SocialPostAgent.Application.Platforms;
using SocialPostAgent.Domain;
using SocialPostAgent.Domain.Entities;
using SocialPostAgent.Domain.Repositories;

namespace SocialPostAgent.Application.Orchestration;

// This file holds the whole "Semantic Kernel Orchestrator" box from the architecture:
// the pipeline contract, the scheduling contract it hands off to, the Semantic Kernel
// plugin that exposes each platform publisher as a callable function, and the
// orchestrator implementation that walks Generate -> Guardrails -> HITL/Autonomous ->
// Schedule -> Publish. It is kept in one file because every piece here only makes sense
// in terms of the others — splitting it further would mean jumping between files to
// read one linear pipeline.

#region Contracts

/// <summary>The full post pipeline, called by the Web API layer.</summary>
public interface IPostOrchestrator
{
    /// <summary>Generates copy and (usually) an image from a brief, runs guardrails, and routes the post accordingly.</summary>
    Task<Post> ComposeAsync(ComposePostRequest request, CancellationToken cancellationToken = default);

    /// <summary>Human approval of a post sitting in Pending Review (HITL path).</summary>
    Task<Post> ApproveAsync(Guid postId, ApprovePostRequest request, CancellationToken cancellationToken = default);

    /// <summary>Human rejection of a post sitting in Pending Review.</summary>
    Task<Post> RejectAsync(Guid postId, RejectPostRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Actually publishes an Approved/Scheduled post to every one of its target platforms.
    /// This is the method the background job scheduler calls back into — see <see cref="IPostScheduler"/>.
    /// </summary>
    Task PublishScheduledPostAsync(Guid postId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Hands a post off to be published now or at a future time. The Application layer only
/// depends on this abstraction; the concrete implementation (Hangfire, in Infrastructure)
/// is a background-job technology choice the pipeline logic should never need to know about.
/// </summary>
public interface IPostScheduler
{
    void ScheduleImmediate(Guid postId);

    void ScheduleAt(Guid postId, DateTimeOffset whenUtc);
}

#endregion

#region Semantic Kernel plugin

/// <summary>
/// Exposes each independent platform publisher as a Semantic Kernel function. The three
/// platforms never call each other or share state — this plugin is only a uniform,
/// SK-native calling surface over the same <see cref="IPlatformPublisher"/> instances the
/// orchestrator would otherwise call directly, which is what lets the "Orchestration
/// Layer (.NET)" box in the architecture route publishing through Semantic Kernel.
/// </summary>
public sealed class SocialPublishingPlugin
{
    private readonly IReadOnlyDictionary<PlatformType, IPlatformPublisher> _publishersByPlatform;

    public SocialPublishingPlugin(IEnumerable<IPlatformPublisher> publishers)
    {
        _publishersByPlatform = publishers.ToDictionary(p => p.Platform);
    }

    [KernelFunction("publish_to_facebook")]
    [Description("Publishes the given post to the configured Facebook Page.")]
    public Task<PlatformPublishResult> PublishToFacebookAsync(Post post, PlatformCredential credential, CancellationToken cancellationToken = default) =>
        DispatchAsync(PlatformType.Facebook, post, credential, cancellationToken);

    [KernelFunction("publish_to_instagram")]
    [Description("Publishes the given post to the configured Instagram Business account.")]
    public Task<PlatformPublishResult> PublishToInstagramAsync(Post post, PlatformCredential credential, CancellationToken cancellationToken = default) =>
        DispatchAsync(PlatformType.Instagram, post, credential, cancellationToken);

    [KernelFunction("publish_to_linkedin")]
    [Description("Publishes the given post to the configured LinkedIn Company Page.")]
    public Task<PlatformPublishResult> PublishToLinkedInAsync(Post post, PlatformCredential credential, CancellationToken cancellationToken = default) =>
        DispatchAsync(PlatformType.LinkedIn, post, credential, cancellationToken);

    private Task<PlatformPublishResult> DispatchAsync(PlatformType platform, Post post, PlatformCredential credential, CancellationToken cancellationToken) =>
        _publishersByPlatform.TryGetValue(platform, out var publisher)
            ? publisher.PublishAsync(post, credential, cancellationToken)
            : Task.FromResult(PlatformPublishResult.Failed($"No publisher is registered for {platform}."));
}

#endregion

#region Orchestrator

/// <summary>
/// Coordinates the full post lifecycle. Every step is deliberately explicit rather than
/// LLM-planned — which platforms to call and in what order is not a decision that needs
/// reasoning, it is fixed by <see cref="Post.TargetPlatforms"/> — so Semantic Kernel is
/// used here as a function-calling surface (see <see cref="SocialPublishingPlugin"/>),
/// not as a planner. That keeps the pipeline auditable and its failure modes boring.
/// </summary>
public sealed class PostOrchestrator : IPostOrchestrator
{
    private const string PluginName = "SocialPublishing";

    private readonly IUnitOfWork _unitOfWork;
    private readonly IContentGenerationService _contentGenerationService;
    private readonly IGraphicsGenerationService _graphicsGenerationService;
    private readonly GuardrailEngine _guardrailEngine;
    private readonly IPostScheduler _scheduler;
    private readonly Kernel _kernel;
    private readonly ILogger<PostOrchestrator> _logger;

    public PostOrchestrator(
        IUnitOfWork unitOfWork,
        IContentGenerationService contentGenerationService,
        IGraphicsGenerationService graphicsGenerationService,
        GuardrailEngine guardrailEngine,
        IPostScheduler scheduler,
        Kernel kernel,
        ILogger<PostOrchestrator> logger)
    {
        _unitOfWork = unitOfWork;
        _contentGenerationService = contentGenerationService;
        _graphicsGenerationService = graphicsGenerationService;
        _guardrailEngine = guardrailEngine;
        _scheduler = scheduler;
        _kernel = kernel;
        _logger = logger;
    }

    public async Task<Post> ComposeAsync(ComposePostRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Platforms.Count == 0)
        {
            throw new ArgumentException("At least one target platform must be selected.", nameof(request));
        }

        var post = new Post
        {
            Brief = request.Brief,
            Mode = request.Mode,
            TargetPlatforms = request.Platforms.Select(p => p.ToString()).ToList(),
            ScheduledAtUtc = request.ScheduledAtUtc,
            Status = PostStatus.Draft
        };

        var captions = await _contentGenerationService.GenerateCaptionsAsync(request.Brief, request.Platforms, cancellationToken);
        foreach (var (platform, caption) in captions)
        {
            post.SetCaptionFor(platform, caption);
        }

        await TryGenerateImageAsync(post, cancellationToken);

        await _unitOfWork.Posts.AddAsync(post, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        await RouteAfterGenerationAsync(post, cancellationToken);

        return post;
    }

    public async Task<Post> ApproveAsync(Guid postId, ApprovePostRequest request, CancellationToken cancellationToken = default)
    {
        var post = await GetPostOrThrowAsync(postId, cancellationToken);

        if (post.Status != PostStatus.PendingReview)
        {
            throw new InvalidOperationException($"Post {postId} is not pending review (current status: {post.Status}).");
        }

        post.ReviewNotes = request.ReviewNotes;
        post.Status = PostStatus.Approved;

        if (request.ScheduledAtUtc is { } scheduledAt && scheduledAt > DateTimeOffset.UtcNow)
        {
            post.ScheduledAtUtc = scheduledAt;
            post.Status = PostStatus.Scheduled;
            _unitOfWork.Posts.Update(post);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            _scheduler.ScheduleAt(post.Id, scheduledAt);
        }
        else
        {
            _unitOfWork.Posts.Update(post);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            _scheduler.ScheduleImmediate(post.Id);
        }

        return post;
    }

    public async Task<Post> RejectAsync(Guid postId, RejectPostRequest request, CancellationToken cancellationToken = default)
    {
        var post = await GetPostOrThrowAsync(postId, cancellationToken);

        if (post.Status != PostStatus.PendingReview)
        {
            throw new InvalidOperationException($"Post {postId} is not pending review (current status: {post.Status}).");
        }

        post.Status = PostStatus.Rejected;
        post.ReviewNotes = request.ReviewNotes;

        _unitOfWork.Posts.Update(post);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return post;
    }

    public async Task PublishScheduledPostAsync(Guid postId, CancellationToken cancellationToken = default)
    {
        var post = await GetPostOrThrowAsync(postId, cancellationToken);

        if (post.Status is not (PostStatus.Approved or PostStatus.Scheduled))
        {
            _logger.LogWarning("PublishScheduledPostAsync called for post {PostId} in unexpected status {Status}; skipping.", postId, post.Status);
            return;
        }

        post.Status = PostStatus.Publishing;
        _unitOfWork.Posts.Update(post);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        var anySucceeded = false;

        foreach (var platformName in post.TargetPlatforms)
        {
            if (!Enum.TryParse<PlatformType>(platformName, ignoreCase: true, out var platform))
            {
                continue;
            }

            var result = await PublishToOnePlatformAsync(post, platform, cancellationToken);
            anySucceeded |= result.Success;

            await _unitOfWork.PlatformResults.AddAsync(new PlatformResult
            {
                PostId = post.Id,
                Platform = platform,
                RemotePostId = result.RemotePostId,
                RemoteUrl = result.RemoteUrl,
                Status = result.Success ? PublishStatus.Success : PublishStatus.Failed,
                ErrorMessage = result.ErrorMessage
            }, cancellationToken);
        }

        post.Status = anySucceeded ? PostStatus.Published : PostStatus.Failed;
        post.PublishedAtUtc = anySucceeded ? DateTimeOffset.UtcNow : post.PublishedAtUtc;

        _unitOfWork.Posts.Update(post);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private async Task<PlatformPublishResult> PublishToOnePlatformAsync(Post post, PlatformType platform, CancellationToken cancellationToken)
    {
        var credentials = await _unitOfWork.Credentials.ListAsync(c => c.Platform == platform, cancellationToken);
        var credential = credentials.FirstOrDefault();

        if (credential is null)
        {
            _logger.LogWarning("No credential configured for {Platform}; post {PostId} cannot be published there.", platform, post.Id);
            return PlatformPublishResult.Failed($"No {platform} credential is configured.");
        }

        var functionName = platform switch
        {
            PlatformType.Facebook => "publish_to_facebook",
            PlatformType.Instagram => "publish_to_instagram",
            PlatformType.LinkedIn => "publish_to_linkedin",
            _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, "Unknown platform.")
        };

        var function = _kernel.Plugins.GetFunction(PluginName, functionName);
        var arguments = new KernelArguments
        {
            ["post"] = post,
            ["credential"] = credential
        };

        try
        {
            var functionResult = await _kernel.InvokeAsync(function, arguments, cancellationToken);
            return functionResult.GetValue<PlatformPublishResult>()
                ?? PlatformPublishResult.Failed($"The {platform} publish function returned no result.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Publishing post {PostId} to {Platform} threw unexpectedly", post.Id, platform);
            return PlatformPublishResult.Failed($"Unexpected error publishing to {platform}: {ex.Message}");
        }
    }

    /// <summary>
    /// After generation, decides whether the post needs a human (HITL, or an autonomous
    /// post that failed a blocking guardrail) or can proceed straight to scheduling.
    /// </summary>
    private async Task RouteAfterGenerationAsync(Post post, CancellationToken cancellationToken)
    {
        var report = await _guardrailEngine.EvaluateAsync(post, cancellationToken);

        if (post.Mode == PostMode.Hitl)
        {
            post.Status = PostStatus.PendingReview;
        }
        else if (report.HasBlockingFailure)
        {
            post.Status = PostStatus.PendingReview;
            post.ReviewNotes = "Routed to review automatically: one or more guardrails blocked autonomous publishing.";
        }
        else
        {
            post.Status = PostStatus.Approved;
        }

        _unitOfWork.Posts.Update(post);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        if (post.Status == PostStatus.Approved)
        {
            if (post.ScheduledAtUtc is { } scheduledAt && scheduledAt > DateTimeOffset.UtcNow)
            {
                post.Status = PostStatus.Scheduled;
                _unitOfWork.Posts.Update(post);
                await _unitOfWork.SaveChangesAsync(cancellationToken);
                _scheduler.ScheduleAt(post.Id, scheduledAt);
            }
            else
            {
                _scheduler.ScheduleImmediate(post.Id);
            }
        }
    }

    private async Task TryGenerateImageAsync(Post post, CancellationToken cancellationToken)
    {
        try
        {
            var prompt = $"A brand-appropriate social media graphic for: {post.Brief}";
            var image = await _graphicsGenerationService.GenerateImageAsync(prompt, cancellationToken);
            post.ImagePrompt = prompt;
            post.ImagePath = image.PublicUrl;
        }
        catch (Exception ex)
        {
            // Non-fatal: RequiredMediaGuardrail will catch a missing image if Instagram is
            // targeted, forcing the post to review instead of silently dropping it.
            _logger.LogError(ex, "Image generation failed for a post with brief '{Brief}'", post.Brief);
        }
    }

    private async Task<Post> GetPostOrThrowAsync(Guid postId, CancellationToken cancellationToken)
    {
        return await _unitOfWork.Posts.GetByIdAsync(postId, cancellationToken)
            ?? throw new KeyNotFoundException($"Post {postId} was not found.");
    }
}

#endregion
