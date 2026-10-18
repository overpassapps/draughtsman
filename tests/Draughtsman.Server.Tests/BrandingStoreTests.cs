using System.Buffers.Binary;
using Draughtsman.Core.Media;
using Draughtsman.Server.Theming;
using Microsoft.Extensions.Logging.Abstractions;

namespace Draughtsman.Server.Tests;

/// <summary>
/// Branding persistence (M3): an admin's overrides are written under the data folder's <c>branding/</c> directory, so
/// a fresh service constructed over the same folder - the next process start - serves them, and a reset clears them.
/// This is the disk half the HTTP tests cannot prove, since they never restart the host.
/// </summary>
public class BrandingStoreTests
{
    [Fact]
    public void Overrides_survive_a_restart_and_a_reset_clears_them()
    {
        string folder = Path.Combine(Path.GetTempPath(), "draughtsman-branding-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var dataFolder = new DataFolder(folder);
            LoadedChromeTheme theme = new() { Theme = Theme(), Source = "test" };
            ValidatedBrandingImage image = BrandingImages.Validate(Png(24, 12), BrandingSlot.Logo);

            ChromeBrandingService first = Service(theme, dataFolder);
            first.SetProductName("Acme Diagrams");
            first.SetAsset(BrandingSlot.Logo, image);

            // A second service over the same folder is what a restart constructs.
            ChromeBrandingService second = Service(theme, dataFolder);
            Assert.Equal("Acme Diagrams", second.Current.ProductName);
            Assert.NotNull(second.Current.Logo);
            Assert.Equal(image.Content, second.Current.Logo!.Content);
            Assert.True(second.Current.Overrides.Has(BrandingSlot.Logo));

            second.Reset();

            ChromeBrandingService third = Service(theme, dataFolder);
            Assert.Equal("Draughtsman", third.Current.ProductName);
            Assert.Null(third.Current.Logo);
            Assert.False(third.Current.Overrides.HasProductName);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static ChromeBrandingService Service(LoadedChromeTheme theme, DataFolder folder)
    {
        return new ChromeBrandingService(
            theme,
            new BrandingStore(folder, NullLogger<BrandingStore>.Instance),
            new OrgThemeService(new OrgThemeStore(folder, NullLogger<OrgThemeStore>.Instance), NullLogger<OrgThemeService>.Instance));
    }

    private static ChromeTheme Theme()
    {
        return new ChromeTheme
        {
            Name = "Overpass",
            ProductName = "Draughtsman",
            Logo = "assets/overpass-logo-light.svg",
            Mode = "dark",
            Colors = new Dictionary<string, string> { ["brand"] = "#c0392b" },
            Radius = 6,
            Font = new Dictionary<string, string> { ["ui"] = "sans-serif" },
        };
    }

    private static byte[] Png(int width, int height)
    {
        byte[] bytes = new byte[24];
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(bytes, 0);
        "IHDR"u8.CopyTo(bytes.AsSpan(12, 4));
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), height);
        return bytes;
    }
}
