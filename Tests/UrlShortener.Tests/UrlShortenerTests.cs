using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using UrlShortener.Application;
using UrlShortener.Controllers;
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
        var workflow = new OrchestrationService(new SqliteAuditSink(environment.Db), new SqliteWorkflowStateStore(environment.Db), new[] { (IWorkflowStageHandler)new BuiltInWorkflowStageHandler(new FakeTestRunner(), new SourceTreeChangeApplier(environment.SourceRoot), new FakePullRequestPublisher(), TimeProvider.System) }, TimeProvider.System);

        var initial = await workflow.ExecuteAsync(new WorkflowRequest("Add analytics", "brownfield"), CancellationToken.None);
        var completed = await workflow.ExecuteAsync(new WorkflowRequest("Change flaky analytics after a change", "brownfield", WorkflowId: initial.WorkflowId), CancellationToken.None);

        Assert.Equal("completed", completed.Status);
        Assert.Equal(1, completed.Metrics.RetryCount);
        Assert.Contains(completed.Decisions, decision => decision.Contains("re-planned", StringComparison.OrdinalIgnoreCase));
        Assert.True(await environment.Db.AuditEvents.AnyAsync(item => item.Action == "retry"));
        Assert.Equal(4, await environment.Db.AuditEvents.CountAsync(item => item.Action == "worker-started" && (item.Stage == "implementation" || item.Stage == "documentation")));
        Assert.Equal("completed", (await environment.Db.Workflows.SingleAsync(item => item.WorkflowId == completed.WorkflowId)).Status);
        var persistedMetrics = await environment.Db.Workflows.SingleAsync(item => item.WorkflowId == completed.WorkflowId);
        Assert.Equal(completed.Metrics.RetryCount, persistedMetrics.RetryCount);
        Assert.True(persistedMetrics.EndToEndLatencyMs >= 0);
        Assert.Equal(completed.Stages.Count, await environment.Db.WorkflowStages.CountAsync(item => item.WorkflowId == completed.WorkflowId));
        Assert.Equal(12, await environment.Db.WorkflowGateEvidence.CountAsync(item => item.WorkflowId == completed.WorkflowId));
        Assert.True(await environment.Db.WorkflowGateEvidence.Where(item => item.WorkflowId == completed.WorkflowId).AllAsync(item => item.Passed));
        var gateEvidence = await new SqliteWorkflowStateStore(environment.Db).GetGateEvidenceAsync(completed.WorkflowId, CancellationToken.None);
        Assert.Equal(12, gateEvidence.Count);
        Assert.Contains(gateEvidence, item => item.Stage == "implementation" && item.Gate == "entry");
        Assert.Contains(gateEvidence, item => item.Stage == "release-readiness" && item.Gate == "exit");
        var persistedArtifacts = await environment.Db.WorkflowArtifacts.Where(item => item.WorkflowId == completed.WorkflowId).ToListAsync();
        Assert.Equal(6, persistedArtifacts.Count);
        Assert.All(persistedArtifacts, artifact =>
        {
            Assert.StartsWith("PASS:", artifact.Validation);
            Assert.StartsWith("{", artifact.Content);
        });
        Assert.Contains(completed.Artifacts, artifact => artifact.Kind == "release-evidence");
        var releaseEvidence = persistedArtifacts.Single(artifact => artifact.Stage == "release-readiness");
        Assert.Contains("https://github.com/example/pull/1", releaseEvidence.Content, StringComparison.Ordinal);
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

    [Fact]
    public async Task FailedImplementationRetriesAndCompensatesGeneratedChanges()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var workflow = new OrchestrationService(new SqliteAuditSink(environment.Db), new SqliteWorkflowStateStore(environment.Db), new[] { (IWorkflowStageHandler)new BuiltInWorkflowStageHandler(new FakeTestRunner(), new SourceTreeChangeApplier(environment.SourceRoot), new FakePullRequestPublisher(), TimeProvider.System) }, TimeProvider.System);

        var result = await workflow.ExecuteAsync(new WorkflowRequest("fail-implementation", "brownfield"), CancellationToken.None);

        Assert.Equal("rolled-back", result.Status);
        Assert.Equal(1, result.Metrics.RollbackCount);
        Assert.True(await environment.Db.AuditEvents.AnyAsync(item => item.Action == "rollback" && item.Stage == "implementation"));
        var rolledBackStage = await environment.Db.WorkflowStages.SingleAsync(item => item.WorkflowId == result.WorkflowId && item.Stage == "implementation");
        Assert.Equal(nameof(WorkflowFailureClass.ExternalSideEffect), rolledBackStage.FailureClass);
        Assert.Equal("completed: implementation change applier removed generated side effects.", rolledBackStage.CompensationStatus);
        Assert.False(Directory.Exists(Path.Combine(environment.SourceRoot, "GeneratedWorkflowChanges")));
    }

    [Fact]
    public async Task IdempotencyKeyReturnsTheExistingWorkflow()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var workflow = new OrchestrationService(new SqliteAuditSink(environment.Db), new SqliteWorkflowStateStore(environment.Db), new[] { (IWorkflowStageHandler)new BuiltInWorkflowStageHandler(new FakeTestRunner(), new SourceTreeChangeApplier(environment.SourceRoot), new FakePullRequestPublisher(), TimeProvider.System) }, TimeProvider.System);
        var first = await workflow.ExecuteAsync(new WorkflowRequest("Add analytics", "greenfield", IdempotencyKey: "request-123"), CancellationToken.None);
        var second = await workflow.ExecuteAsync(new WorkflowRequest("Different requirement", "greenfield", IdempotencyKey: "request-123"), CancellationToken.None);

        Assert.Equal(first.WorkflowId, second.WorkflowId);
        Assert.Single(await environment.Db.Workflows.ToListAsync());
    }

    [Fact]
    public async Task WorkflowLeasePreventsConcurrentExecutionAndReconcilesRecovery()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var firstStore = new SqliteWorkflowStateStore(environment.Db);
        var secondStore = new SqliteWorkflowStateStore(environment.Db);
        var firstLease = await firstStore.AcquireLeaseAsync("workflow-lock", TimeSpan.FromMinutes(1), CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => secondStore.AcquireLeaseAsync("workflow-lock", TimeSpan.FromMinutes(1), CancellationToken.None));
        await firstStore.ReleaseLeaseAsync(firstLease, CancellationToken.None);
        var recoveredLease = await secondStore.AcquireLeaseAsync("workflow-lock", TimeSpan.FromMinutes(1), CancellationToken.None);
        await secondStore.ReleaseLeaseAsync(recoveredLease, CancellationToken.None);

        await firstStore.StartAsync("recoverable", new WorkflowRequest("recover", "greenfield"), CancellationToken.None);
        await firstStore.ReconcileInterruptedAsync("recoverable", CancellationToken.None);
        Assert.Equal("recovering", (await environment.Db.Workflows.SingleAsync(item => item.WorkflowId == "recoverable")).Status);
    }

    [Fact]
    public async Task AmbiguousScenarioStopsUntilAcceptanceCriteriaAreProvided()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var workflow = new OrchestrationService(new SqliteAuditSink(environment.Db), new SqliteWorkflowStateStore(environment.Db), new[] { (IWorkflowStageHandler)new BuiltInWorkflowStageHandler(new FakeTestRunner(), new SourceTreeChangeApplier(environment.SourceRoot), new FakePullRequestPublisher(), TimeProvider.System) }, TimeProvider.System);

        var blocked = await workflow.ExecuteAsync(new WorkflowRequest("Make links fast and reliable", "ambiguous"), CancellationToken.None);
        var completed = await workflow.ExecuteAsync(new WorkflowRequest("Make links fast and reliable with acceptance criteria: p95 latency must be under 100ms", "ambiguous", WorkflowId: blocked.WorkflowId), CancellationToken.None);

        Assert.Equal("blocked", blocked.Status);
        Assert.Equal("completed", completed.Status);
    }

    [Fact]
    public async Task EmptyRequirementIsBlockedByEntryGateAndAuditIsCorrelated()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var audit = new SqliteAuditSink(environment.Db);
        var workflow = new OrchestrationService(audit, new SqliteWorkflowStateStore(environment.Db), new[] { (IWorkflowStageHandler)new BuiltInWorkflowStageHandler(new FakeTestRunner(), new SourceTreeChangeApplier(environment.SourceRoot), new FakePullRequestPublisher(), TimeProvider.System) }, TimeProvider.System);

        var result = await workflow.ExecuteAsync(new WorkflowRequest("", "greenfield", CorrelationId: "correlation-1"), CancellationToken.None);

        Assert.Equal("blocked", result.Status);
        Assert.Contains(audit.ReadAll(), item => item.CorrelationId == "correlation-1" && item.Stage == "workflow");
    }

    [Fact]
    public async Task ShortUrlControllerReturnsCreatedResponseContract()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var controller = new ShortUrlsController(environment.Service)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.HttpContext.Request.Scheme = "https";
        controller.HttpContext.Request.Host = new HostString("short.test");

        var result = await controller.Create(new CreateShortUrlRequest("https://example.com", null), CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.Equal(StatusCodes.Status201Created, created.StatusCode);
        Assert.IsType<ShortUrlResponse>(created.Value);
    }

    [Fact]
    public async Task WorkflowControllerExposesPersistedGateEvidence()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var stateStore = new SqliteWorkflowStateStore(environment.Db);
        var workflow = new OrchestrationService(new SqliteAuditSink(environment.Db), stateStore, new[] { (IWorkflowStageHandler)new BuiltInWorkflowStageHandler(new FakeTestRunner(), new SourceTreeChangeApplier(environment.SourceRoot), new FakePullRequestPublisher(), TimeProvider.System) }, TimeProvider.System);
        var completed = await workflow.ExecuteAsync(new WorkflowRequest("Run checks", "greenfield"), CancellationToken.None);
        var controller = new WorkflowController(workflow, stateStore);

        var result = await controller.Gates(completed.WorkflowId, CancellationToken.None);

        var response = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(12, Assert.IsAssignableFrom<IReadOnlyList<WorkflowGateEvidence>>(response.Value).Count);
    }

    [Fact]
    public async Task ExternalAgentHandlerReplacesArchitectureStageAndPreservesContracts()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var externalHandler = new ExternalArchitectureHandler();
        var builtIn = new BuiltInWorkflowStageHandler(new FakeTestRunner(), new SourceTreeChangeApplier(environment.SourceRoot), new FakePullRequestPublisher(), TimeProvider.System);
        var workflow = new OrchestrationService(new SqliteAuditSink(environment.Db), new SqliteWorkflowStateStore(environment.Db), new[] { (IWorkflowStageHandler)externalHandler, builtIn }, TimeProvider.System);

        var result = await workflow.ExecuteAsync(new WorkflowRequest("Create a greenfield URL shortener", "greenfield"), CancellationToken.None);

        Assert.Equal("completed", result.Status);
        Assert.True(externalHandler.Executed);
        var architecture = await environment.Db.WorkflowArtifacts.SingleAsync(item => item.WorkflowId == result.WorkflowId && item.Stage == "architecture");
        Assert.Contains("external-agent", architecture.Content, StringComparison.Ordinal);
        Assert.Equal("architecture", architecture.Kind);
        Assert.True(await environment.Db.WorkflowGateEvidence.AnyAsync(item => item.WorkflowId == result.WorkflowId && item.Stage == "architecture" && item.Gate == "exit" && item.Passed));
    }

    [Fact]
    public async Task LiveAgentToolProviderHonorsArtifactAndCompensationContracts()
    {
        var compensated = false;
        using var server = new TestServer(new WebHostBuilder().Configure(app => app.Run(async context =>
        {
            if (context.Request.Path.Value?.EndsWith("/execute", StringComparison.Ordinal) == true)
            {
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsJsonAsync(new { kind = "architecture", content = "{\"provider\":\"live-test-server\"}", validation = "PASS: provider validated architecture." });
            }
            else
            {
                compensated = true;
                await context.Response.WriteAsJsonAsync(new { status = "completed: provider compensation executed" });
            }
        })));
        using var client = server.CreateClient();
        var handler = new HttpAgentToolStageHandler(client, "architecture", "architecture");
        var context = new WorkflowGateContext(new WorkflowRequest("Use external architecture", "greenfield"), new Dictionary<string, WorkflowStageResult>(), new Dictionary<string, WorkflowArtifact>(), [], []);

        var artifact = await handler.ExecuteAsync("architecture", context, "live-provider-workflow", CancellationToken.None);
        var compensation = await handler.CompensateAsync("architecture", "live-provider-workflow", CancellationToken.None);

        Assert.Equal("architecture", artifact.Kind);
        Assert.Contains("live-test-server", artifact.Content, StringComparison.Ordinal);
        Assert.StartsWith("completed", compensation, StringComparison.Ordinal);
        Assert.True(compensated);
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

    private sealed class ExternalArchitectureHandler : IWorkflowStageHandler
    {
        public bool Executed { get; private set; }
        public string StageName => "architecture";
        public string ArtifactKind => "architecture";
        public Task<string> CompensateAsync(string stageName, string workflowId, CancellationToken cancellationToken) => Task.FromResult("external-agent-compensated");
        public Task<WorkflowArtifact> ExecuteAsync(string stageName, WorkflowGateContext context, string workflowId, CancellationToken cancellationToken)
        {
            Executed = true;
            return Task.FromResult(new WorkflowArtifact(workflowId, stageName, "architecture", "{\"source\":\"external-agent\",\"design\":\"validated\"}", "PASS: external agent architecture validated.", DateTimeOffset.UtcNow));
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

    private sealed class FakePullRequestPublisher : IWorkflowPullRequestPublisher
    {
        public Task<string> PublishAsync(string workflowId, WorkflowRequest request, IReadOnlyDictionary<string, WorkflowArtifact> artifacts, CancellationToken cancellationToken) =>
            Task.FromResult("https://github.com/example/pull/1");
    }
}
