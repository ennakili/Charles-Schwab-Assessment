using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using UrlShortener.Models;

namespace UrlShortener.Application;

public sealed record WorkflowNode(
    string Name,
    IReadOnlyList<string> Dependencies,
    string HandlerName,
    Func<WorkflowGateContext, bool> EntryGate,
    Func<WorkflowGateContext, bool> ExitGate,
    string ArtifactKind);

public sealed record WorkflowGateContext(
    WorkflowRequest Request,
    IReadOnlyDictionary<string, WorkflowStageResult> Completed,
    IReadOnlyDictionary<string, WorkflowArtifact> Artifacts,
    IReadOnlyList<string> Decisions,
    IReadOnlyList<string> Risks);

public sealed class OrchestrationService
{
    private readonly IAuditSink auditSink;
    private readonly IWorkflowStateStore stateStore;
    private readonly IWorkflowExecutionLock executionLock;
    private readonly IWorkflowImpactAnalyzer impactAnalyzer;
    private readonly IReadOnlyList<IWorkflowStageHandler> handlers;
    private readonly TimeProvider timeProvider;
    private readonly IReadOnlyList<WorkflowNode> graph;

    public OrchestrationService(IAuditSink auditSink, IWorkflowStateStore stateStore, IEnumerable<IWorkflowStageHandler> handlers, TimeProvider timeProvider)
        : this(auditSink, stateStore, new SqliteWorkflowExecutionLock(stateStore), new SemanticWorkflowImpactAnalyzer(), handlers, timeProvider)
    {
    }

    public OrchestrationService(IAuditSink auditSink, IWorkflowStateStore stateStore, IWorkflowExecutionLock executionLock, IEnumerable<IWorkflowStageHandler> handlers, TimeProvider timeProvider)
        : this(auditSink, stateStore, executionLock, new SemanticWorkflowImpactAnalyzer(), handlers, timeProvider)
    {
    }

    public OrchestrationService(IAuditSink auditSink, IWorkflowStateStore stateStore, IWorkflowExecutionLock executionLock, IWorkflowImpactAnalyzer impactAnalyzer, IEnumerable<IWorkflowStageHandler> handlers, TimeProvider timeProvider)
    {
        this.auditSink = auditSink;
        this.stateStore = stateStore;
        this.executionLock = executionLock;
        this.impactAnalyzer = impactAnalyzer;
        this.handlers = handlers.ToList();
        this.timeProvider = timeProvider;
        graph =
        [
            Node("requirements", [], "requirements", "requirements"),
            Node("architecture", ["requirements"], "architecture", "architecture"),
            Node("implementation", ["architecture"], "implementation", "implementation-change-set"),
            Node("documentation", ["requirements", "architecture"], "documentation", "documentation"),
            Node("tests", ["implementation"], "tests", "test-execution"),
            Node("release-readiness", ["tests", "documentation"], "release-readiness", "release-evidence")
        ];
    }

