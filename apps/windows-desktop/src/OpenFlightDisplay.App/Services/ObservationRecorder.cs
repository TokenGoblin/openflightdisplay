namespace OpenFlightDisplay.App.Services;

using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using OpenFlightDisplay.Core.Aircraft;
using OpenFlightDisplay.Persistence;

/// <summary>Somewhere observations can be recorded.</summary>
/// <remarks>
/// An interface so the feed can be exercised without a database, and so
/// history-disabled mode is a different implementation rather than a null check
/// scattered through the pipeline.
/// </remarks>
public interface IObservationRecorder
{
    /// <summary>Offers a poll's worth of observations for recording.</summary>
    /// <returns>
    /// False if the batch was dropped. Never blocks and never throws — the poll
    /// loop must not be held up, or fail, because history is slow or broken.
    /// </returns>
    bool Record(IReadOnlyList<AircraftState> aircraft);
}

/// <summary>The recorder used when history is switched off.</summary>
/// <remarks>
/// Deliberately a real object rather than a null. History being disabled is the
/// default, and the pipeline should not have to ask whether it exists.
/// </remarks>
public sealed class NullObservationRecorder : IObservationRecorder
{
    public static NullObservationRecorder Instance { get; } = new();

    private NullObservationRecorder()
    {
    }

    /// <inheritdoc/>
    public bool Record(IReadOnlyList<AircraftState> aircraft) => true;
}

/// <summary>
/// Writes observations to the history database on a background drain, behind a
/// bounded queue.
/// </summary>
/// <remarks>
/// <para>
/// The queue is bounded and drops the oldest batch when full. A SQLite write is
/// fast but not instant, and a poll can carry a thousand aircraft; an unbounded
/// queue would turn a slow disk into unbounded memory growth over a multi-day
/// run. Dropping the oldest is the right end to drop — recent observations are
/// what trails and the live views need.
/// </para>
/// <para>
/// Every drop is counted and surfaced on the diagnostics page rather than
/// silently swallowed.
/// </para>
/// </remarks>
public sealed partial class HistoryObservationRecorder : IObservationRecorder, IAsyncDisposable
{
    private const int QueueCapacity = 32;

    private readonly HistoryStore _store;
    private readonly ILogger<HistoryObservationRecorder> _logger;
    private readonly Channel<IReadOnlyList<AircraftState>> _queue;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _drain;

    private long _dropped;
    private long _written;

    public HistoryObservationRecorder(
        HistoryStore store,
        ILogger<HistoryObservationRecorder> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _logger = logger;

        _queue = Channel.CreateBounded<IReadOnlyList<AircraftState>>(
            new BoundedChannelOptions(QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });

        _drain = Task.Run(DrainAsync);
    }

    /// <summary>Batches dropped because the queue was full.</summary>
    public long DroppedBatches => Interlocked.Read(ref _dropped);

    /// <summary>Observations successfully written.</summary>
    public long WrittenObservations => Interlocked.Read(ref _written);

    /// <summary>Batches waiting to be written.</summary>
    public int QueueDepth => _queue.Reader.Count;

    /// <inheritdoc/>
    public bool Record(IReadOnlyList<AircraftState> aircraft)
    {
        ArgumentNullException.ThrowIfNull(aircraft);

        if (aircraft.Count == 0)
        {
            return true;
        }

        // TryWrite on a DropOldest channel always succeeds unless the channel is
        // completed, so a false here means shutdown, not backpressure. The drop
        // counter is incremented by the channel silently evicting, which is why
        // depth is sampled rather than compared.
        if (_queue.Writer.TryWrite(aircraft))
        {
            return true;
        }

        Interlocked.Increment(ref _dropped);
        return false;
    }

    private async Task DrainAsync()
    {
        try
        {
            await foreach (IReadOnlyList<AircraftState> batch in
                _queue.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                try
                {
                    int written = _store.RecordBatch(batch);
                    Interlocked.Add(ref _written, written);
                }
#pragma warning disable CA1031 // History must never take the application down.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    // A failed write loses one batch of history. That is
                    // regrettable; stopping the drain loop, and therefore all
                    // future history, would be worse.
                    Interlocked.Increment(ref _dropped);
                    LogWriteFailed(_logger, ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();

        // Give the drain a moment to flush what is already queued, then stop
        // regardless — shutdown must not hang on a slow disk.
        Task completed = await Task.WhenAny(_drain, Task.Delay(TimeSpan.FromSeconds(5)))
            .ConfigureAwait(false);

        if (completed != _drain)
        {
            await _shutdown.CancelAsync().ConfigureAwait(false);
        }

        _shutdown.Dispose();
    }

    [LoggerMessage(
        EventId = 5000,
        Level = LogLevel.Error,
        Message = "Failed to write a batch of observations to the history database")]
    private static partial void LogWriteFailed(ILogger logger, Exception ex);
}
