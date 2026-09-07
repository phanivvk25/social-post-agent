using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SocialPostAgent.Application.Security;
using SocialPostAgent.Domain;
using SocialPostAgent.Domain.Entities;

namespace SocialPostAgent.Application.Platforms;

/// <summary>
/// Publishes to an Instagram Business/Creator account via the Meta Graph API's Content
/// Publishing flow. Instagram has no dedicated API of its own — it rides on the same
/// Graph API as Facebook, but always requires two calls: create a media container from
/// a publicly reachable image URL, then publish that container. A text-only post is
/// rejected by the API itself, which is also why <c>RequiredMediaGuardrail</c> exists.
/// </summary>
public sealed class InstagramPublisher : IPlatformPublisher
{
    private static readonly TimeSpan ContainerPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ContainerReadyTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _httpClient;
    private readonly ICredentialProtector _credentialProtector;
    private readonly MetaGraphApiOptions _options;
    private readonly ILogger<InstagramPublisher> _logger;

    public InstagramPublisher(
        HttpClient httpClient,
        ICredentialProtector credentialProtector,
        IOptions<MetaGraphApiOptions> options,
        ILogger<InstagramPublisher> logger)
    {
        _httpClient = httpClient;
        _credentialProtector = credentialProtector;
        _options = options.Value;
        _logger = logger;
    }

    public PlatformType Platform => PlatformType.Instagram;

    public async Task<PlatformPublishResult> PublishAsync(
        Post post,
        PlatformCredential credential,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(credential.AccountId))
        {
            return PlatformPublishResult.Failed("No Instagram Business Account ID is configured for this credential.");
        }

        if (string.IsNullOrWhiteSpace(post.ImagePath))
        {
            return PlatformPublishResult.Failed("Instagram requires an image — this post has none.");
        }

        var accessToken = _credentialProtector.Unprotect(credential.EncryptedAccessToken);
        var caption = post.InstagramText ?? string.Empty;

        try
        {
            var containerId = await CreateMediaContainerAsync(credential.AccountId, post.ImagePath, caption, accessToken, cancellationToken);
            if (containerId is null)
            {
                return PlatformPublishResult.Failed("Instagram rejected the media container creation request.");
            }

            var ready = await WaitForContainerReadyAsync(containerId, accessToken, cancellationToken);
            if (!ready)
            {
                return PlatformPublishResult.Failed("Instagram's media container did not finish processing in time.");
            }

            var publishUrl = $"{_options.BaseUrl}/{_options.ApiVersion}/{credential.AccountId}/media_publish";
            var publishResponse = await _httpClient.PostAsync(publishUrl, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["creation_id"] = containerId,
                ["access_token"] = accessToken
            }), cancellationToken);

            if (!publishResponse.IsSuccessStatusCode)
            {
                var error = await ReadErrorMessageAsync(publishResponse, cancellationToken);
                _logger.LogWarning("Instagram publish failed for post {PostId}: {Message}", post.Id, error);
                return PlatformPublishResult.Failed(error);
            }

            var published = await publishResponse.Content.ReadFromJsonAsync<MediaEnvelope>(cancellationToken: cancellationToken);
            var mediaId = published?.Id;

            if (string.IsNullOrEmpty(mediaId))
            {
                return PlatformPublishResult.Failed("Instagram accepted the publish request but returned no media ID.");
            }

            return PlatformPublishResult.Succeeded(mediaId, remoteUrl: null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogError(ex, "Instagram publish threw for post {PostId}", post.Id);
            return PlatformPublishResult.Failed($"Could not reach the Instagram Graph API: {ex.Message}");
        }
    }

    private async Task<string?> CreateMediaContainerAsync(
        string igBusinessAccountId,
        string imageUrl,
        string caption,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var url = $"{_options.BaseUrl}/{_options.ApiVersion}/{igBusinessAccountId}/media";

        var response = await _httpClient.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["image_url"] = imageUrl,
            ["caption"] = caption,
            ["access_token"] = accessToken
        }), cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await ReadErrorMessageAsync(response, cancellationToken);
            _logger.LogWarning("Instagram media container creation failed: {Message}", error);
            return null;
        }

        var body = await response.Content.ReadFromJsonAsync<MediaEnvelope>(cancellationToken: cancellationToken);
        return body?.Id;
    }

    /// <summary>
    /// Instagram processes the image asynchronously after container creation; publishing
    /// before it reports "FINISHED" fails. This polls status_code on a short interval up
    /// to <see cref="ContainerReadyTimeout"/> before giving up.
    /// </summary>
    private async Task<bool> WaitForContainerReadyAsync(string containerId, string accessToken, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.Add(ContainerReadyTimeout);
        var statusUrl = $"{_options.BaseUrl}/{_options.ApiVersion}/{containerId}?fields=status_code&access_token={accessToken}";

        while (DateTimeOffset.UtcNow < deadline)
        {
            var response = await _httpClient.GetAsync(statusUrl, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var status = await response.Content.ReadFromJsonAsync<ContainerStatusEnvelope>(cancellationToken: cancellationToken);
                if (string.Equals(status?.StatusCode, "FINISHED", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (string.Equals(status?.StatusCode, "ERROR", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            await Task.Delay(ContainerPollInterval, cancellationToken);
        }

        return false;
    }

    private static async Task<string> ReadErrorMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var error = await response.Content.ReadFromJsonAsync<GraphApiErrorEnvelope>(cancellationToken: cancellationToken);
        return error?.Error?.Message ?? $"Instagram Graph API returned {(int)response.StatusCode}.";
    }

    private sealed class MediaEnvelope
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }
    }

    private sealed class ContainerStatusEnvelope
    {
        [JsonPropertyName("status_code")]
        public string? StatusCode { get; set; }
    }

    private sealed class GraphApiErrorEnvelope
    {
        [JsonPropertyName("error")]
        public GraphApiError? Error { get; set; }
    }

    private sealed class GraphApiError
    {
        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }
}
