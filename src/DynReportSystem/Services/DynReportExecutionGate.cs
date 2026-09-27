namespace DynReportSystem.Services;

/// <summary>
/// Process-local protection against report/query storms. This is intentionally
/// simple for the current single-node deployment and can later be replaced by
/// a distributed limiter when DynReport scales out.
/// </summary>
public sealed class DynReportExecutionGate(IConfiguration config)
{
    private readonly SemaphoreSlim _queryGate = new(
        Math.Clamp(config.GetValue("Runtime:MaxConcurrentQueries", 8), 1, 64));

    public async Task<IDisposable> EnterQueryAsync(
        CancellationToken cancellationToken = default)
    {
        await _queryGate.WaitAsync(cancellationToken);
        return new Releaser(_queryGate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public void Dispose()
        {
            Interlocked.Exchange(ref _gate, null)?.Release();
        }
    }
}
