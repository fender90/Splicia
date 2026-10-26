namespace Splicia;

/// <summary>
/// Parallel loop for use on the UI thread. A plain Parallel.For blocks the STA thread in a wait that keeps pumping
/// window messages, so WM_PAINT/timer messages re-enter mid-decode (half-drawn or skipped frames = flicker).
/// Here the work runs on pool threads and the UI thread spin-waits, which never pumps.
/// </summary>
static class NoPump
{
    public static void For(int count, Action<int> body)
    {
        var task = Task.Run(() => Parallel.For(0, count, body));
        var spin = new SpinWait();
        while (!task.IsCompleted) spin.SpinOnce(sleep1Threshold: -1); // Yield/Sleep(0) only: no message pumping
        task.GetAwaiter().GetResult(); // rethrow failures
    }
}
