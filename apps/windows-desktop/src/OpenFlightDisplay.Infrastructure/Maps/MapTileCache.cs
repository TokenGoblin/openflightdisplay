namespace OpenFlightDisplay.Infrastructure.Maps;

using System.Globalization;
using Microsoft.Extensions.Logging;
using OpenFlightDisplay.Core.Geo;

/// <summary>
/// Fetches and caches OpenStreetMap raster tiles for the radar backdrop.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is someone else's donated bandwidth.</b> OpenStreetMap's tile usage
/// policy is a condition of use, not advice, and every constraint below exists
/// to honour it: an identifying User-Agent, cache-first with a long local
/// retention, no more than a couple of requests in flight, a zoom ceiling set in
/// <see cref="SlippyMap.MaxZoom"/>, and a hard cap on tiles per draw applied by
/// the caller. There is no pre-fetching and no bulk download.
/// </para>
/// <para>
/// Attribution is <b>not</b> optional and is not handled here — the radar shows
/// it whenever the backdrop is on. See <c>docs/ATTRIBUTION.md</c>.
/// </para>
/// <para>
/// A tile that cannot be fetched is simply absent. The radar draws its rings and
/// symbols regardless, so losing the network degrades the backdrop rather than
/// the instrument.
/// </para>
/// </remarks>
public sealed partial class MapTileCache : IDisposable
{
    /// <summary>
    /// OpenStreetMap's standard raster tile endpoint.
    /// </summary>
    /// <remarks>
    /// No subdomain rotation: it is deprecated for this service and would only
    /// serve to work around the connection limit that exists on purpose.
    /// </remarks>
    public const string OpenStreetMapUrlTemplate = "https://tile.openstreetmap.org/{z}/{x}/{y}.png";

    /// <summary>
    /// How long a cached tile is reused before being refetched.
    /// </summary>
    /// <remarks>
    /// Thirty days. Coastlines and roads do not move quickly, and a shorter
    /// window would mean re-requesting the same imagery for no benefit.
    /// </remarks>
    public static readonly TimeSpan MaxCacheAge = TimeSpan.FromDays(30);

    /// <summary>
    /// Requests allowed in flight at once.
    /// </summary>
    /// <remarks>
    /// Two. The policy asks for no heavy parallelism, and a radar backdrop is
    /// never urgent — tiles appearing over a second or two is imperceptible next
    /// to a poll interval measured in seconds.
    /// </remarks>
    private const int MaxConcurrentFetches = 2;

    private readonly HttpClient _httpClient;
    private readonly ILogger<MapTileCache> _logger;
    private readonly string _cacheDirectory;
    private readonly SemaphoreSlim _throttle = new(MaxConcurrentFetches, MaxConcurrentFetches);

    /// <summary>Tiles already known to be unavailable, so they are asked for once.</summary>
    private readonly HashSet<TileId> _failed = [];

    public MapTileCache(HttpClient httpClient, ILogger<MapTileCache> logger, string? cacheDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClient;
        _logger = logger;
        _cacheDirectory = cacheDirectory ?? DefaultCacheDirectory;
    }

    /// <summary>
    /// Where tiles are cached.
    /// </summary>
    /// <remarks>
    /// <c>LocalApplicationData</c>, not <c>ApplicationData</c>: cached imagery is
    /// disposable, machine-specific and potentially large, and has no business
    /// following a roaming profile around.
    /// </remarks>
    public static string DefaultCacheDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenFlightDisplay",
        "tiles");

    /// <summary>Tiles served from disk without a request.</summary>
    public long CacheHits { get; private set; }

    /// <summary>Tiles fetched over the network.</summary>
    public long Fetched { get; private set; }

    /// <summary>Local path for a tile, fetching it if necessary.</summary>
    /// <returns>The path, or <c>null</c> if the tile is unavailable.</returns>
    public async Task<string?> GetTileAsync(TileId tile, CancellationToken cancellationToken)
    {
        string path = PathFor(tile);

        if (File.Exists(path))
        {
            var age = DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(path);
            if (age < MaxCacheAge)
            {
                CacheHits++;
                return path;
            }
        }

        lock (_failed)
        {
            // Asked for once. A tile that 404s at the edge of coverage would
            // otherwise be re-requested on every single redraw.
            if (_failed.Contains(tile))
            {
                return File.Exists(path) ? path : null;
            }
        }

        await _throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await FetchAsync(tile, path, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _throttle.Release();
        }
    }

    /// <summary>Deletes every cached tile.</summary>
    /// <returns>Bytes reclaimed.</returns>
    public long Clear()
    {
        long bytes = 0;

        try
        {
            if (!Directory.Exists(_cacheDirectory))
            {
                return 0;
            }

            foreach (string file in Directory.EnumerateFiles(_cacheDirectory, "*.png", SearchOption.AllDirectories))
            {
                bytes += new FileInfo(file).Length;
            }

            Directory.Delete(_cacheDirectory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogCacheClearFailed(_logger, ex);
        }

        lock (_failed)
        {
            _failed.Clear();
        }

        return bytes;
    }

    /// <summary>Bytes currently held on disk.</summary>
    public long CacheBytes()
    {
        try
        {
            return Directory.Exists(_cacheDirectory)
                ? Directory.EnumerateFiles(_cacheDirectory, "*.png", SearchOption.AllDirectories)
                    .Sum(f => new FileInfo(f).Length)
                : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _throttle.Dispose();

    private string PathFor(TileId tile) => Path.Combine(
        _cacheDirectory,
        tile.Zoom.ToString(CultureInfo.InvariantCulture),
        tile.X.ToString(CultureInfo.InvariantCulture),
        $"{tile.Y.ToString(CultureInfo.InvariantCulture)}.png");

    private async Task<string?> FetchAsync(TileId tile, string path, CancellationToken cancellationToken)
    {
        string url = OpenStreetMapUrlTemplate
            .Replace("{z}", tile.Zoom.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{x}", tile.X.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{y}", tile.Y.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

        try
        {
            using HttpResponseMessage response =
                await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                lock (_failed)
                {
                    _failed.Add(tile);
                }

                LogTileUnavailable(_logger, tile.Zoom, tile.X, tile.Y, (int)response.StatusCode);

                // A stale cached copy beats a hole in the backdrop.
                return File.Exists(path) ? path : null;
            }

            byte[] bytes = await response.Content
                .ReadAsByteArrayAsync(cancellationToken)
                .ConfigureAwait(false);

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // Written to a temporary name and moved, so a torn write cannot
            // leave a half-decoded image that the UI then tries to render.
            string temp = path + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);

            Fetched++;
            return path;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            // Offline, or a slow disk. The backdrop degrades; the radar does not.
            LogTileFetchFailed(_logger, ex, tile.Zoom, tile.X, tile.Y);
            return File.Exists(path) ? path : null;
        }
    }

    [LoggerMessage(
        EventId = 6000,
        Level = LogLevel.Debug,
        Message = "Map tile {Zoom}/{X}/{Y} unavailable (HTTP {Status}); it will not be requested again")]
    private static partial void LogTileUnavailable(ILogger logger, int zoom, int x, int y, int status);

    [LoggerMessage(
        EventId = 6001,
        Level = LogLevel.Debug,
        Message = "Map tile {Zoom}/{X}/{Y} could not be fetched; the backdrop will have a gap")]
    private static partial void LogTileFetchFailed(ILogger logger, Exception ex, int zoom, int x, int y);

    [LoggerMessage(
        EventId = 6002,
        Level = LogLevel.Warning,
        Message = "The map tile cache could not be cleared")]
    private static partial void LogCacheClearFailed(ILogger logger, Exception ex);
}
