using System.Diagnostics;
using System.Text;

namespace NexPane.Architecture.Tests;

/// <summary>
/// TODO.md P1.12: eng/check-placeholders.ps1 must pass on the repository (zero-placeholder rule) and must catch
/// every rule it claims to enforce. Violations are planted only in throwaway temp trees, never in the repository.
/// </summary>
public sealed class PlaceholderScannerTests
{
    private static string ScannerPath => Path.Combine(RepositoryModel.Root, "eng", "check-placeholders.ps1");

    [Fact]
    public void Repository_has_no_placeholders_stubs_or_fake_work()
    {
        var (exitCode, output) = RunScanner(RepositoryModel.Root);
        exitCode.Should().Be(0, $"the zero-placeholder rule must hold for src, tools, installer and eng. Scanner output:{Environment.NewLine}{output}");
    }

    public static TheoryData<string, string, string> PlantedViolations() => new()
    {
        { "PH001", "src/Planted/Todo.cs", "// TODO: finish this later\ninternal static class A { }\n" },
        { "PH001", "src/Planted/Fixme.cs", "internal static class A { } // fixme\n" },
        { "PH001", "src/Planted/Hack.cs", "// HACK: works around the bug\ninternal static class A { }\n" },
        { "PH001", "tools/Planted/Xxx.cs", "// XXX revisit\ninternal static class A { }\n" },
        { "PH001", "eng/planted.ps1", "# TODO wire this up\nWrite-Host 'x'\n" },
        { "PH002", "src/Planted/NotImpl.cs", "internal static class A { internal static int F() => throw new NotImplementedException(); }\n" },
        { "PH003", "src/Planted/ComingSoon.xaml", "<TextBlock Text=\"Coming Soon\" />\n" },
        { "PH003", "src/Planted/NotImplText.cs", "internal static class A { internal const string M = \"This feature is not implemented\"; }\n" },
        { "PH003", "src/Planted/Lorem.cs", "internal static class A { internal const string M = \"Lorem ipsum dolor sit amet\"; }\n" },
        { "PH003", "installer/planted.iss", "; placeholder until branding exists\n" },
        { "PH004", "src/Planted/NotSupported.cs", "internal static class A { internal static void F() => throw new NotSupportedException(); }\n" },
        { "PH005", "src/Planted/EmptyCatch.cs", "internal static class A { internal static void F() { try { G(); } catch (IOException) { } } static void G() { } }\n" },
        { "PH005", "src/Planted/CommentCatch.cs", "internal static class A {\n  internal static void F() {\n    try { G(); }\n    catch\n    {\n      // ignore\n    }\n  }\n  static void G() { }\n}\n" },
        { "PH005", "eng/planted-catch.ps1", "try { Remove-Item x } catch { }\n" },
        { "PH005", "installer/planted-except.iss", "[Code]\nprocedure P;\nbegin\n  try\n    Foo;\n  except\n  end;\nend;\n" },
        { "PH006", "src/Planted/Delay.cs", "internal static class A { internal static async Task F() { await Task.Delay(1500); } }\n" },
        { "PH006", "src/Planted/Sleep.cs", "internal static class A { internal static void F() { Thread.Sleep(500); } }\n" },
        { "PH007", "src/Planted/Handler.cs", "internal sealed class A {\n  private void OnClick(object sender, RoutedEventArgs e)\n  {\n    Console.WriteLine(\"clicked\");\n  }\n}\n" },
        { "PH007", "src/Planted/Lambda.cs", "internal sealed class A { void Wire(Button b) { b.Click += (s, e) => System.Diagnostics.Debug.WriteLine(\"clicked\"); } }\n" },
        { "PH008", "src/Planted/Fake.cs", "internal static class A { internal static readonly string[] FakeUsers = [\"alice\"]; }\n" },
        { "PH008", "src/Planted/Mock.cs", "internal sealed class MockSearchProvider { }\n" },
        { "PH008", "src/Planted/Dummy.json", "{ \"name\": \"dummy\" }\n" },
        { "PH008", "src/Planted/Sample.cs", "internal static class A { // returns sample data for the grid\n }\n" },
        { "PH008", "src/Planted/Doe.cs", "internal static class A { internal const string Owner = \"John Doe\"; }\n" },
    };

    [Theory]
    [MemberData(nameof(PlantedViolations))]
    public void Planted_violation_is_reported_with_non_zero_exit(string rule, string relativePath, string content)
    {
        using var tree = new TempTree();
        tree.Write(relativePath, content);

        var (exitCode, output) = RunScanner(tree.Root);

        exitCode.Should().Be(1, $"a planted {rule} violation must fail the scan. Output:{Environment.NewLine}{output}");
        output.Should().Contain($"{relativePath}:").And.Contain($" {rule} ");
    }

