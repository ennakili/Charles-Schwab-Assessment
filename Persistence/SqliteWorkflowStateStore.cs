using Microsoft.EntityFrameworkCore;
using UrlShortener.Application;

namespace UrlShortener.Persistence;

public sealed class SqliteWorkflowStateStore(UrlShortenerDbContext db) : IWorkflowStateStore
{
    public async Task<WorkflowState> StartAsync(string workflowId, WorkflowRequest request, CancellationToken cancellationToken)
    {
        var existing = await db.Workflows.Include(item => item.Stages).SingleOrDefaultAsync(item => item.WorkflowId == workflowId, cancellationToken);
        if (existing is not null)
            return ToState(existing);

        var entity = new WorkflowEntity
        {
            WorkflowId = workflowId,
            Requirement = request.Requirement,
            Scenario = request.Scenario,
            CorrelationId = request.CorrelationId ?? workflowId,
            Status = "running"
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
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetStatusAsync(string workflowId, string status, CancellationToken cancellationToken)
    {
        var workflow = await db.Workflows.SingleAsync(item => item.WorkflowId == workflowId, cancellationToken);
        workflow.Status = status;
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

    private static WorkflowState ToState(WorkflowEntity entity) => new(
        entity.WorkflowId,
        entity.Requirement,
        entity.Scenario,
        entity.CorrelationId,
        entity.Status,
        entity.Stages
            .OrderBy(item => item.StartedAt)
            .Select(item => new WorkflowStageResult(item.Stage, item.Status, item.Attempts, item.Detail, item.StartedAt, item.CompletedAt))
            .ToList());
}
