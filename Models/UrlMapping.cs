namespace UrlShortener.Models;

public sealed record UrlMapping(
    string Code,
    string Destination,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    long ClickCount)
{
    public bool IsExpired(DateTimeOffset now) => ExpiresAt is not null && ExpiresAt <= now;

    public UrlMapping RegisterClick() => this with { ClickCount = ClickCount + 1 };
}
