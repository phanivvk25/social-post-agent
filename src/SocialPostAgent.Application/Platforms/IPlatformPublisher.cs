using SocialPostAgent.Domain;
using SocialPostAgent.Domain.Entities;

namespace SocialPostAgent.Application.Platforms;

/// <summary>Outcome of a single publish attempt against one platform's API.</summary>
public sealed record PlatformPublishResult(bool Success, string? RemotePostId, string? RemoteUrl, string? ErrorMessage)
{
    public static PlatformPublishResult Succeeded(string remotePostId, string? remoteUrl) =>
        new(true, remotePostId, remoteUrl, null);

    public static PlatformPublishResult Failed(string errorMessage) =>
        new(false, null, null, errorMessage);
}

/// <summary>
/// One implementation per platform (Facebook, Instagram, LinkedIn). Each is intentionally
/// independent of the others — publishing to one platform never depends on the outcome of
/// another, so there is no inter-module communication, only a shared contract the
/// orchestrator calls against.
/// </summary>
public interface IPlatformPublisher
{
    PlatformType Platform { get; }

    Task<PlatformPublishResult> PublishAsync(
        Post post,
        PlatformCredential credential,
        CancellationToken cancellationToken = default);
}
