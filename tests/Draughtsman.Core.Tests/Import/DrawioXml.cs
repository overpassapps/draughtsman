using System.IO.Compression;
using System.Text;

namespace Draughtsman.Core.Tests.Import;

/// <summary>Builders for the draw.io text the importer tests read: the model, the file around it and draw.io's own compression.</summary>
internal static class DrawioXml
{
    public const string TwoCells = "<mxCell id=\"0\"/><mxCell id=\"1\" parent=\"0\"/>";

    /// <summary>An <c>mxGraphModel</c> with the two cells every file starts with (the root and the default layer) and then <paramref name="cells"/>.</summary>
    public static string Model(params string[] cells)
    {
        return "<mxGraphModel><root>" + TwoCells + string.Concat(cells) + "</root></mxGraphModel>";
    }

    /// <summary>A plain (uncompressed) <c>mxfile</c> whose pages are the given models, named Page-1, Page-2 and so on.</summary>
    public static string File(params string[] models)
    {
        var builder = new StringBuilder("<mxfile host=\"app.diagrams.net\">");
        for (int i = 0; i < models.Length; i++)
        {
            builder.Append("<diagram id=\"page").Append(i + 1).Append("\" name=\"Page-").Append(i + 1).Append("\">").Append(models[i]).Append("</diagram>");
        }

        return builder.Append("</mxfile>").ToString();
    }

    /// <summary>A vertex with the geometry draw.io writes for it.</summary>
    public static string Vertex(string id, string value, string style, double x, double y, double w, double h, string parent = "1")
    {
        return $"<mxCell id=\"{id}\" value=\"{Escape(value)}\" style=\"{style}\" vertex=\"1\" parent=\"{parent}\"><mxGeometry x=\"{x}\" y=\"{y}\" width=\"{w}\" height=\"{h}\" as=\"geometry\"/></mxCell>";
    }

    /// <summary>An edge, with any inner geometry markup (waypoints) as <paramref name="geometry"/>.</summary>
    public static string Edge(string id, string? source, string? target, string value = "", string style = "", string geometry = "", string parent = "1")
    {
        string from = source is null ? string.Empty : $" source=\"{source}\"";
        string to = target is null ? string.Empty : $" target=\"{target}\"";
        return $"<mxCell id=\"{id}\" value=\"{Escape(value)}\" style=\"{style}\" edge=\"1\" parent=\"{parent}\"{from}{to}><mxGeometry relative=\"1\" as=\"geometry\">{geometry}</mxGeometry></mxCell>";
    }

    public static string Escape(string text)
    {
        return text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal).Replace("\n", "&#xa;", StringComparison.Ordinal);
    }

    /// <summary>What a <c>&lt;diagram&gt;</c> holds when draw.io compresses it: the model URL-encoded, deflated without a header, then base64.</summary>
    public static string Compress(string model)
    {
        return Convert.ToBase64String(Deflate(Encoding.UTF8.GetBytes(Uri.EscapeDataString(model))));
    }

    public static byte[] Deflate(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            deflate.Write(bytes);
        }

        return output.ToArray();
    }

    /// <summary>The same bytes behind a zlib header, which some tools write instead of draw.io's headerless stream.</summary>
    public static byte[] ZLib(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(bytes);
        }

        return output.ToArray();
    }

    /// <summary>A file whose single page is compressed.</summary>
    public static string CompressedFile(string model, string name = "Page-1")
    {
        return $"<mxfile><diagram id=\"p1\" name=\"{name}\">{Compress(model)}</diagram></mxfile>";
    }
}
