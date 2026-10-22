using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Dictum.Mac.Interop;
using Dictum.Platform;

namespace Dictum.Mac.Shell;

/// <summary>
/// The menu bar icon (SPEC §8): an NSStatusItem of Dictum's own, not Avalonia's TrayIcon, because Avalonia.Native 11.3.22
/// never raises TrayIcon.Clicked: its NSStatusItem has no target or action and IAvnTrayIcon has no click callback
/// (native/Avalonia.Native/src/OSX/trayicon.mm and src/Avalonia.Native/avn.idl at tag 11.3.22, read 23/10/2026), so a click
/// could only open a menu, and Dictum has no menu: the panel has everything. A left or right click opens the panel at the
/// pointer (NSEvent.mouseLocation, in the UI's coordinates), as on Windows. The icon is TrayIconArt's drawing for the
/// state, in colour (not a template image, so red and amber show), redrawn when the menu bar turns light or dark. Lives on
/// the UI thread, which on a Mac is the main thread AppKit needs: AppShell creates it and calls it there.
/// Selectors (macOS 27 SDK, read 23/10/2026): NSStatusBar.h systemStatusBar / statusItemWithLength: / removeStatusItem:
/// (:25-28); NSStatusItem.h button (:64); NSControl.h target (weak), action, sendActionOn: (:24-46); NSView.h toolTip
/// (:313); NSImage.h initWithData:, size (:122), template (:176); NSEvent.h mouseLocation (:527); NSAppearance.h
/// effectiveAppearance (:90), bestMatchFromAppearancesWithNames: (:56).
/// </summary>
internal sealed class TrayHost : ITrayHost
{
    /// <summary>
    /// The icon's side in points, sized as Avalonia sizes its own tray image (trayicon.mm SetIcon: floor of the menu font's
    /// point size × 1.333333), and drawn at twice that in pixels for Retina displays.
    /// </summary>
    internal static int IconPoints()
    {
        using var pool = ObjC.AutoreleasePool.Begin();
        var font = AppKit.SendDouble(ObjC.Class("NSFont"), ObjC.Sel("menuFontOfSize:"), 0);
        var points = font == 0 ? 0 : Math.Floor(ObjC.SendDouble(font, ObjC.Sel("pointSize")) * 1.333333);
        return points >= 12 ? (int)points : 18; // 18: the result for the 14 pt menu font, should the font not answer
    }

    private const int MaxTooltip = 127;
    private static TrayHost? s_current;
    private static readonly Lazy<nint> Target = new(MakeTarget);

    private readonly Action<ScreenPoint?> _activated;
    private readonly ILog _log;
    private readonly Notifications _notifications;
    private nint _item;
    private nint _button;
    private readonly int _iconPoints = IconPoints();
    private TrayState _state = TrayState.Idle;
    private bool? _lightDrawn;

    public TrayHost(Action<ScreenPoint?> activated, ILog log, AppBundle? bundle)
    {
        _activated = activated;
        _log = log;
        _notifications = new Notifications(log, bundle);
        using var pool = ObjC.AutoreleasePool.Begin();
        var bar = ObjC.Send(ObjC.Class("NSStatusBar"), ObjC.Sel("systemStatusBar"));
        _item = ObjC.Send(AppKit.SendDouble(bar, ObjC.Sel("statusItemWithLength:"), AppKit.SquareStatusItemLength), ObjC.Sel("retain"));
        _button = ObjC.Send(_item, ObjC.Sel("button"));
        if (_button == 0)
        {
            log.Warn("The menu bar icon could not be created (NSStatusItem has no button).");
            return;
        }
        s_current = this;
        ObjC.SendVoid(_button, ObjC.Sel("setTarget:"), Target.Value);
        ObjC.SendVoid(_button, ObjC.Sel("setAction:"), ObjC.Sel("dictumTrayClicked:"));
        ObjC.Send(_button, ObjC.Sel("sendActionOn:"), (nint)AppKit.LeftOrRightMouseUp);
        Redraw(force: true);
        SetTooltip("Dictum");
        if (Application.Current?.PlatformSettings is { } settings) settings.ColorValuesChanged += OnColoursChanged;
        log.Info("Menu bar icon added.");
    }

    private void OnColoursChanged(object? sender, PlatformColorValues e) => Redraw(force: false);

    public void SetState(TrayState state)
    {
        if (state == _state) return;
        _state = state;
        Redraw(force: true);
    }

    public void SetTooltip(string text)
    {
        if (_button == 0) return;
        var tip = text.Length <= MaxTooltip ? text : text[..(MaxTooltip - 1)] + "…";
        using var pool = ObjC.AutoreleasePool.Begin();
        ObjC.SendVoid(_button, ObjC.Sel("setToolTip:"), ObjC.NSString(tip));
    }

    public void Notify(string title, string text, bool warning) => _notifications.Show(title, text, warning);

