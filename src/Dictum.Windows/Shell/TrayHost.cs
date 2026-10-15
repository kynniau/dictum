using System.Runtime.InteropServices;
using Dictum.Core;
using Dictum.Platform;
using Dictum.Windows.Interop;
using Microsoft.Win32;

namespace Dictum.Windows.Shell;

/// <summary>
/// The notification-area icon (SPEC §8), with Shell_NotifyIcon directly: no WinForms. A click (or Enter/Space on the
/// keyboard, or the context-menu key) opens the tray panel; there is no native menu, the panel has everything. Uses
/// NOTIFYICON_VERSION_4, so the click position arrives with the message, and re-adds itself when Explorer restarts
/// ("TaskbarCreated"). The icon is redrawn when the Windows theme switches between a light and a dark taskbar.
/// </summary>
internal sealed class TrayHost : ITrayHost
{
    private const int IconId = 1;
    private const int CallbackMessage = Native.WM_APP + 16;
    private const uint WM_SETTINGCHANGE = 0x001A;
    private const int MaxTooltip = 127;

    private readonly Action<ScreenPoint?> _activated;
    private readonly ILog _log;
    private readonly uint _taskbarCreated;
    private readonly HiddenWindow _window;
    private nint _icon;
    private TrayState _state = TrayState.Idle;
    private string _tooltip = "Dictum";
    private long _lastActivation;

    public TrayHost(Action<ScreenPoint?> activated, ILog log)
    {
        _activated = activated;
        _log = log;
        _taskbarCreated = NativeShell.RegisterWindowMessage("TaskbarCreated");
        _window = new HiddenWindow("Tray", messageOnly: false, OnMessage);
        _icon = CreateIcon(_state);
        Add();
    }

    private nint? OnMessage(uint msg, nint wParam, nint lParam)
    {
        if (msg == CallbackMessage)
        {
            var notification = (int)(lParam & 0xFFFF);
            if (notification is NativeShell.WM_LBUTTONUP or NativeShell.NIN_SELECT or NativeShell.NIN_KEYSELECT or NativeShell.WM_CONTEXTMENU)
            {
                // GET_X_LPARAM / GET_Y_LPARAM of wParam: the anchor point, signed (monitors left of or above the primary).
                Activate(new ScreenPoint((short)(wParam & 0xFFFF), (short)((wParam >> 16) & 0xFFFF)));
            }
            return 0;
        }
        if (msg == _taskbarCreated)
        {
            Add(); // Explorer restarted: the icon must be added again
            return 0;
        }
        if (msg == WM_SETTINGCHANGE) Redraw(); // a light or dark taskbar
        return null;
    }

    /// <summary>One click sends both a mouse-up and NIN_SELECT; act once.</summary>
    private void Activate(ScreenPoint point)
    {
        var now = Environment.TickCount64;
        if (now - _lastActivation < 400) return;
        _lastActivation = now;
        _activated(point);
    }

    private NativeShell.NOTIFYICONDATAW Data(int flags) => new()
    {
        cbSize = Marshal.SizeOf<NativeShell.NOTIFYICONDATAW>(),
        hWnd = _window.Handle,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = _tooltip,
        szInfo = "",
        szInfoTitle = "",
    };

    private void Add()
    {
        var data = Data(NativeShell.NIF_MESSAGE | NativeShell.NIF_ICON | NativeShell.NIF_TIP | NativeShell.NIF_SHOWTIP);
        if (!NativeShell.Shell_NotifyIcon(NativeShell.NIM_ADD, ref data))
        {
            _log.Warn("The tray icon could not be added (Shell_NotifyIcon NIM_ADD failed); it is retried when Explorer restarts.");
            return;
        }
        data.uTimeoutOrVersion = NativeShell.NOTIFYICON_VERSION_4;
        NativeShell.Shell_NotifyIcon(NativeShell.NIM_SETVERSION, ref data);
        _log.Info("Tray icon added to the notification area.");
    }

