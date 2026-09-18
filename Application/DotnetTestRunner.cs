using System.Diagnostics;

namespace UrlShortener.Application;

public sealed class DotnetTestRunner : IWorkflowTestRunner
{
    private const string Command = "dotnet test Tests/UrlShortener.Tests/UrlShortener.Tests.csproj --no-restore --logger console;verbosity=minimal";

    public async Task<TestExecutionEvidence> RunAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = Directory.GetCurrentDirectory(),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("test");
        process.StartInfo.ArgumentList.Add("Tests/UrlShortener.Tests/UrlShortener.Tests.csproj");
        process.StartInfo.ArgumentList.Add("--no-restore");
        process.StartInfo.ArgumentList.Add("--logger");
        process.StartInfo.ArgumentList.Add("console;verbosity=minimal");

        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = (await outputTask) + (await errorTask);
        stopwatch.Stop();

        return new TestExecutionEvidence(Command, process.ExitCode, process.ExitCode == 0, stopwatch.ElapsedMilliseconds, output, DateTimeOffset.UtcNow);
    }
}