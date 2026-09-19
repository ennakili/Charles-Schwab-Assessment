using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using UrlShortener.Models;

namespace UrlShortener.Application;

public sealed class UrlShortenerService(
    IUrlMappingRepository mappings,
    IClickEventRepository clicks,
    IShortCodeGenerator codeGenerator,
    TimeProvider timeProvider,
    IDestinationAbusePolicy? abusePolicy = null,
    UrlShortenerMetrics? metrics = null,
    ILogger<UrlShortenerService>? logger = null)
{
    private const int MaxCodeGenerationAttempts = 5;
    private readonly IDestinationAbusePolicy abusePolicy = abusePolicy ?? new DefaultDestinationAbusePolicy();
    private readonly UrlShortenerMetrics metrics = metrics ?? new UrlShortenerMetrics();
    private readonly ILogger logger = logger ?? NullLogger<UrlShortenerService>.Instance;

    public async Task<ShortUrlResult> CreateAsync(CreateShortUrlCommand command, string publicOrigin, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(command.Destination, UriKind.Absolute, out var destination) || destination.Scheme is not ("http" or "https"))
            throw new UrlShortenerException("Destination must be an absolute HTTP or HTTPS URL.");
        if (command.ExpiresAt <= timeProvider.GetUtcNow())
            throw new UrlShortenerException("Expiration must be in the future.");

        try
        {
            abusePolicy.Validate(destination);
        }
        catch (UrlShortenerException exception)
        {
            metrics.RecordRejected("abuse-policy");
            logger.LogWarning("Rejected short URL request for host {DestinationHost}: {Reason}", destination.Host, exception.Message);
            throw;
        }

        var now = timeProvider.GetUtcNow();
        for (var attempt = 1; attempt <= MaxCodeGenerationAttempts; attempt++)
        {
            var mapping = new UrlMapping(codeGenerator.Generate(command.Destination), destination.ToString(), now, command.ExpiresAt, 0);
            try
            {
                await mappings.AddAsync(mapping, cancellationToken);
                metrics.RecordCreated();
                logger.LogInformation("Created short URL {Code} for destination host {DestinationHost} on attempt {Attempt}", mapping.Code, destination.Host, attempt);
                return ToResult(mapping, publicOrigin);
            }
            catch (ShortCodeCollisionException) when (attempt < MaxCodeGenerationAttempts)
            {
                metrics.RecordCodeCollision();
                logger.LogWarning("Short code collision on attempt {Attempt} of {MaxAttempts}; regenerating.", attempt, MaxCodeGenerationAttempts);
            }
        }

        throw new UrlShortenerException("Unable to generate a unique short code after multiple attempts.");
    }

    public async Task<UrlMapping?> ResolveAsync(string code, string? referer, string? userAgent, string? ipAddress, CancellationToken cancellationToken)
    {
        var mapping = await mappings.GetAsync(code, cancellationToken);
        if (mapping is null || mapping.IsExpired(timeProvider.GetUtcNow()))
            return null;

        await mappings.UpdateAsync(mapping.RegisterClick(), cancellationToken);
        await clicks.AddAsync(new ClickEvent(code, timeProvider.GetUtcNow(), referer, userAgent, ipAddress), cancellationToken);
        metrics.RecordResolved();
        logger.LogInformation("Resolved short URL {Code}", code);
        return mapping;
    }

    public async Task<AnalyticsResult?> GetAnalyticsAsync(string code, CancellationToken cancellationToken)
    {
        var mapping = await mappings.GetAsync(code, cancellationToken);
        if (mapping is null)
            return null;
        return new AnalyticsResult(code, mapping.ClickCount, await clicks.GetLastClickedAtAsync(code, cancellationToken));
    }

    private static ShortUrlResult ToResult(UrlMapping mapping, string publicOrigin) =>
        new(mapping.Code, $"{publicOrigin.TrimEnd('/')}/r/{mapping.Code}", mapping.Destination, mapping.CreatedAt, mapping.ExpiresAt);
}
