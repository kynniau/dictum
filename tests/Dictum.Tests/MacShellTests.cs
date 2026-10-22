using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Dictum.Audio;
using Dictum.Mac.Interop;
using Dictum.Mac.Shell;
using Dictum.Platform;
using Dictum.Storage;

namespace Dictum.Tests;

/// <summary>
/// The macOS shell (src/Dictum.Mac/Shell): login item, single instance, window flags, menu bar icon, notifications,
/// removal and sound cues. The pure parts run on every OS; the parts that call macOS run only on a Mac; the ones that make
/// a sound or change which app is active also need DICTUM_UI_TESTS=1. The class is marked macOS-only for the platform
/// analyser (Dictum.Mac is a macOS assembly); the pure tests in it still run, and pass, everywhere.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacShellTests
{
    private const string OnMac = "Calls macOS.";

    // ---- Sound cues -------------------------------------------------------------------------------------------------

    /// <summary>
    /// The tones moved from the Windows build's SoundCues to Core unchanged: these hashes and the length are those of the
    /// original SoundCues.Wav(…) bytes, computed from that code before the move (23/10/2026).
    /// </summary>
    [Fact]
    public void Cue_tones_are_the_windows_bytes_unchanged()
    {
        var start = CueTones.Wav(CueTones.Notes(SoundCue.Start));
        var stop = CueTones.Wav(CueTones.Notes(SoundCue.Stop));
        Assert.Equal(5336, start.Length);
        Assert.Equal("0747C441369B10C2E96F694252381E1266176FF15193C756E316EB24A40191B1", Convert.ToHexString(SHA256.HashData(start)));
        Assert.Equal("B41ECD302167A91F21A22A7F1C87A9C560B547A6AF647299C3246B3328D3671D", Convert.ToHexString(SHA256.HashData(stop)));
    }

    [Fact]
    public void Each_cue_is_an_nssound_macos_can_read()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), OnMac);
        foreach (var cue in new[] { SoundCue.Start, SoundCue.Stop })
        {
            var sound = SoundCues.Sound(cue);
            Assert.NotEqual(0, sound);
            Assert.InRange(ObjC.SendDouble(sound, ObjC.Sel("duration")), 0.11, 0.13); // two 60 ms notes
        }
    }

    [Fact]
    public void Playing_a_cue_returns_at_once()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), OnMac);
        Assert.SkipUnless(TestEnv.Get("UI_TESTS") == "1", "Set DICTUM_UI_TESTS=1 (plays the two cues out loud).");
        var clock = Stopwatch.StartNew();
        SoundCues.Play(SoundCue.Start);
        SoundCues.Play(SoundCue.Stop); // cuts off the first
        Assert.True(clock.ElapsedMilliseconds < 100, $"Play blocked for {clock.ElapsedMilliseconds} ms");
        Thread.Sleep(300);
    }

    // ---- Bundle and removal -----------------------------------------------------------------------------------------

    [Fact]
    public void The_bundle_is_found_only_from_its_contents_macos_folder()
    {
        var root = Directory.CreateTempSubdirectory("dms-");
        try
        {
            var macos = Path.Combine(root.FullName, "Dictum.app", "Contents", "MacOS");
            Assert.Equal(Path.Combine(root.FullName, "Dictum.app"), AppBundle.Containing(macos)?.Path);
            Assert.Equal(Path.Combine(root.FullName, "Dictum.app"), AppBundle.Containing(macos + Path.DirectorySeparatorChar)?.Path);
            Assert.Null(AppBundle.Containing(Path.Combine(root.FullName, "bin", "Release")));
            Assert.Null(AppBundle.Containing(Path.Combine(root.FullName, "Dictum", "Contents", "MacOS"))); // not a .app
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void Only_a_bundle_whose_plist_names_dictum_is_the_installed_copy()
    {
        using var fake = new FakeBundle(AppBundle.Identifier);
        Assert.Equal(AppBundle.Identifier, RemovalSteps.BundleIdentifier(fake.InfoPlist));
        Assert.Null(fake.Steps().ProgramFolderProblem());
        Assert.Equal("the login item", fake.Steps().StartupItemsPhrase); // the Storage page's "Remove everything" sentence on a Mac (PROPOSED)

        using var other = new FakeBundle("com.example.other");
        Assert.Equal(RemovalSteps.NotInstalledCopy, other.Steps().ProgramFolderProblem());

        File.WriteAllBytes(other.InfoPlist, "bplist00Ñ\u0001\u0002"u8.ToArray()); // binary plists are not read: unverified
        Assert.Null(RemovalSteps.BundleIdentifier(other.InfoPlist));
        File.Delete(other.InfoPlist);
        Assert.Null(RemovalSteps.BundleIdentifier(other.InfoPlist));

        var noBundle = new RemovalSteps(NullLog.Instance, null, "/tmp/build", () => new FakeStartup());
        Assert.Equal(RemovalSteps.NotInstalledCopy, noBundle.ProgramFolderProblem());
        Assert.False(noBundle.ScheduleProgramFolderRemoval("/tmp/build", out var reason));
        Assert.Equal(RemovalSteps.NotInstalledCopy, reason);
    }

    [Fact]
    public void A_translocated_copy_is_never_moved()
    {
        var bundle = new AppBundle("/private/var/folders/xy/abc/T/AppTranslocation/0123-4567/d/Dictum.app");
        var steps = new RemovalSteps(NullLog.Instance, bundle, Path.Combine(bundle.Path, "Contents", "MacOS"), () => new FakeStartup());
        Assert.Equal(RemovalSteps.NotInstalledCopy, steps.ProgramFolderProblem()); // no Info.plist there at all

        using var fake = new FakeBundle(AppBundle.Identifier, "AppTranslocation/0123");
        Assert.Equal(RemovalSteps.Translocated, fake.Steps().ProgramFolderProblem());
    }

    [Fact]
    public void Removal_moves_only_this_bundle_and_passes_paths_through_the_environment()
    {
        using var fake = new FakeBundle(AppBundle.Identifier);
        var started = new List<ProcessStartInfo>();
        var steps = fake.Steps(started.Add);

        Assert.False(steps.ScheduleProgramFolderRemoval(Path.GetTempPath(), out var reason));
        Assert.Equal(RemovalSteps.NotInstalledCopy, reason);
        Assert.Empty(started);

        Assert.True(steps.ScheduleProgramFolderRemoval(fake.MacOS, out reason)); // what AppShell passes: AppPaths.ExeDir
        Assert.Null(reason);
        Assert.True(steps.ScheduleProgramFolderRemoval(fake.Bundle, out _));
        Assert.Equal(2, started.Count);
        var start = started[0];
        Assert.Equal("/bin/sh", start.FileName);
        Assert.Equal(["-c", RemovalSteps.Script], start.ArgumentList);
        Assert.False(start.UseShellExecute);
        Assert.Equal(fake.Bundle, start.Environment[RemovalSteps.BundleVariable]);
        Assert.Equal("4242", start.Environment[RemovalSteps.WaitVariable]);
        Assert.Equal(fake.Trash, start.Environment[RemovalSteps.TrashVariable]);
        Assert.DoesNotContain(fake.Bundle, RemovalSteps.Script, StringComparison.Ordinal);
        Assert.DoesNotContain("Dictum.app", RemovalSteps.Script, StringComparison.Ordinal);

        // A copy running from elsewhere never moves the bundle it was handed.
        var stranger = new RemovalSteps(NullLog.Instance, new AppBundle(fake.Bundle), "/tmp/other/MacOS", () => new FakeStartup(), started.Add, 4242, fake.Trash);
        Assert.False(stranger.ScheduleProgramFolderRemoval(fake.MacOS, out _));
    }

    [Fact]
    public void Removal_lists_the_login_item_and_the_bundle_and_unregisters_the_login_item()
    {
        using var fake = new FakeBundle(AppBundle.Identifier);
        var startup = new FakeStartup { Enabled = true };
        var steps = fake.Steps(startup: startup);
        var locations = steps.PlatformLocations();
        Assert.Contains(locations, l => l is { Category: DataCategory.StartupEntry, Path: RemovalSteps.LoginItemPath });
        var program = Assert.Single(locations, l => l.Category == DataCategory.ProgramFolder);
        Assert.Equal(fake.Bundle, program.Path);
        Assert.True(program.IsFolder);

        Assert.Empty(steps.RemoveShortcutAndStartup());
        Assert.Equal([false], startup.Requests);
        startup.Problem = "denied";
        Assert.Equal(["launch at login: denied"], steps.RemoveShortcutAndStartup());

        var noBundle = new RemovalSteps(NullLog.Instance, null, "/tmp/build", () => startup);
        Assert.DoesNotContain(noBundle.PlatformLocations(), l => l.Category == DataCategory.ProgramFolder);
        Assert.Empty(noBundle.RemoveShortcutAndStartup());
        Assert.Equal(2, startup.Requests.Count); // nothing asked without a bundle
    }

    [Fact]
    public void The_removal_script_moves_the_bundle_to_the_trash_after_the_process_ends()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Runs /bin/sh.");
        using var fake = new FakeBundle(AppBundle.Identifier);
        using var sleeper = Process.Start(new ProcessStartInfo("/bin/sleep", "1") { UseShellExecute = false })!;
        var clock = Stopwatch.StartNew();
        Assert.Equal(0, Run(fake.Steps(waitFor: sleeper.Id).RemovalStart()));
        Assert.True(clock.ElapsedMilliseconds >= 700, $"the step did not wait for the process ({clock.ElapsedMilliseconds} ms)");
        Assert.False(Directory.Exists(fake.Bundle));
        Assert.True(File.Exists(Path.Combine(fake.Trash, "Dictum.app", "Contents", "Info.plist")));

        // A second Dictum.app gets a dated name rather than replacing the one already in the Trash.
        fake.Recreate();
        Assert.Equal(0, Run(fake.Steps(waitFor: 0).RemovalStart()));
        Assert.False(Directory.Exists(fake.Bundle));
        Assert.Single(Directory.GetDirectories(fake.Trash, "Dictum 20*.app"));

        // The script checks its inputs again: a path that is not a .app is refused.
        fake.Recreate();
        var start = fake.Steps(waitFor: 0).RemovalStart();
        start.Environment[RemovalSteps.BundleVariable] = Path.GetDirectoryName(fake.Bundle);
        Assert.Equal(2, Run(start));
        Assert.True(Directory.Exists(fake.Bundle));
    }

    private static int Run(ProcessStartInfo start)
    {
        using var process = Process.Start(start)!;
        Assert.True(process.WaitForExit(15000), "the removal step did not finish");
        return process.ExitCode;
    }

    // ---- Login item -------------------------------------------------------------------------------------------------

    [Fact]
    public void Login_item_results_become_the_right_sentence()
    {
        Assert.Null(LoginItem.Problem(true, true, 0, null, ServiceManagement.Enabled));
        Assert.Null(LoginItem.Problem(true, false, ServiceManagement.ErrorAlreadyRegistered, "already", ServiceManagement.Enabled));
        Assert.Equal(LoginItem.NeedsApproval, LoginItem.Problem(true, true, 0, null, ServiceManagement.RequiresApproval));
        Assert.Equal(LoginItem.NeedsApproval, LoginItem.Problem(true, false, ServiceManagement.ErrorLaunchDeniedByUser, "denied", ServiceManagement.NotRegistered));
        Assert.Equal(LoginItem.NeedsApproval, LoginItem.Problem(true, false, ServiceManagement.ErrorAlreadyRegistered, "already", ServiceManagement.RequiresApproval));
        Assert.Equal("macOS did not turn on launch at login: Invalid signature.", LoginItem.Problem(true, false, 3, "Invalid signature.", ServiceManagement.NotRegistered));
        Assert.Equal("macOS did not turn on launch at login: status 3", LoginItem.Problem(true, true, 0, null, ServiceManagement.NotFound));
        Assert.Null(LoginItem.Problem(false, true, 0, null, ServiceManagement.NotRegistered));
        Assert.Null(LoginItem.Problem(false, false, ServiceManagement.ErrorJobNotFound, "not found", ServiceManagement.NotRegistered));
        Assert.Equal("macOS did not turn off launch at login: Operation not permitted", LoginItem.Problem(false, false, 4, "Operation not permitted", ServiceManagement.Enabled));
    }

    [Fact]
    public async Task Outside_a_bundle_launch_at_login_is_off_and_explains_why()
    {
        var item = new LoginItem(NullLog.Instance, null);
        Assert.False(item.IsEnabled());
        Assert.Equal(LoginItem.NeedsBundle, await item.SetEnabledAsync(true));
        Assert.Null(await item.SetEnabledAsync(false));
        Assert.StartsWith("macOS login item", item.Mechanism, StringComparison.Ordinal);
    }

    /// <summary>Reads SMAppService's status for this test host (never registers: that would add a real login item).</summary>
    [Fact]
    public void The_login_item_status_is_readable()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), OnMac);
        using var pool = ObjC.AutoreleasePool.Begin();
        var status = ServiceManagement.Status(ServiceManagement.MainAppService());
        Assert.InRange(status, ServiceManagement.NotRegistered, ServiceManagement.NotFound);
        Assert.Equal(status == ServiceManagement.Enabled, new LoginItem(NullLog.Instance, new AppBundle("/Applications/Dictum.app")).IsEnabled());
    }

    // ---- Single instance --------------------------------------------------------------------------------------------

    [Fact]
    public void A_file_opened_with_fileshare_none_cannot_be_opened_again_in_the_same_process()
    {
        var root = Directory.CreateTempSubdirectory("dms-");
        try
        {
            var path = Path.Combine(root.FullName, SingleInstance.LockFileName);
            using var first = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            Assert.Throws<IOException>(() => new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void A_second_launch_signals_the_running_copy_or_times_out_when_nothing_listens()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix domain socket files; the class only runs on macOS.");
        var root = Directory.CreateTempSubdirectory("dms-");
        var lockFile = Path.Combine(root.FullName, SingleInstance.LockFileName);
        var socketFile = Path.Combine(root.FullName, SingleInstance.SocketFileName);
        try
        {
            using var first = SingleInstance.Acquire(lockFile, socketFile, TimeSpan.Zero);
            Assert.Equal(InstanceOutcome.Owned, first.Outcome);

            // Held, nobody listening yet (still starting): the bounded wait runs out.
            var clock = Stopwatch.StartNew();
            using (var early = SingleInstance.Acquire(lockFile, socketFile, TimeSpan.FromMilliseconds(600)))
            {
                Assert.Equal(InstanceOutcome.TimedOut, early.Outcome);
                Assert.InRange(clock.ElapsedMilliseconds, 500, 5000);
            }

            File.WriteAllText(socketFile, "stale"); // left by a copy that crashed: removed before binding
            using var signals = new SemaphoreSlim(0);
            using (first.ListenForOtherLaunches(() => signals.Release()))
            {
                using (var second = SingleInstance.Acquire(lockFile, socketFile, TimeSpan.FromSeconds(5)))
                    Assert.Equal(InstanceOutcome.SignalledRunningCopy, second.Outcome);
                Assert.True(signals.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken), "the running copy was not signalled");
                using (var third = SingleInstance.Acquire(lockFile, socketFile, TimeSpan.Zero))
                    Assert.Equal(InstanceOutcome.SignalledRunningCopy, third.Outcome);
                Assert.True(signals.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken), "the second signal was not delivered");
            }
            Assert.False(File.Exists(socketFile)); // the listener removes its socket when it stops
            Assert.False(SingleInstance.TrySignal(socketFile));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>
    /// A socket path the system refuses (over 104 bytes on macOS: a long home path) never throws out of AppShell.Start: the
    /// bind failure is a warning in the log, the lock is still held, and the returned handle disposes quietly (lens on PR #67).
    /// Observed failing before the catch: ArgumentOutOfRangeException from UnixDomainSocketEndPoint left the first launch dead.
    /// </summary>
    [Fact]
    public void A_socket_path_the_system_refuses_is_logged_and_the_launch_goes_on_without_a_listener()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix domain socket files; the class only runs on macOS.");
        var root = Directory.CreateTempSubdirectory("dms-");
        try
        {
            var deep = Path.Combine(root.FullName, new string('a', 120)); // well past sun_path's 104 bytes with the socket name
            Directory.CreateDirectory(deep);
            var log = new RecordingLog();
            using var first = SingleInstance.Acquire(Path.Combine(root.FullName, SingleInstance.LockFileName), Path.Combine(deep, SingleInstance.SocketFileName), TimeSpan.Zero, log);
            Assert.Equal(InstanceOutcome.Owned, first.Outcome);
            using (var handle = first.ListenForOtherLaunches(() => { }))
                Assert.NotNull(handle);
            var warning = Assert.Single(log.Lines, l => l.Level == LogLevel.Warning);
            Assert.Contains("cannot listen for a second launch", warning.Message);
            Assert.Contains("wait 30 s", warning.Message);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void The_lock_passes_to_the_next_launch_when_the_running_copy_quits()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix domain socket files; the class only runs on macOS.");
        var root = Directory.CreateTempSubdirectory("dms-");
        var lockFile = Path.Combine(root.FullName, "sub", SingleInstance.LockFileName); // the folder is made when missing
        var socketFile = Path.Combine(root.FullName, "sub", SingleInstance.SocketFileName);
        try
        {
            var first = SingleInstance.Acquire(lockFile, socketFile, TimeSpan.Zero);
            Assert.Equal(InstanceOutcome.Owned, first.Outcome);
            _ = Task.Delay(400, TestContext.Current.CancellationToken).ContinueWith(_ => first.Dispose(), TaskScheduler.Default); // quitting
            using var next = SingleInstance.Acquire(lockFile, socketFile, TimeSpan.FromSeconds(5));
            Assert.Equal(InstanceOutcome.Owned, next.Outcome);
            Assert.True(File.Exists(lockFile)); // never deleted
            using var late = SingleInstance.Acquire(lockFile, socketFile, TimeSpan.Zero);
            Assert.Equal(InstanceOutcome.TimedOut, late.Outcome);
            Assert.Throws<InvalidOperationException>(() => late.ListenForOtherLaunches(() => { }));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    // ---- Window flags and screen coordinates ------------------------------------------------------------------------

    [Fact]
    public void AppKit_points_flip_into_the_ui_coordinates_and_back()
    {
        Assert.Equal(new ScreenPoint(100, 30), ScreenSpace.FromAppKit(new CGPoint(100.4, 1087.6), 1117.5));
        var appKit = ScreenSpace.ToAppKit(100, 30, 1117.5);
        Assert.Equal(100, appKit.X);
        Assert.Equal(1087.5, appKit.Y);
        Assert.Equal(new ScreenPoint(-1440, -200), ScreenSpace.FromAppKit(new CGPoint(-1440, 1317.5), 1117.5)); // a display left of and above the primary
        Assert.Equal(new ScreenRect(10, 25, 800, 600), ScreenSpace.FromCoreGraphics(new CGRect(10.2, 25, 799.6, 600)));
        Assert.Equal((7u, 9u), new WindowFlags().OverlayStyles(7, 9));
        Assert.Equal((nuint)0x151, WindowFlags.OverlayBehaviour); // 1 | 16 | 64 | 256, NSWindow.h:130-141
    }

    [Fact]
    public void The_frontmost_app_stands_in_for_the_foreground_window()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), OnMac);
        var flags = new WindowFlags();
        Assert.False(flags.IsWindow(0));
        Assert.False(flags.IsWindow(int.MaxValue));
        Assert.Null(flags.AppName(0));
        Assert.False(flags.TryActivate(0));
        _ = flags.ReducedMotion; // answers without throwing; the value is the user's setting
        var pid = flags.ForegroundWindow();
        Assert.True(pid >= 0);
        if (pid == 0) return; // no app is frontmost (a session without a desktop)
        Assert.True(flags.IsWindow(pid));
        Assert.False(string.IsNullOrWhiteSpace(flags.AppName(pid)));
        if (flags.ForegroundBounds() is { } bounds) Assert.True(bounds.Width > 0 && bounds.Height > 0, bounds.ToString());
        Assert.NotNull(AppKit.PrimaryScreenTop());
    }

    [Fact]
    public void Handing_the_foreground_back_is_accepted_by_macos()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), OnMac);
        Assert.SkipUnless(TestEnv.Get("UI_TESTS") == "1", "Set DICTUM_UI_TESTS=1 (asks macOS to activate the frontmost app).");
        var flags = new WindowFlags();
        var pid = flags.ForegroundWindow();
        Assert.SkipWhen(pid == 0, "No app is frontmost.");
        Assert.True(flags.TryActivate(pid));
    }

    /// <summary>Every Objective-C class and selector the shell sends exists on this Mac: a misspelt selector fails here, not in a tester's hands.</summary>
    [Fact]
    public void Every_class_and_selector_the_shell_uses_exists()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), OnMac);
        _ = ServiceManagement.MainAppService(); // loads the frameworks the classes live in
        NativeLibrary.Load("/System/Library/Frameworks/UserNotifications.framework/UserNotifications"); // its center is never asked for outside a bundle
        using var pool = ObjC.AutoreleasePool.Begin();
        var instance = new Dictionary<string, string[]>
        {
            ["NSWindow"] = ["setLevel:", "setIgnoresMouseEvents:", "setCollectionBehavior:", "setHidesOnDeactivate:", "setFrameTopLeftPoint:", "frame"],
            ["NSScreen"] = ["frame"],
            ["NSWorkspace"] = ["frontmostApplication", "accessibilityDisplayShouldReduceMotion"],
            ["NSRunningApplication"] = ["processIdentifier", "localizedName", "isTerminated", "activateWithOptions:"],
            ["NSStatusBar"] = ["statusItemWithLength:", "removeStatusItem:"],
            ["NSStatusItem"] = ["button"],
            ["NSStatusBarButton"] = ["setTarget:", "setAction:", "sendActionOn:", "setToolTip:", "setImage:", "effectiveAppearance"],
            ["NSImage"] = ["initWithData:", "setSize:", "setTemplate:"],
            ["NSAppearance"] = ["bestMatchFromAppearancesWithNames:"],
            ["NSSound"] = ["initWithData:", "play", "stop", "duration"],
            ["NSFont"] = ["pointSize"],
            ["SMAppService"] = ["registerAndReturnError:", "unregisterAndReturnError:", "status"],
            ["UNUserNotificationCenter"] = ["setDelegate:", "requestAuthorizationWithOptions:completionHandler:", "addNotificationRequest:withCompletionHandler:"],
            ["UNMutableNotificationContent"] = ["setTitle:", "setBody:"],
        };
        var classMethods = new Dictionary<string, string[]>
        {
            ["NSStatusBar"] = ["systemStatusBar"],
            ["NSWorkspace"] = ["sharedWorkspace"],
            ["NSRunningApplication"] = ["runningApplicationWithProcessIdentifier:"],
            ["NSScreen"] = ["screens"],
            ["NSEvent"] = ["mouseLocation"],
            ["NSFont"] = ["menuFontOfSize:"],
            ["NSData"] = ["dataWithBytes:length:"],
            ["NSArray"] = ["arrayWithObjects:count:"],
            ["SMAppService"] = ["mainAppService", "openSystemSettingsLoginItems"],
            ["UNUserNotificationCenter"] = ["currentNotificationCenter"],
            ["UNNotificationRequest"] = ["requestWithIdentifier:content:trigger:"],
        };
        var missing = new List<string>();
        foreach (var (cls, selectors) in instance)
        {
            foreach (var selector in selectors)
                if (!AppKit.SendBool(ObjC.Class(cls), ObjC.Sel("instancesRespondToSelector:"), ObjC.Sel(selector))) missing.Add($"-[{cls} {selector}]");
        }
        foreach (var (cls, selectors) in classMethods)
        {
            foreach (var selector in selectors)
                if (!AppKit.SendBool(ObjC.Class(cls), ObjC.Sel("respondsToSelector:"), ObjC.Sel(selector))) missing.Add($"+[{cls} {selector}]");
        }
        Assert.Empty(missing);
        Assert.InRange(TrayHost.IconPoints(), 12, 40);
    }

    // ---- Menu bar icon and notifications ----------------------------------------------------------------------------

    [Fact]
    public async Task The_tray_drawing_becomes_a_png_macos_can_show()
    {
        var png = await UiSession.Run(() => TrayHost.Png(TrayIconArt.Render(TrayState.Recording, 36, lightTaskbar: false), 36));
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], png.Take(4));
        if (!OperatingSystem.IsMacOS()) return;
        using var pool = ObjC.AutoreleasePool.Begin();
        var image = TrayHost.Image(png, 18);
        Assert.NotEqual(0, image);
        var size = ObjC.SendPoint(image, ObjC.Sel("size")); // NSSize: two doubles, as NSPoint
        Assert.Equal(18, size.X);
        Assert.Equal(18, size.Y);
        ObjC.Release(image);
    }

    [Fact]
    public void A_notice_outside_the_bundle_goes_to_the_log_only()
    {
        var log = new RecordingLog();
        new Notifications(log, null).Show("Dictum", " The model could not load. ", warning: false);
        var line = Assert.Single(log.Lines);
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Contains("Dictum: The model could not load.", line.Message, StringComparison.Ordinal);
        Assert.Equal(("Dictum", "Body", "au.kynni.dictum.notice"), Notifications.Compose("  ", "Body"));
        Assert.Equal(("Title", "Body", Notifications.RequestIdentifier), Notifications.Compose(" Title ", "Body\n"));
    }

    // ---- Blocks -----------------------------------------------------------------------------------------------------

    [Fact]
    public void A_block_literal_has_the_clang_abi_layout()
    {
        Assert.Equal(16, (int)Marshal.OffsetOf<Blocks.Literal>(nameof(Blocks.Literal.Invoke)));
        Assert.Equal(24, (int)Marshal.OffsetOf<Blocks.Literal>(nameof(Blocks.Literal.Descriptor)));
        Assert.Equal(32, Marshal.SizeOf<Blocks.Literal>());
        Assert.Equal(16, Marshal.SizeOf<Blocks.Descriptor>());
        Assert.Equal(1 << 28, Blocks.IsGlobal);
    }

    [Fact]
    public void The_runtime_treats_a_hand_made_block_as_global_and_calls_it()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), OnMac);
        (nint, byte, nint) lastCall = default;
        BlockBody body = (b, flag, value) => lastCall = (b, flag, value); // a delegate here: this test project does not compile unsafe code
        var block = Blocks.Global(Marshal.GetFunctionPointerForDelegate(body));
        Assert.Equal(block, BlockCopy(block)); // copying a global block returns it unchanged (Block ABI)
        BlockRelease(block); // and releasing it does nothing
        Marshal.GetDelegateForFunctionPointer<BlockBody>(Blocks.InvokeOf(block))(block, 1, 77);
        Assert.Equal((block, (byte)1, (nint)77), lastCall);
        GC.KeepAlive(body);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void BlockBody(nint block, byte flag, nint value);

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "_Block_copy")] private static extern nint BlockCopy(nint block);
    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "_Block_release")] private static extern void BlockRelease(nint block);

    // ---- Fakes ------------------------------------------------------------------------------------------------------

    private sealed class FakeStartup : IStartupRegistration
    {
        public bool Enabled { get; set; }
        public string? Problem { get; set; }
        public List<bool> Requests { get; } = [];
        public string Mechanism => "test";
        public bool IsEnabled() => Enabled;

        public Task<string?> SetEnabledAsync(bool enabled)
        {
            Requests.Add(enabled);
            return Task.FromResult(Problem);
        }
    }

    /// <summary>A Dictum.app in a scratch folder (Contents/MacOS, an XML Info.plist) and a scratch Trash beside it.</summary>
    private sealed class FakeBundle : IDisposable
    {
        private readonly DirectoryInfo _root;
        private readonly string _identifier;

        public FakeBundle(string identifier, string? subfolder = null)
        {
            _root = Directory.CreateTempSubdirectory("dms-");
            _identifier = identifier;
            // A subfolder is joined with "/" on every OS (Windows accepts it): RemovalSteps looks for "/AppTranslocation/" as macOS writes it.
            Bundle = subfolder is null ? Path.Combine(_root.FullName, "Dictum.app") : $"{_root.FullName}/{subfolder}/Dictum.app";
            Trash = Path.Combine(_root.FullName, "Trash");
            Recreate();
        }

        public string Bundle { get; }
        public string Trash { get; }
        public string MacOS => Path.Combine(Bundle, "Contents", "MacOS");
        public string InfoPlist => Path.Combine(Bundle, "Contents", "Info.plist");

        public void Recreate()
        {
            Directory.CreateDirectory(MacOS);
            File.WriteAllText(InfoPlist, $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0">
                <dict>
                  <key>CFBundleName</key>
                  <string>Dictum</string>
                  <key>CFBundleIdentifier</key>
                  <string>{_identifier}</string>
                </dict>
                </plist>
                """);
        }

        public RemovalSteps Steps(Action<ProcessStartInfo>? launch = null, IStartupRegistration? startup = null, int waitFor = 4242) =>
            new(NullLog.Instance, new AppBundle(Bundle), MacOS, () => startup ?? new FakeStartup(), launch, waitFor, Trash);

        public void Dispose() => _root.Delete(recursive: true);
    }
}
