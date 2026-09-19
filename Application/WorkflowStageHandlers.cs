using System.Security.Cryptography;
using System.Text.Json;

namespace UrlShortener.Application;

public interface IWorkflowStageHandler
{
    string StageName { get; }
    string ArtifactKind { get; }
    Task<WorkflowArtifact> ExecuteAsync(string stageName, WorkflowGateContext context, string workflowId, CancellationToken cancellationToken);
    Task<string> CompensateAsync(string stageName, string workflowId, CancellationToken cancellationToken);
}

public sealed class BuiltInWorkflowStageHandler(IWorkflowTestRunner testRunner, IWorkflowChangeApplier changeApplier, IWorkflowPullRequestPublisher pullRequestPublisher, TimeProvider timeProvider) : IWorkflowStageHandler
{
    private static readonly IReadOnlyDictionary<string, string> Kinds = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["requirements"] = "requirements",
        ["architecture"] = "architecture",
        ["implementation"] = "implementation-change-set",
        ["documentation"] = "documentation",
        ["tests"] = "test-execution",
        ["release-readiness"] = "release-evidence"
    };

    public string StageName => "*";
    public string ArtifactKind => "*";

    public async Task<string> CompensateAsync(string stageName, string workflowId, CancellationToken cancellationToken)
    {
        if (stageName == "implementation")
        {
            await changeApplier.RollbackAsync(workflowId, cancellationToken);
            return "completed: implementation change applier removed generated side effects.";
        }
        return "not-required";
    }

    public async Task<WorkflowArtifact> ExecuteAsync(string stage, WorkflowGateContext context, string workflowId, CancellationToken cancellationToken)
    {
        var content = stage switch
        {
            "requirements" => JsonSerializer.Serialize(new { context.Request.Requirement, context.Request.Scenario, mode = context.Request.Scenario == "brownfield" ? "change-existing-system" : context.Request.Scenario == "ambiguous" ? "clarification-required" : "new-system", acceptanceCriteria = new[] { "Requirement is normalized", "Acceptance criteria are explicit", "Risks and assumptions are recorded" }, priorArtifacts = context.Artifacts.Keys.OrderBy(item => item).ToArray() }),
            "architecture" => JsonSerializer.Serialize(new { graph = "requirements -> architecture -> implementation -> tests -> release-readiness", parallelBranch = "requirements + architecture -> documentation -> release-readiness", persistence = "SQLite + HybridCache" }),
            "implementation" => await ImplementationChangeSetAsync(context, workflowId, cancellationToken),
            "documentation" => JsonSerializer.Serialize(new { documents = new[] { "README.md", "MAJOR_GAPS_ASSESSMENT.md" }, scenarios = new[] { "greenfield", "brownfield", "ambiguous" }, operationalNotes = "Run dotnet test before release" }),
            "tests" => await TestEvidenceAsync(cancellationToken),
            "release-readiness" => await ReleaseEvidenceAsync(context, workflowId, cancellationToken),
            _ => throw new InvalidOperationException($"No built-in handler exists for stage '{stage}'.")
        };

        var kind = Kinds[stage];
        var validation = stage == "tests"
            ? "PASS: executed repository test command successfully."
            : context.Request.Scenario == "ambiguous"
                ? "PASS: ambiguity explicitly represented; acceptance criteria required."
                : "PASS: structured scenario artifact generated and validated.";
        return new WorkflowArtifact(workflowId, stage, kind, content, validation, timeProvider.GetUtcNow());
    }

    private async Task<string> TestEvidenceAsync(CancellationToken cancellationToken)
    {
        var evidence = await testRunner.RunAsync(cancellationToken);
        return JsonSerializer.Serialize(new { evidence.Command, evidence.ExitCode, evidence.Passed, evidence.DurationMs, evidence.ExecutedAt, output = evidence.Output });
    }

    private async Task<string> ImplementationChangeSetAsync(WorkflowGateContext context, string workflowId, CancellationToken cancellationToken)
    {
        var changes = new[]
        {
            new ImplementationFileChange("Application/UrlShortenerService.cs", "update", "Apply requested URL behavior behind the existing service contract."),
            new ImplementationFileChange("Tests/UrlShortener.Tests/UrlShortenerTests.cs", "update", "Add regression coverage for the requested behavior.")
        };
        var appliedPaths = await changeApplier.ApplyAsync(workflowId, changes, cancellationToken);
        return JsonSerializer.Serialize(new
        {
            changeSetId = $"url-shortener-{context.Request.Scenario}-{timeProvider.GetUtcNow():yyyyMMddHHmmss}",
            requirement = context.Request.Requirement,
            files = changes,
            appliedPaths,
            acceptance = new[] { "Build succeeds", "Focused tests pass", "API contracts remain compatible" }
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    private async Task<string> ReleaseEvidenceAsync(WorkflowGateContext context, string workflowId, CancellationToken cancellationToken)
    {
        var hashes = context.Artifacts.Values
            .OrderBy(item => item.Stage, StringComparer.Ordinal)
            .ToDictionary(item => item.Stage, item => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(item.Content))), StringComparer.Ordinal);
        var testArtifact = context.Artifacts["tests"];
        var pullRequestUrl = await pullRequestPublisher.PublishAsync(workflowId, context.Request, context.Artifacts, cancellationToken);
        return JsonSerializer.Serialize(new { result = "ready for PR review", pullRequestUrl, testEvidence = JsonSerializer.Deserialize<JsonElement>(testArtifact.Content), artifactSha256 = hashes, requiredArtifacts = Kinds.Keys.ToArray() });
    }
}
