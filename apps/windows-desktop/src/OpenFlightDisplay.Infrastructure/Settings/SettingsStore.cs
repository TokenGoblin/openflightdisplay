namespace OpenFlightDisplay.Infrastructure.Settings;

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OpenFlightDisplay.Core.Settings;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON, atomically.
/// </summary>
/// <remarks>
/// <para>
/// Writes go to a temporary file in the same directory, are flushed to disk, and
/// are then moved into place. A crash or power loss can therefore lose the
/// <i>new</i> settings but can never corrupt the existing ones — the same
/// guarantee the firmware gets from its atomic LittleFS write, and the reason
/// the original project has a "no reboot loops from corrupt config" test.
/// </para>
/// <para>
/// A file that is unreadable or malformed falls back to defaults rather than
/// throwing. Settings being corrupt must not prevent the application starting;
/// the user can always reconfigure, but only if they can get in.
/// </para>
/// </remarks>
public sealed partial class SettingsStore : IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _filePath;
    private readonly ILogger<SettingsStore> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public SettingsStore(string filePath, ILogger<SettingsStore> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(logger);

        _filePath = filePath;
        _logger = logger;
    }

    /// <summary>Default settings location under the user's roaming profile.</summary>
    public static string DefaultFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OpenFlightDisplay",
        "settings.json");

    /// <summary>
    /// Reads settings from disk, falling back to defaults when absent or unreadable.
    /// </summary>
    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath))
        {
            LogNoSettingsFile(_logger, _filePath);
            return new AppSettings();
        }

        try
        {
            await using FileStream stream = File.OpenRead(_filePath);
            AppSettings? settings = await JsonSerializer
                .DeserializeAsync<AppSettings>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);

            if (settings is null)
            {
                // Valid JSON "null". Treated the same as a corrupt file.
                LogUnreadableSettings(_logger, null, _filePath);
                return new AppSettings();
            }

            if (settings.SchemaVersion > new AppSettings().SchemaVersion)
            {
                // Written by a newer build. Refuse to guess at a shape we do not
                // understand -- the same rule the wire protocol applies to an
                // unrecognised schemaVersion.
                LogFutureSchema(_logger, settings.SchemaVersion, _filePath);
                return new AppSettings();
            }

            return settings;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Corrupt or inaccessible settings must not stop the app starting.
            LogUnreadableSettings(_logger, ex, _filePath);
            return new AppSettings();
        }
    }

    /// <summary>Writes settings to disk atomically.</summary>
    /// <returns>True if the write succeeded.</returns>
    public async Task<bool> SaveAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // Serialised: two concurrent saves racing on the same temp path would
        // let one delete the other's file mid-move.
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string directory = Path.GetDirectoryName(_filePath)!;
            Directory.CreateDirectory(directory);

            // Temp file in the SAME directory: File.Move is only atomic within a
            // volume, and %TEMP% is frequently on a different one.
            string tempPath = Path.Combine(directory, $".{Path.GetFileName(_filePath)}.{Guid.NewGuid():N}.tmp");

            try
            {
                await using (FileStream stream = File.Create(tempPath))
                {
                    await JsonSerializer
                        .SerializeAsync(stream, settings, SerializerOptions, cancellationToken)
                        .ConfigureAwait(false);

                    // Force to disk before the move. Without this the rename can
                    // land ahead of the data and a power loss leaves a
                    // zero-length settings file - which is worse than the old one.
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(tempPath, _filePath, overwrite: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogSaveFailed(_logger, ex, _filePath);

                // Leaving a stray temp file behind would accumulate over time.
                TryDelete(tempPath);
                return false;
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _writeLock.Dispose();

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort. A leftover temp file is untidy, not harmful.
        }
    }

    [LoggerMessage(EventId = 3000, Level = LogLevel.Information,
        Message = "No settings file at {Path}; starting with defaults")]
    private static partial void LogNoSettingsFile(ILogger logger, string path);

    [LoggerMessage(EventId = 3001, Level = LogLevel.Warning,
        Message = "Settings at {Path} could not be read; falling back to defaults")]
    private static partial void LogUnreadableSettings(ILogger logger, Exception? ex, string path);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Warning,
        Message = "Settings at {Path} declare schema version {Version}, which is newer than this build understands; using defaults")]
    private static partial void LogFutureSchema(ILogger logger, int version, string path);

    [LoggerMessage(EventId = 3003, Level = LogLevel.Error,
        Message = "Failed to save settings to {Path}")]
    private static partial void LogSaveFailed(ILogger logger, Exception ex, string path);
}
