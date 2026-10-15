using System.Runtime.InteropServices;
using Dictum.Core;
using Dictum.Hotkey;
using Dictum.Insert;
using Dictum.Windows.Interop;

namespace Dictum.Windows.Input;

/// <summary>SendInput-based <see cref="IInputSender"/>.</summary>
public sealed class SendInputSender(ILog log) : IInputSender
{
    private const int TypeBatchChars = 32;
    private static readonly int[] Modifiers = [Vk.LControl, Vk.RControl, Vk.LShift, Vk.RShift, Vk.LMenu, Vk.RMenu, Vk.LWin, Vk.RWin];

    public void ReleaseModifiers()
    {
        var down = Modifiers.Where(Native.IsKeyDown).ToList();
        if (down.Count == 0) return;
        var inputs = new List<Native.INPUT>();
        if (down.Any(Vk.IsWinOrAlt))
        {
            // Mask first so releasing Win/Alt does not open Start or a menu bar.
            inputs.Add(Key(Vk.MenuMask, false));
            inputs.Add(Key(Vk.MenuMask, true));
        }
        inputs.AddRange(down.Select(vk => Key(vk, true)));
        Send(inputs, "release modifiers");
    }

    public void SendCtrlV() => Send([Key(Vk.Control, false), Key(Vk.V, false), Key(Vk.V, true), Key(Vk.Control, true)], "Ctrl+V");

    public async Task TypeTextAsync(string text)
    {
        var batch = new List<Native.INPUT>(TypeBatchChars * 2);
        var chars = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') continue;
            if (c is '\n' or '\r')
            {
                batch.Add(Key(Vk.Return, false));
                batch.Add(Key(Vk.Return, true));
            }
            else
            {
                batch.Add(Unicode(c, false));
                batch.Add(Unicode(c, true));
            }
            if (++chars % TypeBatchChars == 0)
            {
                Send(batch, "typing");
                batch.Clear();
                await Task.Delay(5); // lets slow apps drain their input queue
            }
        }
        if (batch.Count > 0) Send(batch, "typing");
    }

    private void Send(IList<Native.INPUT> inputs, string what)
    {
        var array = inputs.ToArray();
        var sent = Native.SendInput((uint)array.Length, array, Marshal.SizeOf<Native.INPUT>());
        if (sent != array.Length)
        {
            log.Warn($"SendInput ({what}) inserted {sent} of {array.Length} events (Win32 error {Marshal.GetLastWin32Error()}).");
        }
    }

    private static Native.INPUT Key(int vk, bool up)
    {
        var extended = vk is Vk.RControl or Vk.RMenu or Vk.LWin or Vk.RWin;
        return new Native.INPUT
        {
            type = Native.INPUT_KEYBOARD,
            U = new Native.InputUnion
            {
                ki = new Native.KEYBDINPUT
                {
                    wVk = (ushort)vk,
                    dwFlags = (up ? Native.KEYEVENTF_KEYUP : 0) | (extended ? Native.KEYEVENTF_EXTENDEDKEY : 0),
                    dwExtraInfo = Native.InjectedMarker,
                },
            },
        };
    }

    private static Native.INPUT Unicode(char c, bool up) => new()
    {
        type = Native.INPUT_KEYBOARD,
        U = new Native.InputUnion
        {
            ki = new Native.KEYBDINPUT
            {
                wScan = c,
                dwFlags = Native.KEYEVENTF_UNICODE | (up ? Native.KEYEVENTF_KEYUP : 0),
                dwExtraInfo = Native.InjectedMarker,
            },
        },
    };
}

