namespace OpenFlightDisplay.Providers.LocalReceiver;

using System.Text.Json;
using OpenFlightDisplay.Core.Aircraft;
using OpenFlightDisplay.Providers.Tar1090;

/// <summary>Result of reading a receiver's <c>aircraft.json</c>.</summary>
/// <param name="Aircraft">Aircraft the receiver is currently tracking.</param>
/// <param name="ReceiverTime">
/// The receiver's own clock at the moment it wrote the file, or <c>null</c> if
/// it did not report one.
/// </param>
/// <param name="TotalMessages">Messages the receiver has decoded, if reported.</param>
public readonly record struct LocalReceiverSnapshot(
    IReadOnlyList<AircraftState> Aircraft,
    DateTimeOffset? ReceiverTime,
    long? TotalMessages);

/// <summary>
/// Reads the <c>aircraft.json</c> served by dump1090, readsb and tar1090.
/// </summary>
/// <remarks>
/// <para>
/// The per-aircraft schema is identical to adsb.lol's, so the mapping is shared
/// via <see cref="Tar1090AircraftReader"/>. Two things differ and both matter:
/// the array lives under <c>aircraft</c> rather than <c>ac</c>, and the file
/// carries the receiver's own <c>now</c> timestamp.
/// </para>
/// <para>
/// <b>Ages are measured against the receiver's clock, not ours.</b> A receiver
/// that has died while its web server keeps serving the last file it wrote is
/// the characteristic local-receiver failure: the JSON parses perfectly, every
/// <c>seen_pos</c> is small, and the aircraft look live. Anchoring to the
/// receiver's <c>now</c> is what makes those positions age correctly instead of
/// being permanently reborn as fresh on every poll.
/// </para>
/// </remarks>
public static class LocalReceiverNormalizer
{
    /// <summary>
    /// Parses an <c>aircraft.json</c> body.
    /// </summary>
    /// <param name="fetchedAt">Our clock when the response arrived.</param>
    /// <exception cref="JsonException">The body is not valid JSON at all.</exception>
    public static LocalReceiverSnapshot Parse(
        string json,
        string providerId,
        DateTimeOffset fetchedAt)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            return new LocalReceiverSnapshot([], null, null);
        }

        DateTimeOffset? receiverTime = ReadReceiverTime(root);
        long? totalMessages = root.TryGetProperty("messages", out JsonElement messages)
            && messages.ValueKind == JsonValueKind.Number
            && messages.TryGetInt64(out long count)
                ? count
                : null;

        if (!root.TryGetProperty("aircraft", out JsonElement aircraft)
            || aircraft.ValueKind != JsonValueKind.Array)
        {
            return new LocalReceiverSnapshot([], receiverTime, totalMessages);
        }

        // Aircraft ages are relative to the receiver's own clock where it gave
        // one; falling back to ours only when it did not.
        DateTimeOffset anchor = receiverTime ?? fetchedAt;

        var results = new List<AircraftState>(aircraft.GetArrayLength());
        foreach (JsonElement element in aircraft.EnumerateArray())
        {
            if (Tar1090AircraftReader.Read(element, providerId, anchor) is { } state)
            {
                results.Add(state);
            }
        }

        return new LocalReceiverSnapshot(results, receiverTime, totalMessages);
    }

    /// <summary>
    /// Reads the receiver's <c>now</c>, a Unix timestamp in fractional seconds.
    /// </summary>
    /// <remarks>
    /// Rejects non-positive and non-finite values rather than turning them into
    /// a timestamp in 1970, which would mark every aircraft stale and report a
    /// working receiver as broken.
    /// </remarks>
    private static DateTimeOffset? ReadReceiverTime(JsonElement root)
    {
        if (!root.TryGetProperty("now", out JsonElement now)
            || now.ValueKind != JsonValueKind.Number
            || !now.TryGetDouble(out double unixSeconds)
            || double.IsNaN(unixSeconds)
            || double.IsInfinity(unixSeconds)
            || unixSeconds <= 0)
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeMilliseconds((long)(unixSeconds * 1000.0));
    }
}
