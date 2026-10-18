using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Draughtsman.Core.Tests;

/// <summary>
/// The Dockerfile's publish stage copies only some repo folders, but the server's projects embed files from outside their
/// own folder (docs/schema, docs/spec/dsl.grammar, themes/). A file missing from those COPY lines fails only inside
/// `docker build`, which CI and verify-local never run, so this holds the two lists together: every file a reachable
/// project embeds must be copied, and every COPY source must exist.
/// </summary>
public sealed class DockerfileBuildContextTests
{
    private static readonly string s_root = FindRepoRoot();

    [Fact]
    public void PublishStage_CopiesEveryFileTheServerProjectsEmbed()
    {
        IReadOnlyList<string> copied = PublishStageCopySources(File.ReadAllText(Path.Combine(s_root, "Dockerfile")));

        List<string> missing = EmbeddedFilesOf("src/Draughtsman.Server/Draughtsman.Server.csproj")
            .Where(file => !IsCovered(file, copied))
            .ToList();

        Assert.True(missing.Count == 0, "Dockerfile publish stage does not COPY: " + string.Join(", ", missing));
    }

    [Fact]
    public void PublishStage_CopiesOnlyPathsThatExist()
    {
        IReadOnlyList<string> copied = PublishStageCopySources(File.ReadAllText(Path.Combine(s_root, "Dockerfile")));

        List<string> absent = copied
            .Where(source => !File.Exists(Path.Combine(s_root, source)) && !Directory.Exists(Path.Combine(s_root, source)))
            .ToList();

        Assert.True(absent.Count == 0, "Dockerfile publish stage COPY sources not in the repo: " + string.Join(", ", absent));
    }

    [Fact]
    public void Checker_FlagsAnEmbeddedFileTheCopyListLacks()
    {
        // Guards the checker itself: the pre-fix Dockerfile (no dsl.grammar COPY) must fail the coverage rule.
        string dockerfile = File.ReadAllText(Path.Combine(s_root, "Dockerfile"))
            .Replace("COPY docs/spec/dsl.grammar docs/spec/dsl.grammar", string.Empty, StringComparison.Ordinal);

        IReadOnlyList<string> copied = PublishStageCopySources(dockerfile);

        Assert.Contains(EmbeddedFilesOf("src/Draughtsman.Server/Draughtsman.Server.csproj"), file => !IsCovered(file, copied));
    }

    /// <summary>Repo-relative, forward-slash sources of the non-`--from` COPY lines in the `publish` stage.</summary>
    private static IReadOnlyList<string> PublishStageCopySources(string dockerfile)
    {
        List<string> sources = [];
        bool inPublish = false;
        foreach (string raw in dockerfile.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("FROM ", StringComparison.OrdinalIgnoreCase))
            {
                inPublish = Regex.IsMatch(line, @"\sAS\s+publish\s*$", RegexOptions.IgnoreCase);
            }
            else if (inPublish && line.StartsWith("COPY ", StringComparison.Ordinal) && !line.Contains("--from=", StringComparison.Ordinal))
            {
                string[] parts = line["COPY ".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                sources.AddRange(parts[..^1].Select(p => p.TrimEnd('/')));
            }
        }

        return sources;
    }

    private static bool IsCovered(string file, IReadOnlyList<string> copied)
    {
        return copied.Any(source => file == source || file.StartsWith(source + "/", StringComparison.Ordinal));
    }

    /// <summary>Repo-relative paths of every EmbeddedResource file in the project and its ProjectReference closure.</summary>
    private static IEnumerable<string> EmbeddedFilesOf(string projectRelativePath)
    {
        HashSet<string> seen = [];
        Queue<string> queue = new([Path.GetFullPath(Path.Combine(s_root, projectRelativePath))]);
        List<string> files = [];
        while (queue.Count > 0)
        {
            string project = queue.Dequeue();
            if (!seen.Add(project))
            {
                continue;
            }

            string directory = Path.GetDirectoryName(project)!;
            XDocument xml = XDocument.Load(project);
            foreach (XElement reference in xml.Descendants("ProjectReference"))
            {
                queue.Enqueue(Path.GetFullPath(Path.Combine(directory, Normalise((string)reference.Attribute("Include")!))));
            }

            foreach (string include in xml.Descendants("EmbeddedResource").Select(e => (string?)e.Attribute("Include")).OfType<string>())
            {
                if (include.Contains("$(", StringComparison.Ordinal))
                {
                    continue; // a property whose default sits under src/, which is copied whole
                }

                files.AddRange(Expand(directory, Normalise(include)));
            }
        }

        Assert.NotEmpty(files);
        return files.Distinct();
    }

    private static string Normalise(string path)
    {
        return path.Replace('\\', '/');
    }

    private static IEnumerable<string> Expand(string directory, string pattern)
    {
        string full = Path.GetFullPath(Path.Combine(directory, pattern));
        string folder = Path.GetDirectoryName(full)!;
        string name = Path.GetFileName(full);
        IEnumerable<string> matches = name.Contains('*', StringComparison.Ordinal)
            ? Directory.GetFiles(folder, name)
            : [full];
        foreach (string match in matches)
        {
            yield return Path.GetRelativePath(s_root, match).Replace('\\', '/');
        }
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Draughtsman.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Draughtsman.sln not found above the test binary.");
    }
}
