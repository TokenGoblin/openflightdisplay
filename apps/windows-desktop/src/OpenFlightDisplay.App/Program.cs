namespace OpenFlightDisplay.App;

using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;

/// <summary>
/// Application entry point, replacing the one WinUI would generate.
/// </summary>
/// <remarks>
/// <para>
/// A custom <c>Main</c> is required because single-instance redirection has to
/// happen <b>before</b> <see cref="Microsoft.UI.Xaml.Application.Start"/>. By the
/// time <c>App</c> is constructed the second process already exists and has
/// begun initialising; redirecting from there would still briefly open a second
/// database connection and a second poll loop.
/// </para>
/// <para>
/// Single instance matters here for a concrete reason, not tidiness: two copies
/// of the application write to the same history database in
/// <c>%APPDATA%\OpenFlightDisplay\history.db</c>. SQLite's WAL mode keeps that
/// from corrupting anything, but the result is one file interleaving
/// observations from two differently-configured sessions, which was observed in
/// practice.
/// </para>
/// </remarks>
public static class Program
{
    /// <summary>
    /// Key identifying this application's single instance.
    /// </summary>
    /// <remarks>
    /// A fixed string rather than a generated one, so every launch of the same
    /// build finds the same instance.
    /// </remarks>
    private const string InstanceKey = "OpenFlightDisplay.Desktop.MainInstance";

    [STAThread]
    public static int Main(string[] args)
    {
        if (RedirectToExistingInstance())
        {
            // Another instance owns the key and has been activated. Exit
            // quietly with success — being asked to start twice is not an error.
            return 0;
        }

        // The parameter is named rather than discarded: using `_` for both the
        // lambda parameter and the discard below makes `_ = new App()` assign to
        // the parameter instead of constructing the application.
        Microsoft.UI.Xaml.Application.Start(initParams =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());

            SynchronizationContext.SetSynchronizationContext(context);

            // App registers itself with the framework in its constructor; the
            // instance is deliberately not retained here.
            _ = new App();
        });

        return 0;
    }

    /// <summary>
    /// Hands activation to an already-running instance, if there is one.
    /// </summary>
    /// <returns>True if this process should exit immediately.</returns>
    private static bool RedirectToExistingInstance()
    {
        try
        {
            AppInstance instance = AppInstance.FindOrRegisterForKey(InstanceKey);

            if (instance.IsCurrent)
            {
                return false;
            }

            // RedirectActivationToAsync must not be awaited on this thread: it
            // completes only once the target instance has handled the
            // activation, and blocking the STA thread here deadlocks. Run it on
            // the thread pool and wait on the result instead.
            AppActivationArguments activation =
                AppInstance.GetCurrent().GetActivatedEventArgs();

            // AsTask because RedirectActivationToAsync returns an IAsyncAction,
            // which has no ConfigureAwait of its own.
            Task.Run(() => instance.RedirectActivationToAsync(activation).AsTask())
                .GetAwaiter()
                .GetResult();

            return true;
        }
#pragma warning disable CA1031 // Failing to redirect must not block startup.
        catch (Exception)
#pragma warning restore CA1031
        {
            // If the lifecycle API is unavailable — which happens in some
            // unpackaged and sandboxed configurations — start normally. Two
            // instances is a worse outcome than one, but no instance at all is
            // worse than both.
            return false;
        }
    }
}
