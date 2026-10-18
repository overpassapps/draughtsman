namespace Draughtsman.Server.Export;

/// <summary>
/// Caps how many Chromium instances render at once across the whole process (AU-09), whichever door the request came in
/// by: the HTTP limiter refuses a caller early, but the MCP tool and the HTTP route both end in the renderer, so the cap
/// that holds for certain lives here. A render waits a short while for a slot rather than failing on the spot, since an
/// agent's retry costs more than a few seconds' wait, then gives up with <see cref="PdfBusyException"/>.
/// </summary>
public sealed class PdfRenderGate
{
    /// <summary>How long a render waits for a slot before it is refused as busy.</summary>
    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim? _slots;
    private readonly TimeSpan _wait;

    /// <param name="permits">Renders allowed at once; zero or less means no cap.</param>
    public PdfRenderGate(int permits, TimeSpan? wait = null)
    {
        _slots = permits > 0 ? new SemaphoreSlim(permits, permits) : null;
        _wait = wait ?? DefaultWait;
    }

    /// <summary>Takes a slot; dispose the result to give it back.</summary>
    /// <exception cref="PdfBusyException">No slot came free within the wait.</exception>
    public async Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        if (_slots is null)
        {
            return NoSlot.Instance;
        }

        if (!await _slots.WaitAsync(_wait, cancellationToken).ConfigureAwait(false))
        {
            throw new PdfBusyException();
        }

        return new Slot(_slots);
    }

    private sealed class NoSlot : IDisposable
    {
        public static readonly NoSlot Instance = new();

        public void Dispose()
        {
        }
    }

    private sealed class Slot(SemaphoreSlim slots) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                slots.Release();
            }
        }
    }
}
