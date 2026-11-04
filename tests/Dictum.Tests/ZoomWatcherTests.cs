using Dictum.Meetings.Import;

namespace Dictum.Tests;

public sealed class ZoomWatcherTests
{
    [Fact]
    public void Watcher_raises_only_when_the_file_has_stopped_growing()
    {
        using var fixture = new MeetingFixture();
        Directory.CreateDirectory(fixture.Root);
        var clock = new FakeTimeProvider();
        var seen = new List<string>();
        using var watcher = new ZoomRecordingsWatcher(clock);
        watcher.Start(fixture.Root, seen.Add);
        var folder = Path.Combine(fixture.Root, "synthetic-session");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "audio1.m4a");
        File.WriteAllBytes(path, []);
        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.Empty(seen);
        File.WriteAllBytes(path, [1]);
        clock.Advance(TimeSpan.FromSeconds(9.75));
        Assert.Empty(seen); // Growth restarted the stability interval.
        using (var output = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            Assert.Empty(seen); // A stable size alone is insufficient while Zoom owns the file.
        }
        clock.Advance(TimeSpan.FromMilliseconds(250));
        Assert.Equal(new[] { path }, seen);
    }

    [Fact]
    public void Watcher_ignores_files_present_at_start()
    {
        using var fixture = new MeetingFixture();
        Directory.CreateDirectory(fixture.Root);
        var path = Path.Combine(fixture.Root, "video1.mp4");
        File.WriteAllBytes(path, []);
        var clock = new FakeTimeProvider();
        var seen = new List<string>();
        using (var watcher = new ZoomRecordingsWatcher(clock))
        {
            watcher.Start(fixture.Root, seen.Add);
            File.WriteAllBytes(path, [1, 2]);
            clock.Advance(TimeSpan.FromMinutes(1));
            Assert.Empty(seen);
        }
        using var importing = new ZoomRecordingsWatcher(clock) { ImportExisting = true };
        importing.Start(fixture.Root, seen.Add);
        clock.Advance(TimeSpan.FromSeconds(9.75));
        Assert.Empty(seen);
        clock.Advance(TimeSpan.FromMilliseconds(250));
        Assert.Equal(new[] { path }, seen);
    }

    [Fact]
    public void Watcher_raises_once_per_file()
    {
        using var fixture = new MeetingFixture();
        Directory.CreateDirectory(fixture.Root);
        var clock = new FakeTimeProvider();
        var seen = new List<string>();
        using var watcher = new ZoomRecordingsWatcher(clock);
        watcher.Start(fixture.Root, seen.Add);
        var first = Path.Combine(fixture.Root, "video1.MP4");
        var second = Path.Combine(fixture.Root, "audio1.m4a");
        File.WriteAllBytes(first, []);
        File.WriteAllBytes(second, []);
        File.WriteAllBytes(Path.Combine(fixture.Root, "ignored.wav"), MeetingFixture.Wav());
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(2, seen.Count);
        Assert.Contains(first, seen);
        Assert.Contains(second, seen);
        File.WriteAllBytes(first, [1]);
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(2, seen.Count);
        watcher.Dispose();
        File.WriteAllBytes(Path.Combine(fixture.Root, "video2.mp4"), []);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(2, seen.Count);
        Assert.Equal(0, clock.ArmedTimers);
    }

    [Fact]
    public void Default_zoom_folder_matches_the_documented_path()
    {
        // Check the path without enumerating or reading any recording outside this worktree.
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", "Zoom");
        Assert.Equal((OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) && Directory.Exists(expected)
            ? expected : null, ZoomFolders.Default());
    }

    [Fact]
    public void Watcher_accepts_an_injected_stability_window()
    {
        using var fixture = new MeetingFixture();
        Directory.CreateDirectory(fixture.Root);
        var clock = new FakeTimeProvider();
        var seen = new List<string>();
        using var watcher = new ZoomRecordingsWatcher(clock, TimeSpan.FromSeconds(2));
        watcher.Start(fixture.Root, seen.Add);
        File.WriteAllBytes(Path.Combine(fixture.Root, "video1.mp4"), []);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Empty(seen);
        clock.Advance(TimeSpan.FromMilliseconds(250));
        Assert.Single(seen);
    }
}
