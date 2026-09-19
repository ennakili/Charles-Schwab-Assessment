using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using UrlShortener.Models;

namespace UrlShortener.Application;

/// <summary>Generates short codes suitable for multi-instance deployment: unguessable and independent of process-local state.</summary>
public sealed class RandomShortCodeGenerator : IShortCodeGenerator
{
    private const string Alphabet = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const int Length = 8;

    public string Generate(string destination)
    {
        Span<byte> buffer = stackalloc byte[Length];
        RandomNumberGenerator.Fill(buffer);
        Span<char> chars = stackalloc char[Length];
        for (var i = 0; i < Length; i++)
            chars[i] = Alphabet[buffer[i] % Alphabet.Length];
        return new string(chars);
    }
}

/// <summary>Rejects destinations that would let a short URL be used for SSRF or internal-network abuse.</summary>
public interface IDestinationAbusePolicy
{
    void Validate(Uri destination);
}

/// <summary>Blocks loopback, private-network, link-local (including cloud metadata endpoints), and configured hostnames.</summary>
public sealed class DefaultDestinationAbusePolicy(IReadOnlyList<string>? blockedHosts = null) : IDestinationAbusePolicy
{
    private static readonly string[] BlockedLiteralHosts = ["localhost", "0.0.0.0", "::1"];
    private readonly IReadOnlyList<string> blockedHosts = blockedHosts ?? [];

    public void Validate(Uri destination)
    {
        var host = destination.Host;
        if (BlockedLiteralHosts.Contains(host, StringComparer.OrdinalIgnoreCase))
            throw new UrlShortenerException($"Destination host '{host}' is not allowed.");

        if (IPAddress.TryParse(host, out var address) && IsDisallowedAddress(address))
            throw new UrlShortenerException($"Destination host '{host}' is a private, loopback, or link-local address.");

        if (blockedHosts.Any(blocked => host.Equals(blocked, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + blocked, StringComparison.OrdinalIgnoreCase)))
            throw new UrlShortenerException($"Destination host '{host}' is blocked by policy.");
    }

    private static bool IsDisallowedAddress(IPAddress address) =>
        IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || IsPrivateIPv4Network(address);

    private static bool IsPrivateIPv4Network(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168)
            || (bytes[0] == 169 && bytes[1] == 254); // link-local, includes the 169.254.169.254 cloud metadata endpoint
    }
}

/// <summary>Structured, exportable counters for URL shortener operations.</summary>
public sealed class UrlShortenerMetrics
{
    public const string MeterName = "UrlShortener";

    private readonly Counter<long> created;
    private readonly Counter<long> resolved;
    private readonly Counter<long> rejected;
    private readonly Counter<long> codeCollisions;
    private readonly Counter<long> rateLimited;

    public UrlShortenerMetrics(IMeterFactory? meterFactory = null)
    {
        var meter = meterFactory?.Create(MeterName) ?? new Meter(MeterName, "1.0.0");
        created = meter.CreateCounter<long>("url_shortener.short_urls.created", description: "Short URLs successfully created.");
        resolved = meter.CreateCounter<long>("url_shortener.short_urls.resolved", description: "Short URLs successfully resolved and redirected.");
        rejected = meter.CreateCounter<long>("url_shortener.short_urls.rejected", description: "Create requests rejected by abuse policy.");
        codeCollisions = meter.CreateCounter<long>("url_shortener.short_urls.code_collisions", description: "Short code generation collisions that required a retry.");
        rateLimited = meter.CreateCounter<long>("url_shortener.requests.rate_limited", description: "Requests rejected by rate limiting.");
    }

    public void RecordCreated() => created.Add(1);
    public void RecordResolved() => resolved.Add(1);
    public void RecordRejected(string reason) => rejected.Add(1, new KeyValuePair<string, object?>("reason", reason));
    public void RecordCodeCollision() => codeCollisions.Add(1);
    public void RecordRateLimited(string policy) => rateLimited.Add(1, new KeyValuePair<string, object?>("policy", policy));
}
