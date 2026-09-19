using UrlShortener.Application;
using UrlShortener.Models;
using UrlShortener.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Reflection;
using System.Runtime.InteropServices;
using StackExchange.Redis;

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
builder.Services.AddSingleton<IShortCodeGenerator, DeterministicShortCodeGenerator>();
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
    context.Response.ContentType = "application/problem+json";
    context.Response.StatusCode = exception is UrlShortenerException ? StatusCodes.Status400BadRequest : StatusCodes.Status500InternalServerError;
    await Results.Problem(
        statusCode: context.Response.StatusCode,
        title: exception is UrlShortenerException ? "Request validation failed" : "Unexpected server error",
        detail: exception?.Message).ExecuteAsync(context);
}));

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();

public partial class Program;
