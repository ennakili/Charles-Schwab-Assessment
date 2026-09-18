namespace UrlShortener.Models;

public sealed record ClickEvent(
    string Code,
    DateTimeOffset OccurredAt,
    string? Referer,
    string? UserAgent,
    string? IpAddress);
