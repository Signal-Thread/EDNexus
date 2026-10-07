using System.Diagnostics;
using System.Threading.Tasks;

namespace EDNexus.App;

/// <summary>
/// Observes a task nobody awaits. Discarding one (<c>_ = DoAsync()</c>) is fine until it throws: the
/// exception then goes unobserved and surfaces on a finalizer thread long after anyone can say which
/// call it came from. This logs it where it happened instead.
/// </summary>
public static class FireAndForget
{
    /// <summary>Run <paramref name="task"/> to completion in the background, tracing any failure under <paramref name="what"/>.</summary>
    public static void Forget(this Task task, string what)
    {
        if (task.IsCompletedSuccessfully) return;
        _ = Observe(task, what);
    }

    private static async Task Observe(Task task, string what)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancelled on purpose (shutdown, superseded request) — not a failure.
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"{what} failed: {ex}");
        }
    }
}
