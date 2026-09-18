using Microsoft.AspNetCore.Mvc;
using UrlShortener.Application;

namespace UrlShortener.Controllers;

[ApiController]
[Route("api/short-urls")]
public sealed class ShortUrlsController(UrlShortenerService service) : ControllerBase
{
    /// <summary>Creates a short URL for an HTTP or HTTPS destination.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(ShortUrlResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ShortUrlResponse>> Create(CreateShortUrlRequest request, CancellationToken cancellationToken)
    {
        var result = await service.CreateAsync(new CreateShortUrlCommand(request.Destination, request.ExpiresAt), GetPublicOrigin(), cancellationToken);
        var response = new ShortUrlResponse(result.Code, result.ShortUrl, result.Destination, result.CreatedAt, result.ExpiresAt);
        return CreatedAtAction(nameof(GetAnalytics), new { code = result.Code }, response);
    }

    /// <summary>Returns click analytics for a short URL.</summary>
    [HttpGet("{code}/analytics")]
    [ProducesResponseType(typeof(AnalyticsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AnalyticsResponse>> GetAnalytics(string code, CancellationToken cancellationToken)
    {
        var result = await service.GetAnalyticsAsync(code, cancellationToken);
        return result is null ? NotFound() : Ok(new AnalyticsResponse(result.Code, result.Clicks, result.LastClickedAt));
    }

    /// <summary>Resolves a short URL and records the click before redirecting.</summary>
    [HttpGet("~/r/{code}")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RedirectToDestination(string code, CancellationToken cancellationToken)
    {
        var mapping = await service.ResolveAsync(code, Request.Headers.Referer, Request.Headers.UserAgent, HttpContext.Connection.RemoteIpAddress?.ToString(), cancellationToken);
        return mapping is null ? NotFound() : Redirect(mapping.Destination);
    }

    private string GetPublicOrigin() => $"{Request.Scheme}://{Request.Host}";
}

/// <summary>Payload for creating a shortened URL.</summary>
public sealed record CreateShortUrlRequest(string Destination, DateTimeOffset? ExpiresAt);

/// <summary>Short URL returned after creation.</summary>
public sealed record ShortUrlResponse(string Code, string ShortUrl, string Destination, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt);

/// <summary>Click analytics for a short URL.</summary>
public sealed record AnalyticsResponse(string Code, long Clicks, DateTimeOffset? LastClickedAt);
