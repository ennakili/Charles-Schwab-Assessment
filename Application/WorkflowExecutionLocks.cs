using StackExchange.Redis;

namespace UrlShortener.Application;

public sealed class SqliteWorkflowExecutionLock(IWorkflowStateStore stateStore) : IWorkflowExecutionLock
{
    public Task<WorkflowLease> AcquireAsync(string workflowId, TimeSpan duration, CancellationToken cancellationToken) => stateStore.AcquireLeaseAsync(workflowId, duration, cancellationToken);
    public Task RenewAsync(WorkflowLease lease, TimeSpan duration, CancellationToken cancellationToken) => stateStore.RenewLeaseAsync(lease, duration, cancellationToken);
    public Task ReleaseAsync(WorkflowLease lease, CancellationToken cancellationToken) => stateStore.ReleaseLeaseAsync(lease, cancellationToken);
}

public sealed class RedisWorkflowExecutionLock(IConnectionMultiplexer connection) : IWorkflowExecutionLock
{
    private const string ReleaseScript = "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end";
    private const string RenewScript = "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('pexpire', KEYS[1], ARGV[2]) else return 0 end";

    public async Task<WorkflowLease> AcquireAsync(string workflowId, TimeSpan duration, CancellationToken cancellationToken)
    {
        var ownerId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
        var key = Key(workflowId);
        var acquired = await connection.GetDatabase().StringSetAsync(key, ownerId, duration, When.NotExists);
        if (!acquired)
            throw new InvalidOperationException($"Workflow '{workflowId}' is already leased by another worker.");
        return new WorkflowLease(workflowId, ownerId, DateTimeOffset.UtcNow.Add(duration));
    }

    public async Task RenewAsync(WorkflowLease lease, TimeSpan duration, CancellationToken cancellationToken)
    {
        var result = await connection.GetDatabase().ScriptEvaluateAsync(RenewScript, [Key(lease.WorkflowId)], [lease.OwnerId, ((long)duration.TotalMilliseconds).ToString()]);
        if ((int)result == 0)
            throw new InvalidOperationException("Workflow Redis lease is no longer owned by this worker.");
    }

    public async Task ReleaseAsync(WorkflowLease lease, CancellationToken cancellationToken) =>
        await connection.GetDatabase().ScriptEvaluateAsync(ReleaseScript, [Key(lease.WorkflowId)], [lease.OwnerId]);

    private static RedisKey Key(string workflowId) => $"urlshortener:workflow-lock:{workflowId}";
}