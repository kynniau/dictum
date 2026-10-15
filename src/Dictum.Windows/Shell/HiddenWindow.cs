using System.Runtime.InteropServices;
using Dictum.Windows.Interop;

namespace Dictum.Windows.Shell;

/// <summary>
/// A window nobody sees, created on the calling thread (the UI thread, whose message loop Avalonia runs, dispatches its
/// messages): the clipboard's owner and the tray icon's message target. Message-only windows do not receive broadcasts,
/// so the tray uses a hidden top-level one to hear "TaskbarCreated" when Explorer restarts.
/// </summary>
internal sealed class HiddenWindow : IDisposable
{
    private static int _count;
    private readonly NativeShell.WndProc _proc; // kept in a field: the window class holds only a raw pointer to it
    private readonly Func<uint, nint, nint, nint?> _onMessage;
    private readonly string _className;

    /// <param name="onMessage">Returns a result to stop default processing, or null to pass the message on.</param>
    public HiddenWindow(string name, bool messageOnly, Func<uint, nint, nint, nint?>? onMessage = null)
    {
        _onMessage = onMessage ?? ((_, _, _) => null);
        _proc = WindowProc;
        _className = $"Dictum.{name}.{Environment.ProcessId}.{Interlocked.Increment(ref _count)}";
        var instance = Native.GetModuleHandle(null);
        var cls = new NativeShell.WNDCLASSEXW
        {
            cbSize = Marshal.SizeOf<NativeShell.WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = instance,
            lpszClassName = _className,
        };
        if (NativeShell.RegisterClassEx(ref cls) == 0) throw new InvalidOperationException($"RegisterClassEx failed (Win32 error {Marshal.GetLastWin32Error()}).");
        Handle = NativeShell.CreateWindowEx(0, _className, $"Dictum {name}", messageOnly ? 0 : NativeShell.WS_POPUP, 0, 0, 0, 0,
            messageOnly ? NativeClipboard.HWND_MESSAGE : 0, 0, instance, 0);
        if (Handle == 0) throw new InvalidOperationException($"CreateWindowEx failed (Win32 error {Marshal.GetLastWin32Error()}).");
    }

    public nint Handle { get; private set; }

    private nint WindowProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            if (_onMessage(msg, wParam, lParam) is { } handled) return handled;
        }
        catch (Exception)
        {
            // Never let an exception cross back into the window manager.
        }
        return NativeShell.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (Handle == 0) return;
        NativeShell.DestroyWindow(Handle);
        Handle = 0;
        NativeShell.UnregisterClass(_className, Native.GetModuleHandle(null));
    }
}
