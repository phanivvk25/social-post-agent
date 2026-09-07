using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using SocialPostAgent.Application.Generation;
using SocialPostAgent.Application.Guardrails;
using SocialPostAgent.Application.Orchestration;
using SocialPostAgent.Application.Platforms;

namespace SocialPostAgent.Application;

/// <summary>
/// Composition root for the Application layer: guardrails, generation services, platform
/// publishers, the Semantic Kernel plugin/orchestrator, and every external HTTP client
/// they need. <see cref="IPostScheduler"/> is deliberately not registered here — its
/// concrete (Hangfire) implementation is a technology choice that belongs to
/// Infrastructure's <c>AddInfrastructure</c>.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<GuardrailOptions>(configuration.GetSection(GuardrailOptions.SectionName));
        services.Configure<AnthropicOptions>(configuration.GetSection(AnthropicOptions.SectionName));
        services.Configure<ImageGenerationOptions>(configuration.GetSection(ImageGenerationOptions.SectionName));
        services.Configure<MetaGraphApiOptions>(configuration.GetSection(MetaGraphApiOptions.SectionName));
        services.Configure<LinkedInApiOptions>(configuration.GetSection(LinkedInApiOptions.SectionName));

        AddGuardrails(services);
        AddGenerationServices(services);
        AddPlatformPublishers(services);
        AddOrchestration(services);

        return services;
    }

    private static void AddGuardrails(IServiceCollection services)
    {
        // Format / mechanical
        services.AddScoped<IGuardrail, CaptionLengthGuardrail>();
        services.AddScoped<IGuardrail, RequiredMediaGuardrail>();
        services.AddScoped<IGuardrail, HashtagCountGuardrail>();

        // Brand-safety / duplicate content
        services.AddScoped<IGuardrail, BannedWordsGuardrail>();
        services.AddScoped<IGuardrail, SensitiveTopicsGuardrail>();
        services.AddScoped<IGuardrail, DuplicateContentGuardrail>();

        // Operational
        services.AddScoped<IGuardrail, DailyPostCapGuardrail>();
        services.AddScoped<IGuardrail, CircuitBreakerGuardrail>();
        services.AddScoped<IGuardrail, KillSwitchGuardrail>();

        services.AddScoped<GuardrailEngine>();
    }

    private static void AddGenerationServices(IServiceCollection services)
    {
        services.AddHttpClient<IContentGenerationService, ContentGenerationService>();
        services.AddHttpClient<IGraphicsGenerationService, GraphicsGenerationService>();
    }

    private static void AddPlatformPublishers(IServiceCollection services)
    {
        services.AddHttpClient<IPlatformPublisher, FacebookPublisher>();
        services.AddHttpClient<IPlatformPublisher, InstagramPublisher>();
        services.AddHttpClient<IPlatformPublisher, LinkedInPublisher>();
    }

    private static void AddOrchestration(IServiceCollection services)
    {
        // AddKernel() registers a transient Kernel and lets us attach plugins whose own
        // dependencies (IEnumerable<IPlatformPublisher>) are resolved from the same
        // container, so SocialPublishingPlugin needs no manual wiring.
        services.AddKernel()
            .Plugins.AddFromType<SocialPublishingPlugin>("SocialPublishing");

        services.AddScoped<IPostOrchestrator, PostOrchestrator>();
    }
}
