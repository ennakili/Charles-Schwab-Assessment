using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace UrlShortener.Application;

public sealed class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";
    public const string PolicyName = "WorkflowControl";
}

/// <summary>Authenticates workflow control requests against a configured shared secret. Fails closed when no key is configured.</summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<ApiKeyAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IConfiguration configuration)
    : AuthenticationHandler<ApiKeyAuthenticationOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var configuredKey = configuration["Workflow:ApiKey"];
        if (string.IsNullOrWhiteSpace(configuredKey))
            return Task.FromResult(AuthenticateResult.Fail("Workflow:ApiKey is not configured; workflow control endpoints are disabled."));

        if (!Request.Headers.TryGetValue(ApiKeyAuthenticationOptions.HeaderName, out var providedKey) || providedKey.Count == 0)
            return Task.FromResult(AuthenticateResult.Fail($"Missing '{ApiKeyAuthenticationOptions.HeaderName}' header."));

        var providedBytes = Encoding.UTF8.GetBytes(providedKey.ToString());
        var configuredBytes = Encoding.UTF8.GetBytes(configuredKey);
        var isValid = providedBytes.Length == configuredBytes.Length && CryptographicOperations.FixedTimeEquals(providedBytes, configuredBytes);
        if (!isValid)
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "workflow-operator")], ApiKeyAuthenticationOptions.SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), ApiKeyAuthenticationOptions.SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
