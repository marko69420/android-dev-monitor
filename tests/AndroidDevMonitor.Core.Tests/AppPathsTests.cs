using AndroidDevMonitor.Core.Configuration;

namespace AndroidDevMonitor.Core.Tests;

public sealed class AppPathsTests
{
    [Fact]
    public void Paths_are_always_absolute_even_when_the_folders_do_not_exist()
    {
        Assert.True(Path.IsPathRooted(AppPaths.HomeDirectory));
        Assert.True(Path.IsPathRooted(AppPaths.LocalDataRoot));
        Assert.True(Path.IsPathRooted(AppPaths.DocumentsDirectory));
        Assert.True(Path.IsPathRooted(AppPaths.ExportsDirectory));
        Assert.EndsWith(Path.Combine("Android Dev Monitor", "Exports"), AppPaths.ExportsDirectory);
    }

    [Fact]
    public void Linux_data_lives_under_local_share_by_default()
    {
        if (!OperatingSystem.IsLinux() || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("XDG_DATA_HOME"))) return;
        Assert.Equal(Path.Combine(AppPaths.HomeDirectory, ".local", "share"), AppPaths.LocalDataRoot);
    }
}
