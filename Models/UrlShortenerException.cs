namespace UrlShortener.Models;

public sealed class UrlShortenerException(string message, Exception? innerException = null) : Exception(message, innerException);
