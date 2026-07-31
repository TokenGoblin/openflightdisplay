namespace OpenFlightDisplay.Providers.Tests;

using OpenFlightDisplay.Core.Aircraft;
using OpenFlightDisplay.Core.Areas;
using OpenFlightDisplay.Core.Quality;
using OpenFlightDisplay.Providers.Replay;
using Xunit;

public class ReplayProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);
    private static readonly CircleArea Area = new(47.6, -122.3, 100.0);

    [Fact]
    public async Task Serves_frames_in_order()
    {
        var provider = Provider(
            Frame("aaa001"),
            Frame("bbb002"),
            Frame("ccc003"));

        Assert.Equal("aaa001", await FirstHexAsync(provider));
        Assert.Equal("bbb002", await FirstHexAsync(provider));
        Assert.Equal("ccc003", await FirstHexAsync(provider));
    }

    [Fact]
    public async Task Reports_exhausted_at_the_end_of_the_recording()
    {
        var provider = Provider(Frame("aaa001"));

        await provider.FetchAircraftAsync(Area, CancellationToken.None);
        var result = await provider.FetchAircraftAsync(Area, CancellationToken.None);

        var exhausted = Assert.IsType<ProviderResult.Exhausted>(result);
        Assert.Equal("test-recording", exhausted.RecordingName);
    }

    [Fact]
    public async Task An_empty_recording_is_immediately_exhausted()
    {
        var provider = Provider();

        var result = await provider.FetchAircraftAsync(Area, CancellationToken.None);

        Assert.IsType<ProviderResult.Exhausted>(result);
    }

    [Fact]
    public async Task Looping_wraps_instead_of_finishing()
    {
        var provider = new ReplayProvider(
            "test-recording",
            [Frame("aaa001"), Frame("bbb002")],
            loop: true,
            timeProvider: new FixedTime(Now));

        Assert.Equal("aaa001", await FirstHexAsync(provider));
        Assert.Equal("bbb002", await FirstHexAsync(provider));
        Assert.Equal("aaa001", await FirstHexAsync(provider));
    }

    [Fact]
    public async Task Reset_rewinds_to_the_first_frame()
    {
        var provider = Provider(Frame("aaa001"), Frame("bbb002"));

        await provider.FetchAircraftAsync(Area, CancellationToken.None);
        provider.Reset();

        Assert.Equal("aaa001", await FirstHexAsync(provider));
    }

    [Fact]
    public async Task Seek_jumps_to_a_frame()
    {
        var provider = Provider(Frame("aaa001"), Frame("bbb002"), Frame("ccc003"));

        provider.Seek(2);

        Assert.Equal("ccc003", await FirstHexAsync(provider));
    }

    [Fact]
    public void Seek_clamps_out_of_range_indices()
    {
        var provider = Provider(Frame("aaa001"), Frame("bbb002"));

        provider.Seek(-5);
        Assert.Equal(0, provider.Position);

        provider.Seek(99);
        Assert.Equal(2, provider.Position);
    }

    [Fact]
    public async Task A_recording_made_long_ago_does_not_play_back_as_stale()
    {
        // The whole point of rebasing timestamps. Without it, every replayed
        // frame would arrive already past the 60s staleness threshold and the
        // board would show nothing but stale warnings.
        var oldFrame = new ReplayFrame(
            Now.AddDays(-3),
            [Aircraft("aaa001", Now.AddDays(-3))]);

        var provider = Provider(oldFrame);

        var result = await provider.FetchAircraftAsync(Area, CancellationToken.None);

        var success = Assert.IsType<ProviderResult.Success>(result);
        Assert.False(Staleness.IsStale(success.Aircraft[0], Now));
    }

    [Fact]
    public async Task Relative_ages_within_a_frame_survive_rebasing()
    {
        // Per-aircraft staleness has to stay meaningful during playback: a
        // record that was 90s old when recorded must still read as stale.
        var frame = new ReplayFrame(
            Now.AddHours(-5),
            [
                Aircraft("fresh1", Now.AddHours(-5)),
                Aircraft("stale1", Now.AddHours(-5).AddSeconds(-90)),
            ]);

        var provider = Provider(frame);

        var result = await provider.FetchAircraftAsync(Area, CancellationToken.None);
        var success = Assert.IsType<ProviderResult.Success>(result);

        Assert.False(Staleness.IsStale(success.Aircraft[0], Now));
        Assert.True(Staleness.IsStale(success.Aircraft[1], Now));
    }

    [Fact]
    public void Frame_count_and_position_are_reported()
    {
        var provider = Provider(Frame("aaa001"), Frame("bbb002"));

        Assert.Equal(2, provider.FrameCount);
        Assert.Equal(0, provider.Position);
    }

    private static async Task<string> FirstHexAsync(ReplayProvider provider)
    {
        var result = await provider.FetchAircraftAsync(Area, CancellationToken.None);
        return Assert.IsType<ProviderResult.Success>(result).Aircraft[0].IcaoHex;
    }

    private static ReplayProvider Provider(params ReplayFrame[] frames)
        => new("test-recording", frames, loop: false, timeProvider: new FixedTime(Now));

    private static ReplayFrame Frame(string hex)
        => new(Now, [Aircraft(hex, Now)]);

    private static AircraftState Aircraft(string hex, DateTimeOffset positionAt) => new()
    {
        Provider = "replay",
        IcaoHex = hex,
        Latitude = 47.61,
        Longitude = -122.33,
        FirstSeen = positionAt,
        LastSeen = positionAt,
        PositionTimestamp = positionAt,
    };

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
