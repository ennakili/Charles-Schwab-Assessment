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
    string? WorkflowId = null);

public sealed record WorkflowState(
    string WorkflowId,
    string Requirement,
    string Scenario,
    string CorrelationId,
    string Status,
    IReadOnlyList<WorkflowStageResult> Stages);

public sealed record WorkflowArtifact(
    string WorkflowId,
    string Stage,
    string Kind,
    string Content,
    string Validation,
    DateTimeOffset CreatedAt);

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
}

public interface IWorkflowStateStore
{
    Task<WorkflowState> StartAsync(string workflowId, WorkflowRequest request, CancellationToken cancellationToken);
    Task<WorkflowState?> GetAsync(string workflowId, CancellationToken cancellationToken);
    Task SaveStageAsync(string workflowId, WorkflowStageResult stage, CancellationToken cancellationToken);
    Task SetStatusAsync(string workflowId, string status, CancellationToken cancellationToken);
    Task SaveArtifactAsync(WorkflowArtifact artifact, CancellationToken cancellationToken);
    Task<IReadOnlyList<WorkflowArtifact>> GetArtifactsAsync(string workflowId, CancellationToken cancellationToken);
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
    DateTimeOffset CompletedAt);

public sealed record WorkflowMetrics(
    double SuccessRate,
    int RetryCount,
    int RollbackCount,
    long EndToEndLatencyMs,
    long MeanTimeToRecoveryMs);
