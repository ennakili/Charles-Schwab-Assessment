using System.Text.Json;

namespace UrlShortener.Application;

public sealed class SourceTreeChangeApplier(string sourceRoot) : IWorkflowChangeApplier
{
    public async Task<IReadOnlyList<string>> ApplyAsync(string workflowId, IReadOnlyList<ImplementationFileChange> changes, CancellationToken cancellationToken)
    {
        var relativePath = Path.Combine("GeneratedWorkflowChanges", workflowId, "implementation-change-set.json");
        var destination = ResolveSafePath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".tmp";
        var content = JsonSerializer.Serialize(new { workflowId, generatedAt = DateTimeOffset.UtcNow, changes }, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(temporary, content, cancellationToken);
        File.Move(temporary, destination, true);
        return [relativePath.Replace(Path.DirectorySeparatorChar, '/')];
    }

    public Task RollbackAsync(string workflowId, CancellationToken cancellationToken)
    {
        var safeId = new string(workflowId.Where(char.IsLetterOrDigit).ToArray());
        var generated = ResolveSafePath(Path.Combine("GeneratedWorkflowChanges", safeId));
        if (Directory.Exists(generated))
            Directory.Delete(generated, true);
        var parent = Path.GetDirectoryName(generated)!;
        if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
            Directory.Delete(parent);
        return Task.CompletedTask;
    }

    private string ResolveSafePath(string relativePath)
    {
        if (Path.IsPathRooted(relativePath) || relativePath.Contains("..", StringComparison.Ordinal))
            throw new InvalidOperationException("Generated change path must remain inside the source root.");

        var root = Path.GetFullPath(sourceRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var destination = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!destination.StartsWith(root, StringComparison.Ordinal))
            throw new InvalidOperationException("Generated change path escaped the source root.");
        return destination;
    }
}