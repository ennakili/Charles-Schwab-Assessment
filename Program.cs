using UrlShortener.Application;
using UrlShortener.Models;
using UrlShortener.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.RateLimiting;
using StackExchange.Redis;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

try
{
    NativeLibrary.SetDllImportResolver(
        typeof(SQLitePCL.SQLite3Provider_sqlite3).Assembly,
        static (libraryName, _, _) => libraryName == "sqlite3" ? NativeLibrary.Load("libsqlite3.so.0") : IntPtr.Zero);
}
catch (InvalidOperationException)
{
    // The resolver is process-wide and may already be configured by an integration-test host.
}
SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddDbContext<UrlShortenerDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("UrlShortener")));
builder.Services.AddHybridCache(options =>
{
    options.MaximumPayloadBytes = 1024 * 1024;
    options.MaximumKeyLength = 256;
});
builder.Services.AddScoped<IUrlMappingRepository, SqliteUrlMappingRepository>();
builder.Services.AddScoped<IClickEventRepository, SqliteClickEventRepository>();
builder.Services.AddSingleton<IShortCodeGenerator, RandomShortCodeGenerator>();
builder.Services.AddSingleton<IDestinationAbusePolicy>(
    _ => new DefaultDestinationAbusePolicy(builder.Configuration.GetSection("Abuse:BlockedHosts").Get<string[]>()));
builder.Services.AddSingleton<UrlShortenerMetrics>();
builder.Services.AddScoped<IAuditSink, SqliteAuditSink>();
builder.Services.AddScoped<IWorkflowStateStore, SqliteWorkflowStateStore>();
var redisConnection = builder.Configuration["Workflow:RedisConnection"];
if (string.IsNullOrWhiteSpace(redisConnection))
    builder.Services.AddScoped<IWorkflowExecutionLock, SqliteWorkflowExecutionLock>();
else
    builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisConnection));
if (!string.IsNullOrWhiteSpace(redisConnection))
    builder.Services.AddScoped<IWorkflowExecutionLock, RedisWorkflowExecutionLock>();
builder.Services.AddScoped<IWorkflowTestRunner, DotnetTestRunner>();
builder.Services.AddScoped<IWorkflowImpactAnalyzer, SemanticWorkflowImpactAnalyzer>();
builder.Services.AddScoped<IWorkflowChangeApplier>(_ => new GitWorktreeChangeApplier(builder.Configuration["Workflow:SourceRoot"] ?? Directory.GetCurrentDirectory()));
builder.Services.AddHttpClient("github", client => client.BaseAddress = new Uri("https://api.github.com/"));
builder.Services.AddScoped<IWorkflowPullRequestPublisher, GitHubPullRequestPublisher>();
builder.Services.AddScoped<IWorkflowStageHandler, BuiltInWorkflowStageHandler>();
builder.Services.AddScoped<UrlShortenerService>();
builder.Services.AddScoped<OrchestrationService>();

builder.Services.AddAuthentication(ApiKeyAuthenticationOptions.SchemeName)
    .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationOptions.SchemeName, _ => { });
builder.Services.AddAuthorization(options =>
    options.AddPolicy(ApiKeyAuthenticationOptions.PolicyName, policy => policy.RequireAuthenticatedUser()));

builder.Services.AddRateLimiter(options =>
{
    options.OnRejected = (context, cancellationToken) =>
    {
        context.HttpContext.RequestServices.GetRequiredService<UrlShortenerMetrics>().RecordRateLimited(context.HttpContext.GetEndpoint()?.DisplayName ?? "unknown");
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        return ValueTask.CompletedTask;
    };

    var createLimits = builder.Configuration.GetSection("RateLimiting:ShortUrlCreate");
    options.AddPolicy("short-url-create", httpContext => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = createLimits.GetValue("PermitLimit", 10),
            Window = TimeSpan.FromSeconds(createLimits.GetValue("WindowSeconds", 60)),
            QueueLimit = 0
        }));

    var redirectLimits = builder.Configuration.GetSection("RateLimiting:Redirect");
    options.AddPolicy("redirect", httpContext => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = redirectLimits.GetValue("PermitLimit", 120),
            Window = TimeSpan.FromSeconds(redirectLimits.GetValue("WindowSeconds", 60)),
            QueueLimit = 0
        }));
});

var otlpEndpoint = builder.Configuration["Telemetry:OtlpEndpoint"];
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("UrlShortener", serviceVersion: "1.0.0"))
    .WithTracing(tracing =>
    {
        tracing.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation();
        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
            tracing.AddOtlpExporter(otlp => otlp.Endpoint = new Uri(otlpEndpoint));
    })
    .WithMetrics(metrics =>
    {
        metrics.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddMeter(UrlShortenerMetrics.MeterName);
        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
            metrics.AddOtlpExporter(otlp => otlp.Endpoint = new Uri(otlpEndpoint));
    });

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<UrlShortenerDbContext>().Database.EnsureCreatedAsync();
}

app.UseExceptionHandler(exceptionApp => exceptionApp.Run(async context =>
{
    var exception = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    var isValidationError = exception is UrlShortenerException;
    if (!isValidationError && exception is not null)
        context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("UnhandledException")
            .LogError(exception, "Unhandled exception processing {Method} {Path} (trace {TraceId})", context.Request.Method, context.Request.Path, context.TraceIdentifier);

    context.Response.ContentType = "application/problem+json";
    context.Response.StatusCode = isValidationError ? StatusCodes.Status400BadRequest : StatusCodes.Status500InternalServerError;
    // Unexpected-error details are never returned to clients outside Development to avoid leaking internals; the trace ID correlates with server logs.
    var detail = isValidationError || app.Environment.IsDevelopment()
        ? exception?.Message
        : $"An unexpected error occurred. Reference trace ID {context.TraceIdentifier} in server logs for details.";
    await Results.Problem(
        statusCode: context.Response.StatusCode,
        title: isValidationError ? "Request validation failed" : "Unexpected server error",
        detail: detail,
        extensions: new Dictionary<string, object?> { ["traceId"] = context.TraceIdentifier }).ExecuteAsync(context);
}));

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();

public partial class Program;
