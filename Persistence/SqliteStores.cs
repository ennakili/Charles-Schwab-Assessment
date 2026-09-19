using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using UrlShortener.Application;
using UrlShortener.Models;

namespace UrlShortener.Persistence;

public sealed class SqliteUrlMappingRepository(
    UrlShortenerDbContext db,
    HybridCache cache) : IUrlMappingRepository
{
    private static string CacheKey(string code) => $"url-mapping:{code}";

    public async Task<UrlMapping> AddAsync(UrlMapping mapping, CancellationToken cancellationToken)
    {
        if (await db.UrlMappings.AsNoTracking().AnyAsync(item => item.Code == mapping.Code, cancellationToken))
            throw new ShortCodeCollisionException(mapping.Code);

        var entity = ToEntity(mapping);
        db.UrlMappings.Add(entity);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            db.Entry(entity).State = EntityState.Detached;
            throw new ShortCodeCollisionException(mapping.Code, exception);
        }

        await cache.SetAsync(CacheKey(mapping.Code), mapping, cancellationToken: cancellationToken);
        return mapping;
    }

    public Task<UrlMapping?> GetAsync(string code, CancellationToken cancellationToken) =>
        cache.GetOrCreateAsync(
            CacheKey(code),
            async cancel => await db.UrlMappings
                .AsNoTracking()
                .Where(item => item.Code == code)
                .Select(item => new UrlMapping(item.Code, item.Destination, item.CreatedAt, item.ExpiresAt, item.ClickCount))
                .SingleOrDefaultAsync(cancel),
            cancellationToken: cancellationToken).AsTask();

    public async Task<UrlMapping?> UpdateAsync(UrlMapping mapping, CancellationToken cancellationToken)
    {
        var entity = await db.UrlMappings.SingleOrDefaultAsync(item => item.Code == mapping.Code, cancellationToken);
        if (entity is null)
            return null;

        entity.Destination = mapping.Destination;
        entity.CreatedAt = mapping.CreatedAt;
        entity.ExpiresAt = mapping.ExpiresAt;
        entity.ClickCount = mapping.ClickCount;
        await db.SaveChangesAsync(cancellationToken);
        await cache.SetAsync(CacheKey(mapping.Code), mapping, cancellationToken: cancellationToken);
        return mapping;
    }

    private static UrlMappingEntity ToEntity(UrlMapping mapping) => new()
    {
        Code = mapping.Code,
        Destination = mapping.Destination,
        CreatedAt = mapping.CreatedAt,
        ExpiresAt = mapping.ExpiresAt,
        ClickCount = mapping.ClickCount
    };
}

public sealed class SqliteClickEventRepository(UrlShortenerDbContext db) : IClickEventRepository
{
    public async Task AddAsync(ClickEvent clickEvent, CancellationToken cancellationToken)
    {
        db.ClickEvents.Add(new ClickEventEntity
        {
            Code = clickEvent.Code,
            OccurredAt = clickEvent.OccurredAt,
            Referer = clickEvent.Referer,
            UserAgent = clickEvent.UserAgent,
            IpAddress = clickEvent.IpAddress
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<DateTimeOffset?> GetLastClickedAtAsync(string code, CancellationToken cancellationToken)
    {
        var events = await db.ClickEvents
            .AsNoTracking()
            .Where(item => item.Code == code)
            .Select(item => item.OccurredAt)
            .ToListAsync(cancellationToken);
        return events.Count == 0 ? null : events.Max();
    }
}

public sealed class SqliteAuditSink(UrlShortenerDbContext db) : IAuditSink
{
    public void Write(AuditEvent auditEvent)
    {
        db.AuditEvents.Add(new AuditEventEntity
        {
            EventId = auditEvent.EventId,
            OccurredAt = auditEvent.OccurredAt,
            WorkflowId = auditEvent.WorkflowId,
            Stage = auditEvent.Stage,
            Action = auditEvent.Action,
            Outcome = auditEvent.Outcome,
            Detail = auditEvent.Detail,
            CorrelationId = auditEvent.CorrelationId
        });
        db.SaveChanges();
    }

    public IReadOnlyList<AuditEvent> ReadAll() => db.AuditEvents
        .AsNoTracking()
        .ToList()
        .OrderBy(item => item.OccurredAt)
        .Select(item => new AuditEvent(item.EventId, item.OccurredAt, item.WorkflowId, item.Stage, item.Action, item.Outcome, item.Detail, item.CorrelationId))
        .ToList();
}

public sealed class DeterministicShortCodeGenerator : IShortCodeGenerator
{
    private long sequence;

    public string Generate(string destination)
    {
        var value = Interlocked.Increment(ref sequence);
        return $"{ToBase62(value)}{Math.Abs(destination.GetHashCode(StringComparison.Ordinal)).ToString("x")[..4]}";
    }

    private static string ToBase62(long value)
    {
        const string alphabet = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
        var chars = new Stack<char>();
        do
        {
            chars.Push(alphabet[(int)(value % alphabet.Length)]);
            value /= alphabet.Length;
        } while (value > 0);
        return new string(chars.ToArray());
    }
}
