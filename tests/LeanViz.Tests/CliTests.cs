using System.Diagnostics;
using Xunit;

namespace LeanViz.Tests;

/// <summary>
/// The generator is run by hand and by CI with paths that may be wrong. Being wrong must produce a sentence and
/// an exit code, never a stack trace: an unhandled exception in CI says only that something threw, and the first
/// CI run of this repository failed exactly that way.
/// </summary>
public class CliTests
{
    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        string dll = Path.Combine(AppContext.BaseDirectory, "leanviz.dll");
        Assert.True(File.Exists(dll), $"{dll} was not copied next to the tests");
        var psi = new ProcessStartInfo(Environment.ProcessPath ?? "dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(dll);
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }
        using Process p = Process.Start(psi)!;
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, stdout, stderr);
    }

    [Fact]
    public void AMissingDirectoryIsASentence()
    {
        (int code, _, string err) = Run(Path.Combine(Path.GetTempPath(), "leanviz-no-such-" + Guid.NewGuid().ToString("N")), "--out", Path.GetTempPath());
        Assert.Equal(2, code);
        Assert.Contains("no such directory", err, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", err, StringComparison.Ordinal);
    }

    [Fact]
    public void ADirectoryWithoutOleanFilesSaysSo()
    {
        string dir = Path.Combine(Path.GetTempPath(), "leanviz-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            (int code, _, string err) = Run(dir, "--out", Path.Combine(dir, "out"));
            Assert.Equal(1, code);
            Assert.Contains("no .olean files", err, StringComparison.Ordinal);
            Assert.DoesNotContain("Unhandled exception", err, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void NoArgumentsPrintsTheUsage()
    {
        (int code, _, string err) = Run();
        Assert.Equal(2, code);
        Assert.Contains("usage: leanviz", err, StringComparison.Ordinal);
    }
}
