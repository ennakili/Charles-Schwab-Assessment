using Microsoft.EntityFrameworkCore;

namespace UrlShortener.Persistence;

public sealed class UrlShortenerDbContext(DbContextOptions<UrlShortenerDbContext> options) : DbContext(options)
{
    public DbSet<UrlMappingEntity> UrlMappings => Set<UrlMappingEntity>();
    public DbSet<ClickEventEntity> ClickEvents => Set<ClickEventEntity>();
    public DbSet<AuditEventEntity> AuditEvents => Set<AuditEventEntity>();
    public DbSet<WorkflowEntity> Workflows => Set<WorkflowEntity>();
    public DbSet<WorkflowStageEntity> WorkflowStages => Set<WorkflowStageEntity>();
    public DbSet<WorkflowArtifactEntity> WorkflowArtifacts => Set<WorkflowArtifactEntity>();
    public DbSet<WorkflowGateEvidenceEntity> WorkflowGateEvidence => Set<WorkflowGateEvidenceEntity>();
    public DbSet<WorkflowLeaseEntity> WorkflowLeases => Set<WorkflowLeaseEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UrlMappingEntity>(entity =>
        {
            entity.HasKey(item => item.Code);
            entity.Property(item => item.Destination).IsRequired();
            entity.HasIndex(item => item.ExpiresAt);
        });

        modelBuilder.Entity<ClickEventEntity>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.Code, item.OccurredAt });
        });

        modelBuilder.Entity<AuditEventEntity>(entity =>
        {
            entity.HasKey(item => item.EventId);
            entity.HasIndex(item => new { item.WorkflowId, item.OccurredAt });
            entity.Property(item => item.WorkflowId).IsRequired();
            entity.Property(item => item.Stage).IsRequired();
            entity.Property(item => item.Action).IsRequired();
            entity.Property(item => item.Outcome).IsRequired();
            entity.Property(item => item.Detail).IsRequired();
        });

        modelBuilder.Entity<WorkflowEntity>(entity =>
        {
            entity.HasKey(item => item.WorkflowId);
            entity.Property(item => item.Requirement).IsRequired();
            entity.Property(item => item.Scenario).IsRequired();
            entity.Property(item => item.CorrelationId).IsRequired();
            entity.Property(item => item.Status).IsRequired();
            entity.Property(item => item.RequirementRevision).IsRequired();
            entity.HasIndex(item => item.IdempotencyKey).IsUnique();
            entity.HasMany(item => item.Stages).WithOne().HasForeignKey(item => item.WorkflowId);
        });

        modelBuilder.Entity<WorkflowStageEntity>(entity =>
        {
            entity.HasKey(item => new { item.WorkflowId, item.Stage });
            entity.Property(item => item.Status).IsRequired();
            entity.Property(item => item.Detail).IsRequired();
        });

        modelBuilder.Entity<WorkflowArtifactEntity>(entity =>
        {
            entity.HasKey(item => new { item.WorkflowId, item.Stage });
            entity.Property(item => item.Kind).IsRequired();
            entity.Property(item => item.Content).IsRequired();
            entity.Property(item => item.Validation).IsRequired();
        });

        modelBuilder.Entity<WorkflowGateEvidenceEntity>(entity =>
        {
            entity.HasKey(item => new { item.WorkflowId, item.Stage, item.Gate });
            entity.Property(item => item.Detail).IsRequired();
        });

        modelBuilder.Entity<WorkflowLeaseEntity>(entity =>
        {
            entity.HasKey(item => item.WorkflowId);
            entity.Property(item => item.OwnerId).IsRequired();
            entity.Property(item => item.ExpiresAt).IsRequired();
        });
    }
}

public sealed class UrlMappingEntity
{
    public required string Code { get; set; }
    public required string Destination { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public long ClickCount { get; set; }
}

public sealed class ClickEventEntity
{
    public long Id { get; set; }
    public required string Code { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string? Referer { get; set; }
    public string? UserAgent { get; set; }
    public string? IpAddress { get; set; }
}

public sealed class AuditEventEntity
{
    public Guid EventId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public required string WorkflowId { get; set; }
    public required string Stage { get; set; }
    public required string Action { get; set; }
    public required string Outcome { get; set; }
    public required string Detail { get; set; }
    public string? CorrelationId { get; set; }
}

public sealed class WorkflowEntity
{
    public required string WorkflowId { get; set; }
    public required string Requirement { get; set; }
    public required string Scenario { get; set; }
    public required string CorrelationId { get; set; }
    public required string Status { get; set; }
    public string? IdempotencyKey { get; set; }
    public required string RequirementRevision { get; set; }
    public int RetryCount { get; set; }
    public int RollbackCount { get; set; }
    public long EndToEndLatencyMs { get; set; }
    public long MeanTimeToRecoveryMs { get; set; }
    public DateTimeOffset MetricsCapturedAt { get; set; }
    public List<WorkflowStageEntity> Stages { get; set; } = [];
}

public sealed class WorkflowStageEntity
{
    public required string WorkflowId { get; set; }
    public required string Stage { get; set; }
    public required string Status { get; set; }
    public int Attempts { get; set; }
    public required string Detail { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
    public string? FailureClass { get; set; }
    public string? FailureMessage { get; set; }
    public bool FailureRetryable { get; set; }
    public bool CompensationRequired { get; set; }
    public string? CompensationStatus { get; set; }
}

public sealed class WorkflowArtifactEntity
{
    public required string WorkflowId { get; set; }
    public required string Stage { get; set; }
    public required string Kind { get; set; }
    public required string Content { get; set; }
    public required string Validation { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class WorkflowGateEvidenceEntity
{
    public required string WorkflowId { get; set; }
    public required string Stage { get; set; }
    public required string Gate { get; set; }
    public bool Passed { get; set; }
    public required string Detail { get; set; }
    public DateTimeOffset EvaluatedAt { get; set; }
}

public sealed class WorkflowLeaseEntity
{
    public required string WorkflowId { get; set; }
    public required string OwnerId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}
