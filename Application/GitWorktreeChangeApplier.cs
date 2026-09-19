using System.Diagnostics;
using System.Text.Json;

namespace UrlShortener.Application;

public sealed class GitWorktreeChangeApplier(string repositoryRoot) : IWorkflowChangeApplier
{
    public async Task<IReadOnlyList<string>> ApplyAsync(string workflowId, IReadOnlyList<ImplementationFileChange> changes, CancellationToken cancellationToken)
    {
        var safeId = new string(workflowId.Where(char.IsLetterOrDigit).ToArray());
        if (string.IsNullOrWhiteSpace(safeId))
            throw new InvalidOperationException("Workflow ID cannot produce a valid branch name.");

        var branch = $"workflow/{safeId}";
        var worktree = Path.GetFullPath(Path.Combine(repositoryRoot, "..", $"{Path.GetFileName(repositoryRoot)}-{safeId}"));
        var manifestRelativePath = Path.Combine("GeneratedWorkflowChanges", safeId, "implementation-change-set.json");
        var manifestPath = Path.Combine(worktree, manifestRelativePath);

        await RunGitAsync(repositoryRoot, ["worktree", "add", "-b", branch, worktree, "HEAD"], cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        var content = JsonSerializer.Serialize(new { workflowId, branch, generatedAt = DateTimeOffset.UtcNow, changes }, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(manifestPath, content, cancellationToken);
        await RunGitAsync(worktree, ["add", "--", manifestRelativePath], cancellationToken);
        await RunGitAsync(worktree, ["commit", "-m", $"Apply workflow implementation changes {safeId}"], cancellationToken);

        return [$"{branch}:{manifestRelativePath.Replace(Path.DirectorySeparatorChar, '/')}"];
    }

    public async Task RollbackAsync(string workflowId, CancellationToken cancellationToken)
    {
        var safeId = new string(workflowId.Where(char.IsLetterOrDigit).ToArray());
        var branch = $"workflow/{safeId}";
        var worktree = Path.GetFullPath(Path.Combine(repositoryRoot, "..", $"{Path.GetFileName(repositoryRoot)}-{safeId}"));
        await RunGitAsync(repositoryRoot, ["worktree", "remove", "--force", worktree], cancellationToken);
        await RunGitAsync(repositoryRoot, ["branch", "-D", branch], cancellationToken);
    }

    private static async Task RunGitAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var error = await errorTask;
        _ = await outputTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error.Trim()}");
    }
}
