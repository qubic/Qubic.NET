using System.Diagnostics;

namespace Qubic.NodeTester;

/// <summary>
/// Captures one named test's outcome.
/// </summary>
public sealed record TestResult(
    int Index,
    string Name,
    TestStatus Status,
    TimeSpan Duration,
    string? Detail,
    Exception? Error);

public enum TestStatus { Pass, Fail, Skip }

/// <summary>
/// Runs named tests sequentially, prints each line live, and collects results
/// for a final summary.
/// </summary>
public sealed class TestRunner
{
    private readonly List<TestResult> _results = new();
    private int _index = 1;

    public IReadOnlyList<TestResult> Results => _results;

    public async Task RunAsync(string name, Func<Task<string?>> body)
    {
        var idx = _index++;
        var label = $"[{idx,2}] {name,-40}";
        Console.Write(label + " ");
        Console.Out.Flush();

        var sw = Stopwatch.StartNew();
        try
        {
            var detail = await body().ConfigureAwait(false);
            sw.Stop();
            var r = new TestResult(idx, name, TestStatus.Pass, sw.Elapsed, detail, null);
            _results.Add(r);
            Console.WriteLine($"PASS  {Fmt(sw.Elapsed),9}  {detail}");
        }
        catch (Exception ex)
        {
            sw.Stop();
            var r = new TestResult(idx, name, TestStatus.Fail, sw.Elapsed, null, ex);
            _results.Add(r);
            Console.WriteLine($"FAIL  {Fmt(sw.Elapsed),9}  {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void Skip(string name, string reason)
    {
        var idx = _index++;
        var label = $"[{idx,2}] {name,-40}";
        Console.WriteLine($"{label} SKIP             {reason}");
        _results.Add(new TestResult(idx, name, TestStatus.Skip, TimeSpan.Zero, reason, null));
    }

    public void PrintSummary()
    {
        var passed = _results.Count(r => r.Status == TestStatus.Pass);
        var failed = _results.Count(r => r.Status == TestStatus.Fail);
        var skipped = _results.Count(r => r.Status == TestStatus.Skip);
        var total = _results.Sum(r => r.Duration.TotalMilliseconds);

        Console.WriteLine();
        Console.WriteLine(new string('─', 60));
        Console.WriteLine($"summary: {passed} passed, {failed} failed, {skipped} skipped — total {Fmt(TimeSpan.FromMilliseconds(total))}");

        if (failed > 0)
        {
            Console.WriteLine();
            Console.WriteLine("failures:");
            foreach (var r in _results.Where(r => r.Status == TestStatus.Fail))
                Console.WriteLine($"  [{r.Index,2}] {r.Name}: {r.Error?.GetType().Name}: {r.Error?.Message}");
        }
    }

    public int ExitCode => _results.Any(r => r.Status == TestStatus.Fail) ? 1 : 0;

    private static string Fmt(TimeSpan d) =>
        d.TotalSeconds >= 1 ? $"{d.TotalSeconds:0.000}s" : $"{d.TotalMilliseconds:0}ms";
}
