using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SocialPostAgent.Application.Generation;

/// <summary>Image-generation API settings, bound from configuration ("ImageGeneration" section).</summary>
public sealed class ImageGenerationOptions
{
    public const string SectionName = "ImageGeneration";

    public string ApiKey { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = "https://api.openai.com";

    public string Model { get; set; } = "gpt-image-1";

    public string Size { get; set; } = "1024x1024";

    /// <summary>Local folder generated images are written to, served as static files by the API host.</summary>
    public string OutputDirectory { get; set; } = "wwwroot/generated-images";

    /// <summary>
    /// Public base URL the <see cref="OutputDirectory"/> is reachable at. Instagram's publish
    /// API requires a publicly resolvable image URL, so this must point at a real public
    /// address (an ngrok/cloudflared tunnel locally, object storage in production) — see README.
    /// </summary>
    public string PublicBaseUrl { get; set; } = "https://localhost:5001/generated-images";
}

public sealed record GeneratedImage(string LocalPath, string PublicUrl);

public interface IGraphicsGenerationService
{
    Task<GeneratedImage> GenerateImageAsync(string prompt, CancellationToken cancellationToken = default);
}

/// <summary>
/// Calls an image-generation API (OpenAI's Images API by default) and saves the result
/// to local disk under <see cref="ImageGenerationOptions.OutputDirectory"/>. Brand
/// post-processing (logo overlay, platform aspect-ratio cropping) is intentionally left
/// as a documented extension point here rather than implemented against a made-up brand
/// style guide — wire in an image library (e.g. ImageSharp) once real brand assets exist.
/// </summary>
public sealed class GraphicsGenerationService : IGraphicsGenerationService
{
    private readonly HttpClient _httpClient;
    private readonly ImageGenerationOptions _options;
    private readonly ILogger<GraphicsGenerationService> _logger;

    public GraphicsGenerationService(HttpClient httpClient, IOptions<ImageGenerationOptions> options, ILogger<GraphicsGenerationService> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<GeneratedImage> GenerateImageAsync(string prompt, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_options.BaseUrl}/v1/images/generations")
        {
            Content = JsonContent.Create(new ImageGenerationRequest
            {
                Model = _options.Model,
                Prompt = prompt,
                Size = _options.Size,
                ResponseFormat = "b64_json"
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

        var response = await _httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Image generation API returned {StatusCode}: {Body}", (int)response.StatusCode, body);
            throw new InvalidOperationException($"Image generation failed: API returned {(int)response.StatusCode}.");
        }

        var result = await response.Content.ReadFromJsonAsync<ImageGenerationResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Image generation API returned an empty response.");

        var base64 = result.Data.FirstOrDefault()?.Base64Json
            ?? throw new InvalidOperationException("Image generation API response contained no image data.");

        return await SaveImageAsync(base64, cancellationToken);
    }

    private async Task<GeneratedImage> SaveImageAsync(string base64, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_options.OutputDirectory);

        var fileName = $"{Guid.NewGuid():N}.png";
        var localPath = Path.Combine(_options.OutputDirectory, fileName);

        var bytes = Convert.FromBase64String(base64);
        await File.WriteAllBytesAsync(localPath, bytes, cancellationToken);

        var publicUrl = $"{_options.PublicBaseUrl.TrimEnd('/')}/{fileName}";

        _logger.LogInformation("Generated image saved to {LocalPath}, publicly reachable at {PublicUrl}", localPath, publicUrl);

        return new GeneratedImage(localPath, publicUrl);
    }

    private sealed class ImageGenerationRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("prompt")]
        public string Prompt { get; set; } = string.Empty;

        [JsonPropertyName("size")]
        public string Size { get; set; } = string.Empty;

        [JsonPropertyName("response_format")]
        public string ResponseFormat { get; set; } = "b64_json";
    }

    private sealed class ImageGenerationResponse
    {
        [JsonPropertyName("data")]
        public IReadOnlyList<ImageGenerationData> Data { get; set; } = Array.Empty<ImageGenerationData>();
    }

    private sealed class ImageGenerationData
    {
        [JsonPropertyName("b64_json")]
        public string? Base64Json { get; set; }
    }
}
