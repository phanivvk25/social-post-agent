using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SocialPostAgent.Domain;

namespace SocialPostAgent.Application.Generation;

/// <summary>Anthropic API settings, bound from configuration ("Anthropic" section).</summary>
public sealed class AnthropicOptions
{
    public const string SectionName = "Anthropic";

    public string ApiKey { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = "https://api.anthropic.com";

    public string ApiVersion { get; set; } = "2023-06-01";

    public string Model { get; set; } = "claude-sonnet-5";

    public int MaxTokens { get; set; } = 1024;
}

public interface IContentGenerationService
{
    /// <summary>
    /// Generates one platform-tailored caption per requested platform from a single
    /// plain-language brief, in one model call.
    /// </summary>
    Task<IReadOnlyDictionary<PlatformType, string>> GenerateCaptionsAsync(
        string brief,
        IReadOnlyCollection<PlatformType> platforms,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Calls the Anthropic Messages API to turn a brief into per-platform copy. The prompt
/// asks the model to return strict JSON keyed by platform name so the response can be
/// parsed deterministically rather than scraped out of free-form prose.
/// </summary>
public sealed class ContentGenerationService : IContentGenerationService
{
    private readonly HttpClient _httpClient;
    private readonly AnthropicOptions _options;
    private readonly ILogger<ContentGenerationService> _logger;

    public ContentGenerationService(HttpClient httpClient, IOptions<AnthropicOptions> options, ILogger<ContentGenerationService> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyDictionary<PlatformType, string>> GenerateCaptionsAsync(
        string brief,
        IReadOnlyCollection<PlatformType> platforms,
        CancellationToken cancellationToken = default)
    {
        if (platforms.Count == 0)
        {
            return new Dictionary<PlatformType, string>();
        }

        var platformNames = platforms.Select(p => p.ToString()).ToArray();
        var prompt = BuildPrompt(brief, platformNames);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_options.BaseUrl}/v1/messages")
        {
            Content = JsonContent.Create(new AnthropicRequest
            {
                Model = _options.Model,
                MaxTokens = _options.MaxTokens,
                Messages = new[] { new AnthropicMessage { Role = "user", Content = prompt } }
            })
        };
        request.Headers.Add("x-api-key", _options.ApiKey);
        request.Headers.Add("anthropic-version", _options.ApiVersion);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await _httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Anthropic API returned {StatusCode}: {Body}", (int)response.StatusCode, body);
            throw new InvalidOperationException($"Content generation failed: Anthropic API returned {(int)response.StatusCode}.");
        }

        var result = await response.Content.ReadFromJsonAsync<AnthropicResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Anthropic API returned an empty response.");

        var text = result.Content.FirstOrDefault(c => c.Type == "text")?.Text
            ?? throw new InvalidOperationException("Anthropic API response contained no text block.");

        return ParseCaptions(text, platforms);
    }

    private static string BuildPrompt(string brief, IReadOnlyCollection<string> platformNames)
    {
        var platformList = string.Join(", ", platformNames);

        return $$"""
            You are writing social media copy for a single brand. Given the brief below,
            write one caption for each of these platforms: {{platformList}}.

            Brief: {{brief}}

            Rules:
            - Facebook: conversational, can be longer, plain text.
            - Instagram: punchy, hashtag-friendly, under 2200 characters.
            - LinkedIn: professional tone, under 3000 characters, no excessive hashtags.
            - Do not invent facts, prices, or dates that were not in the brief.

            Respond with ONLY a JSON object whose keys are exactly the platform names given
            above and whose values are the caption strings. No commentary, no markdown fences.
            """;
    }

    private IReadOnlyDictionary<PlatformType, string> ParseCaptions(string jsonText, IReadOnlyCollection<PlatformType> platforms)
    {
        var trimmed = jsonText.Trim().Trim('`');

        Dictionary<string, string>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(trimmed, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Could not parse generated captions as JSON: {Text}", trimmed);
            throw new InvalidOperationException("The content model did not return valid JSON captions.", ex);
        }

        if (parsed is null)
        {
            throw new InvalidOperationException("The content model returned no captions.");
        }

        var result = new Dictionary<PlatformType, string>();
        foreach (var platform in platforms)
        {
            if (parsed.TryGetValue(platform.ToString(), out var caption) && !string.IsNullOrWhiteSpace(caption))
            {
                result[platform] = caption.Trim();
            }
            else
            {
                _logger.LogWarning("Content model did not return a caption for {Platform}", platform);
            }
        }

        return result;
    }

    private sealed class AnthropicRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("max_tokens")]
        public int MaxTokens { get; set; }

        [JsonPropertyName("messages")]
        public IReadOnlyList<AnthropicMessage> Messages { get; set; } = Array.Empty<AnthropicMessage>();
    }

    private sealed class AnthropicMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;
    }

    private sealed class AnthropicResponse
    {
        [JsonPropertyName("content")]
        public IReadOnlyList<AnthropicContentBlock> Content { get; set; } = Array.Empty<AnthropicContentBlock>();
    }

    private sealed class AnthropicContentBlock
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }
}
