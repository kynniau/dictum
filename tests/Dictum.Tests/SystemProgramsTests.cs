using Dictum.Windows.Shell;

namespace Dictum.Tests;

/// <summary>Windows' own programs are found through the system directory Windows reports, not through a variable a parent process can set.</summary>
public sealed class SystemProgramsTests
{
    [Fact]
    public void The_Windows_folder_ignores_a_SystemRoot_variable_set_in_the_process()
    {
        TestEnv.WindowsOnly("Finds Windows' own programs through the Windows system directory.");
        var expected = Path.GetDirectoryName(Environment.SystemDirectory);
        var original = Environment.GetEnvironmentVariable("SystemRoot");
        try
        {
            Environment.SetEnvironmentVariable("SystemRoot", Path.Combine(Path.GetTempPath(), "fakewin"));
            Assert.Equal(expected, SystemPrograms.Root);
            Assert.Equal(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                SystemPrograms.WindowsPowerShell(SystemPrograms.Root), ignoreCase: true);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SystemRoot", original);
        }
    }
}
