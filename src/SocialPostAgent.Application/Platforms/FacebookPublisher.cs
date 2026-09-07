using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SocialPostAgent.Application.Security;
using SocialPostAgent.Domain;
using SocialPostAgent.Domain.Entities;

namespace SocialPostAgent.Application.Platforms;

/// <summary>Shared Meta Graph API settings, bound from configuration ("MetaGraphApi" section).</summary>
public sealed class MetaGraphApiOptions
{
    public const string SectionName = "MetaGraphApi";

    public string BaseUrl { get; set; } = "https://graph.facebook.com";

    public string ApiVersion { get; set; } = "v21.0";
}

/// <summary>
/// Publishes a post to a Facebook Page via the Meta Graph API's <c>/feed</c> edge.
/// A Page access token (not a user token) is required — see README for how the app
/// review / permissions flow works.
/// </summary>
public sealed class FacebookPublisher : IPlatformPublisher
{
    private readonly HttpClient _httpClient;
    private readonly ICredentialProtector _credentialProtector;
    private readonly MetaGraphApiOptions _options;
    private readonly ILogger<FacebookPublisher> _logger;

    public FacebookPublisher(
        HttpClient httpClient,
        ICredentialProtector credentialProtector,
        IOptions<MetaGraphApiOptions> options,
        ILogger<FacebookPublisher> logger)
    {
        _httpClient = httpClient;
        _credentialProtector = credentialProtector;
        _options = options.Value;
        _logger = logger;
    }

    public PlatformType Platform => PlatformType.Facebook;

    public async Task<PlatformPublishResult> PublishAsync(
        Post post,
        PlatformCredential credential,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(credential.AccountId))
        {
            return PlatformPublishResult.Failed("No Facebook Page ID is configured for this credential.");
        }

        var pageAccessToken = _credentialProtector.Unprotect(credential.EncryptedAccessToken);
        var caption = post.FacebookText ?? string.Empty;
        var url = $"{_options.BaseUrl}/{_options.ApiVersion}/{credential.AccountId}/feed";

        try
        {
            HttpResponseMessage response;

            if (!string.IsNullOrWhiteSpace(post.ImagePath))
            {
                // Image posts go through /photos with the caption as the "message" field,
                // which both attaches the image and creates the feed post in one call.
                var photoUrl = $"{_options.BaseUrl}/{_options.ApiVersion}/{credential.AccountId}/photos";
                response = await _httpClient.PostAsync(photoUrl, new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["url"] = post.ImagePath,
                    ["message"] = caption,
                    ["access_token"] = pageAccessToken
                }), cancellationToken);
            }
            else
            {
                response = await _httpClient.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["message"] = caption,
                    ["access_token"] = pageAccessToken
                }), cancellationToken);
            }

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadFromJsonAsync<GraphApiErrorEnvelope>(cancellationToken: cancellationToken);
                var message = error?.Error?.Message ?? $"Facebook API returned {(int)response.StatusCode}.";
                _logger.LogWarning("Facebook publish failed for post {PostId}: {Message}", post.Id, message);
                return PlatformPublishResult.Failed(message);
            }

            var body = await response.Content.ReadFromJsonAsync<GraphApiPostEnvelope>(cancellationToken: cancellationToken);
            var remoteId = body?.Id ?? body?.PostId ?? string.Empty;

            if (string.IsNullOrEmpty(remoteId))
            {
                return PlatformPublishResult.Failed("Facebook accepted the request but returned no post ID.");
            }

            var pageScopedId = remoteId.Contains('_') ? remoteId.Split('_')[1] : remoteId;
            var remoteUrl = $"https://www.facebook.com/{remoteId.Replace('_', '/')}";

            return PlatformPublishResult.Succeeded(remoteId, remoteUrl);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogError(ex, "Facebook publish threw for post {PostId}", post.Id);
            return PlatformPublishResult.Failed($"Could not reach the Facebook API: {ex.Message}");
        }
    }

    private sealed class GraphApiPostEnvelope
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("post_id")]
        public string? PostId { get; set; }
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

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("code")]
        public int Code { get; set; }
    }
}
