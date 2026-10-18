using System.IO.Compression;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Models;
using Draughtsman.Server.Configuration;
using Draughtsman.Storage;
using Draughtsman.Storage.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Draughtsman.Server.Tests;

/// <summary>Builds a small but complete SQLite data folder without a server, so the backup and restore rules can be driven directly.</summary>
internal static class BackupTestSupport
{
    /// <summary>A PNG header with a 240x120 IHDR: enough for the asset sniffer and the branding dimension check, like the branding tests use.</summary>
    public static readonly byte[] AssetBytes = Png(240, 120);

    public static byte[] Png(int width, int height)
    {
        byte[] bytes = new byte[24];
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(bytes, 0);
        "IHDR"u8.CopyTo(bytes.AsSpan(12, 4));
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16, 4), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), height);
        return bytes;
    }

    public static async Task SeedFolder(string folder, int documents = 2)
    {
        Directory.CreateDirectory(folder);
        var options = new StorageOptions(StorageProvider.Sqlite, StorageOptions.DefaultSqliteConnectionString(folder));
        await using (DraughtsmanDbContext db = StorageSetup.CreateContext(options))
        {
            await StorageMigrator.Migrate(db, options, NullLogger.Instance);
            var store = new EfStore(db, options);
            DateTimeOffset now = new(2026, 9, 20, 22, 0, 0, TimeSpan.Zero);
            for (int i = 1; i <= documents; i++)
            {
                await store.CreateDocument(new Document($"doc_seed_{i}", Document.CurrentSchemaVersion, $"Seeded {i}", DocumentType.Flowchart, [], [], new Meta(now, now, "test")));
            }

            db.Users.Add(new UserRow { Id = "usr_seed", Email = "seed@example.com", DisplayName = "Seed", Role = "Admin", PasswordHash = "hash", CreatedAt = now.UtcDateTime, Active = true });
            await db.SaveChangesAsync();
        }

        SqlitePools.Release(folder);
        Write(folder, "draughtsman.yaml", "server:\n  port: 5999\n");
        Write(folder, "assets/ab/abcdef.png", AssetBytes);
        Write(folder, "branding/branding.json", "{\"productName\":\"Seed Co\"}");
        Write(folder, "themes/org/acme.yaml", "name: acme\n");
        // Secrets: the key ring, the AI settings it encrypts, and the ring's own backups.
        Write(folder, "keys/key-1.xml", "<key>SECRET-KEY-MATERIAL</key>");
        Write(folder, "ai/settings.json", "{\"apiKey\":\"ENCRYPTED\"}");
        Write(folder, "backups/key-ring-1/keys/key-1.xml", "<key>SECRET-KEY-MATERIAL</key>");
        // Things a backup must never carry.
        Write(folder, "assets/ab/half.tmp", "half-written upload");
        Write(folder, "data.db.pre-migration-20260101000000", "an old copy");
    }

    public static void Write(string folder, string relative, string text)
    {
        Write(folder, relative, System.Text.Encoding.UTF8.GetBytes(text));
    }

    public static void Write(string folder, string relative, byte[] bytes)
    {
        string path = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    public static List<string> Entries(string bundle)
    {
        using ZipArchive zip = ZipFile.OpenRead(bundle);
        return zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    public static string ReadEntry(string bundle, string name)
    {
        using ZipArchive zip = ZipFile.OpenRead(bundle);
        using var reader = new StreamReader((zip.GetEntry(name) ?? throw new FileNotFoundException(name)).Open());
        return reader.ReadToEnd();
    }

    /// <summary>Edits a finished bundle in place, the way a damaged or hostile one would differ from what <c>backup</c> wrote.</summary>
    public static void Edit(string bundle, Action<ZipArchive> edit)
    {
        using var zip = ZipFile.Open(bundle, ZipArchiveMode.Update);
        edit(zip);
    }

    public static void ReplaceEntry(ZipArchive zip, string name, string text)
    {
        zip.GetEntry(name)?.Delete();
        ZipArchiveEntry entry = zip.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(text);
    }
}
