using System.Net.Http.Json;
using System.Text.Json;

namespace UrlShortener.Application;

public sealed class HttpAgentToolStageHandler(
    HttpClient client,
    string stageName,
    string artifactKind) : IWorkflowStageHandler
{
    public string StageName => stageName;
    public string ArtifactKind => artifactKind;

    public async Task<WorkflowArtifact> ExecuteAsync(string stageName, WorkflowGateContext context, string workflowId, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync($"stages/{stageName}/execute", new
        {
            workflowId,
            context.Request.Requirement,
            context.Request.Scenario,
            inputArtifacts = context.Artifacts.Keys.OrderBy(item => item).ToArray()
        }, cancellationToken);
        response.EnsureSuccessStatusCode();
        var providerArtifact = await response.Content.ReadFromJsonAsync<ProviderArtifact>(cancellationToken)
            ?? throw new InvalidOperationException("Agent provider returned an empty artifact response.");
        if (providerArtifact.Kind != artifactKind || string.IsNullOrWhiteSpace(providerArtifact.Content) || !providerArtifact.Validation.StartsWith("PASS:", StringComparison.Ordinal))
            throw new InvalidOperationException("Agent provider returned an artifact that violates the workflow contract.");
        return new WorkflowArtifact(workflowId, stageName, providerArtifact.Kind, providerArtifact.Content, providerArtifact.Validation, DateTimeOffset.UtcNow);
    }

    public async Task<string> CompensateAsync(string stageName, string workflowId, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync($"stages/{stageName}/compensate", new { workflowId }, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.GetProperty("status").GetString() ?? "completed";
    }

    private sealed record ProviderArtifact(string Kind, string Content, string Validation);
}