    public async Task<WorkflowResult> ExecuteAsync(WorkflowRequest request, CancellationToken cancellationToken)
    {
        ValidateGraph();
        var workflowId = request.WorkflowId ?? Guid.NewGuid().ToString("N");
        var lease = await executionLock.AcquireAsync(workflowId, TimeSpan.FromMinutes(2), cancellationToken);
        try
        {
        await stateStore.ReconcileInterruptedAsync(workflowId, cancellationToken);
        var state = await stateStore.StartAsync(workflowId, request, cancellationToken);
        workflowId = state.WorkflowId;
        var artifacts = (await stateStore.GetArtifactsAsync(workflowId, cancellationToken)).ToDictionary(item => item.Stage, StringComparer.Ordinal);
        var decisions = new List<string>();
        var risks = new List<string>();
        var requirementRevision = RequirementRevisionFor(request.Requirement);
        if (state.RequirementRevision != requirementRevision)
        {
            var impact = await impactAnalyzer.AnalyzeAsync(state.Requirement, request.Requirement, artifacts, cancellationToken);
            var impactedStages = ComputeImpactedStages(impact.RootStages);
            await stateStore.ReplanAsync(workflowId, request.Requirement, request.Scenario, requirementRevision, impactedStages, cancellationToken);
            state = await stateStore.StartAsync(workflowId, request, cancellationToken);
            decisions.Add("Re-planned downstream nodes after a requirement revision changed.");
            risks.Add($"Requirement revision invalidated impacted stages: {string.Join(", ", impactedStages)}.");
            Audit(workflowId, "requirements", "replanned", $"{impact.Rationale} Invalidated: {string.Join(", ", impactedStages)}.", state.CorrelationId);
            artifacts = (await stateStore.GetArtifactsAsync(workflowId, cancellationToken)).ToDictionary(item => item.Stage, StringComparer.Ordinal);
        }
        var stopwatch = Stopwatch.StartNew();
        var stages = state.Stages.ToDictionary(item => item.Stage, StringComparer.Ordinal);
        var retries = state.Metrics.RetryCount;
        var rollbacks = state.Metrics.RollbackCount;
        var recoveredAt = state.Metrics.EndToEndLatencyMs;

        while (graph.Any(node => !stages.TryGetValue(node.Name, out var stage) || stage.Status != "completed"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var context = new WorkflowGateContext(request, stages, artifacts, decisions, risks);
            var ready = graph.Where(node => !stages.ContainsKey(node.Name) && node.Dependencies.All(stages.ContainsKey)).ToList();
            if (ready.Count == 0)
                return await StopAsync(workflowId, stages.Values.ToList(), "blocked", "No graph node passed its dependency gate.", retries, rollbacks, stopwatch, recoveredAt, decisions, risks, artifacts.Values.ToList(), state.CorrelationId, cancellationToken);
            foreach (var node in ready)
            {
                var passed = node.EntryGate(context);
                await stateStore.SaveGateEvidenceAsync(new WorkflowGateEvidence(workflowId, node.Name, "entry", passed, passed ? "Dependencies and entry policy satisfied." : "Entry policy rejected the ready node.", timeProvider.GetUtcNow()), cancellationToken);
                if (!passed)
                    return await StopAsync(workflowId, stages.Values.ToList(), "blocked", "A workflow entry gate rejected a ready node.", retries, rollbacks, stopwatch, recoveredAt, decisions, risks, artifacts.Values.ToList(), state.CorrelationId, cancellationToken);
            }

            foreach (var node in ready)
                Audit(workflowId, node.Name, "worker-started", "Isolated ready-node worker started.", state.CorrelationId);

            var executions = await Task.WhenAll(ready.Select(node => ExecuteNodeAsync(node, workflowId, context, cancellationToken)));
            foreach (var node in ready)
            {
                var execution = executions.Single(item => item.Stage.Stage == node.Name);
                retries += execution.RetryCount;
                rollbacks += execution.RollbackCount;
                stages[node.Name] = execution.Stage;
                await stateStore.SaveStageAsync(workflowId, execution.Stage, cancellationToken);
                if (execution.RetryCount > 0)
                    Audit(workflowId, node.Name, "retry", "Transient worker failure recovered by bounded retry.", state.CorrelationId);
                if (execution.RollbackCount > 0)
                    Audit(workflowId, node.Name, "rollback", "Worker exhausted bounded retries; prior checkpoints retained.", state.CorrelationId);
                if (execution.Artifact is not null && execution.Stage.Status == "completed")
                {
                    artifacts[node.Name] = execution.Artifact;
                    await stateStore.SaveArtifactAsync(execution.Artifact, cancellationToken);
                }
                if (execution.Stage.Status != "completed")
                    return await StopAsync(workflowId, stages.Values.ToList(), execution.Stage.Status, execution.Stage.Detail, retries, rollbacks, stopwatch, recoveredAt, decisions, risks, artifacts.Values.ToList(), state.CorrelationId, cancellationToken);
            }

            await SaveMetricsAsync(workflowId, retries, rollbacks, recoveredAt, stopwatch, cancellationToken);
            await executionLock.RenewAsync(lease, TimeSpan.FromMinutes(2), cancellationToken);

            var exitContext = new WorkflowGateContext(request, stages, artifacts, decisions, risks);
            foreach (var node in ready)
            {
                var passed = node.ExitGate(exitContext);
                await stateStore.SaveGateEvidenceAsync(new WorkflowGateEvidence(workflowId, node.Name, "exit", passed, passed ? "Artifact validation passed." : "Artifact validation failed.", timeProvider.GetUtcNow()), cancellationToken);
                if (!passed)
                    return await StopAsync(workflowId, stages.Values.ToList(), "blocked", $"Exit gate rejected node '{node.Name}'.", retries, rollbacks, stopwatch, recoveredAt, decisions, risks, artifacts.Values.ToList(), state.CorrelationId, cancellationToken);
                Audit(workflowId, node.Name, "exit-gate-passed", "Validated handler artifact output.", state.CorrelationId);
            }
        }

        stopwatch.Stop();
        await SaveMetricsAsync(workflowId, retries, rollbacks, recoveredAt, stopwatch, cancellationToken);
        await stateStore.SetStatusAsync(workflowId, "completed", cancellationToken);
        return Build(workflowId, "completed", stages.Values.ToList(), retries, rollbacks, stopwatch, recoveredAt, decisions, risks, artifacts.Values.ToList());
        }
        finally
        {
            await executionLock.ReleaseAsync(lease, CancellationToken.None);
        }
    }

    public IReadOnlyList<AuditEvent> ReadAudit() => auditSink.ReadAll();

    private async Task<NodeExecution> ExecuteNodeAsync(WorkflowNode node, string workflowId, WorkflowGateContext context, CancellationToken cancellationToken)
    {
        var started = timeProvider.GetUtcNow();
        var handler = handlers.FirstOrDefault(item => item.StageName == node.HandlerName || item.StageName == "*");
        if (handler is null)
            return new NodeExecution(new WorkflowStageResult(node.Name, "blocked", 0, $"No handler registered for '{node.HandlerName}'.", started, timeProvider.GetUtcNow()), null, 0, 0);

        var attempts = 0;
        WorkflowFailure? failure = null;
        while (attempts++ < 2)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (node.Name == "tests" && context.Request.Requirement.Contains("flaky", StringComparison.OrdinalIgnoreCase) && attempts == 1)
                    throw new WorkflowStageException(new WorkflowFailure(WorkflowFailureClass.Transient, "Injected transient test failure.", true, false));

                var artifact = await handler.ExecuteAsync(node.Name, context, workflowId, cancellationToken);
                if (artifact.Kind != node.ArtifactKind)
                    return new NodeExecution(new WorkflowStageResult(node.Name, "blocked", attempts, $"Handler returned artifact kind '{artifact.Kind}', expected '{node.ArtifactKind}'.", started, timeProvider.GetUtcNow()), artifact, attempts > 1 ? 1 : 0, 0);
                if (context.Request.Requirement.Contains($"fail-{node.Name}", StringComparison.OrdinalIgnoreCase))
                    throw new WorkflowStageException(new WorkflowFailure(WorkflowFailureClass.ExternalSideEffect, $"Injected failure for stage '{node.Name}'.", false, true));
                return new NodeExecution(new WorkflowStageResult(node.Name, "completed", attempts, artifact.Validation, started, timeProvider.GetUtcNow()), artifact, attempts > 1 ? 1 : 0, 0);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (attempts >= 2)
                    break;
            }
            catch (WorkflowStageException exception)
            {
                failure = exception.Failure;
                Audit(workflowId, node.Name, "failure-classified", $"{failure.Classification}: {failure.Message}", context.Request.CorrelationId ?? workflowId);
                if (attempts >= 2)
                    break;
            }
            catch (Exception exception)
            {
                failure = new WorkflowFailure(WorkflowFailureClass.Unknown, exception.Message, false, node.Name is "implementation" or "release-readiness");
                Audit(workflowId, node.Name, "failure-classified", $"{failure.Classification}: {failure.Message}", context.Request.CorrelationId ?? workflowId);
                break;
            }
        }

