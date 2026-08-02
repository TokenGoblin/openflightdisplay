namespace OpenFlightDisplay.Providers.Tests;

using OpenFlightDisplay.Core.Aircraft;
using OpenFlightDisplay.Providers.Replay;
using Xunit;

/// <summary>
/// The recording format. The cases that matter are a session that ended badly
/// and a recording that must not quietly lose the distinction between "not
/// reported" and zero.
/// </summary>
public sealed class ReplayFileTests : IDisposable
{
    private static readonly DateTimeOffset Recorded = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly string _directory;

    public ReplayFileTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"ofd-replay-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Test cleanup only.
        }
    }

    [Fact]
    public async Task A_recording_round_trips_through_a_file()
    {
        string path = Path.Combine(_directory, $"session{ReplayFile.Extension}");

        await using (ReplayRecorder recorder =
            await ReplayRecorder.StartAsync(path, "adsblol", Recorded))
        {
            await recorder.WriteFrameAsync([Aircraft("abc123")], Recorded);
            await recorder.WriteFrameAsync([Aircraft("abc123"), Aircraft("def456")], Recorded.AddSeconds(2));

            Assert.Equal(2, recorder.FrameCount);
        }

        var result = Assert.IsType<ReplayLoadResult.Loaded>(await ReplayFile.LoadAsync(path));

        Assert.Equal(0, result.SkippedLines);
        Assert.Equal("adsblol", result.Recording.ProviderId);
        Assert.Equal(2, result.Recording.Frames.Count);
        Assert.Single(result.Recording.Frames[0].Aircraft);
        Assert.Equal(2, result.Recording.Frames[1].Aircraft.Count);
    }

    [Fact]
    public async Task Nulls_survive_the_round_trip_as_nulls_not_zeroes()
    {
        // The single most load-bearing rule in this codebase. A recording exists
        // to reproduce a problem, so a format that turned "not reported" into
        // zero would fabricate the very data being investigated.
        string path = Path.Combine(_directory, $"nulls{ReplayFile.Extension}");

        AircraftState sparse = Aircraft("abc123") with
        {
            GeometricAltitudeFt = null,
            BarometricAltitudeFt = null,
            GroundSpeedKt = null,
            TrackHeadingDeg = null,
            VerticalRateFtPerMin = null,
            Callsign = null,
            Squawk = null,
        };

        await using (ReplayRecorder recorder =
            await ReplayRecorder.StartAsync(path, "adsblol", Recorded))
        {
            await recorder.WriteFrameAsync([sparse], Recorded);
        }

        var result = Assert.IsType<ReplayLoadResult.Loaded>(await ReplayFile.LoadAsync(path));
        AircraftState read = result.Recording.Frames[0].Aircraft[0];

        Assert.Null(read.GeometricAltitudeFt);
        Assert.Null(read.BarometricAltitudeFt);
        Assert.Null(read.GroundSpeedKt);
        Assert.Null(read.TrackHeadingDeg);
        Assert.Null(read.VerticalRateFtPerMin);
        Assert.Null(read.Callsign);
        Assert.Null(read.Squawk);
        Assert.Null(read.AltitudeFt);
    }

    [Fact]
    public async Task A_truncated_final_line_is_skipped_not_fatal()
    {
        // What a session that crashed mid-write leaves behind. Refusing the
        // whole file would discard everything captured correctly, which is the
        // opposite of what a crash recording is for.
        string path = Path.Combine(_directory, $"truncated{ReplayFile.Extension}");

        await using (ReplayRecorder recorder =
            await ReplayRecorder.StartAsync(path, "adsblol", Recorded))
        {
            await recorder.WriteFrameAsync([Aircraft("abc123")], Recorded);
            await recorder.WriteFrameAsync([Aircraft("def456")], Recorded.AddSeconds(2));
        }

        // Chop the last line in half, as an abrupt exit would.
        string[] lines = await File.ReadAllLinesAsync(path);
        lines[^1] = lines[^1][..(lines[^1].Length / 2)];
        await File.WriteAllLinesAsync(path, lines);

        var result = Assert.IsType<ReplayLoadResult.Loaded>(await ReplayFile.LoadAsync(path));

        Assert.Single(result.Recording.Frames);
        Assert.Equal(1, result.SkippedLines);
    }

    [Fact]
    public async Task An_empty_sky_is_recorded_as_a_frame_rather_than_dropped()
    {
        // "Nothing was visible at this moment" is a real observation. Dropping
        // it would compress the timeline over exactly the gap being examined.
        string path = Path.Combine(_directory, $"empty{ReplayFile.Extension}");

        await using (ReplayRecorder recorder =
            await ReplayRecorder.StartAsync(path, "mock", Recorded))
        {
            await recorder.WriteFrameAsync([], Recorded);
            await recorder.WriteFrameAsync([Aircraft("abc123")], Recorded.AddSeconds(2));
        }

        var result = Assert.IsType<ReplayLoadResult.Loaded>(await ReplayFile.LoadAsync(path));

        Assert.Equal(2, result.Recording.Frames.Count);
        Assert.Empty(result.Recording.Frames[0].Aircraft);
    }

    [Fact]
    public async Task A_file_that_is_not_a_recording_is_rejected_with_a_reason()
    {
        string path = Path.Combine(_directory, "notes.txt");
        await File.WriteAllTextAsync(path, "just some text\nand another line\n");

        var failed = Assert.IsType<ReplayLoadResult.Failed>(await ReplayFile.LoadAsync(path));

        Assert.Contains("not an OpenFlightDisplay recording", failed.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_file_is_rejected()
    {
        string path = Path.Combine(_directory, $"empty2{ReplayFile.Extension}");
        await File.WriteAllTextAsync(path, string.Empty);

        var failed = Assert.IsType<ReplayLoadResult.Failed>(await ReplayFile.LoadAsync(path));
        Assert.Contains("empty", failed.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_header_with_no_frames_is_rejected_rather_than_played_as_nothing()
    {
        // Otherwise selecting it would report "replay complete" instantly, which
        // is the dead end this whole feature exists to remove.
        string path = Path.Combine(_directory, $"headeronly{ReplayFile.Extension}");
        await File.WriteAllTextAsync(path, ReplayFile.BuildHeaderLine("adsblol", Recorded) + "\n");

        var failed = Assert.IsType<ReplayLoadResult.Failed>(await ReplayFile.LoadAsync(path));
        Assert.Contains("no usable frames", failed.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_newer_schema_version_is_refused_rather_than_guessed_at()
    {
        string path = Path.Combine(_directory, $"future{ReplayFile.Extension}");
        await File.WriteAllTextAsync(
            path,
            $$"""{"SchemaVersion":{{ReplayFile.CurrentSchemaVersion + 1}},"RecordedAt":"2026-08-01T12:00:00+00:00","ProviderId":"adsblol"}"""
            + "\n");

        var failed = Assert.IsType<ReplayLoadResult.Failed>(await ReplayFile.LoadAsync(path));
        Assert.Contains("newer than this build", failed.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_file_is_reported_not_thrown()
    {
        var failed = Assert.IsType<ReplayLoadResult.Failed>(
            await ReplayFile.LoadAsync(Path.Combine(_directory, "nope.ofdreplay")));

        Assert.Contains("no file", failed.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_loaded_recording_plays_back_through_the_provider()
    {
        // End to end: the point of the format is that ReplayProvider can serve it.
        string path = Path.Combine(_directory, $"playback{ReplayFile.Extension}");

        await using (ReplayRecorder recorder =
            await ReplayRecorder.StartAsync(path, "adsblol", Recorded))
        {
            await recorder.WriteFrameAsync([Aircraft("abc123")], Recorded);
            await recorder.WriteFrameAsync([Aircraft("def456")], Recorded.AddSeconds(2));
        }

        var loaded = Assert.IsType<ReplayLoadResult.Loaded>(await ReplayFile.LoadAsync(path));
        var provider = new ReplayProvider(loaded.Recording.Name, loaded.Recording.Frames);
        var area = new Core.Areas.CircleArea(47.6, -122.3, 100);

        var first = Assert.IsType<ProviderResult.Success>(
            await provider.FetchAircraftAsync(area, CancellationToken.None));
        Assert.Equal("abc123", first.Aircraft[0].IcaoHex);

        var second = Assert.IsType<ProviderResult.Success>(
            await provider.FetchAircraftAsync(area, CancellationToken.None));
        Assert.Equal("def456", second.Aircraft[0].IcaoHex);

        Assert.IsType<ProviderResult.Exhausted>(
            await provider.FetchAircraftAsync(area, CancellationToken.None));
    }

    private static AircraftState Aircraft(string hex) => new()
    {
        Provider = "test",
        IcaoHex = hex,
        Callsign = "TST123",
        Latitude = 47.61,
        Longitude = -122.33,
        GeometricAltitudeFt = 30000,
        GroundSpeedKt = 420,
        TrackHeadingDeg = 180,
        FirstSeen = Recorded,
        LastSeen = Recorded,
        PositionTimestamp = Recorded,
    };
}
