using UrlShortener.Application;
using UrlShortener.Models;
using UrlShortener.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Reflection;
using System.Runtime.InteropServices;

NativeLibrary.SetDllImportResolver(
    typeof(SQLitePCL.SQLite3Provider_sqlite3).Assembly,
    static (libraryName, _, _) => libraryName == "sqlite3" ? NativeLibrary.Load("libsqlite3.so.0") : IntPtr.Zero);
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
builder.Services.AddScoped<IWorkflowTestRunner, DotnetTestRunner>();
builder.Services.AddScoped<IWorkflowChangeApplier>(_ => new SourceTreeChangeApplier(builder.Configuration["Workflow:SourceRoot"] ?? Directory.GetCurrentDirectory()));
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
