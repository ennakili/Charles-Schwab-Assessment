using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using StackExchange.Redis;
using UrlShortener.Application;
using Xunit;

namespace UrlShortener.Tests;

public sealed class ProviderAndHostIntegrationTests
{
    [Fact]
    public async Task FullHostCreatesShortUrlAndReturnsAnalytics()
    {
        using var factory = new UrlShortenerWebApplicationFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var create = await client.PostAsJsonAsync("/api/short-urls", new { destination = "https://example.com/host-test" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var response = await create.Content.ReadFromJsonAsync<Dictionary<string, JsonElement>>();
        var code = response!["code"].GetString();

        using var redirect = await client.GetAsync($"/r/{code}");
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.Equal("https://example.com/host-test", redirect.Headers.Location!.ToString());

        using var analytics = await client.GetAsync($"/api/short-urls/{code}/analytics");
        Assert.Equal(HttpStatusCode.OK, analytics.StatusCode);
        var metrics = await analytics.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, metrics.GetProperty("clicks").GetInt64());
    }

    [Fact]
    public async Task ShortUrlCreationIsRateLimitedPerClient()
    {
        using var factory = new UrlShortenerWebApplicationFactory(rateLimitPermitLimit: 2);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var first = await client.PostAsJsonAsync("/api/short-urls", new { destination = "https://example.com/rate-1" });
        using var second = await client.PostAsJsonAsync("/api/short-urls", new { destination = "https://example.com/rate-2" });
        using var third = await client.PostAsJsonAsync("/api/short-urls", new { destination = "https://example.com/rate-3" });

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
    }

    [Fact]
    public async Task WorkflowControlEndpointsRequireApiKey()
    {
        using var factory = new UrlShortenerWebApplicationFactory(workflowApiKey: "test-workflow-key");

        using var missingKeyClient = factory.CreateClient();
        using var missingKeyResponse = await missingKeyClient.GetAsync("/api/workflows/audit");
        Assert.Equal(HttpStatusCode.Unauthorized, missingKeyResponse.StatusCode);

        using var wrongKeyClient = factory.CreateClient();
        wrongKeyClient.DefaultRequestHeaders.Add("X-Api-Key", "wrong-key");
        using var wrongKeyResponse = await wrongKeyClient.GetAsync("/api/workflows/audit");
        Assert.Equal(HttpStatusCode.Unauthorized, wrongKeyResponse.StatusCode);

        using var validKeyClient = factory.CreateClient();
        validKeyClient.DefaultRequestHeaders.Add("X-Api-Key", "test-workflow-key");
        using var validKeyResponse = await validKeyClient.GetAsync("/api/workflows/audit");
        Assert.Equal(HttpStatusCode.OK, validKeyResponse.StatusCode);
    }

    [Fact]
    public async Task WorkflowControlEndpointsAreDisabledWhenNoApiKeyIsConfigured()
    {
        using var factory = new UrlShortenerWebApplicationFactory(workflowApiKey: "");
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/workflows/audit");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DeployedRedisLeaseContractWorksWhenEnabled()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("RUN_DEPLOYED_PROVIDER_CONTRACTS"), "true", StringComparison.OrdinalIgnoreCase))
            return;
        var connectionString = Environment.GetEnvironmentVariable("REDIS_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("REDIS_CONNECTION is required for deployed Redis contract tests.");

        await using var connection = await ConnectionMultiplexer.ConnectAsync(connectionString);
        var lease = new RedisWorkflowExecutionLock(connection);
        var workflowId = $"provider-contract-{Guid.NewGuid():N}";
        var acquired = await lease.AcquireAsync(workflowId, TimeSpan.FromSeconds(30), CancellationToken.None);
        await lease.RenewAsync(acquired, TimeSpan.FromSeconds(30), CancellationToken.None);
        await lease.ReleaseAsync(acquired, CancellationToken.None);
    }

    [Fact]
    public async Task DeployedGitHubContractCanReadRepositoryWhenEnabled()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("RUN_DEPLOYED_PROVIDER_CONTRACTS"), "true", StringComparison.OrdinalIgnoreCase))
            return;
        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        var repository = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") ?? "ennakili/Charles-Schwab-Assessment";
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("GITHUB_TOKEN is required for deployed GitHub contract tests.");

        using var client = new HttpClient { BaseAddress = new Uri("https://api.github.com/") };
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.UserAgent.Add(new System.Net.Http.Headers.ProductInfoHeaderValue("UrlShortenerContractTests", "1.0"));
        using var response = await client.GetAsync($"repos/{repository}");
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(repository.Split('/')[1], payload.GetProperty("name").GetString());
    }

    private sealed class UrlShortenerWebApplicationFactory(int? rateLimitPermitLimit = null, string? workflowApiKey = null) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:UrlShortener", $"Data Source=host-test-{Guid.NewGuid():N}.db");
            builder.UseSetting("Workflow:SourceRoot", Path.GetTempPath());
            if (rateLimitPermitLimit is not null)
                builder.UseSetting("RateLimiting:ShortUrlCreate:PermitLimit", rateLimitPermitLimit.Value.ToString());
            if (workflowApiKey is not null)
                builder.UseSetting("Workflow:ApiKey", workflowApiKey);
        }
    }
}
