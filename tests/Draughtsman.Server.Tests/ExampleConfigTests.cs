using System.Reflection;
using System.Text.Json;
using Draughtsman.Server.Configuration;
using YamlDotNet.RepresentationModel;

namespace Draughtsman.Server.Tests;

/// <summary>
/// docs/draughtsman.example.yaml ships in every release archive as the annotated reference of draughtsman.yaml. It must
/// list every setting, at its real default, and name no setting that does not exist: a key added to
/// <see cref="DraughtsmanConfig"/> without a line in the example, or a typo in the example, fails here.
/// </summary>
public class ExampleConfigTests
{
    private static string ExamplePath()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "Draughtsman.sln")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        return Path.Combine(directory ?? throw new FileNotFoundException("Draughtsman.sln not found above the test binaries."), "docs", "draughtsman.example.yaml");
    }

    [Fact]
    public void Loading_the_example_gives_exactly_the_defaults()
    {
        DraughtsmanConfig fromExample = ConfigLoader.Parse(File.ReadAllText(ExamplePath()));

        Assert.Equal(JsonSerializer.Serialize(new DraughtsmanConfig()), JsonSerializer.Serialize(fromExample));
    }

    [Fact]
    public void Copying_the_whole_example_file_locks_no_email_field_in_the_admin_page()
    {
        // The example lists every key at its default, and an administrator may copy it whole. The precedence rule (a value the file sets
        // wins and locks its field) must read a key left at its default as "not set", or copying the example would lock the page.
        DraughtsmanConfig fromExample = ConfigLoader.Parse(File.ReadAllText(ExamplePath()));

        var settings = new Draughtsman.Server.Email.EmailSettings(fromExample.Email, fromExample.Server, Path.GetTempPath(), _ => null);

        Assert.Empty(settings.Locks);
        Assert.False(settings.Configured);
    }

    [Fact]
    public void A_key_that_differs_from_the_example_default_does_lock_its_field()
    {
        DraughtsmanConfig changed = ConfigLoader.Parse(File.ReadAllText(ExamplePath()).Replace("    host: \"\"", "    host: smtp.example.com", StringComparison.Ordinal));

        var settings = new Draughtsman.Server.Email.EmailSettings(changed.Email, changed.Server, Path.GetTempPath(), _ => null);

        Assert.Equal(["host"], settings.Locks.Keys);
    }

    [Fact]
    public void The_example_names_every_setting_and_only_real_ones()
    {
        var yaml = new YamlStream();
        yaml.Load(new StringReader(File.ReadAllText(ExamplePath())));
        HashSet<string> inExample = Keys((YamlMappingNode)yaml.Documents[0].RootNode, prefix: string.Empty).ToHashSet(StringComparer.Ordinal);
        HashSet<string> inCode = Keys(typeof(DraughtsmanConfig), prefix: string.Empty).ToHashSet(StringComparer.Ordinal);

        Assert.Empty(inCode.Except(inExample));
        Assert.Empty(inExample.Except(inCode));
    }

    private static IEnumerable<string> Keys(YamlMappingNode node, string prefix)
    {
        foreach (KeyValuePair<YamlNode, YamlNode> entry in node.Children)
        {
            string path = prefix + ((YamlScalarNode)entry.Key).Value;
            if (entry.Value is YamlMappingNode child)
            {
                foreach (string key in Keys(child, path + "."))
                {
                    yield return key;
                }
            }
            else
            {
                yield return path;
            }
        }
    }

    private static IEnumerable<string> Keys(Type type, string prefix)
    {
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            string path = prefix + char.ToLowerInvariant(property.Name[0]) + property.Name[1..];
            if (property.PropertyType.IsClass && property.PropertyType != typeof(string) && !property.PropertyType.IsGenericType)
            {
                foreach (string key in Keys(property.PropertyType, path + "."))
                {
                    yield return key;
                }
            }
            else
            {
                yield return path;
            }
        }
    }
}
