using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Draughtsman.Core.Import;
using Draughtsman.Core.Import.Drawio;
using Draughtsman.Tests.Shared;

namespace Draughtsman.Core.Tests.Import;

/// <summary>
/// A .drawio file is XML from a stranger, often compressed. Each case here is an attack on an XML reader or a decompressor,
/// and each asserts the outcome the reader promises: the source is refused, in words, having read nothing it was not meant to
/// and having allocated nothing it did not have to.
/// </summary>
public class DrawioSecurityTests
{
    private static readonly string s_oneBox = DrawioXml.Model(DrawioXml.Vertex("2", "Box", string.Empty, 0, 0, 10, 10));

    private static string BillionLaughs(int levels)
    {
        var dtd = new StringBuilder("<!DOCTYPE lolz [\n<!ENTITY lol \"lol\">\n");
        for (int level = 1; level <= levels; level++)
        {
            string previous = level == 1 ? "lol" : $"lol{level - 1}";
            dtd.Append("<!ENTITY lol").Append(level).Append(" \"").Append(string.Concat(Enumerable.Repeat($"&{previous};", 10))).Append("\">\n");
        }

        return "<?xml version=\"1.0\"?>\n" + dtd + $"]>\n<mxfile><diagram name=\"&lol{levels};\">{s_oneBox}</diagram></mxfile>";
    }

