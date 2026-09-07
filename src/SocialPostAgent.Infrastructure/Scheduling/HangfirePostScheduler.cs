using Hangfire;
using SocialPostAgent.Application.Orchestration;

namespace SocialPostAgent.Infrastructure.Scheduling;

/// <summary>
/// Hangfire-backed implementation of <see cref="IPostScheduler"/>. Enqueuing here only
/// records a job in the <c>Posts</c>-adjacent Hangfire storage tables in Postgres — the
/// Hangfire server process configured in the API host (see Program.cs) is what actually
/// picks the job up and calls back into <see cref="IPostOrchestrator.PublishScheduledPostAsync"/>.
/// </summary>
public sealed class HangfirePostScheduler : IPostScheduler
{
    private readonly IBackgroundJobClient _backgroundJobClient;

    public HangfirePostScheduler(IBackgroundJobClient backgroundJobClient)
    {
        _backgroundJobClient = backgroundJobClient;
    }

    public void ScheduleImmediate(Guid postId)
    {
        _backgroundJobClient.Enqueue<IPostOrchestrator>(orchestrator =>
            orchestrator.PublishScheduledPostAsync(postId, CancellationToken.None));
    }

    public void ScheduleAt(Guid postId, DateTimeOffset whenUtc)
    {
        _backgroundJobClient.Schedule<IPostOrchestrator>(
            orchestrator => orchestrator.PublishScheduledPostAsync(postId, CancellationToken.None),
            whenUtc);
    }
}
