using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace UrlShortener.Application;

public sealed class GitHubPullRequestPublisher(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration)
    : IWorkflowPullRequestPublisher
{
    public async Task<string> PublishAsync(string workflowId, WorkflowRequest request, IReadOnlyDictionary<string, WorkflowArtifact> artifacts, CancellationToken cancellationToken)
    {
        var appliedPath = JsonDocument.Parse(artifacts["implementation"].Content).RootElement.GetProperty("appliedPaths")[0].GetString();
        var branch = appliedPath?.Split(':', 2)[0];
        if (string.IsNullOrWhiteSpace(branch))
            throw new InvalidOperationException("Implementation artifact does not identify a workflow branch.");

        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("GITHUB_TOKEN is required to create the workflow pull request.");

        var repository = GetRepository(configuration["Workflow:Repository"] ?? "https://github.com/ennakili/Charles-Schwab-Assessment.git");
        await RunGitAsync(["push", "-u", "origin", branch], cancellationToken);

        var testEvidence = artifacts["tests"].Content;
        var hashes = artifacts.Values.ToDictionary(item => item.Stage, item => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(item.Content))));
        var body = $"## Workflow implementation\n\n" +
            $"Workflow: `{workflowId}`\n\n" +
            $"Requirement: {request.Requirement}\n\n" +
            $"Scenario: `{request.Scenario}`\n\n" +
            $"### Validation\n\n```json\n{testEvidence}\n```\n\n" +
            $"### Artifact hashes\n\n```json\n{JsonSerializer.Serialize(hashes, new JsonSerializerOptions { WriteIndented = true })}\n```\n\n" +
            "Review the generated implementation manifest and confirm the requested change before merging.\n";

        var client = httpClientFactory.CreateClient("github");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("UrlShortenerWorkflow", "1.0"));
        using var response = await client.PostAsJsonAsync($"repos/{repository}/pulls", new
        {
            title = $"Workflow: {request.Scenario} URL shortener change",
            head = branch,
            @base = configuration["Workflow:PullRequestBase"] ?? "main",
            body
        }, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var pullRequest = document.RootElement;
        var pullRequestNumber = pullRequest.GetProperty("number").GetInt32();
        await ValidateRequiredChecksAsync(client, repository, configuration["Workflow:PullRequestBase"] ?? "main", cancellationToken);
        await ConfigureReviewersAsync(client, repository, pullRequestNumber, cancellationToken);
        await ConfigureLabelsAsync(client, repository, pullRequestNumber, cancellationToken);
        if (bool.TryParse(configuration["Workflow:EnableMergeQueue"], out var enableMergeQueue) && enableMergeQueue)
            await AddToMergeQueueAsync(client, repository, pullRequest.GetProperty("node_id").GetString()!, cancellationToken);
        return pullRequest.GetProperty("html_url").GetString()!;
    }

    private async Task ValidateRequiredChecksAsync(HttpClient client, string repository, string baseBranch, CancellationToken cancellationToken)
    {
        var required = configuration.GetSection("Workflow:RequiredChecks").Get<string[]>() ?? [];
        if (required.Length == 0)
            return;

        using var response = await client.GetAsync($"repos/{repository}/branches/{Uri.EscapeDataString(baseBranch)}/protection", cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var configured = document.RootElement.GetProperty("required_status_checks").GetProperty("contexts").EnumerateArray().Select(item => item.GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = required.Where(check => !configured.Contains(check)).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException($"Required GitHub checks are missing from branch protection: {string.Join(", ", missing)}.");
    }

    private async Task ConfigureReviewersAsync(HttpClient client, string repository, int pullRequestNumber, CancellationToken cancellationToken)
    {
        var reviewers = configuration.GetSection("Workflow:Reviewers").Get<string[]>() ?? [];
        var teams = configuration.GetSection("Workflow:TeamReviewers").Get<string[]>() ?? [];
        if (reviewers.Length == 0 && teams.Length == 0)
            return;
        using var response = await client.PostAsJsonAsync($"repos/{repository}/pulls/{pullRequestNumber}/requested_reviewers", new { reviewers, team_reviewers = teams }, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task ConfigureLabelsAsync(HttpClient client, string repository, int pullRequestNumber, CancellationToken cancellationToken)
    {
        var labels = configuration.GetSection("Workflow:Labels").Get<string[]>() ?? [];
        if (labels.Length == 0)
            return;
        using var response = await client.PostAsJsonAsync($"repos/{repository}/issues/{pullRequestNumber}/labels", new { labels }, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task AddToMergeQueueAsync(HttpClient client, string repository, string pullRequestNodeId, CancellationToken cancellationToken)
    {
        var query = "mutation($pullRequestId:ID!,$queueName:String!){enqueuePullRequest(input:{pullRequestId:$pullRequestId,queueName:$queueName}){mergeQueueEntry{ id }}}";
        using var response = await client.PostAsJsonAsync("graphql", new { query, variables = new { pullRequestId = pullRequestNodeId, queueName = configuration["Workflow:MergeQueueName"] ?? "default" } }, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task RunGitAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = configuration["Workflow:SourceRoot"] ?? Directory.GetCurrentDirectory(),
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var error = await errorTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error.Trim()}");
    }

    private static string GetRepository(string remote)
    {
        var uri = new Uri(remote.EndsWith(".git", StringComparison.Ordinal) ? remote : $"{remote}.git");
        var path = uri.AbsolutePath.Trim('/');
        return path.EndsWith(".git", StringComparison.Ordinal) ? path[..^4] : path;
    }
}