    /// <summary>Draws the state's icon for the menu bar as it is now; skipped when only the appearance was asked about and it has not changed.</summary>
    private void Redraw(bool force)
    {
        if (_button == 0) return;
        using var pool = ObjC.AutoreleasePool.Begin();
        var light = MenuBarIsLight();
        if (!force && light == _lightDrawn) return;
        _lightDrawn = light;
        var png = Png(TrayIconArt.Render(_state, _iconPoints * 2, light), _iconPoints * 2);
        var image = Image(png, _iconPoints);
        if (image == 0)
        {
            _log.Warn("The menu bar icon could not be drawn (NSImage refused the PNG).");
            return;
        }
        ObjC.SendVoid(_button, ObjC.Sel("setImage:"), image);
        ObjC.Release(image);
    }

    /// <summary>An NSImage (owned) of <paramref name="png"/>, <paramref name="points"/> square, in colour.</summary>
    internal static unsafe nint Image(byte[] png, int points)
    {
        nint data;
        fixed (byte* bytes = png) data = ObjC.Send(ObjC.Class("NSData"), ObjC.Sel("dataWithBytes:length:"), (nint)bytes, png.Length);
        var image = ObjC.Send(ObjC.Send(ObjC.Class("NSImage"), ObjC.Sel("alloc")), ObjC.Sel("initWithData:"), data);
        if (image == 0) return 0;
        AppKit.SendSize(image, ObjC.Sel("setSize:"), new CGSize(points, points));
        ObjC.SendVoid(image, ObjC.Sel("setTemplate:"), false);
        return image;
    }

    /// <summary>TrayIconArt's straight-alpha BGRA pixels as PNG bytes, through Avalonia's Skia bitmap (the app's own renderer).</summary>
    internal static unsafe byte[] Png(byte[] bgra, int size)
    {
        fixed (byte* pixels = bgra)
        {
            using var bitmap = new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Unpremul, (nint)pixels, new PixelSize(size, size), new Vector(144, 144), size * 4);
            using var stream = new MemoryStream();
            bitmap.Save(stream);
            return stream.ToArray();
        }
    }

    /// <summary>
    /// Whether the menu bar behind the icon is light (dark bars) or dark (light bars): the status button's effective
    /// appearance, which follows the menu bar rather than only the system setting.
    /// </summary>
    private unsafe bool MenuBarIsLight()
    {
        var appearance = ObjC.Send(_button, ObjC.Sel("effectiveAppearance"));
        if (appearance == 0) return false;
        var names = stackalloc nint[] { AppearanceName("NSAppearanceNameAqua"), AppearanceName("NSAppearanceNameDarkAqua") };
        var array = ObjC.Send(ObjC.Class("NSArray"), ObjC.Sel("arrayWithObjects:count:"), (nint)names, 2);
        var best = ObjC.Send(appearance, ObjC.Sel("bestMatchFromAppearancesWithNames:"), array);
        return best != 0 && AppKit.SendBool(best, ObjC.Sel("isEqualToString:"), names[0]);
    }

    /// <summary>An exported NSAppearanceName constant (NSAppearance.h:62-64).</summary>
    private static nint AppearanceName(string symbol) =>
        Marshal.ReadIntPtr(NativeLibrary.GetExport(NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit"), symbol));

    private static unsafe nint MakeTarget()
    {
        var cls = ObjC.DefineClass("DictumTrayTarget", "NSObject", ("dictumTrayClicked:", (nint)(delegate* unmanaged<nint, nint, nint, void>)&OnClicked, "v@:@"));
        return ObjC.Send(ObjC.Send(cls, ObjC.Sel("alloc")), ObjC.Sel("init")); // owned for the process: the button's target is weak
    }

    /// <summary>The button's action, on the main thread: the pointer's position, in the UI's coordinates, to the shell.</summary>
    [UnmanagedCallersOnly]
    private static void OnClicked(nint self, nint sel, nint sender)
    {
        var tray = s_current;
        if (tray is null) return;
        try
        {
            ScreenPoint? point = null;
            using (ObjC.AutoreleasePool.Begin())
            {
                if (AppKit.PrimaryScreenTop() is { } top) point = ScreenSpace.FromAppKit(ObjC.SendPoint(ObjC.Class("NSEvent"), ObjC.Sel("mouseLocation")), top);
            }
            tray._activated(point);
        }
        catch (Exception ex)
        {
            tray._log.Warn($"The menu bar click failed: {ex.Message}"); // never let an exception cross back into AppKit
        }
    }

    public void Dispose()
    {
        if (Application.Current?.PlatformSettings is { } settings) settings.ColorValuesChanged -= OnColoursChanged;
        if (s_current == this) s_current = null;
        if (_item == 0) return;
        using var pool = ObjC.AutoreleasePool.Begin();
        ObjC.SendVoid(ObjC.Send(ObjC.Class("NSStatusBar"), ObjC.Sel("systemStatusBar")), ObjC.Sel("removeStatusItem:"), _item);
        ObjC.Release(_item);
        _item = 0;
        _button = 0;
    }
}
