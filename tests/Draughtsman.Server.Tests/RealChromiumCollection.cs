namespace Draughtsman.Server.Tests;

/// <summary>
/// Every test class that starts a real Chromium (or the real Playwright driver) shares this collection, so they run one after another. Two real browsers starting at once
/// (one test's frozen or bystander browser, another's render) refused to launch on a loaded Mac ("could not be launched", a 500) although each
/// passes alone: the test is of the PDF path, not of how many Chromiums a host can start together.
/// </summary>
[CollectionDefinition(Name)]
public sealed class RealChromiumCollection
{
    public const string Name = "Real Chromium";
}