    public void SetState(TrayState state)
    {
        if (state == _state) return;
        _state = state;
        Redraw();
    }

    private void Redraw()
    {
        var old = _icon;
        _icon = CreateIcon(_state);
        var data = Data(NativeShell.NIF_ICON);
        NativeShell.Shell_NotifyIcon(NativeShell.NIM_MODIFY, ref data);
        if (old != 0) NativeShell.DestroyIcon(old);
    }

    public void SetTooltip(string text)
    {
        _tooltip = text.Length <= MaxTooltip ? text : text[..(MaxTooltip - 1)] + "…";
        var data = Data(NativeShell.NIF_TIP | NativeShell.NIF_SHOWTIP);
        NativeShell.Shell_NotifyIcon(NativeShell.NIM_MODIFY, ref data);
    }

    public void Notify(string title, string text, bool warning)
    {
        var data = Data(NativeShell.NIF_INFO);
        data.szInfoTitle = title.Length < 64 ? title : title[..63];
        data.szInfo = text.Length < 256 ? text : text[..252] + "…";
        data.dwInfoFlags = warning ? NativeShell.NIIF_WARNING : NativeShell.NIIF_INFO;
        NativeShell.Shell_NotifyIcon(NativeShell.NIM_MODIFY, ref data);
    }

    private static nint CreateIcon(TrayState state)
    {
        var size = Math.Max(16, NativeShell.GetSystemMetrics(NativeShell.SM_CXSMICON));
        // Idle shows the app's own icon (Dictum.ico, also Dictum.exe's); the other states keep their drawn colours.
        if (state == TrayState.Idle && AppIcon(size) is var icon and not 0) return icon;
        var bits = IconBits.Resource(TrayIconArt.Render(state, size, LightTaskbar()), size);
        return NativeShell.CreateIconFromResourceEx(bits, (uint)bits.Length, true, 0x00030000, size, size, 0);
    }

    /// <summary>Dictum.ico beside the exe at <paramref name="size"/> px, or 0 when it is missing (the drawn idle icon is used then).</summary>
    internal static nint AppIcon(int size)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Dictum.ico");
        return File.Exists(path) ? NativeShell.LoadImage(0, path, NativeShell.IMAGE_ICON, size, size, NativeShell.LR_LOADFROMFILE) : 0;
    }

    /// <summary>
    /// HKCU\…\Themes\Personalize\SystemUsesLightTheme = 1 when the taskbar is light (value name recalled, not verified on
    /// Microsoft Learn); absent or unreadable counts as dark, the Windows 11 default.
    /// </summary>
    private static bool LightTaskbar()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int light && light == 1;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        var data = Data(0);
        NativeShell.Shell_NotifyIcon(NativeShell.NIM_DELETE, ref data);
        if (_icon != 0) NativeShell.DestroyIcon(_icon);
        _icon = 0;
        _window.Dispose();
    }
}

/// <summary>An icon resource (BITMAPINFOHEADER, 32-bit bottom-up colour rows, then a 1-bit AND mask) from BGRA pixels.</summary>
internal static class IconBits
{
    public static byte[] Resource(byte[] bgra, int size)
    {
        var maskRow = (size + 31) / 32 * 4;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(40);           // biSize
        writer.Write(size);         // biWidth
        writer.Write(size * 2);     // biHeight: colour plus mask
        writer.Write((short)1);     // biPlanes
        writer.Write((short)32);    // biBitCount
        writer.Write(0);            // biCompression = BI_RGB
        writer.Write(size * size * 4 + maskRow * size); // biSizeImage
        writer.Write(0L);           // biXPelsPerMeter, biYPelsPerMeter
        writer.Write(0L);           // biClrUsed, biClrImportant
        for (var row = size - 1; row >= 0; row--) writer.Write(bgra, row * size * 4, size * 4);
        writer.Write(new byte[maskRow * size]); // all zero: the alpha channel decides
        return stream.ToArray();
    }
}
