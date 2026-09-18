using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using UrlShortener.Application;
using UrlShortener.Persistence;
using Xunit;

namespace UrlShortener.Tests;

public sealed class UrlShortenerTests
{
    [Fact]
    public async Task CreateRejectsNonHttpDestination()
    {
        await using var environment = await TestEnvironment.CreateAsync();

        var exception = await Assert.ThrowsAsync<UrlShortener.Models.UrlShortenerException>(() =>
            environment.Service.CreateAsync(new CreateShortUrlCommand("javascript:alert(1)", null), "https://short.test", CancellationToken.None));

        Assert.Contains("HTTP or HTTPS", exception.Message);
    }

    [Fact]
    public async Task ResolveIncrementsAnalyticsInSqlite()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var created = await environment.Service.CreateAsync(new CreateShortUrlCommand("https://example.com", null), "https://short.test", CancellationToken.None);

        var mapping = await environment.Service.ResolveAsync(created.Code, "https://referrer.test", "test-agent", "127.0.0.1", CancellationToken.None);
        var analytics = await environment.Service.GetAnalyticsAsync(created.Code, CancellationToken.None);

        Assert.NotNull(mapping);
        Assert.Equal(1, analytics!.Clicks);
        Assert.NotNull(analytics.LastClickedAt);
        Assert.Equal(1, await environment.Db.ClickEvents.CountAsync());
    }

    [Fact]
    public async Task ExpiredUrlDoesNotResolve()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        await using var environment = await TestEnvironment.CreateAsync(clock);
        var created = await environment.Service.CreateAsync(new CreateShortUrlCommand("https://example.com", clock.GetUtcNow().AddMinutes(1)), "https://short.test", CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(2));

        Assert.Null(await environment.Service.ResolveAsync(created.Code, null, null, null, CancellationToken.None));
    }

    [Fact]
    public async Task WorkflowExecutesAndPersistsCheckpoints()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var workflow = new OrchestrationService(new SqliteAuditSink(environment.Db), new SqliteWorkflowStateStore(environment.Db), new[] { (IWorkflowStageHandler)new BuiltInWorkflowStageHandler(new FakeTestRunner(), new SourceTreeChangeApplier(environment.SourceRoot), TimeProvider.System) }, TimeProvider.System);

        var completed = await workflow.ExecuteAsync(new WorkflowRequest("Add flaky tests after a change", "brownfield"), CancellationToken.None);

        Assert.Equal("completed", completed.Status);
        Assert.Equal(1, completed.Metrics.RetryCount);
        Assert.Contains(completed.Decisions, decision => decision.Contains("re-planned", StringComparison.OrdinalIgnoreCase));
        Assert.True(await environment.Db.AuditEvents.AnyAsync(item => item.Action == "retry"));
        Assert.Equal(2, await environment.Db.AuditEvents.CountAsync(item => item.Action == "worker-started" && (item.Stage == "implementation" || item.Stage == "documentation")));
        Assert.Equal("completed", (await environment.Db.Workflows.SingleAsync(item => item.WorkflowId == completed.WorkflowId)).Status);
        Assert.Equal(completed.Stages.Count, await environment.Db.WorkflowStages.CountAsync(item => item.WorkflowId == completed.WorkflowId));
        var persistedArtifacts = await environment.Db.WorkflowArtifacts.Where(item => item.WorkflowId == completed.WorkflowId).ToListAsync();
        Assert.Equal(6, persistedArtifacts.Count);
        Assert.All(persistedArtifacts, artifact =>
        {
            Assert.StartsWith("PASS:", artifact.Validation);
            Assert.StartsWith("{", artifact.Content);
        });
        Assert.Contains(completed.Artifacts, artifact => artifact.Kind == "release-evidence");
        var testExecution = persistedArtifacts.Single(artifact => artifact.Stage == "tests");
        Assert.Equal("test-execution", testExecution.Kind);
        Assert.Contains("\"Passed\":true", testExecution.Content, StringComparison.OrdinalIgnoreCase);
        var implementation = persistedArtifacts.Single(artifact => artifact.Stage == "implementation");
        using var implementationDocument = JsonDocument.Parse(implementation.Content);
        Assert.Equal(2, implementationDocument.RootElement.GetProperty("files").GetArrayLength());
        Assert.All(implementationDocument.RootElement.GetProperty("files").EnumerateArray(), file =>
        {
            Assert.False(string.IsNullOrWhiteSpace(file.GetProperty("path").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(file.GetProperty("operation").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(file.GetProperty("content").GetString()));
        });
        var appliedPath = implementationDocument.RootElement.GetProperty("appliedPaths")[0].GetString()!;
        Assert.True(File.Exists(Path.Combine(environment.SourceRoot, appliedPath.Replace('/', Path.DirectorySeparatorChar))));
    }

    private sealed class TestEnvironment : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly ServiceProvider cacheProvider;
        private readonly string sourceRoot;
        public UrlShortenerDbContext Db { get; }
        public UrlShortenerService Service { get; }
        public string SourceRoot => sourceRoot;

        private TestEnvironment(SqliteConnection connection, UrlShortenerDbContext db, ServiceProvider cacheProvider, string sourceRoot, TimeProvider timeProvider)
        {
            this.connection = connection;
            Db = db;
            this.cacheProvider = cacheProvider;
            this.sourceRoot = sourceRoot;
            Service = new UrlShortenerService(
                new SqliteUrlMappingRepository(db, cacheProvider.GetRequiredService<HybridCache>()),
                new SqliteClickEventRepository(db),
                new DeterministicShortCodeGenerator(),
                timeProvider);
        }

        public static async Task<TestEnvironment> CreateAsync(TimeProvider? timeProvider = null)
        {
            if (!resolverConfigured)
            {
                NativeLibrary.SetDllImportResolver(
                    typeof(SQLitePCL.SQLite3Provider_sqlite3).Assembly,
                    static (libraryName, _, _) => libraryName == "sqlite3" ? NativeLibrary.Load("libsqlite3.so.0") : IntPtr.Zero);
                resolverConfigured = true;
            }
            SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
            var connection = new SqliteConnection("Data Source=:memory:");
            var sourceRoot = Directory.CreateTempSubdirectory("url-shortener-workflow-").FullName;
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<UrlShortenerDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new UrlShortenerDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var services = new ServiceCollection();
            services.AddHybridCache();
            var cacheProvider = services.BuildServiceProvider();
            return new TestEnvironment(connection, db, cacheProvider, sourceRoot, timeProvider ?? TimeProvider.System);
        }

        private static bool resolverConfigured;

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await cacheProvider.DisposeAsync();
            await connection.DisposeAsync();
            Directory.Delete(sourceRoot, true);
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset current) : TimeProvider
    {
        private DateTimeOffset current = current;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan duration) => current += duration;
    }

    private sealed class FakeTestRunner : IWorkflowTestRunner
    {
        public Task<TestExecutionEvidence> RunAsync(CancellationToken cancellationToken) => Task.FromResult(
            new TestExecutionEvidence("dotnet test Tests/UrlShortener.Tests/UrlShortener.Tests.csproj", 0, true, 12, "Test summary: total: 4, failed: 0, succeeded: 4", DateTimeOffset.UtcNow));
    }
}
