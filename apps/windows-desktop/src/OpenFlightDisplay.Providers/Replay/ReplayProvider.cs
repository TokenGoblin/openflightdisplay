namespace OpenFlightDisplay.Providers.Replay;

using OpenFlightDisplay.Core.Aircraft;
using OpenFlightDisplay.Core.Areas;

/// <summary>One recorded poll: the aircraft seen at a moment in time.</summary>
public sealed record ReplayFrame(DateTimeOffset ObservedAt, IReadOnlyList<AircraftState> Aircraft);

/// <summary>
/// Plays back a recorded session frame by frame.
/// </summary>
/// <remarks>
/// <para>
/// Used for demos, for reproducing a bug against the exact data that caused it,
/// and for testing the pipeline against real provider output with no network.
/// </para>
/// <para>
/// Timestamps are rewritten as each frame is served, so a recording made
/// yesterday does not immediately present as an hour-old stale feed. The
/// <i>relative</i> spacing between observations inside a frame is preserved,
/// which is what keeps per-aircraft staleness meaningful during playback.
/// </para>
/// </remarks>
public sealed class ReplayProvider : IAviationDataProvider
{
    private readonly IReadOnlyList<ReplayFrame> _frames;
    private readonly TimeProvider _timeProvider;
    private readonly bool _loop;

    private int _position;

    /// <param name="loop">
    /// When true, playback wraps instead of finishing. Useful for an unattended
    /// kiosk demo that should never stop on a "replay complete" screen.
    /// </param>
    public ReplayProvider(
        string recordingName,
        IReadOnlyList<ReplayFrame> frames,
        bool loop = false,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordingName);
        ArgumentNullException.ThrowIfNull(frames);

        RecordingName = recordingName;
        _frames = frames;
        _loop = loop;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Name of the recording being played.</summary>
    public string RecordingName { get; }

    /// <summary>Total frames in the recording.</summary>
    public int FrameCount => _frames.Count;

    /// <summary>Index of the next frame to serve.</summary>
    public int Position => _position;

    /// <inheritdoc/>
    public string Id => "replay";

    /// <inheritdoc/>
    public string DisplayName => $"Replay: {RecordingName}";

    /// <inheritdoc/>
    public bool RequiresApiKey => false;

    /// <inheritdoc/>
    public TimeSpan RecommendedPollInterval => TimeSpan.FromSeconds(2);

    /// <summary>Rewinds to the start.</summary>
    public void Reset() => _position = 0;

    /// <summary>Jumps to a specific frame, clamped to the recording's bounds.</summary>
    public void Seek(int frameIndex)
        => _position = Math.Clamp(frameIndex, 0, Math.Max(0, _frames.Count));

    /// <inheritdoc/>
    public Task<ProviderResult> FetchAircraftAsync(
        MonitoringArea area,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(area);
        cancellationToken.ThrowIfCancellationRequested();

        if (_frames.Count == 0)
        {
            return Task.FromResult<ProviderResult>(
                new ProviderResult.Exhausted(RecordingName));
        }

        if (_position >= _frames.Count)
        {
            if (!_loop)
            {
                return Task.FromResult<ProviderResult>(
                    new ProviderResult.Exhausted(RecordingName));
            }

            _position = 0;
        }

        ReplayFrame frame = _frames[_position];
        _position++;

        DateTimeOffset now = _timeProvider.GetUtcNow();

        // Shift every timestamp by the same delta so the frame presents as
        // current while the age differences between aircraft survive.
        TimeSpan shift = now - frame.ObservedAt;

        var rebased = frame.Aircraft
            .Select(a => a with
            {
                FirstSeen = a.FirstSeen + shift,
                LastSeen = a.LastSeen + shift,
                PositionTimestamp = a.PositionTimestamp + shift,
            })
            .ToList();

        return Task.FromResult<ProviderResult>(new ProviderResult.Success(rebased, now));
    }
}
