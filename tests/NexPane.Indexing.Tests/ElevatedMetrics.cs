namespace NexPane.Indexing.Tests;

/// <summary>
/// Measurements of the <c>[Elevated]</c> suite for QA: written to the test output and, when
/// <c>NEXPANE_ELEVATED_METRICS</c> names a file (set by <c>eng\run-elevated-tests.ps1</c>), appended to it with a UTC
/// timestamp so the runner can copy them into its log.
/// </summary>
internal static class ElevatedMetrics
{
    private static readonly Lock Gate = new();

    public static void Write(string line)
    {
        var stamped = $"{DateTime.UtcNow:u}  {line}";
        TestContext.Current.TestOutputHelper?.WriteLine(stamped);
        TestContext.Current.SendDiagnosticMessage(stamped);
        var file = Environment.GetEnvironmentVariable("NEXPANE_ELEVATED_METRICS");
        if (!string.IsNullOrEmpty(file))
        {
            lock (Gate)
            {
                File.AppendAllText(file, stamped + Environment.NewLine);
            }
        }
    }
}
