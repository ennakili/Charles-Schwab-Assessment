using System.Net.Http;
using UrlShortener.Application;
using Xunit;

namespace UrlShortener.Tests;

public sealed class DeployedProviderContractTests
{
    [Fact]
    public async Task DeployedAgentProviderHonorsExecuteAndCompensateContract()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("RUN_DEPLOYED_PROVIDER_CONTRACTS"), "true", StringComparison.OrdinalIgnoreCase))
            return;

        var baseUrl = Environment.GetEnvironmentVariable("AGENT_PROVIDER_BASE_URL");
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException("AGENT_PROVIDER_BASE_URL is required when deployed provider contract tests are enabled.");

        using var client = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
        var handler = new HttpAgentToolStageHandler(client, "architecture", "architecture");
        var context = new WorkflowGateContext(
            new WorkflowRequest("Validate deployed agent provider integration", "greenfield"),
            new Dictionary<string, WorkflowStageResult>(),
            new Dictionary<string, WorkflowArtifact>(),
            [],
            []);

        var artifact = await handler.ExecuteAsync("architecture", context, "deployed-provider-contract", CancellationToken.None);
        var compensation = await handler.CompensateAsync("architecture", "deployed-provider-contract", CancellationToken.None);

        Assert.Equal("architecture", artifact.Kind);
        Assert.StartsWith("PASS:", artifact.Validation, StringComparison.Ordinal);
        Assert.NotEmpty(artifact.Content);
        Assert.StartsWith("completed", compensation, StringComparison.Ordinal);
    }
}
