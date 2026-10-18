using Draughtsman.Server.KeyRing;

namespace Draughtsman.Server.Tests;

/// <summary>
/// The strings tried as "the folder an earlier build was running from" (<see cref="LegacyDiscriminators.Spellings"/>). A secret is bound to the exact string the host handed Data
/// Protection, so a path an operator types must be tried the way Windows or Linux would have spelled it. The rules are pure over a platform flag, so these run on every host
/// and each Windows rule is shown against the spelling it exists for (a case that fails without the rule), not only against one that already works.
/// </summary>
public sealed class LegacyDiscriminatorSpellingTests
{
    [Theory]
    [InlineData(@"C:\Program Files\Draughtsman\", @"C:\Program Files\Draughtsman")]
    [InlineData(@"C:\Program Files\Draughtsman", @"C:\Program Files\Draughtsman\")]
    public void A_windows_path_is_tried_with_and_without_its_trailing_backslash(string typed, string other)
    {
        IReadOnlyList<string> spellings = LegacyDiscriminators.Spellings(typed, windows: true);

        Assert.Equal(typed, spellings[0]);
        Assert.Contains(other, spellings);
    }

    [Theory]
    [InlineData("C:/Program Files/Draughtsman/")]
    [InlineData("C:/Program Files/Draughtsman")]
    public void A_windows_path_typed_with_forward_slashes_is_also_tried_the_way_the_host_spells_it_with_backslashes(string typed)
    {
        IReadOnlyList<string> spellings = LegacyDiscriminators.Spellings(typed, windows: true);

        // The host's own content root: backslashes, with the trailing one. Nothing else equals what Data Protection was given.
        Assert.Contains(@"C:\Program Files\Draughtsman\", spellings);
        Assert.Contains(@"C:\Program Files\Draughtsman", spellings);
        // And exactly as typed, since a content root given on a command line is kept verbatim by the host.
        Assert.Contains(typed, spellings);
    }

    [Theory]
    [InlineData(@"c:\apps\draughtsman\", @"C:\apps\draughtsman\")]
    [InlineData(@"C:\apps\draughtsman", @"c:\apps\draughtsman\")]
    [InlineData("c:/apps/draughtsman", @"C:\apps\draughtsman\")]
    public void A_windows_drive_letter_is_tried_in_both_cases(string typed, string expected)
    {
        Assert.Contains(expected, LegacyDiscriminators.Spellings(typed, windows: true));
    }

    [Fact]
    public void A_windows_unc_path_keeps_its_two_leading_backslashes()
    {
        IReadOnlyList<string> spellings = LegacyDiscriminators.Spellings(@"\\server\share\Draughtsman", windows: true);

        Assert.Contains(@"\\server\share\Draughtsman\", spellings);
        Assert.Contains(@"\\server\share\Draughtsman", spellings);
        Assert.All(spellings, s => Assert.StartsWith(@"\\", s, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(@"C:\", new[] { @"C:\", @"c:\" })]
    [InlineData("/", new[] { "/" })]
    public void A_root_has_no_bare_form(string root, string[] expected)
    {
        Assert.Equal(expected, LegacyDiscriminators.Spellings(root, windows: root != "/"));
    }

    [Theory]
    [InlineData("/opt/draughtsman-0.1.0/", "/opt/draughtsman-0.1.0")]
    [InlineData("/opt/draughtsman-0.1.0", "/opt/draughtsman-0.1.0/")]
    public void A_posix_path_is_tried_with_and_without_its_trailing_slash_and_nothing_with_backslashes(string typed, string other)
    {
        IReadOnlyList<string> spellings = LegacyDiscriminators.Spellings(typed, windows: false);

        Assert.Equal([typed, other], spellings);
        // A backslash is an ordinary character in a Linux or macOS folder name: never invented, never converted.
        Assert.DoesNotContain(spellings, s => s.Contains('\\', StringComparison.Ordinal));
    }

    [Fact]
    public void A_posix_path_with_a_backslash_in_its_name_is_left_alone()
    {
        Assert.Equal([@"/srv/odd\name/", @"/srv/odd\name"], LegacyDiscriminators.Spellings(@"/srv/odd\name/", windows: false));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Spellings_never_repeat_and_ignore_blank_input_and_surrounding_spaces(bool windows)
    {
        IReadOnlyList<string> spellings = LegacyDiscriminators.Spellings("  /srv/x/  ", windows);

        Assert.Equal(spellings.Count, spellings.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("/srv/x/", spellings[0]);
        Assert.Empty(LegacyDiscriminators.Spellings("   ", windows));
    }
}
