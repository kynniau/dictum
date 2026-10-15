using System.Diagnostics;
using Dictum.Platform;
using Dictum.Windows.Interop;

namespace Dictum.Windows.Shell;

/// <summary>
/// The overlay windows' Win32 styling (SPEC §7, §21): WS_EX_NOACTIVATE (never the foreground window, so the paste never
/// lands in it), WS_EX_TRANSPARENT with WS_EX_LAYERED (clicks pass through to the window below; a layered window needs
/// its alpha set once to be drawn at all), WS_EX_TOOLWINDOW (no taskbar button, not in Alt+Tab) and WS_EX_TOPMOST.
/// Avalonia recomputes a window's extended style whenever it shows the window, so the UI hands <see cref="OverlayStyles"/>
/// to Avalonia's style callback and the flags survive every show.
/// </summary>
internal sealed class WindowFlags : IWindowFlags
{
    private const uint OverlayExStyle = Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST;

    public (uint Style, uint ExStyle) OverlayStyles(uint style, uint exStyle) => (style, (exStyle | OverlayExStyle) & ~Native.WS_EX_APPWINDOW);

    public void MakeOverlay(nint handle)
    {
        var ex = (uint)Native.GetWindowLongPtr(handle, Native.GWL_EXSTYLE);
        Native.SetWindowLongPtr(handle, Native.GWL_EXSTYLE, (nint)OverlayStyles(0, ex).ExStyle);
        Native.SetLayeredWindowAttributes(handle, 0, 255, Native.LWA_ALPHA);
    }

    public void Move(nint handle, int x, int y) =>
        Native.SetWindowPos(handle, 0, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);

    public nint ForegroundWindow() => Native.GetForegroundWindow();

    public ScreenRect? ForegroundBounds()
    {
        var window = Native.GetForegroundWindow();
        if (window == 0 || !Native.GetWindowRect(window, out var r)) return null;
        return new ScreenRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    /// <summary>SPI_GETCLIENTAREAANIMATION (0x1042, Microsoft Learn): false means the user turned animation effects off.</summary>
    public bool ReducedMotion => Native.SystemParametersInfo(Native.SPI_GETCLIENTAREAANIMATION, 0, out var animations, 0) && !animations;

    public string? AppName(nint window)
    {
        if (window == 0) return null;
        try
        {
            Native.GetWindowThreadProcessId(window, out var pid);
            using var process = Process.GetProcessById((int)pid);
            var description = process.MainModule?.FileVersionInfo.FileDescription;
            return string.IsNullOrWhiteSpace(description) ? process.ProcessName : description.Trim();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null; // gone, or an elevated process whose module this one may not read
        }
    }

    public bool IsWindow(nint handle) => handle != 0 && Native.IsWindow(handle);

    public bool TryActivate(nint handle)
    {
        if (!IsWindow(handle)) return false;
        Native.SetForegroundWindow(handle);
        return Native.GetForegroundWindow() == handle;
    }
}
