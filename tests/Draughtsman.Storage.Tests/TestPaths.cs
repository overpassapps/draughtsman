namespace Draughtsman.Storage.Tests;

internal static class TestPaths
{
    public static string NewTempFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "draughtsman-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    public static void DeleteTempFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort: a lingering handle on Windows should not fail the test that already passed.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
