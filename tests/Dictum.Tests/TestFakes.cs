using System.Collections.Concurrent;
using Dictum.Audio;
using Dictum.Core;
using Dictum.Transcribe;
using Dictum.Windows.Shell;

namespace Dictum.Tests;

/// <summary>Returns fixed text; optionally runs a hook (e.g. cancel) or waits on a gate first.</summary>
internal sealed class FakeTranscriber(string text, Action? onTranscribe = null, Task? gate = null) : ITranscriber
{
    public int Calls { get; private set; }

    public async Task<string> TranscribeAsync(float[] samples, CancellationToken cancellationToken)
    {
        Calls++;
        onTranscribe?.Invoke();
        if (gate is not null) await gate.WaitAsync(cancellationToken);
        return text;
    }
}

/// <summary>Keeps every log line in memory.</summary>
internal sealed class RecordingLog : ILog
{
    public ConcurrentQueue<(LogLevel Level, string Message)> Lines { get; } = new();

    public void Write(LogLevel level, string message) => Lines.Enqueue((level, message));

    public string All => string.Join("\n", Lines.Select(l => l.Message));
}

/// <summary>Records pill and tray updates from the controller.</summary>
internal sealed class FakeUi : IDictationUi
{
    public ConcurrentQueue<PillView> Pills { get; } = new();

    public ConcurrentQueue<PillState> States { get; } = new();

    public void ShowPill(PillState state)
    {
        States.Enqueue(state);
        Pills.Enqueue(state.View);
    }

    public void SetTrayState(TrayState state) { }

    /// <summary>Each "No sound from mic" balloon the controller asked for.</summary>
    public ConcurrentQueue<NoSoundCause> NoSoundWarnings { get; } = new();

    /// <summary>The tray panel's heard-nothing line, as last set (null: never set).</summary>
    public bool? HeardNothing { get; private set; }

    public void WarnNoSound(NoSoundCause cause) => NoSoundWarnings.Enqueue(cause);

    public void SetHeardNothing(bool heardNothing) => HeardNothing = heardNothing;
}

/// <summary>Microphone stand-in: Stop returns two seconds of "speech".</summary>
internal sealed class FakeCapture : IAudioCapture
{
    public float CurrentLevel => 0f;

    public event Action<Exception>? Failed;

    public void Start(string? deviceSetting, long keyDownTickMs) { }

    public CapturedAudio Stop() => new(new float[32000], 0.2f);

    public void RaiseFailed() => Failed?.Invoke(new InvalidOperationException("test"));
}

/// <summary>The test gates: DICTUM_* variables, with the earlier DICTATE_* names still accepted so existing scripts keep working.</summary>
internal static class TestEnv
{
    public static string? Get(string name) => Value("DICTUM_" + name) ?? Value("DICTATE_" + name);

    /// <summary>Skips a test that needs a Windows-only mechanism (named in <paramref name="reason"/>); a no-op on Windows.</summary>
    public static void WindowsOnly(string reason) => Assert.SkipUnless(OperatingSystem.IsWindows(), reason);

    /// <summary>Why a test that holds a file open to make it undeletable runs only on Windows.</summary>
    public const string HeldOpen = "Holds a file open so it cannot be deleted, a Windows sharing rule; elsewhere an open file can still be deleted.";

    private static string? Value(string variable) => Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value ? value : null;
}

/// <summary>
/// Absolute paths for tests of platform-neutral logic written with Windows paths. On Windows every path is returned exactly as
/// written, so those tests run as before; elsewhere a drive-letter path (X:\a\b) becomes the absolute path /X/a/b, so the logic
/// under test sees an absolute path there too.
/// </summary>
internal static class TestPaths
{
    /// <summary>Why a case that needs a share (\\server\share) or a device path (\\?\...) runs only on Windows.</summary>
    public const string WindowsForm = "Uses a \\\\server\\share or \\\\?\\ device path, path forms only Windows has.";

    public static string Native(string windowsPath) =>
        OperatingSystem.IsWindows() || windowsPath.Length < 3 || windowsPath[1] != ':' || windowsPath[2] != '\\'
            ? windowsPath
            : "/" + windowsPath[0] + windowsPath[2..].Replace('\\', '/');

    /// <summary><paramref name="json"/> with <paramref name="windowsPath"/>, as a JSON string escapes it, made <see cref="Native(string)"/>.</summary>
    public static string InJson(string json, string windowsPath) =>
        json.Replace(windowsPath.Replace("\\", "\\\\"), Native(windowsPath).Replace("\\", "\\\\"), StringComparison.Ordinal);
}

/// <summary>The HKCU Run key in memory: launch at login and the removal steps read and write this instead of the registry.</summary>
internal sealed class FakeRunKey : IRunKey
{
    public Dictionary<string, string> Values { get; } = [];
    public string? Get(string name) => Values.GetValueOrDefault(name);
    public void Set(string name, string value) => Values[name] = value;
    public void Delete(string name) => Values.Remove(name);
}

/// <summary>Dictum's Settings &gt; Apps entry in memory: no values means no entry.</summary>
internal sealed class FakeSetupRecord : ISetupRecord
{
    public Dictionary<string, string> Values { get; } = [];
    public string? Get(string name) => Values.GetValueOrDefault(name);
}
