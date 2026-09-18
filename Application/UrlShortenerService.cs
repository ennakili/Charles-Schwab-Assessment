using UrlShortener.Models;

namespace UrlShortener.Application;

public sealed class UrlShortenerService(
    IUrlMappingRepository mappings,
    IClickEventRepository clicks,
    IShortCodeGenerator codeGenerator,
    TimeProvider timeProvider)
{
    public async Task<ShortUrlResult> CreateAsync(CreateShortUrlCommand command, string publicOrigin, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(command.Destination, UriKind.Absolute, out var destination) || destination.Scheme is not ("http" or "https"))
            throw new UrlShortenerException("Destination must be an absolute HTTP or HTTPS URL.");
        if (command.ExpiresAt <= timeProvider.GetUtcNow())
            throw new UrlShortenerException("Expiration must be in the future.");

        var now = timeProvider.GetUtcNow();
        var mapping = new UrlMapping(codeGenerator.Generate(command.Destination), destination.ToString(), now, command.ExpiresAt, 0);
        await mappings.AddAsync(mapping, cancellationToken);
        return ToResult(mapping, publicOrigin);
    }

    public async Task<UrlMapping?> ResolveAsync(string code, string? referer, string? userAgent, string? ipAddress, CancellationToken cancellationToken)
    {
        var mapping = await mappings.GetAsync(code, cancellationToken);
        if (mapping is null || mapping.IsExpired(timeProvider.GetUtcNow()))
            return null;

        await mappings.UpdateAsync(mapping.RegisterClick(), cancellationToken);
        await clicks.AddAsync(new ClickEvent(code, timeProvider.GetUtcNow(), referer, userAgent, ipAddress), cancellationToken);
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
