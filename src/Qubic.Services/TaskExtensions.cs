using System.Diagnostics;

namespace Qubic.Services;

/// <summary>
/// Extension methods for safe fire-and-forget async task execution.
/// </summary>
public static class TaskExtensions
{
    /// <summary>
    /// Safely fires a task in the background, catching and logging exceptions
    /// instead of letting them go unobserved.
    /// </summary>
    public static void FireAndForget(this Task task, Action<Exception>? onError = null)
    {
        task.ContinueWith(t =>
        {
            if (t.Exception == null) return;
            var ex = t.Exception.InnerException ?? t.Exception;
            Debug.WriteLine($"[FireAndForget] {ex.GetType().Name}: {ex.Message}");
            onError?.Invoke(ex);
        }, TaskContinuationOptions.OnlyOnFaulted);
    }
}
