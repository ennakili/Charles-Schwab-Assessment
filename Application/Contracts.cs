using UrlShortener.Models;

namespace UrlShortener.Application;

public sealed record CreateShortUrlCommand(string Destination, DateTimeOffset? ExpiresAt);

public sealed record ShortUrlResult(string Code, string ShortUrl, string Destination, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt);

public sealed record AnalyticsResult(string Code, long Clicks, DateTimeOffset? LastClickedAt);

public interface IUrlMappingRepository
{
    Task<UrlMapping> AddAsync(UrlMapping mapping, CancellationToken cancellationToken);
    Task<UrlMapping?> GetAsync(string code, CancellationToken cancellationToken);
    Task<UrlMapping?> UpdateAsync(UrlMapping mapping, CancellationToken cancellationToken);
}

public interface IClickEventRepository
{
    Task AddAsync(ClickEvent clickEvent, CancellationToken cancellationToken);
    Task<DateTimeOffset?> GetLastClickedAtAsync(string code, CancellationToken cancellationToken);
}

public interface IShortCodeGenerator
{
    string Generate(string destination);
}

public interface IAuditSink
{
    void Write(AuditEvent auditEvent);
    IReadOnlyList<AuditEvent> ReadAll();
}

public sealed record AuditEvent(
    Guid EventId,
    DateTimeOffset OccurredAt,
    string WorkflowId,
    string Stage,
    string Action,
    string Outcome,
    string Detail,
    string? CorrelationId);

public sealed record WorkflowRequest(
    string Requirement,
    string Scenario,
    string? CorrelationId = null,
    string? WorkflowId = null,
    string? IdempotencyKey = null);

public sealed record WorkflowState(
    string WorkflowId,
    string Requirement,
    string Scenario,
    string CorrelationId,
    string Status,
    IReadOnlyList<WorkflowStageResult> Stages,
    WorkflowMetrics Metrics,
    string? IdempotencyKey,
    string RequirementRevision);

public sealed record WorkflowArtifact(
    string WorkflowId,
    string Stage,
    string Kind,
    string Content,
    string Validation,
    DateTimeOffset CreatedAt);

public sealed record WorkflowGateEvidence(
    string WorkflowId,
    string Stage,
    string Gate,
    bool Passed,
    string Detail,
    DateTimeOffset EvaluatedAt);

public enum WorkflowFailureClass
{
    Transient,
    Permanent,
    ExternalSideEffect,
    Cancelled,
    Unknown
}

public sealed record WorkflowFailure(
    WorkflowFailureClass Classification,
    string Message,
    bool Retryable,
    bool CompensationRequired);

public sealed record WorkflowMetricsSnapshot(
    int RetryCount,
    int RollbackCount,
    long EndToEndLatencyMs,
    long MeanTimeToRecoveryMs,
    DateTimeOffset CapturedAt);

public sealed record WorkflowLease(string WorkflowId, string OwnerId, DateTimeOffset ExpiresAt);

public interface IWorkflowExecutionLock
{
    Task<WorkflowLease> AcquireAsync(string workflowId, TimeSpan duration, CancellationToken cancellationToken);
    Task RenewAsync(WorkflowLease lease, TimeSpan duration, CancellationToken cancellationToken);
    Task ReleaseAsync(WorkflowLease lease, CancellationToken cancellationToken);
}

public sealed record TestExecutionEvidence(
    string Command,
    int ExitCode,
    bool Passed,
    long DurationMs,
    string Output,
    DateTimeOffset ExecutedAt);

public interface IWorkflowTestRunner
{
    Task<TestExecutionEvidence> RunAsync(CancellationToken cancellationToken);
}

public sealed record ImplementationFileChange(string Path, string Operation, string Content);

public interface IWorkflowChangeApplier
{
    Task<IReadOnlyList<string>> ApplyAsync(string workflowId, IReadOnlyList<ImplementationFileChange> changes, CancellationToken cancellationToken);
    Task RollbackAsync(string workflowId, CancellationToken cancellationToken);
}

public interface IWorkflowPullRequestPublisher
{
    Task<string> PublishAsync(string workflowId, WorkflowRequest request, IReadOnlyDictionary<string, WorkflowArtifact> artifacts, CancellationToken cancellationToken);
}

public interface IWorkflowStateStore
{
    Task<WorkflowState> StartAsync(string workflowId, WorkflowRequest request, CancellationToken cancellationToken);
    Task<WorkflowState?> GetAsync(string workflowId, CancellationToken cancellationToken);
    Task SaveStageAsync(string workflowId, WorkflowStageResult stage, CancellationToken cancellationToken);
    Task SetStatusAsync(string workflowId, string status, CancellationToken cancellationToken);
    Task SaveArtifactAsync(WorkflowArtifact artifact, CancellationToken cancellationToken);
    Task<IReadOnlyList<WorkflowArtifact>> GetArtifactsAsync(string workflowId, CancellationToken cancellationToken);
    Task SaveGateEvidenceAsync(WorkflowGateEvidence evidence, CancellationToken cancellationToken);
    Task<IReadOnlyList<WorkflowGateEvidence>> GetGateEvidenceAsync(string workflowId, CancellationToken cancellationToken);
    Task SaveMetricsAsync(string workflowId, WorkflowMetricsSnapshot metrics, CancellationToken cancellationToken);
    Task<WorkflowLease> AcquireLeaseAsync(string workflowId, TimeSpan duration, CancellationToken cancellationToken);
    Task RenewLeaseAsync(WorkflowLease lease, TimeSpan duration, CancellationToken cancellationToken);
    Task ReleaseLeaseAsync(WorkflowLease lease, CancellationToken cancellationToken);
    Task ReconcileInterruptedAsync(string workflowId, CancellationToken cancellationToken);
    Task ReplanAsync(string workflowId, string requirement, string scenario, string requirementRevision, IReadOnlyList<string> impactedStages, CancellationToken cancellationToken);
}

public sealed record WorkflowResult(
    string WorkflowId,
    string Status,
    IReadOnlyList<WorkflowStageResult> Stages,
    WorkflowMetrics Metrics,
    IReadOnlyList<string> Decisions,
    IReadOnlyList<string> Risks,
    IReadOnlyList<WorkflowArtifact> Artifacts);

public sealed record WorkflowStageResult(
    string Stage,
    string Status,
    int Attempts,
    string Detail,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    WorkflowFailure? Failure = null,
    string? CompensationStatus = null);

public sealed record WorkflowMetrics(
    double SuccessRate,
    int RetryCount,
    int RollbackCount,
    long EndToEndLatencyMs,
    long MeanTimeToRecoveryMs);
