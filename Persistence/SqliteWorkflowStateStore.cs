using Microsoft.EntityFrameworkCore;
using UrlShortener.Application;

namespace UrlShortener.Persistence;

public sealed class SqliteWorkflowStateStore(UrlShortenerDbContext db) : IWorkflowStateStore
{
    public async Task<WorkflowLease> AcquireLeaseAsync(string workflowId, TimeSpan duration, CancellationToken cancellationToken)
    {
        var ownerId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(duration);
        var lease = await db.WorkflowLeases.SingleOrDefaultAsync(item => item.WorkflowId == workflowId, cancellationToken);
        if (lease is not null && lease.ExpiresAt > now)
            throw new InvalidOperationException($"Workflow '{workflowId}' is already leased by another worker.");

        if (lease is null)
            db.WorkflowLeases.Add(new WorkflowLeaseEntity { WorkflowId = workflowId, OwnerId = ownerId, ExpiresAt = expiresAt });
        else
        {
            lease.OwnerId = ownerId;
            lease.ExpiresAt = expiresAt;
        }
        await db.SaveChangesAsync(cancellationToken);
        return new WorkflowLease(workflowId, ownerId, expiresAt);
    }

    public async Task RenewLeaseAsync(WorkflowLease lease, TimeSpan duration, CancellationToken cancellationToken)
    {
        var entity = await db.WorkflowLeases.SingleOrDefaultAsync(item => item.WorkflowId == lease.WorkflowId && item.OwnerId == lease.OwnerId, cancellationToken)
            ?? throw new InvalidOperationException("Workflow lease is no longer owned by this worker.");
        entity.ExpiresAt = DateTimeOffset.UtcNow.Add(duration);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ReleaseLeaseAsync(WorkflowLease lease, CancellationToken cancellationToken)
    {
        var entity = await db.WorkflowLeases.SingleOrDefaultAsync(item => item.WorkflowId == lease.WorkflowId && item.OwnerId == lease.OwnerId, cancellationToken);
        if (entity is not null)
        {
            db.WorkflowLeases.Remove(entity);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task ReconcileInterruptedAsync(string workflowId, CancellationToken cancellationToken)
    {
        var workflow = await db.Workflows.SingleOrDefaultAsync(item => item.WorkflowId == workflowId, cancellationToken);
        if (workflow?.Status == "running")
        {
            workflow.Status = "recovering";
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task ReplanAsync(string workflowId, string requirement, string scenario, string requirementRevision, IReadOnlyList<string> impactedStages, CancellationToken cancellationToken)
    {
        await db.WorkflowStages.Where(item => item.WorkflowId == workflowId && impactedStages.Contains(item.Stage)).ExecuteDeleteAsync(cancellationToken);
        await db.WorkflowArtifacts.Where(item => item.WorkflowId == workflowId && impactedStages.Contains(item.Stage)).ExecuteDeleteAsync(cancellationToken);
        await db.WorkflowGateEvidence.Where(item => item.WorkflowId == workflowId && impactedStages.Contains(item.Stage)).ExecuteDeleteAsync(cancellationToken);
        await db.WorkflowApprovals.Where(item => item.WorkflowId == workflowId && impactedStages.Contains(item.Stage)).ExecuteDeleteAsync(cancellationToken);
        db.ChangeTracker.Clear();
        var workflow = await db.Workflows.SingleAsync(item => item.WorkflowId == workflowId, cancellationToken);
        workflow.Requirement = requirement;
        workflow.Scenario = scenario;
        workflow.RequirementRevision = requirementRevision;
        workflow.Status = "replanned";
        await db.SaveChangesAsync(cancellationToken);
    }
    public async Task<WorkflowState> StartAsync(string workflowId, WorkflowRequest request, CancellationToken cancellationToken)
    {
        var existing = await db.Workflows.Include(item => item.Stages).SingleOrDefaultAsync(item => item.WorkflowId == workflowId || (request.IdempotencyKey != null && item.IdempotencyKey == request.IdempotencyKey), cancellationToken);
        if (existing is not null)
            return ToState(existing);

        var entity = new WorkflowEntity
        {
            WorkflowId = workflowId,
            Requirement = request.Requirement,
            Scenario = request.Scenario,
            CorrelationId = request.CorrelationId ?? workflowId,
            Status = "running",
            IdempotencyKey = request.IdempotencyKey,
            MetricsCapturedAt = DateTimeOffset.UtcNow,
            RequirementRevision = RequirementRevision.For(request.Requirement)
        };
        db.Workflows.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return ToState(entity);
    }

    public async Task<WorkflowState?> GetAsync(string workflowId, CancellationToken cancellationToken)
    {
        var entity = await db.Workflows
            .Include(item => item.Stages)
            .SingleOrDefaultAsync(item => item.WorkflowId == workflowId, cancellationToken);
        return entity is null ? null : ToState(entity);
    }

    public async Task SaveStageAsync(string workflowId, WorkflowStageResult stage, CancellationToken cancellationToken)
    {
        var entity = await db.WorkflowStages.SingleOrDefaultAsync(item => item.WorkflowId == workflowId && item.Stage == stage.Stage, cancellationToken);
        if (entity is null)
        {
            entity = new WorkflowStageEntity { WorkflowId = workflowId, Stage = stage.Stage, Status = stage.Status, Detail = stage.Detail };
            db.WorkflowStages.Add(entity);
        }

        entity.Status = stage.Status;
        entity.Attempts = stage.Attempts;
        entity.Detail = stage.Detail;
        entity.StartedAt = stage.StartedAt;
        entity.CompletedAt = stage.CompletedAt;
        entity.FailureClass = stage.Failure?.Classification.ToString();
        entity.FailureMessage = stage.Failure?.Message;
        entity.FailureRetryable = stage.Failure?.Retryable ?? false;
        entity.CompensationRequired = stage.Failure?.CompensationRequired ?? false;
        entity.CompensationStatus = stage.CompensationStatus;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetStatusAsync(string workflowId, string status, CancellationToken cancellationToken)
    {
        var workflow = await db.Workflows.SingleAsync(item => item.WorkflowId == workflowId, cancellationToken);
        workflow.Status = status;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveMetricsAsync(string workflowId, WorkflowMetricsSnapshot metrics, CancellationToken cancellationToken)
    {
        var workflow = await db.Workflows.SingleAsync(item => item.WorkflowId == workflowId, cancellationToken);
        workflow.RetryCount = metrics.RetryCount;
        workflow.RollbackCount = metrics.RollbackCount;
        workflow.EndToEndLatencyMs = metrics.EndToEndLatencyMs;
        workflow.MeanTimeToRecoveryMs = metrics.MeanTimeToRecoveryMs;
        workflow.MetricsCapturedAt = metrics.CapturedAt;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveArtifactAsync(WorkflowArtifact artifact, CancellationToken cancellationToken)
    {
        var entity = await db.WorkflowArtifacts.SingleOrDefaultAsync(item => item.WorkflowId == artifact.WorkflowId && item.Stage == artifact.Stage, cancellationToken);
        if (entity is null)
        {
            entity = new WorkflowArtifactEntity { WorkflowId = artifact.WorkflowId, Stage = artifact.Stage, Kind = artifact.Kind, Content = artifact.Content, Validation = artifact.Validation };
            db.WorkflowArtifacts.Add(entity);
        }

        entity.Kind = artifact.Kind;
        entity.Content = artifact.Content;
        entity.Validation = artifact.Validation;
        entity.CreatedAt = artifact.CreatedAt;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<WorkflowArtifact>> GetArtifactsAsync(string workflowId, CancellationToken cancellationToken) =>
        await db.WorkflowArtifacts
            .AsNoTracking()
            .Where(item => item.WorkflowId == workflowId)
            .Select(item => new WorkflowArtifact(item.WorkflowId, item.Stage, item.Kind, item.Content, item.Validation, item.CreatedAt))
            .ToListAsync(cancellationToken);

    public async Task SaveGateEvidenceAsync(WorkflowGateEvidence evidence, CancellationToken cancellationToken)
    {
        var entity = await db.WorkflowGateEvidence.SingleOrDefaultAsync(item => item.WorkflowId == evidence.WorkflowId && item.Stage == evidence.Stage && item.Gate == evidence.Gate, cancellationToken);
        if (entity is null)
        {
            entity = new WorkflowGateEvidenceEntity { WorkflowId = evidence.WorkflowId, Stage = evidence.Stage, Gate = evidence.Gate, Detail = evidence.Detail };
            db.WorkflowGateEvidence.Add(entity);
        }

        entity.Passed = evidence.Passed;
        entity.Detail = evidence.Detail;
        entity.EvaluatedAt = evidence.EvaluatedAt;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<WorkflowGateEvidence>> GetGateEvidenceAsync(string workflowId, CancellationToken cancellationToken) =>
        await db.WorkflowGateEvidence
            .AsNoTracking()
            .Where(item => item.WorkflowId == workflowId)
            .OrderBy(item => item.Stage)
            .ThenBy(item => item.Gate)
            .Select(item => new WorkflowGateEvidence(item.WorkflowId, item.Stage, item.Gate, item.Passed, item.Detail, item.EvaluatedAt))
            .ToListAsync(cancellationToken);

    public async Task SaveApprovalDecisionAsync(string workflowId, string stage, string decision, string approver, string? reason, DateTimeOffset decidedAt, CancellationToken cancellationToken)
    {
        var entity = await db.WorkflowApprovals.SingleOrDefaultAsync(item => item.WorkflowId == workflowId && item.Stage == stage, cancellationToken);
        if (entity is null)
        {
            entity = new WorkflowApprovalEntity { WorkflowId = workflowId, Stage = stage, Decision = decision, Approver = approver };
            db.WorkflowApprovals.Add(entity);
        }

        entity.Decision = decision;
        entity.Approver = approver;
        entity.Reason = reason;
        entity.DecidedAt = decidedAt;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<WorkflowApprovalDecision?> GetApprovalDecisionAsync(string workflowId, string stage, CancellationToken cancellationToken)
    {
        var entity = await db.WorkflowApprovals.AsNoTracking().SingleOrDefaultAsync(item => item.WorkflowId == workflowId && item.Stage == stage, cancellationToken);
        return entity is null ? null : new WorkflowApprovalDecision(entity.WorkflowId, entity.Stage, entity.Decision, entity.Approver, entity.Reason, entity.DecidedAt);
    }

    private static WorkflowState ToState(WorkflowEntity entity) => new(
        entity.WorkflowId,
        entity.Requirement,
        entity.Scenario,
        entity.CorrelationId,
        entity.Status,
        entity.Stages
            .OrderBy(item => item.StartedAt)
            .Select(item => new WorkflowStageResult(item.Stage, item.Status, item.Attempts, item.Detail, item.StartedAt, item.CompletedAt))
            .ToList(),
        new WorkflowMetrics(0, entity.RetryCount, entity.RollbackCount, entity.EndToEndLatencyMs, entity.MeanTimeToRecoveryMs),
        entity.IdempotencyKey,
        entity.RequirementRevision);
}

    internal static class RequirementRevision
    {
        public static string For(string requirement) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(requirement)));
    }
