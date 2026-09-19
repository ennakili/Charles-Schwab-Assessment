namespace UrlShortener.Models;

public class UrlShortenerException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>Thrown when a generated short code collides with an existing mapping; callers may retry with a new code.</summary>
public sealed class ShortCodeCollisionException(string code, Exception? innerException = null)
    : UrlShortenerException($"The short code '{code}' already exists.", innerException);
