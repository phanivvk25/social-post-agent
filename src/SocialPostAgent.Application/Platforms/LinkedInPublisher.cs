using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SocialPostAgent.Application.Security;
using SocialPostAgent.Domain;
using SocialPostAgent.Domain.Entities;

namespace SocialPostAgent.Application.Platforms;

/// <summary>LinkedIn REST API settings, bound from configuration ("LinkedInApi" section).</summary>
public sealed class LinkedInApiOptions
{
    public const string SectionName = "LinkedInApi";

    public string BaseUrl { get; set; } = "https://api.linkedin.com";

    /// <summary>Sent as the required "LinkedIn-Version" header, e.g. "202405".</summary>
    public string ApiVersion { get; set; } = "202405";
}

/// <summary>
/// Publishes to a LinkedIn Company Page via the versioned LinkedIn REST "Posts" API.
/// Requires the organization to have granted the app's Community Management API
/// product access and the <c>w_organization_social</c> scope — see README for the
/// (slow) app-review process this depends on.
/// </summary>
public sealed class LinkedInPublisher : IPlatformPublisher
{
    private readonly HttpClient _httpClient;
    private readonly ICredentialProtector _credentialProtector;
    private readonly LinkedInApiOptions _options;
    private readonly ILogger<LinkedInPublisher> _logger;

    public LinkedInPublisher(
        HttpClient httpClient,
        ICredentialProtector credentialProtector,
        IOptions<LinkedInApiOptions> options,
        ILogger<LinkedInPublisher> logger)
    {
        _httpClient = httpClient;
        _credentialProtector = credentialProtector;
        _options = options.Value;
        _logger = logger;
    }

    public PlatformType Platform => PlatformType.LinkedIn;

    public async Task<PlatformPublishResult> PublishAsync(
        Post post,
        PlatformCredential credential,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(credential.AccountId))
        {
            return PlatformPublishResult.Failed("No LinkedIn organization URN is configured for this credential.");
        }

        var accessToken = _credentialProtector.Unprotect(credential.EncryptedAccessToken);
        var organizationUrn = credential.AccountId.StartsWith("urn:li:organization:", StringComparison.Ordinal)
            ? credential.AccountId
            : $"urn:li:organization:{credential.AccountId}";

        var requestBody = new LinkedInPostRequest
        {
            Author = organizationUrn,
            Commentary = post.LinkedInText ?? string.Empty,
            Visibility = "PUBLIC",
            Distribution = new LinkedInDistribution { FeedDistribution = "MAIN_FEED" },
            LifecycleState = "PUBLISHED"
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_options.BaseUrl}/rest/posts")
        {
            Content = JsonContent.Create(requestBody)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("LinkedIn-Version", _options.ApiVersion);
        request.Headers.Add("X-Restli-Protocol-Version", "2.0.0");

        try
        {
            var response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("LinkedIn publish failed for post {PostId} with status {StatusCode}: {Body}",
                    post.Id, (int)response.StatusCode, error);
                return PlatformPublishResult.Failed($"LinkedIn API returned {(int)response.StatusCode}: {Truncate(error, 300)}");
            }

            // A successful LinkedIn Posts API call returns the new post's URN in the
            // "x-restli-id" response header, not in the (usually empty) response body.
            var postUrn = response.Headers.TryGetValues("x-restli-id", out var values)
                ? values.FirstOrDefault()
                : null;

            if (string.IsNullOrEmpty(postUrn))
            {
                return PlatformPublishResult.Failed("LinkedIn accepted the post but returned no post identifier.");
            }

            return PlatformPublishResult.Succeeded(postUrn, remoteUrl: null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogError(ex, "LinkedIn publish threw for post {PostId}", post.Id);
            return PlatformPublishResult.Failed($"Could not reach the LinkedIn API: {ex.Message}");
        }
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "...";

    private sealed class LinkedInPostRequest
    {
        [JsonPropertyName("author")]
        public string Author { get; set; } = string.Empty;

        [JsonPropertyName("commentary")]
        public string Commentary { get; set; } = string.Empty;

        [JsonPropertyName("visibility")]
        public string Visibility { get; set; } = "PUBLIC";

        [JsonPropertyName("distribution")]
        public LinkedInDistribution Distribution { get; set; } = new();

        [JsonPropertyName("lifecycleState")]
        public string LifecycleState { get; set; } = "PUBLISHED";

        [JsonPropertyName("isReshareDisabledByAuthor")]
        public bool IsReshareDisabledByAuthor { get; set; } = false;
    }

    private sealed class LinkedInDistribution
    {
        [JsonPropertyName("feedDistribution")]
        public string FeedDistribution { get; set; } = "MAIN_FEED";
    }
}
