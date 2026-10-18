using Draughtsman.Server.Cli;
using Draughtsman.Server.Configuration;

namespace Draughtsman.Server.Tests;

public class AppLayoutTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "draughtsman-tests", Guid.NewGuid().ToString("N"));

    public AppLayoutTests()
    {
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        Directory.Delete(_folder, recursive: true);
    }

    private void MakePackaged()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "wwwroot"));
        File.WriteAllText(Path.Combine(_folder, "wwwroot", "index.html"), "<html></html>");
    }

    [Fact]
    public void A_folder_with_a_built_spa_beside_the_binary_is_a_packaged_install()
    {
        Assert.False(AppLayout.IsPackaged(_folder));
        MakePackaged();
        Assert.True(AppLayout.IsPackaged(_folder));
    }

    [Fact]
    public void A_packaged_install_anchors_the_content_root_on_its_own_folder()
    {
        Assert.Null(AppLayout.ContentRoot(_folder));
        MakePackaged();
        Assert.Equal(Path.GetFullPath(_folder), AppLayout.ContentRoot(_folder));
    }

    [Fact]
    public void The_default_data_folder_is_beside_a_packaged_install_not_in_the_working_directory()
    {
        MakePackaged();

        string folder = ConfigLoader.ResolveDataFolder(environmentOverride: string.Empty, baseDirectory: _folder);

        Assert.Equal(Path.Combine(Path.GetFullPath(_folder), "data"), folder);
        Assert.NotEqual(Path.GetFullPath("./data"), folder);
    }

    [Fact]
    public void Outside_a_packaged_install_the_default_data_folder_stays_relative_to_the_working_directory()
    {
        string folder = ConfigLoader.ResolveDataFolder(environmentOverride: string.Empty, baseDirectory: _folder);

        Assert.Equal(Path.GetFullPath("./data"), folder);
    }

    [Fact]
    public void An_explicit_data_folder_wins_over_the_packaged_default()
    {
        MakePackaged();
        string chosen = Path.Combine(Path.GetTempPath(), "draughtsman-tests", "chosen");

        Assert.Equal(Path.GetFullPath(chosen), ConfigLoader.ResolveDataFolder(environmentOverride: chosen, baseDirectory: _folder));
    }
}