    [Fact]
    public void Legitimate_code_passes()
    {
        using var tree = new TempTree();
        tree.Write("src/Clean/Audio.cs", """
            namespace Clean;
            internal static class Audio
            {
                internal const int SampleRate = 16000; // resample to 16 kHz mono
                internal static string PlaceholderText => "Search";
                internal static void F(Stream s)
                {
                    try { s.Flush(); }
                    catch (IOException ex) { Log(ex.Message); throw; }
                }
                internal static void G() => throw new NotSupportedException("Archive format is read-only.");
                private static void Log(string m) => System.Diagnostics.Trace.TraceWarning(m);
            }
            """);
        tree.Write("src/Clean/Handler.cs", """
            internal sealed class H
            {
                private void OnClick(object sender, EventArgs e)
                {
                    System.Diagnostics.Debug.WriteLine("click");
                    Navigate();
                }
                private static void Navigate() { }
            }
            """);
        tree.Write("eng/clean.ps1", "try { Remove-Item x } catch { Write-Error $_; exit 1 }\n");
        tree.Write("src/Clean/obj/Generated.cs", "// TODO generated output is ignored\n");
        tree.Write("src/Clean/bin/Release/Thing.json", "{ \"path\": \"bin/placeholder/NexPane.Core.dll\" }\n");

        var (exitCode, output) = RunScanner(tree.Root);

        exitCode.Should().Be(0, $"legitimate code and bin/obj output must not be flagged. Output:{Environment.NewLine}{output}");
    }

    [Fact]
    public void Allowlist_entry_with_justification_suppresses_exactly_its_hit()
    {
        using var tree = new TempTree();
        tree.Write("src/Net/Retry.cs", "internal static class R { internal static Task Backoff(TimeSpan d, CancellationToken ct) => Task.Delay(d, ct); }\n");
        tree.Write("eng/placeholder-allowlist.json", """
            { "entries": [ { "path": "src/Net/Retry.cs", "rule": "PH006", "match": "Task.Delay(d, ct)",
                             "justification": "Exponential backoff between reconnect attempts, not fake work." } ] }
            """);

        RunScanner(tree.Root).ExitCode.Should().Be(0);

        tree.Write("src/Net/Other.cs", "internal static class O { internal static Task W() => Task.Delay(10); }\n");
        var (exitCode, output) = RunScanner(tree.Root);
        exitCode.Should().Be(1, "an allowlist entry must not cover other files");
        output.Should().Contain("src/Net/Other.cs:1:").And.NotContain("src/Net/Retry.cs:1:");
    }

    public static TheoryData<string, string> InvalidAllowlists() => new()
    {
        { "missing justification", """{ "entries": [ { "path": "src/Net/Retry.cs", "rule": "PH006", "match": "Task.Delay(d, ct)" } ] }""" },
        { "short justification", """{ "entries": [ { "path": "src/Net/Retry.cs", "rule": "PH006", "match": "Task.Delay(d, ct)", "justification": "needed" } ] }""" },
        { "too broad match", """{ "entries": [ { "path": "src/Net/Retry.cs", "rule": "PH006", "match": "Delay", "justification": "Exponential backoff between reconnect attempts." } ] }""" },
        { "unknown rule", """{ "entries": [ { "path": "src/Net/Retry.cs", "rule": "PH999", "match": "Task.Delay(d, ct)", "justification": "Exponential backoff between reconnect attempts." } ] }""" },
        { "stale entry", """{ "entries": [ { "path": "src/Net/Retry.cs", "rule": "PH006", "match": "Task.Delay(d, ct)", "justification": "Exponential backoff between reconnect attempts." }, { "path": "src/Net/Gone.cs", "rule": "PH001", "match": "TODO: removed long ago", "justification": "Entry whose code no longer exists must be reported." } ] }""" },
        { "not json", "entries: [ path = x ]" },
    };

    [Theory]
    [MemberData(nameof(InvalidAllowlists))]
    public void Invalid_or_stale_allowlist_fails_with_exit_code_2(string reason, string allowlistJson)
    {
        using var tree = new TempTree();
        tree.Write("src/Net/Retry.cs", "internal static class R { internal static Task Backoff(TimeSpan d, CancellationToken ct) => Task.Delay(d, ct); }\n");
        tree.Write("eng/placeholder-allowlist.json", allowlistJson);

        var (exitCode, output) = RunScanner(tree.Root);

        exitCode.Should().Be(2, $"{reason} must be rejected. Output:{Environment.NewLine}{output}");
    }

    private static (int ExitCode, string Output) RunScanner(string root)
    {
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var psi = new ProcessStartInfo(powershell)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", ScannerPath, "-Root", root })
        {
            psi.ArgumentList.Add(arg);
        }

        var output = new StringBuilder();
        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => Append(output, e.Data);
        process.ErrorDataReceived += (_, e) => Append(output, e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit(TimeSpan.FromMinutes(2)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("check-placeholders.ps1 did not finish within 2 minutes.");
        }

        process.WaitForExit();
        lock (output)
        {
            return (process.ExitCode, output.ToString());
        }
    }

    private static void Append(StringBuilder sb, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (sb)
        {
            sb.AppendLine(line);
        }
    }

    /// <summary>A throwaway repository-shaped folder under %TEMP%, deleted on dispose.</summary>
    private sealed class TempTree : IDisposable
    {
        internal TempTree()
        {
            Root = Path.Combine(Path.GetTempPath(), "nexpane-placeholder-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        internal string Root { get; }

        internal void Write(string relativePath, string content)
        {
            var full = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content.ReplaceLineEndings("\r\n"), new UTF8Encoding(false));
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