        var compensation = failure?.CompensationRequired == true ? await handler.CompensateAsync(node.Name, workflowId, cancellationToken) : "not-required";
        var detail = failure is null ? "Node failed after bounded retries; prior checkpoints retained." : failure.Message;
        return new NodeExecution(new WorkflowStageResult(node.Name, "rolled-back", attempts - 1, detail, started, timeProvider.GetUtcNow(), failure, compensation), null, 1, 1);
    }

    private async Task<WorkflowResult> StopAsync(string workflowId, List<WorkflowStageResult> stages, string status, string detail, int retries, int rollbacks, Stopwatch stopwatch, long recoveredAt, List<string> decisions, List<string> risks, List<WorkflowArtifact> artifacts, string correlationId, CancellationToken cancellationToken)
    {
        stopwatch.Stop();
        await SaveMetricsAsync(workflowId, retries, rollbacks, recoveredAt, stopwatch, cancellationToken);
        Audit(workflowId, "workflow", status, detail, correlationId);
        await stateStore.SetStatusAsync(workflowId, status, cancellationToken);
        return Build(workflowId, status, stages, retries, rollbacks, stopwatch, recoveredAt, decisions, risks, artifacts);
    }

    private void Audit(string workflowId, string stage, string action, string detail, string correlationId) =>
        auditSink.Write(new AuditEvent(Guid.NewGuid(), timeProvider.GetUtcNow(), workflowId, stage, action, action.Contains("gate") || action == "completed" ? "success" : action, detail, correlationId));

    private static string RequirementRevisionFor(string requirement) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(requirement)));

    private Task SaveMetricsAsync(string workflowId, int retries, int rollbacks, long recoveredAt, Stopwatch stopwatch, CancellationToken cancellationToken) =>
        stateStore.SaveMetricsAsync(workflowId, new WorkflowMetricsSnapshot(retries, rollbacks, recoveredAt + stopwatch.ElapsedMilliseconds, rollbacks == 0 ? 0 : stopwatch.ElapsedMilliseconds, timeProvider.GetUtcNow()), cancellationToken);

    private WorkflowNode Node(string name, IReadOnlyList<string> dependencies, string handlerName, string artifactKind) =>
        new(name, dependencies, handlerName, context => context.Request.Requirement.Length > 0 && (context.Request.Scenario != "ambiguous" || HasAcceptanceCriteria(context.Request.Requirement)), context => context.Artifacts.TryGetValue(name, out var artifact) && artifact.Kind == artifactKind && artifact.Content.Length > 0 && artifact.Validation.StartsWith("PASS", StringComparison.Ordinal), artifactKind);

    private static bool HasAcceptanceCriteria(string requirement) =>
        requirement.Contains("acceptance", StringComparison.OrdinalIgnoreCase) || requirement.Contains("must", StringComparison.OrdinalIgnoreCase) || requirement.Contains("criteria", StringComparison.OrdinalIgnoreCase);

    private IReadOnlyList<string> ComputeImpactedStages(IReadOnlyList<string> changedStages)
    {
        var impacted = changedStages.ToHashSet(StringComparer.Ordinal);
        var expanded = true;
        while (expanded)
        {
            expanded = false;
            foreach (var node in graph)
            {
                if (!impacted.Contains(node.Name) && node.Dependencies.Any(impacted.Contains) && impacted.Add(node.Name))
                    expanded = true;
            }
        }
        return graph.Where(node => impacted.Contains(node.Name)).Select(node => node.Name).ToList();
    }

    private void ValidateGraph()
    {
        var names = graph.Select(node => node.Name).ToHashSet(StringComparer.Ordinal);
        if (names.Count != graph.Count || graph.Any(node => node.Dependencies.Any(dependency => !names.Contains(dependency))))
            throw new InvalidOperationException("Workflow graph contains duplicate or unknown dependencies.");
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in graph) Visit(node.Name);
        void Visit(string name)
        {
            if (visiting.Contains(name)) throw new InvalidOperationException("Workflow graph contains a dependency cycle.");
            if (!visited.Add(name)) return;
            visiting.Add(name);
            foreach (var dependency in graph.Single(node => node.Name == name).Dependencies) Visit(dependency);
            visiting.Remove(name);
        }
    }

    private sealed record NodeExecution(WorkflowStageResult Stage, WorkflowArtifact? Artifact, int RetryCount, int RollbackCount);

    private sealed class WorkflowStageException(WorkflowFailure failure) : Exception(failure.Message)
    {
        public WorkflowFailure Failure { get; } = failure;
    }

    private static WorkflowResult Build(string workflowId, string status, List<WorkflowStageResult> stages, int retries, int rollbacks, Stopwatch stopwatch, long recoveredAt, List<string> decisions, List<string> risks, List<WorkflowArtifact> artifacts) =>
        new(workflowId, status, stages, new WorkflowMetrics(stages.Count == 0 ? 0 : stages.Count(item => item.Status == "completed") / (double)stages.Count, retries, rollbacks, recoveredAt + stopwatch.ElapsedMilliseconds, rollbacks == 0 ? 0 : stopwatch.ElapsedMilliseconds), decisions, risks, artifacts);
}
