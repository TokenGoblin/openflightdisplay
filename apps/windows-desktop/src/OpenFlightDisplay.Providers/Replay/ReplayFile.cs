namespace OpenFlightDisplay.Providers.Replay;

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenFlightDisplay.Core.Aircraft;

/// <summary>A recording loaded from disk.</summary>
public sealed record ReplayRecording(
    string Name,
    int SchemaVersion,
    DateTimeOffset RecordedAt,
    string ProviderId,
    IReadOnlyList<ReplayFrame> Frames);

/// <summary>Outcome of loading a recording.</summary>
public abstract record ReplayLoadResult
{
    private ReplayLoadResult()
    {
    }

    public sealed record Loaded(ReplayRecording Recording, int SkippedLines) : ReplayLoadResult;

    /// <summary>The file could not be used, with a reason worth showing.</summary>
    public sealed record Failed(string Detail) : ReplayLoadResult;
}

/// <summary>
/// Reads and writes replay recordings.
/// </summary>
/// <remarks>
/// <para>
/// <b>JSON Lines</b>: a header object on the first line, then one frame per
/// line. Chosen over a single JSON document because a recording is written
/// incrementally over a long session — appending a line needs no rewrite, and a
/// process that dies mid-session loses at most the last partial line instead of
/// producing a file with no closing bracket that cannot be parsed at all.
/// </para>
/// <para>
/// Frames carry full <see cref="AircraftState"/> records, so a recording
/// preserves every nullable exactly as the provider reported it. Writing a
/// reduced shape would bake this project's most load-bearing rule — nullable
/// means "not reported", never zero — out of the one artefact intended for
/// reproducing bugs.
/// </para>
/// </remarks>
public static class ReplayFile
{
    /// <summary>Current on-disk format version.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Conventional file extension.</summary>
    public const string Extension = ".ofdreplay";

    private static readonly JsonSerializerOptions Options = new()
    {
        // Computed properties like AltitudeFt and VerticalTrend have no setter.
        // Writing them would bloat every frame with values that are ignored on
        // read and could silently disagree with the fields they derive from.
        IgnoreReadOnlyProperties = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Header written as the first line.</summary>
    private sealed record Header(
        int SchemaVersion,
        DateTimeOffset RecordedAt,
        string ProviderId);

    /// <summary>One frame line.</summary>
    private sealed record FrameLine(
        DateTimeOffset ObservedAt,
        IReadOnlyList<AircraftState> Aircraft);

    /// <summary>Serializes the header line.</summary>
    public static string BuildHeaderLine(string providerId, DateTimeOffset recordedAt)
        => JsonSerializer.Serialize(
            new Header(CurrentSchemaVersion, recordedAt, providerId), Options);

    /// <summary>Serializes one frame line.</summary>
    public static string BuildFrameLine(ReplayFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return JsonSerializer.Serialize(new FrameLine(frame.ObservedAt, frame.Aircraft), Options);
    }

    /// <summary>
    /// Loads a recording.
    /// </summary>
    /// <remarks>
    /// A malformed line is skipped and counted rather than failing the load: the
    /// common cause is a session that ended abruptly, leaving a truncated final
    /// line, and refusing to open the whole recording over that would discard
    /// everything that was captured correctly. The count is reported so the user
    /// is told rather than quietly given a short recording.
    /// </remarks>
    public static async Task<ReplayLoadResult> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            if (!File.Exists(path))
            {
                return new ReplayLoadResult.Failed($"There is no file at {path}.");
            }

            await using FileStream stream = File.OpenRead(path);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            string? headerLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(headerLine))
            {
                return new ReplayLoadResult.Failed("The file is empty.");
            }

            Header? header;
            try
            {
                header = JsonSerializer.Deserialize<Header>(headerLine, Options);
            }
            catch (JsonException)
            {
                return new ReplayLoadResult.Failed(
                    "The first line is not a recording header, so this is not an "
                    + "OpenFlightDisplay recording.");
            }

            if (header is null)
            {
                return new ReplayLoadResult.Failed("The recording header is missing.");
            }

            if (header.SchemaVersion > CurrentSchemaVersion)
            {
                // Same rule the settings store applies: refuse to guess at a
                // shape this build does not understand.
                return new ReplayLoadResult.Failed(
                    string.Create(
                        CultureInfo.CurrentCulture,
                        $"This recording is version {header.SchemaVersion}, which is newer than "
                        + $"this build understands (version {CurrentSchemaVersion})."));
            }

            var frames = new List<ReplayFrame>();
            int skipped = 0;

            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    if (JsonSerializer.Deserialize<FrameLine>(line, Options) is { } frame)
                    {
                        frames.Add(new ReplayFrame(frame.ObservedAt, frame.Aircraft ?? []));
                    }
                    else
                    {
                        skipped++;
                    }
                }
                catch (JsonException)
                {
                    skipped++;
                }
            }

            if (frames.Count == 0)
            {
                return new ReplayLoadResult.Failed(
                    "The recording has a valid header but no usable frames.");
            }

            var recording = new ReplayRecording(
                Path.GetFileNameWithoutExtension(path),
                header.SchemaVersion,
                header.RecordedAt,
                header.ProviderId,
                frames);

            return new ReplayLoadResult.Loaded(recording, skipped);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ReplayLoadResult.Failed($"The file could not be read: {ex.Message}");
        }
    }
}
