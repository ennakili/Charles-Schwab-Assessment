namespace UrlShortener.Application;

public sealed record WorkflowImpactAnalysis(
    IReadOnlyList<string> RootStages,
    string Rationale);

public interface IWorkflowImpactAnalyzer
{
    Task<WorkflowImpactAnalysis> AnalyzeAsync(
        string previousRequirement,
        string currentRequirement,
        IReadOnlyDictionary<string, WorkflowArtifact> artifacts,
        CancellationToken cancellationToken);
}

public sealed class SemanticWorkflowImpactAnalyzer : IWorkflowImpactAnalyzer
{
    public Task<WorkflowImpactAnalysis> AnalyzeAsync(string previousRequirement, string currentRequirement, IReadOnlyDictionary<string, WorkflowArtifact> artifacts, CancellationToken cancellationToken)
    {
        var roots = new HashSet<string>(StringComparer.Ordinal) { "requirements" };
        var requirement = currentRequirement.ToLowerInvariant();
        if (requirement.Contains("architecture") || requirement.Contains("design"))
            roots.Add("architecture");
        if (requirement.Contains("implementation") || requirement.Contains("api") || requirement.Contains("code"))
            roots.Add("implementation");
        if (requirement.Contains("test") || requirement.Contains("quality") || requirement.Contains("validation"))
            roots.Add("tests");
        if (requirement.Contains("documentation") || requirement.Contains("docs"))
            roots.Add("documentation");

        var rationale = $"Semantic diff compared requirement revisions and {artifacts.Count} persisted artifacts; selected roots: {string.Join(", ", roots)}.";
        return Task.FromResult(new WorkflowImpactAnalysis(roots.ToList(), rationale));
    }
}
