namespace OpenFlightDisplay.App.Tests;

using OpenFlightDisplay.App.Services;
using OpenFlightDisplay.Core.Aircraft;
using Xunit;

/// <summary>
/// Fanning one poll out to history and to a session recording at once.
/// </summary>
public class ObservationRecorderTests
{
    [Fact]
    public void Every_recorder_receives_the_batch()
    {
        var a = new CountingRecorder();
        var b = new CountingRecorder();

        new CompositeObservationRecorder(a, b).Record([Aircraft()]);

        Assert.Equal(1, a.Batches);
        Assert.Equal(1, b.Batches);
    }

    [Fact]
    public void A_recorder_that_drops_does_not_stop_the_others()
    {
        // A full history queue must not silently cost you a replay recording,
        // which is often the thing being captured to reproduce a problem.
        var refusing = new RefusingRecorder();
        var working = new CountingRecorder();

        bool all = new CompositeObservationRecorder(refusing, working).Record([Aircraft()]);

        Assert.False(all);
        Assert.Equal(1, working.Batches);
    }

    [Fact]
    public void Success_is_reported_only_when_every_recorder_accepted()
    {
        Assert.True(new CompositeObservationRecorder(
            new CountingRecorder(), new CountingRecorder()).Record([Aircraft()]));

        Assert.False(new CompositeObservationRecorder(
            new CountingRecorder(), new RefusingRecorder()).Record([Aircraft()]));
    }

    [Fact]
    public void An_empty_composite_accepts_and_does_nothing()
        => Assert.True(new CompositeObservationRecorder().Record([Aircraft()]));

    [Fact]
    public void The_null_recorder_accepts_everything_without_storing_it()
    {
        // History off is a real implementation rather than a null check
        // scattered through the pipeline.
        Assert.True(NullObservationRecorder.Instance.Record([Aircraft()]));
        Assert.True(NullObservationRecorder.Instance.Record([]));
    }

    private static AircraftState Aircraft() => new()
    {
        Provider = "test",
        IcaoHex = "abc123",
        Latitude = 47.61,
        Longitude = -122.33,
        FirstSeen = DateTimeOffset.UnixEpoch,
        LastSeen = DateTimeOffset.UnixEpoch,
        PositionTimestamp = DateTimeOffset.UnixEpoch,
    };

    private sealed class CountingRecorder : IObservationRecorder
    {
        public int Batches { get; private set; }

        public bool Record(IReadOnlyList<AircraftState> aircraft)
        {
            Batches++;
            return true;
        }
    }

    private sealed class RefusingRecorder : IObservationRecorder
    {
        public bool Record(IReadOnlyList<AircraftState> aircraft) => false;
    }
}
