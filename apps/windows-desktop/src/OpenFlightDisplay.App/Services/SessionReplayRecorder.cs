namespace OpenFlightDisplay.App.Services;

using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using OpenFlightDisplay.Core.Aircraft;
using OpenFlightDisplay.Providers.Replay;

/// <summary>
/// Fans one poll out to several recorders.
/// </summary>
/// <remarks>
/// The feed has a single recorder slot. Recording a replay while history is also
/// on is a normal thing to want — capturing a session to reproduce a bug does
/// not mean giving up the history database — so the fan-out lives here rather
/// than as a second slot on the feed.
/// </remarks>
public sealed class CompositeObservationRecorder : IObservationRecorder
{
    private readonly IReadOnlyList<IObservationRecorder> _recorders;

    public CompositeObservationRecorder(params IObservationRecorder[] recorders)
    {
        ArgumentNullException.ThrowIfNull(recorders);
        _recorders = recorders;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Every recorder is offered the batch even after one reports a drop, so a
    /// full history queue cannot stop a replay being captured.
    /// </remarks>
    public bool Record(IReadOnlyList<AircraftState> aircraft)
    {
        bool all = true;

        foreach (IObservationRecorder recorder in _recorders)
        {
            all &= recorder.Record(aircraft);
        }

        return all;
    }
}

/// <summary>
/// Captures the live feed to a replay recording.
/// </summary>
/// <remarks>
/// <para>
/// Queued and drained on a background task for the same reason history is: the
/// poll loop must never wait on a disk, and <see cref="Record"/> is contractually
/// non-blocking.
/// </para>
/// <para>
/// The queue is bounded and drops the <b>newest</b> batch when full — the
/// opposite of the history recorder, which drops the oldest. A recording is a
/// contiguous timeline, and dropping from the middle to admit a newer frame
/// would produce a file that looks complete while silently skipping the moment
/// being investigated. Dropping the newest keeps the recording honest: it stops
/// cleanly rather than developing holes.
/// </para>
/// </remarks>
public sealed partial class SessionReplayRecorder : IObservationRecorder, IAsyncDisposable
{
    private const int QueueCapacity = 64;

    private readonly ReplayRecorder _writer;
    private readonly ILogger _logger;
    private readonly Channel<(IReadOnlyList<AircraftState> Aircraft, DateTimeOffset At)> _queue;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _drain;
    private readonly TimeProvider _timeProvider;

    private long _dropped;

    public SessionReplayRecorder(
        ReplayRecorder writer,
        ILogger logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(logger);

        _writer = writer;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;

        _queue = Channel.CreateBounded<(IReadOnlyList<AircraftState>, DateTimeOffset)>(
            new BoundedChannelOptions(QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
                SingleWriter = false,
            });

        _drain = Task.Run(DrainAsync);
    }

    /// <summary>Where the recording is being written.</summary>
    public string Path => _writer.Path;

    /// <summary>Frames written so far.</summary>
    public int FrameCount => _writer.FrameCount;

    /// <summary>Batches dropped because the queue was full.</summary>
    public long DroppedBatches => Interlocked.Read(ref _dropped);

    /// <inheritdoc/>
    public bool Record(IReadOnlyList<AircraftState> aircraft)
    {
        ArgumentNullException.ThrowIfNull(aircraft);

        // An empty poll is written rather than skipped: "nothing was visible"
        // is an observation, and dropping it would compress the timeline.
        if (_queue.Writer.TryWrite((aircraft, _timeProvider.GetUtcNow())))
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
            await foreach ((IReadOnlyList<AircraftState> aircraft, DateTimeOffset at) in
                _queue.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                try
                {
                    await _writer.WriteFrameAsync(aircraft, at, _shutdown.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
#pragma warning disable CA1031 // Recording must never take the application down.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    Interlocked.Increment(ref _dropped);
                    LogFrameFailed(_logger, ex);
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

        Task completed = await Task.WhenAny(_drain, Task.Delay(TimeSpan.FromSeconds(5)))
            .ConfigureAwait(false);

        if (completed != _drain)
        {
            await _shutdown.CancelAsync().ConfigureAwait(false);
        }

        _shutdown.Dispose();
        await _writer.DisposeAsync().ConfigureAwait(false);
    }

    [LoggerMessage(
        EventId = 5100,
        Level = LogLevel.Error,
        Message = "Failed to write a replay frame; the recording will have a gap")]
    private static partial void LogFrameFailed(ILogger logger, Exception ex);
}
