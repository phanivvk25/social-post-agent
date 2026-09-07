using Hangfire;
using Hangfire.PostgreSql;
using Serilog;
using SocialPostAgent.Application;
using SocialPostAgent.Infrastructure;
using SocialPostAgent.Infrastructure.Persistence;

Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext());

    var connectionString = builder.Configuration.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured.");

    // Domain + Infrastructure + Application composition — see each layer's own
    // DependencyInjection.cs for what it registers and why.
    builder.Services.AddApplication(builder.Configuration);
    builder.Services.AddInfrastructure(builder.Configuration);

    // Hangfire's client/server/dashboard registration is a host concern, so it lives here
    // rather than in Infrastructure's AddInfrastructure — only the IPostScheduler
    // abstraction PostOrchestrator depends on is registered there.
    builder.Services.AddHangfire(config => config
        .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings()
        .UsePostgreSqlStorage(options => options.UseNpgsqlConnection(connectionString)));
    builder.Services.AddHangfireServer();

    builder.Services.AddControllers();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(options =>
    {
        options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
        {
            Title = "Social Post Agent API",
            Version = "v1",
            Description = "Compose, review and publish social posts to Facebook, Instagram and LinkedIn."
        });
    });

    builder.Services.AddCors(options =>
    {
        options.AddPolicy("Dashboard", policy => policy
            .WithOrigins(builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>())
            .AllowAnyHeader()
            .AllowAnyMethod());
    });

    var app = builder.Build();

    // This project deliberately skips EF Core Migrations while the schema is still
    // moving during local development — see Infrastructure.DependencyInjection for the
    // tradeoffs and when to switch to a real migrations workflow.
    await app.Services.EnsureDatabaseCreatedAsync();

    // Dev convenience: encrypts and upserts any platform token supplied via
    // user-secrets/appsettings ("PlatformCredentials" section) on every startup.
    // See PlatformCredentialSeeder for why this isn't how production would do it.
    await app.Services.SeedPlatformCredentialsFromConfigurationAsync(app.Configuration);

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI(options => options.SwaggerEndpoint("/swagger/v1/swagger.json", "Social Post Agent API v1"));
    }

    app.UseSerilogRequestLogging();
    app.UseHttpsRedirection();
    app.UseStaticFiles(); // serves wwwroot/generated-images
    app.UseCors("Dashboard");
    app.UseAuthorization();

    app.UseHangfireDashboard("/hangfire");

    app.MapControllers();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Social Post Agent API terminated unexpectedly during startup");
}
finally
{
    Log.CloseAndFlush();
}
