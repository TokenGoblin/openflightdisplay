namespace OpenFlightDisplay.App.Services;

using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using OpenFlightDisplay.Core.Alerts;

/// <summary>Delivers alert events to the user.</summary>
public interface IAlertNotifier
{
    /// <summary>Shows an alert. Must not throw.</summary>
    void Notify(AlertEvent alertEvent);
}

/// <summary>Delivers nothing. Used when notifications are switched off.</summary>
public sealed class NullAlertNotifier : IAlertNotifier
{
    public static NullAlertNotifier Instance { get; } = new();

    private NullAlertNotifier()
    {
    }

    /// <inheritdoc/>
    public void Notify(AlertEvent alertEvent)
    {
    }
}

/// <summary>
/// Delivers alerts as Windows toast notifications.
/// </summary>
/// <remarks>
/// <para>
/// Registration is required for an unpackaged app and is done once at
/// construction. If it fails — which it does on some machines and in some
/// sandboxes — the notifier degrades to doing nothing rather than throwing, and
/// the in-app alert list still shows everything. A missing toast should not
/// cost the user the alert itself.
/// </para>
/// <para>
/// Rate limiting and deduplication happen upstream in
/// <see cref="AlertEvaluator"/>. This class deliberately holds no suppression
/// logic of its own, so there is exactly one place where the question "should
/// this alert fire?" is answered.
/// </para>
/// </remarks>
public sealed partial class ToastAlertNotifier : IAlertNotifier, IDisposable
{
    private readonly ILogger<ToastAlertNotifier> _logger;
    private readonly bool _registered;

    public ToastAlertNotifier(ILogger<ToastAlertNotifier> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;

        try
        {
            AppNotificationManager.Default.Register();
            _registered = true;
        }
#pragma warning disable CA1031 // Notifications are optional; the app is not.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogRegistrationFailed(_logger, ex);
            _registered = false;
        }
    }

    /// <summary>True if Windows accepted the registration.</summary>
    public bool IsAvailable => _registered;

    /// <inheritdoc/>
    public void Notify(AlertEvent alertEvent)
    {
        ArgumentNullException.ThrowIfNull(alertEvent);

        if (!_registered || !alertEvent.Channels.HasFlag(AlertChannels.Toast))
        {
            return;
        }

        try
        {
            // Values are escaped because callsigns come from a provider and are
            // not trusted: the toast payload is XML, and an unescaped '&' or '<'
            // in a callsign would produce a malformed notification that silently
            // fails to appear.
            AppNotification notification = new AppNotificationBuilder()
                .AddText(WebUtility.HtmlEncode(alertEvent.RuleName))
                .AddText(WebUtility.HtmlEncode(alertEvent.Message))
                .BuildNotification();

            AppNotificationManager.Default.Show(notification);
        }
#pragma warning disable CA1031 // A failed toast must not break the feed.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogShowFailed(_logger, ex);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!_registered)
        {
            return;
        }

        try
        {
            AppNotificationManager.Default.Unregister();
        }
#pragma warning disable CA1031 // Shutdown must not throw on the way out.
        catch (Exception)
#pragma warning restore CA1031
        {
            // Nothing useful to do at shutdown.
        }
    }

    [LoggerMessage(
        EventId = 6000,
        Level = LogLevel.Warning,
        Message = "Could not register for Windows notifications; alerts will only appear in-app")]
    private static partial void LogRegistrationFailed(ILogger logger, Exception ex);

    [LoggerMessage(
        EventId = 6001,
        Level = LogLevel.Warning,
        Message = "Failed to show a Windows notification")]
    private static partial void LogShowFailed(ILogger logger, Exception ex);
}
