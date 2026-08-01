<#
.SYNOPSIS
    Captures a window to a PNG in true physical pixels.

.DESCRIPTION
    Screenshotting a window on a scaled display is the one thing in this project
    that has already produced a wrong conclusion twice, so it lives in a script
    rather than being retyped each time.

    The trap: a DPI-*unaware* process sees a virtualised desktop. On a 150%
    display, GetWindowRect on a 1440x817 window returns 960x545 - the physical
    size divided by the scale factor. Capture that rect against the real screen
    and you crop the top-left two-thirds of the window. The result looks exactly
    like a UI rendering 1.5x too large and overflowing its window, which is what
    it was mistaken for. Nothing was wrong with the app.

    PowerShell is not per-monitor DPI aware, and its process-wide awareness
    cannot be changed once set. SetThreadDpiAwarenessContext can be, and applies
    to every user32 call this thread makes afterwards - so the rect and the
    capture agree, both in physical pixels.

    See the DPI section of docs/WINDOWS_DESKTOP.md.

.PARAMETER ProcessName
    Process whose main window is captured. Defaults to the desktop app.

.PARAMETER Path
    Output PNG path.

.PARAMETER Foreground
    Raise the window first. CopyFromScreen reads the actual screen, so anything
    overlapping the window is captured instead of it.

.EXAMPLE
    ./Capture-Window.ps1 -Path radar.png -Foreground
#>
[CmdletBinding()]
param(
    [string]$ProcessName = 'OpenFlightDisplay.App',
    [Parameter(Mandatory = $true)][string]$Path,
    [switch]$Foreground
)

$ErrorActionPreference = 'Stop'

Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class OfdWindowCapture
{
    // -4 = DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
    private static readonly IntPtr PerMonitorAwareV2 = new IntPtr(-4);

    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    public static string Capture(IntPtr hwnd, string path, bool foreground)
    {
        // Must come before any other user32 call on this thread.
        SetThreadDpiAwarenessContext(PerMonitorAwareV2);

        if (foreground)
        {
            ShowWindow(hwnd, 9); // SW_RESTORE
            SetForegroundWindow(hwnd);
            System.Threading.Thread.Sleep(1200);
        }

        RECT r;
        if (!GetWindowRect(hwnd, out r))
        {
            throw new InvalidOperationException("GetWindowRect failed.");
        }

        int width = r.Right - r.Left;
        int height = r.Bottom - r.Top;

        using (var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb))
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(
                r.Left, r.Top, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
            bitmap.Save(path, ImageFormat.Png);
        }

        uint dpi = GetDpiForWindow(hwnd);
        return string.Format(
            "{0}x{1} physical at {2} DPI (scale {3:N2})", width, height, dpi, dpi / 96.0);
    }
}
'@

$process = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue |
    Where-Object { $_.MainWindowHandle -ne 0 } |
    Select-Object -First 1

if (-not $process) {
    throw "No running '$ProcessName' process with a main window was found."
}

$full = [System.IO.Path]::GetFullPath($Path)
$result = [OfdWindowCapture]::Capture($process.MainWindowHandle, $full, $Foreground.IsPresent)

Write-Output "Captured $result"
Write-Output "Saved to $full"
