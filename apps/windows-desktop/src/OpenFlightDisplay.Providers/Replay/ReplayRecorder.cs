namespace OpenFlightDisplay.Providers.Replay;

using System.Text;
using OpenFlightDisplay.Core.Aircraft;

/// <summary>
/// Writes a live session to a replay recording as it happens.
/// </summary>
/// <remarks>
/// <para>
/// A loader with no way to produce a file would be a feature nobody could use,
/// so recording and replay ship together.
/// </para>
/// <para>
/// <b>Flushed after every frame.</b> A recording exists to reproduce a problem,
/// and the problems worth reproducing include the ones that take the process
/// down — buffering would lose exactly the frames that mattered. Frames are
/// small and arrive every couple of seconds, so the cost is irrelevant.
/// </para>
/// </remarks>
public sealed class ReplayRecorder : IAsyncDisposable
{
    private readonly StreamWriter _writer;

    private ReplayRecorder(StreamWriter writer, string path)
    {
        _writer = writer;
        Path = path;
    }

    /// <summary>Where the recording is being written.</summary>
    public string Path { get; }

    /// <summary>Frames written so far.</summary>
    public int FrameCount { get; private set; }

    /// <summary>
    /// Starts a recording, creating or truncating the file.
    /// </summary>
    public static async Task<ReplayRecorder> StartAsync(
        string path,
        string providerId,
        DateTimeOffset recordedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        string? directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var writer = new StreamWriter(path, append: false, Encoding.UTF8);
        await writer.WriteLineAsync(ReplayFile.BuildHeaderLine(providerId, recordedAt))
            .ConfigureAwait(false);

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);

        return new ReplayRecorder(writer, path);
    }

    /// <summary>Appends one poll's worth of aircraft.</summary>
    /// <remarks>
    /// An empty sky is recorded as an empty frame rather than skipped: "nothing
    /// was visible at this moment" is a real observation, and dropping it would
    /// silently compress the timeline during exactly the gap someone is
    /// investigating.
    /// </remarks>
    public async Task WriteFrameAsync(
        IReadOnlyList<AircraftState> aircraft,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(aircraft);

        await _writer.WriteLineAsync(
                ReplayFile.BuildFrameLine(new ReplayFrame(observedAt, aircraft)).AsMemory(),
                cancellationToken)
            .ConfigureAwait(false);

        await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);

        FrameCount++;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _writer.FlushAsync().ConfigureAwait(false);
        await _writer.DisposeAsync().ConfigureAwait(false);
    }
}
