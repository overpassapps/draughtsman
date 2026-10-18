using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Draughtsman.Core.Models;
using Draughtsman.Core.Serialization;
using Draughtsman.Core.Storage;
using Draughtsman.Core.Text;
using Draughtsman.Server.Assets;

namespace Draughtsman.Server.Backup;

/// <summary>One document in the export: where it is in the zip, and the DSL entry's absence if the printer could not print it.</summary>
public sealed record ExportedDocument(string Id, string Title, string Type, string Json, string? Dsl, DateTimeOffset? DeletedAt = null);

public sealed record ExportedAsset(string Id, string ContentType, long Size, string Sha256, string File);

public sealed record ExportManifest(
    string Format,
    DateTimeOffset ExportedAt,
    string DraughtsmanVersion,
    IReadOnlyList<ExportedDocument> Documents,
    IReadOnlyList<ExportedAsset> Assets,
    IReadOnlyList<string> Skipped);

/// <summary>
/// "Export everything" (OP-01): every document as its JSON and as DSL text, and every uploaded image, in one zip an
/// administrator can hand over or keep as an exit copy in open formats. It is a way out, not a backup: it holds the current
/// version of each document only (no checkpoints or audit), no users, no settings; <see cref="BackupWriter"/> is the backup.
/// Documents in the bin are left out, since they are deleted as far as anyone using the instance is concerned, unless the
/// caller asks for them (<c>includeBin</c>): they then sit under <c>bin/</c>, marked with when they were deleted, never mixed
/// in with the live ones.
/// </summary>
public static class InstanceExport
{
    public const string FormatId = "draughtsman-export/1";

    private const int Page = 200;

    private static readonly JsonSerializerOptions s_manifestOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true, NewLine = "\n" };

    /// <summary>Writes the export to <paramref name="destination"/> (a seekable stream, so the zip can be sized and sent whole).</summary>
    public static async Task<ExportManifest> WriteAsync(Stream destination, IStore store, IStoreMaintenance maintenance, DataFolder dataFolder, string version, bool includeBin = false, CancellationToken cancellationToken = default)
    {
        var documents = new List<ExportedDocument>();
        var assets = new List<ExportedAsset>();
        var skipped = new List<string>();
        using var zip = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);

        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int offset = 0; ; offset += Page)
        {
            DocumentPage page = await store.ListDocuments(new DocumentQuery(null, offset, Page), cancellationToken);
            foreach (DocumentSummary summary in page.Items)
            {
                Document? document = await store.GetDocument(summary.Id, cancellationToken);
                if (document is null)
                {
                    continue;
                }

                string stem = UniqueStem(summary.Id, usedNames);
                string jsonPath = $"documents/{stem}.json";
                AddText(zip, jsonPath, DocumentJson.Serialize(document, indented: true));
                string? dslPath = null;
                try
                {
                    string dsl = Dsl.Print(document);
                    dslPath = $"documents/{stem}.dsl";
                    AddText(zip, dslPath, dsl);
                }
                catch (Exception error) when (error is InvalidOperationException or ArgumentException or FormatException)
                {
                    skipped.Add($"{summary.Id}: no DSL text ({error.Message}); the JSON is included.");
                }

                documents.Add(new ExportedDocument(summary.Id, document.Title, document.Type.ToString().ToLowerInvariant(), jsonPath, dslPath));
            }

            if (page.Items.Count < Page)
            {
                break;
            }
        }

        if (includeBin)
        {
            for (int offset = 0; ; offset += Page)
            {
                BinPage page = await store.ListBin(new BinQuery(null, offset, Page), cancellationToken);
                foreach (BinnedDocument binned in page.Items)
                {
                    Document? document = await store.GetBinnedSnapshot(binned.Id, cancellationToken);
                    if (document is null)
                    {
                        continue;
                    }

                    string stem = UniqueStem(binned.Id, usedNames);
                    string jsonPath = $"bin/{stem}.json";
                    AddText(zip, jsonPath, DocumentJson.Serialize(document, indented: true));
                    string? dslPath = null;
                    try
                    {
                        string dsl = Dsl.Print(document);
                        dslPath = $"bin/{stem}.dsl";
                        AddText(zip, dslPath, dsl);
                    }
                    catch (Exception error) when (error is InvalidOperationException or ArgumentException or FormatException)
                    {
                        skipped.Add($"{binned.Id} (in the bin): no DSL text ({error.Message}); the JSON is included.");
                    }

                    documents.Add(new ExportedDocument(binned.Id, document.Title, document.Type.ToString().ToLowerInvariant(), jsonPath, dslPath, binned.DeletedAt));
                }

                if (page.Items.Count < Page)
                {
                    break;
                }
            }
        }

        for (int offset = 0; ; offset += Page)
        {
            IReadOnlyList<AssetInfo> page = await maintenance.ListAssets(offset, Page, cancellationToken);
            foreach (AssetInfo asset in page)
            {
                string source = Path.Combine(dataFolder.Path, asset.Path);
                if (!File.Exists(source))
                {
                    skipped.Add($"asset {asset.Id}: the file {asset.Path} is missing from the data folder.");
                    continue;
                }

                string entryName = $"assets/{asset.Id}{Path.GetExtension(asset.Path)}";
                ZipArchiveEntry entry = zip.CreateEntry(entryName, CompressionLevel.Fastest);
                await using (Stream target = entry.Open())
                await using (FileStream input = File.OpenRead(source))
                {
                    await input.CopyToAsync(target, cancellationToken);
                }

                assets.Add(new ExportedAsset(asset.Id, asset.ContentType, asset.Size, asset.Hash, entryName));
            }

            if (page.Count < Page)
            {
                break;
            }
        }

        var manifest = new ExportManifest(FormatId, DateTimeOffset.UtcNow, version, documents, assets, skipped);
        AddText(zip, "manifest.json", JsonSerializer.Serialize(manifest, s_manifestOptions));
        AddText(zip, "README.txt",
            "Everything in this Draughtsman instance, in open formats: each document as JSON (documents/<id>.json, the document schema in docs/schema/document.schema.json) and as\n" +
            "DSL text (documents/<id>.dsl, docs/spec/dsl.md), and every uploaded image (assets/). Image nodes refer to their file by props.assetId.\n" +
            "It is the current version of each document only: no checkpoints, audit trail, users or settings. For a backup you can restore, use 'draughtsman backup'.\n" +
            (includeBin ? "Documents that were in the bin when this was made are under bin/ and carry a deletedAt in the manifest; the rest are under documents/.\n" : "Documents in the bin are not included.\n"));
        return manifest;
    }

    /// <summary>A file-name-safe stem for a document id, unique within the zip even where two ids differ only in characters the file name cannot hold.</summary>
    private static string UniqueStem(string id, HashSet<string> used)
    {
        var builder = new StringBuilder(id.Length);
        foreach (char c in id)
        {
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
        }

        string stem = builder.Length == 0 || builder.ToString().All(c => c == '.') ? "document" : builder.ToString();
        string candidate = stem;
        for (int n = 2; !used.Add(candidate); n++)
        {
            candidate = $"{stem}-{n}";
        }

        return candidate;
    }

    private static void AddText(ZipArchive zip, string name, string text)
    {
        ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(text);
    }
}