    [Theory]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(9)]
    public void A_billion_laughs_file_is_refused_before_a_single_entity_is_expanded(int levels)
    {
        // Nine levels is the classic bomb: ten to the ninth "lol" in one attribute, gigabytes. Refused at the DOCTYPE, so it costs nothing.
        long before = GC.GetAllocatedBytesForCurrentThread();

        SourceRejectedException error = Assert.Throws<SourceRejectedException>(() => DrawioReader.Read(BillionLaughs(levels)));

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Contains("DOCTYPE", error.Message, StringComparison.Ordinal);
        Assert.True(allocated < 16 * 1024 * 1024, $"the reader allocated {allocated:N0} bytes on a bomb it should have refused at its first line");
    }

    [Fact]
    public void Even_a_harmless_internal_entity_is_refused_because_draw_io_never_writes_a_DOCTYPE()
    {
        string source = "<!DOCTYPE x [<!ENTITY a \"AAA\">]><mxfile><diagram name=\"&a;\">" + s_oneBox + "</diagram></mxfile>";

        SourceRejectedException error = Assert.Throws<SourceRejectedException>(() => DrawioReader.Read(source));

        Assert.Contains("DOCTYPE", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_external_entity_cannot_read_a_file_from_the_server()
    {
        string secret = Path.Combine(Path.GetTempPath(), $"draughtsman-xxe-{Guid.NewGuid():N}.txt");
        const string Marker = "TOP-SECRET-MARKER-5f2c";
        File.WriteAllText(secret, Marker);
        try
        {
            string source = $"<!DOCTYPE x [<!ENTITY xxe SYSTEM \"{new Uri(secret).AbsoluteUri}\">]><mxfile><diagram name=\"&xxe;\">{s_oneBox}</diagram></mxfile>";

            SourceRejectedException error = Assert.Throws<SourceRejectedException>(() => DrawioReader.Read(source));

            Assert.DoesNotContain(Marker, error.Message, StringComparison.Ordinal);
            Assert.Contains("DOCTYPE", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(secret);
        }
    }

    [Theory]
    [InlineData("<!DOCTYPE x [<!ENTITY xxe SYSTEM \"http://127.0.0.1:{0}/entity\">]><mxfile><diagram name=\"&xxe;\"/></mxfile>")]
    [InlineData("<!DOCTYPE x SYSTEM \"http://127.0.0.1:{0}/evil.dtd\"><mxfile><diagram name=\"x\"/></mxfile>")]
    [InlineData("<!DOCTYPE x [<!ENTITY % remote SYSTEM \"http://127.0.0.1:{0}/params.dtd\"> %remote;]><mxfile><diagram name=\"x\"/></mxfile>")]
    public void The_reader_makes_no_network_request_for_anything_in_the_file(string template)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Assert.Throws<SourceRejectedException>(() => DrawioReader.Read(string.Format(System.Globalization.CultureInfo.InvariantCulture, template, port)));

        // A resolver that fetched the entity would have connected before the reader returned, and the connection would be waiting here.
        Assert.False(listener.Pending(), "something connected to the listener: the reader resolved an external resource");
    }

    [Fact]
    public void A_deflate_stream_that_expands_past_the_limit_is_refused_while_it_is_still_being_read()
    {
        long bomb = (long)DrawioLimits.MaxInflatedBytes * 4;
        string payload = Bomb(bomb, zlib: false);
        long before = GC.GetAllocatedBytesForCurrentThread();

        SourceRejectedException error = Assert.Throws<SourceRejectedException>(() => DrawioReader.Read($"<mxfile><diagram name=\"bomb\">{payload}</diagram></mxfile>"));

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Contains("expands", error.Message, StringComparison.Ordinal);
        Assert.True(payload.Length < 1_000_000, $"the bomb's own text is {payload.Length:N0} characters: small enough to pass the endpoint's 1,000,000-character limit");
        Assert.True(allocated < DrawioLimits.MaxInflatedBytes * 6L, $"the reader allocated {allocated:N0} bytes while refusing a {bomb:N0}-byte expansion: it did not stop at the limit");
    }

    [Fact]
    public void A_zlib_wrapped_bomb_is_refused_the_same_way()
    {
        string payload = Bomb((long)DrawioLimits.MaxInflatedBytes * 2, zlib: true);

        SourceRejectedException error = Assert.Throws<SourceRejectedException>(() => DrawioReader.Read($"<mxfile><diagram name=\"bomb\">{payload}</diagram></mxfile>"));

        Assert.Contains("expands", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_large_but_honest_compressed_page_inside_the_limits_is_still_read()
    {
        // A real model padded out to a quarter of the inflate limit: the limits must not bite a legitimate file.
        string padding = new('a', DrawioLimits.MaxInflatedBytes / 4);
        string model = DrawioXml.Model(DrawioXml.Vertex("2", "Big but honest", string.Empty, 0, 0, 10, 10)).Replace("<root>", "<root><!--" + padding + "-->", StringComparison.Ordinal);

        DrawioFile file = DrawioReader.Read(DrawioXml.CompressedFile(model));

        Assert.Contains(file.Cells, c => c.Id == "2");
    }

    [Fact]
    public void A_stream_of_valid_text_that_is_not_xml_is_refused_without_framework_text_in_the_message()
    {
        string payload = Convert.ToBase64String(DrawioXml.Deflate(Encoding.UTF8.GetBytes("this is not a diagram")));

        SourceRejectedException error = Assert.Throws<SourceRejectedException>(() => DrawioReader.Read($"<mxfile><diagram name=\"x\">{payload}</diagram></mxfile>"));

        Assert.Contains("draw.io", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("System.", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_decompressed_page_that_is_itself_a_DOCTYPE_bomb_is_refused()
    {
        // The DOCTYPE check must apply to the XML inside a compressed page as much as to the file around it.
        string inner = BillionLaughs(6).Replace("&lol6;", "x", StringComparison.Ordinal);
        string model = inner[inner.IndexOf("<!DOCTYPE", StringComparison.Ordinal)..inner.IndexOf("<mxfile>", StringComparison.Ordinal)] + DrawioXml.Model();

        SourceRejectedException error = Assert.Throws<SourceRejectedException>(() => DrawioReader.Read(DrawioXml.CompressedFile(model)));

        Assert.Contains("DOCTYPE", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Nesting_deeper_than_any_real_file_is_refused_without_overflowing_the_stack()
    {
        string source = "<mxfile><diagram name=\"deep\">" + string.Concat(Enumerable.Repeat("<a>", 20_000)) + string.Concat(Enumerable.Repeat("</a>", 20_000)) + "</diagram></mxfile>";

        SourceRejectedException error = Assert.Throws<SourceRejectedException>(() => DrawioReader.Read(source));

        Assert.Contains("nested", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void More_elements_than_any_real_file_are_refused()
    {
        string source = DrawioXml.File(DrawioXml.Model(string.Concat(Enumerable.Repeat("<a/>", DrawioLimits.MaxElements + 1))));

        SourceRejectedException error = Assert.Throws<SourceRejectedException>(() => DrawioReader.Read(source));

        Assert.Contains("elements", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void More_shapes_and_connectors_than_the_limit_are_refused_naming_the_limit()
    {
        string cells = string.Concat(Enumerable.Range(0, DrawioLimits.MaxCells + 1).Select(i => $"<mxCell id=\"v{i}\" value=\"\" style=\"\" vertex=\"1\" parent=\"1\"><mxGeometry as=\"geometry\"/></mxCell>"));

        SourceRejectedException error = Assert.Throws<SourceRejectedException>(() => DrawioReader.Read(DrawioXml.File(DrawioXml.Model(cells))));

        Assert.Contains(DrawioLimits.MaxCells.ToString("N0", System.Globalization.CultureInfo.InvariantCulture), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Exactly_the_limit_of_shapes_is_read()
    {
        string cells = string.Concat(Enumerable.Range(0, DrawioLimits.MaxCells).Select(i => $"<mxCell id=\"v{i}\" value=\"\" style=\"\" vertex=\"1\" parent=\"1\"><mxGeometry as=\"geometry\"/></mxCell>"));

        DrawioFile file = DrawioReader.Read(DrawioXml.File(DrawioXml.Model(cells)));

        Assert.Equal(DrawioLimits.MaxCells, file.Cells.Count(c => c.Vertex));
    }

    [Fact]
    public void An_element_with_a_flood_of_attributes_is_refused()
    {
        string attributes = string.Concat(Enumerable.Range(0, DrawioLimits.MaxAttributesPerElement + 1).Select(i => $" k{i}=\"v\""));
        string source = DrawioXml.File(DrawioXml.Model($"<object label=\"x\" id=\"9\"{attributes}><mxCell vertex=\"1\" parent=\"1\"/></object>"));

        SourceRejectedException error = Assert.Throws<SourceRejectedException>(() => DrawioReader.Read(source));

        Assert.Contains("attributes", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_source_longer_than_the_character_limit_is_refused()
    {
        string source = "<mxfile>" + new string(' ', DrawioLimits.MaxXmlCharacters) + "<diagram/></mxfile>";

        SourceRejectedException error = Assert.Throws<SourceRejectedException>(() => DrawioReader.Read(source));

        Assert.Contains("larger", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void More_pages_than_any_real_file_are_refused()
    {
        string source = "<mxfile>" + string.Concat(Enumerable.Repeat("<diagram name=\"p\"/>", DrawioLimits.MaxPages + 1)) + "</mxfile>";

        SourceRejectedException error = Assert.Throws<SourceRejectedException>(() => DrawioReader.Read(source));

        Assert.Contains("pages", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_page_name_that_is_enormous_is_cut_not_carried()
    {
        string source = $"<mxfile><diagram name=\"{new string('n', 100_000)}\">{s_oneBox}</diagram></mxfile>";

        DrawioFile file = DrawioReader.Read(source);

        Assert.True(file.Pages[0].Name.Length <= DrawioLimits.MaxNameLength);
    }

    [Theory]
    [InlineData("shapes.drawio")]
    [InlineData("containers.drawio")]
    [InlineData("edges.drawio")]
    [InlineData("compressed.drawio")]
    public async Task A_long_run_of_spaces_anywhere_in_a_real_file_never_makes_the_import_slow(string fixture)
    {
        // The audit's whitespace test (ImporterComplexityTests) for this importer: half a megabyte of spaces dropped at sixteen places through
        // a real file lands between attributes, inside values and labels, between elements and inside tags. Each is either read or refused,
        // and quickly: nothing here may scan the run once per character.
        string source = DrawioFixtures.Read(fixture);
        TimeSpan budget = LoadScale.Scaled(TimeSpan.FromSeconds(3));
        await Task.Run(() => TryImport(source));
        var slow = new List<string>();
        for (int i = 1; i <= 16; i++)
        {
            int at = source.Length * i / 17;
            string variant = source.Insert(at, new string(' ', 500_000));
            var timer = System.Diagnostics.Stopwatch.StartNew();
            await Task.Run(() => TryImport(variant));
            if (timer.Elapsed > budget)
            {
                slow.Add($"{timer.ElapsedMilliseconds} ms at {at}: ...{source.Substring(Math.Max(0, at - 30), 30).Replace('\n', ' ')}");
            }
        }

        Assert.True(slow.Count == 0, string.Join(Environment.NewLine, slow.Take(5)));
    }

    private static void TryImport(string source)
    {
        try
        {
            DrawioImporter.Import(source);
        }
        catch (SourceRejectedException)
        {
            // A refusal is an answer; only slowness is wrong.
        }
    }

    /// <summary>The base64 of a deflate (or zlib) stream that expands to <paramref name="inflatedBytes"/>: an XML comment of 'a's behind a valid prefix, streamed so the test itself never holds the expansion.</summary>
    private static string Bomb(long inflatedBytes, bool zlib)
    {
        using var output = new MemoryStream();
        using (Stream compressor = zlib ? new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true) : new DeflateStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            compressor.Write(Encoding.ASCII.GetBytes("%3CmxGraphModel%3E%3Croot%3E%3C%21--"));
            byte[] chunk = Encoding.ASCII.GetBytes(new string('a', 1024 * 1024));
            for (long written = 0; written < inflatedBytes; written += chunk.Length)
            {
                compressor.Write(chunk);
            }

            compressor.Write(Encoding.ASCII.GetBytes("--%3E%3C/root%3E%3C/mxGraphModel%3E"));
        }

        return Convert.ToBase64String(output.ToArray());
    }
}
