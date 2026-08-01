using System.Diagnostics;

namespace CaddyUi.Web.Tests;

public sealed class RepositoryTextContractTests
{
    [Fact]
    public void TrackedGitBlobs_DoNotContainCrLfOrMixedLineEndings()
    {
        var repositoryRoot = FindRepositoryRoot();
        var result = RunGit(repositoryRoot, "ls-files --eol");

        Assert.True(result.ExitCode == 0, result.StandardError);

        var invalidBlobs = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith("i/crlf", StringComparison.Ordinal) ||
                line.StartsWith("i/mixed", StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            invalidBlobs.Length == 0,
            "Tracked Git blobs must use LF line endings. Invalid blobs:" +
            Environment.NewLine +
            string.Join(Environment.NewLine, invalidBlobs));
    }

    [Fact]
    public void GitAttributes_DefineTheRepositoryTextPolicy()
    {
        var repositoryRoot = FindRepositoryRoot();
        var attributes = File.ReadAllLines(Path.Combine(repositoryRoot, ".gitattributes"));

        Assert.Contains("* text=auto", attributes);
        Assert.Contains("*.sh text eol=lf", attributes);
        Assert.Contains("/.githooks/** text eol=lf", attributes);
    }

    private static (int ExitCode, string StandardOutput, string StandardError) RunGit(
        string workingDirectory,
        string arguments)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        process.Start();
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return (process.ExitCode, standardOutput, standardError);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CaddyUi.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
