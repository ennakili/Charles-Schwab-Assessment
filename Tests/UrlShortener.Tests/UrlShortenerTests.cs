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

    [Theory]
    [InlineData("http://127.0.0.1/admin")]
    [InlineData("http://localhost/admin")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://10.0.0.5/internal")]
    [InlineData("http://192.168.1.5/internal")]
    public async Task CreateRejectsPrivateAndLinkLocalDestinations(string destination)
    {
        await using var environment = await TestEnvironment.CreateAsync();

        var exception = await Assert.ThrowsAsync<UrlShortener.Models.UrlShortenerException>(() =>
            environment.Service.CreateAsync(new CreateShortUrlCommand(destination, null), "https://short.test", CancellationToken.None));

        Assert.Contains("host", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DestinationAbusePolicyRejectsConfiguredBlockedHost()
    {
        var policy = new DefaultDestinationAbusePolicy(new[] { "internal.example.com" });

        Assert.Throws<UrlShortener.Models.UrlShortenerException>(() => policy.Validate(new Uri("https://internal.example.com/service")));
        Assert.Throws<UrlShortener.Models.UrlShortenerException>(() => policy.Validate(new Uri("https://reports.internal.example.com/service")));
        policy.Validate(new Uri("https://public.example.com/service"));
    }

    [Fact]
    public void RandomShortCodeGeneratorProducesWellFormedUniqueCodes()
    {
        var generator = new RandomShortCodeGenerator();

        var codes = Enumerable.Range(0, 100).Select(_ => generator.Generate("https://example.com")).ToList();

        Assert.All(codes, code => Assert.Equal(8, code.Length));
        Assert.All(codes, code => Assert.Matches("^[0-9a-zA-Z]{8}$", code));
        Assert.Equal(codes.Count, codes.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task CreateRetriesOnShortCodeCollisionAndSucceeds()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var service = environment.CreateService(new SequenceShortCodeGenerator("duplicate-code", "duplicate-code", "unique-code"));
        await service.CreateAsync(new CreateShortUrlCommand("https://example.com/first", null), "https://short.test", CancellationToken.None);

        var result = await service.CreateAsync(new CreateShortUrlCommand("https://example.com/second", null), "https://short.test", CancellationToken.None);

        Assert.Equal("unique-code", result.Code);
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
        var stateStore = new SqliteWorkflowStateStore(environment.Db);
        var workflow = new OrchestrationService(new SqliteAuditSink(environment.Db), stateStore, new[] { (IWorkflowStageHandler)new BuiltInWorkflowStageHandler(new FakeTestRunner(), new SourceTreeChangeApplier(environment.SourceRoot), new FakePullRequestPublisher(), TimeProvider.System) }, TimeProvider.System);
        var workflowId = Guid.NewGuid().ToString("N");
        await stateStore.SaveApprovalDecisionAsync(workflowId, "release-readiness", "approved", "reviewer@example.com", "Pre-approved for test.", DateTimeOffset.UtcNow, CancellationToken.None);

        var initial = await workflow.ExecuteAsync(new WorkflowRequest("Add analytics", "brownfield", WorkflowId: workflowId), CancellationToken.None);
        var awaitingApproval = await workflow.ExecuteAsync(new WorkflowRequest("Change flaky analytics after a change", "brownfield", WorkflowId: initial.WorkflowId), CancellationToken.None);
        Assert.Equal("awaiting-approval", awaitingApproval.Status);
        Assert.Contains(awaitingApproval.Decisions, decision => decision.Contains("re-planned", StringComparison.OrdinalIgnoreCase));
        await stateStore.SaveApprovalDecisionAsync(awaitingApproval.WorkflowId, "release-readiness", "approved", "reviewer@example.com", "Re-approved after re-plan.", DateTimeOffset.UtcNow, CancellationToken.None);
        var completed = await workflow.ExecuteAsync(new WorkflowRequest("Change flaky analytics after a change", "brownfield", WorkflowId: initial.WorkflowId), CancellationToken.None);

        Assert.Equal("completed", completed.Status);
        Assert.Equal(1, completed.Metrics.RetryCount);
        Assert.True(await environment.Db.AuditEvents.AnyAsync(item => item.Action == "retry"));
        Assert.Equal(4, await environment.Db.AuditEvents.CountAsync(item => item.Action == "worker-started" && (item.Stage == "implementation" || item.Stage == "documentation")));
        Assert.Equal("completed", (await environment.Db.Workflows.SingleAsync(item => item.WorkflowId == completed.WorkflowId)).Status);
        var persistedMetrics = await environment.Db.Workflows.SingleAsync(item => item.WorkflowId == completed.WorkflowId);
        Assert.Equal(completed.Metrics.RetryCount, persistedMetrics.RetryCount);
        Assert.True(persistedMetrics.EndToEndLatencyMs >= 0);
        Assert.Equal(completed.Stages.Count, await environment.Db.WorkflowStages.CountAsync(item => item.WorkflowId == completed.WorkflowId));
        Assert.Equal(13, await environment.Db.WorkflowGateEvidence.CountAsync(item => item.WorkflowId == completed.WorkflowId));
        Assert.True(await environment.Db.WorkflowGateEvidence.Where(item => item.WorkflowId == completed.WorkflowId).AllAsync(item => item.Passed));
        var gateEvidence = await stateStore.GetGateEvidenceAsync(completed.WorkflowId, CancellationToken.None);
        Assert.Equal(13, gateEvidence.Count);
        Assert.Contains(gateEvidence, item => item.Stage == "implementation" && item.Gate == "entry");
        Assert.Contains(gateEvidence, item => item.Stage == "release-readiness" && item.Gate == "exit");
        Assert.Contains(gateEvidence, item => item.Stage == "release-readiness" && item.Gate == "human-approval" && item.Passed);
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
        var stateStore = new SqliteWorkflowStateStore(environment.Db);
        var workflow = new OrchestrationService(new SqliteAuditSink(environment.Db), stateStore, new[] { (IWorkflowStageHandler)new BuiltInWorkflowStageHandler(new FakeTestRunner(), new SourceTreeChangeApplier(environment.SourceRoot), new FakePullRequestPublisher(), TimeProvider.System) }, TimeProvider.System);

        var blocked = await workflow.ExecuteAsync(new WorkflowRequest("Make links fast and reliable", "ambiguous"), CancellationToken.None);
        var awaitingApproval = await workflow.ExecuteAsync(new WorkflowRequest("Make links fast and reliable with acceptance criteria: p95 latency must be under 100ms", "ambiguous", WorkflowId: blocked.WorkflowId), CancellationToken.None);
        Assert.Equal("awaiting-approval", awaitingApproval.Status);
        await stateStore.SaveApprovalDecisionAsync(blocked.WorkflowId, "release-readiness", "approved", "reviewer@example.com", "Approved after acceptance criteria clarified.", DateTimeOffset.UtcNow, CancellationToken.None);
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
        var workflowId = Guid.NewGuid().ToString("N");
        await stateStore.SaveApprovalDecisionAsync(workflowId, "release-readiness", "approved", "reviewer@example.com", "Pre-approved for test.", DateTimeOffset.UtcNow, CancellationToken.None);
        var completed = await workflow.ExecuteAsync(new WorkflowRequest("Run checks", "greenfield", WorkflowId: workflowId), CancellationToken.None);
        var controller = new WorkflowController(workflow, stateStore);

        var result = await controller.Gates(completed.WorkflowId, CancellationToken.None);

        var response = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(13, Assert.IsAssignableFrom<IReadOnlyList<WorkflowGateEvidence>>(response.Value).Count);
    }

    [Fact]
    public async Task ReleaseReadinessStopsForHumanApprovalAndResumesAfterApproval()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var audit = new SqliteAuditSink(environment.Db);
        var stateStore = new SqliteWorkflowStateStore(environment.Db);
        var workflow = new OrchestrationService(audit, stateStore, new[] { (IWorkflowStageHandler)new BuiltInWorkflowStageHandler(new FakeTestRunner(), new SourceTreeChangeApplier(environment.SourceRoot), new FakePullRequestPublisher(), TimeProvider.System) }, TimeProvider.System);
        var controller = new WorkflowController(workflow, stateStore);

        var awaiting = await workflow.ExecuteAsync(new WorkflowRequest("Add analytics", "greenfield"), CancellationToken.None);

        Assert.Equal("awaiting-approval", awaiting.Status);
        Assert.DoesNotContain(awaiting.Artifacts, artifact => artifact.Stage == "release-readiness");
        Assert.Contains(audit.ReadAll(), item => item.WorkflowId == awaiting.WorkflowId && item.Action == "awaiting-approval");
        var pendingGate = Assert.Single(await stateStore.GetGateEvidenceAsync(awaiting.WorkflowId, CancellationToken.None), item => item.Stage == "release-readiness" && item.Gate == "human-approval");
        Assert.False(pendingGate.Passed);
        Assert.Null(await stateStore.GetApprovalDecisionAsync(awaiting.WorkflowId, "release-readiness", CancellationToken.None));

        var approveResult = await controller.Approve(awaiting.WorkflowId, new WorkflowApprovalRequest("release-readiness", "approved", "reviewer@example.com", "Looks good."), CancellationToken.None);
        Assert.IsType<AcceptedResult>(approveResult);
        var storedApproval = Assert.IsType<OkObjectResult>((await controller.GetApproval(awaiting.WorkflowId, "release-readiness", CancellationToken.None)).Result).Value as WorkflowApprovalDecision;
        Assert.Equal("approved", storedApproval!.Decision);

        var completed = await workflow.ExecuteAsync(new WorkflowRequest("Add analytics", "greenfield", WorkflowId: awaiting.WorkflowId), CancellationToken.None);

        Assert.Equal("completed", completed.Status);
        Assert.Contains(completed.Artifacts, artifact => artifact.Kind == "release-evidence");
    }

    [Fact]
    public async Task RejectedApprovalStopsWorkflowPermanently()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var stateStore = new SqliteWorkflowStateStore(environment.Db);
        var workflow = new OrchestrationService(new SqliteAuditSink(environment.Db), stateStore, new[] { (IWorkflowStageHandler)new BuiltInWorkflowStageHandler(new FakeTestRunner(), new SourceTreeChangeApplier(environment.SourceRoot), new FakePullRequestPublisher(), TimeProvider.System) }, TimeProvider.System);

        var awaiting = await workflow.ExecuteAsync(new WorkflowRequest("Add analytics", "greenfield"), CancellationToken.None);
        await stateStore.SaveApprovalDecisionAsync(awaiting.WorkflowId, "release-readiness", "rejected", "reviewer@example.com", "Not ready for release.", DateTimeOffset.UtcNow, CancellationToken.None);
        var rejected = await workflow.ExecuteAsync(new WorkflowRequest("Add analytics", "greenfield", WorkflowId: awaiting.WorkflowId), CancellationToken.None);

        Assert.Equal("rejected", rejected.Status);
        Assert.Equal("rejected", (await environment.Db.Workflows.SingleAsync(item => item.WorkflowId == awaiting.WorkflowId)).Status);
    }

    [Fact]
    public async Task ExternalAgentHandlerReplacesArchitectureStageAndPreservesContracts()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var externalHandler = new ExternalArchitectureHandler();
        var builtIn = new BuiltInWorkflowStageHandler(new FakeTestRunner(), new SourceTreeChangeApplier(environment.SourceRoot), new FakePullRequestPublisher(), TimeProvider.System);
        var stateStore = new SqliteWorkflowStateStore(environment.Db);
        var workflow = new OrchestrationService(new SqliteAuditSink(environment.Db), stateStore, new[] { (IWorkflowStageHandler)externalHandler, builtIn }, TimeProvider.System);
        var workflowId = Guid.NewGuid().ToString("N");
        await stateStore.SaveApprovalDecisionAsync(workflowId, "release-readiness", "approved", "reviewer@example.com", "Pre-approved for test.", DateTimeOffset.UtcNow, CancellationToken.None);

        var result = await workflow.ExecuteAsync(new WorkflowRequest("Create a greenfield URL shortener", "greenfield", WorkflowId: workflowId), CancellationToken.None);

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
        private readonly TimeProvider timeProvider;
        public UrlShortenerDbContext Db { get; }
        public UrlShortenerService Service { get; }
        public string SourceRoot => sourceRoot;

        private TestEnvironment(SqliteConnection connection, UrlShortenerDbContext db, ServiceProvider cacheProvider, string sourceRoot, TimeProvider timeProvider)
        {
            this.connection = connection;
            Db = db;
            this.cacheProvider = cacheProvider;
            this.sourceRoot = sourceRoot;
            this.timeProvider = timeProvider;
            Service = CreateService(new DeterministicShortCodeGenerator());
        }

        public UrlShortenerService CreateService(IShortCodeGenerator generator) =>
            new(new SqliteUrlMappingRepository(Db, cacheProvider.GetRequiredService<HybridCache>()),
                new SqliteClickEventRepository(Db),
                generator,
                timeProvider);

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

    private sealed class SequenceShortCodeGenerator(params string[] codes) : IShortCodeGenerator
    {
        private int index;
        public string Generate(string destination) => codes[Math.Min(index++, codes.Length - 1)];
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
