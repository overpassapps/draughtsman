namespace Draughtsman.Tests.Shared;

/// <summary>Where the committed upgrade fixtures are (tests/upgrade-fixtures/README.md), found from the repository root so any test project can read them.</summary>
internal static class UpgradeFixturePaths
{
    public static string RepositoryRoot
    {
        get
        {
            string? folder = AppContext.BaseDirectory;
            while (folder is not null && !File.Exists(Path.Combine(folder, "Draughtsman.sln")))
            {
                folder = Path.GetDirectoryName(folder);
            }

            return folder ?? throw new InvalidOperationException("Draughtsman.sln was not found above " + AppContext.BaseDirectory);
        }
    }

    public static string Root => Path.Combine(RepositoryRoot, "tests", "upgrade-fixtures");
}
