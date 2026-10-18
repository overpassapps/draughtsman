using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Draughtsman.Core.Import;
using Draughtsman.Core.Import.Visio;
using Draughtsman.Tests.Shared;
using static Draughtsman.Tests.Shared.VisioDocBuilder;

namespace Draughtsman.Core.Tests.Import;

/// <summary>
/// A .vsdx file is a ZIP of XML from a stranger. Each case here is an attack on the ZIP reader, the XML reader or the importer, and each asserts the
/// outcome the reader promises: the source is refused with a message that says why, having read nothing it was not meant to, fetched nothing, and
/// allocated nothing it did not have to. The archives are built (and, where a header must lie, tampered with) in test code.
/// </summary>
public class VisioSecurityTests
{
    private const string Page = "visio/pages/page1.xml";

    /// <summary>A package whose page uses a master, so that every part the reader can open (document, page list, page, masters, master, and their relationships) is opened.</summary>
    private static List<KeyValuePair<string, byte[]>> Parts()
    {
        return new VisioDocBuilder()
            .Master(2, "Process", Shape(1, 1, 1, 1.5, 0.75))
            .Page("P", Instance(1, 2, 3, 3, "Box"))
            .Parts();
    }

    private static byte[] Good()
    {
        return Zip(Parts());
    }

    private static SourceRejectedException Refused(byte[] file)
    {
        return Assert.Throws<SourceRejectedException>(() => VisioImporter.Import(file));
    }

    private static string BillionLaughs(int levels)
    {
        var dtd = new StringBuilder("<!DOCTYPE lolz [\n<!ENTITY lol \"lol\">\n");
        for (int level = 1; level <= levels; level++)
        {
            string previous = level == 1 ? "lol" : $"lol{level - 1}";
            dtd.Append("<!ENTITY lol").Append(level).Append(" \"").Append(string.Concat(Enumerable.Repeat($"&{previous};", 10))).Append("\">\n");
        }

        return "<?xml version=\"1.0\"?>\n" + dtd + $"]>\n<Anything a=\"&lol{levels};\"/>";
    }

    /// <summary>The package with <paramref name="part"/> replaced by <paramref name="xml"/>.</summary>
    private static byte[] Replaced(string part, string xml)
    {
        return VisioDocBuilder.Replace(Parts(), part, xml);
    }

    public static TheoryData<string> EveryPartTheReaderOpens()
    {
        return new TheoryData<string>
        {
            "_rels/.rels",
            "visio/document.xml",
            "visio/_rels/document.xml.rels",
            "visio/pages/pages.xml",
            "visio/pages/_rels/pages.xml.rels",
            Page,
            "visio/masters/masters.xml",
            "visio/masters/_rels/masters.xml.rels",
            "visio/masters/master1.xml",
        };
    }

    // ---- XML: entities and DTDs --------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(EveryPartTheReaderOpens))]
    public void A_billion_laughs_part_is_refused_before_a_single_entity_is_expanded_whichever_part_carries_it(string part)
    {
        // Nine levels is the classic bomb: ten to the ninth "lol" in one attribute, gigabytes. Refused at the DOCTYPE, so it costs nothing.
        byte[] file = Replaced(part, BillionLaughs(9));
        long before = GC.GetAllocatedBytesForCurrentThread();

        SourceRejectedException error = Refused(file);

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Contains("DOCTYPE", error.Message, StringComparison.Ordinal);
        Assert.True(allocated < 16 * 1024 * 1024, $"the importer allocated {allocated:N0} bytes on a bomb it should have refused at its first line");
    }

    [Fact]
    public void Even_a_harmless_internal_entity_is_refused_because_visio_never_writes_a_DOCTYPE()
    {
        byte[] file = Replaced(Page, "<!DOCTYPE x [<!ENTITY a \"AAA\">]><PageContents xmlns='" + MainNamespace + "'><Shapes/></PageContents>");

        Assert.Contains("DOCTYPE", Refused(file).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_external_entity_cannot_read_a_file_from_the_server()
    {
        string secret = Path.Combine(Path.GetTempPath(), $"draughtsman-xxe-{Guid.NewGuid():N}.txt");
        const string Marker = "TOP-SECRET-MARKER-9d41";
        File.WriteAllText(secret, Marker);
        try
        {
            byte[] file = Replaced(Page, $"<!DOCTYPE x [<!ENTITY xxe SYSTEM \"{new Uri(secret).AbsoluteUri}\">]><PageContents><Shapes><Shape ID='1'><Text>&xxe;</Text></Shape></Shapes></PageContents>");

            SourceRejectedException error = Refused(file);

            Assert.DoesNotContain(Marker, error.Message, StringComparison.Ordinal);
            Assert.Contains("DOCTYPE", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(secret);
        }
    }

    [Theory]
    [InlineData("<!DOCTYPE x [<!ENTITY xxe SYSTEM \"http://127.0.0.1:{0}/entity\">]><PageContents><Shapes><Shape ID='1'><Text>&xxe;</Text></Shape></Shapes></PageContents>")]
    [InlineData("<!DOCTYPE x SYSTEM \"http://127.0.0.1:{0}/evil.dtd\"><PageContents><Shapes/></PageContents>")]
    [InlineData("<!DOCTYPE x [<!ENTITY % remote SYSTEM \"http://127.0.0.1:{0}/params.dtd\"> %remote;]><PageContents><Shapes/></PageContents>")]
    public void The_importer_makes_no_network_request_for_anything_in_the_file(string template)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Refused(Replaced(Page, string.Format(CultureInfo.InvariantCulture, template, port)));

        // A resolver that fetched the entity would have connected before the importer returned, and the connection would be waiting here.
        Assert.False(listener.Pending(), "something connected to the listener: the importer resolved an external resource");
    }

    [Fact]
    public void An_external_relationship_is_text_it_is_never_followed()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string rels = $"<Relationships xmlns='{PackageRelationshipsNamespace}'><Relationship Id='rId1' Type='http://schemas.microsoft.com/visio/2010/relationships/pages' Target='http://127.0.0.1:{port}/pages.xml' TargetMode='External'/></Relationships>";
        byte[] file = VisioDocBuilder.Replace(Parts(), "visio/_rels/document.xml.rels", rels);

        ImportResult result = VisioImporter.Import(file);

        Assert.Empty(result.Document.Nodes);
        Assert.False(listener.Pending(), "something connected to the listener: an external relationship was followed");
    }

    // ---- XML: size, depth, counts ------------------------------------------------------------------------------------------------

    [Fact]
    public void Nesting_deeper_than_any_real_file_is_refused_without_overflowing_the_stack()
    {
        string deep = string.Concat(Enumerable.Repeat("<a>", 20_000)) + string.Concat(Enumerable.Repeat("</a>", 20_000));
        byte[] file = new VisioDocBuilder().Page("P", Shape(1, 2, 6, 1, 1, "Box").Replace("</Shape>", deep + "</Shape>", StringComparison.Ordinal)).Build();

        SourceRejectedException error = Refused(file);

        Assert.Contains("levels deep", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Groups_nested_deeper_than_the_limit_are_refused_and_a_realistic_nesting_is_read()
    {
        static string Nest(int depth)
        {
            string inner = Shape(1000, 1, 1, 1, 1, "Leaf");
            for (int level = depth; level >= 1; level--)
            {
                inner = Shape(level, 5, 4, 4, 4, geometry: null, type: "Group", children: inner);
            }

            return inner;
        }

        byte[] tooDeep = new VisioDocBuilder().Page("P", Nest(40)).Build();
        byte[] realistic = new VisioDocBuilder().Page("P", Nest(12)).Build();

        Assert.Contains("levels deep", Refused(tooDeep).Message, StringComparison.Ordinal);
        Assert.Equal(13, VisioImporter.Import(realistic).Document.Nodes.Count);
    }

    [Fact]
    public void More_elements_than_any_real_file_are_refused()
    {
        byte[] file = new VisioDocBuilder().Page("P", Shape(1, 2, 6, 1, 1, "Box").Replace("</Shape>", string.Concat(Enumerable.Repeat("<a/>", VisioLimits.MaxElements + 1)) + "</Shape>", StringComparison.Ordinal)).Build();

        SourceRejectedException error = Refused(file);

        Assert.Contains("XML elements", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void More_shapes_than_the_limit_are_refused_naming_the_limit_and_exactly_the_limit_is_read()
    {
        static string Shapes(int count) => string.Concat(Enumerable.Range(1, count).Select(i => $"<Shape ID='{i}' Type='Shape'><Cell N='PinX' V='1'/><Cell N='PinY' V='1'/><Cell N='Width' V='1'/><Cell N='Height' V='1'/><Text>x</Text></Shape>"));

        SourceRejectedException error = Refused(new VisioDocBuilder().Page("P", Shapes(VisioLimits.MaxShapes + 1)).Build());
        Assert.Contains("10,000 shapes", error.Message, StringComparison.Ordinal);

        ImportResult exactly = VisioImporter.Import(new VisioDocBuilder().Page("P", Shapes(VisioLimits.MaxShapes)).Build());
        Assert.Equal(VisioLimits.MaxShapes, exactly.Document.Nodes.Count);
    }

    [Fact]
    public void More_pages_than_the_limit_are_refused()
    {
        var builder = new VisioDocBuilder();
        for (int i = 0; i <= VisioLimits.MaxPages; i++)
        {
            builder.Page($"P{i}", string.Empty);
        }

        Assert.Contains("500 pages", Refused(builder.Build()).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void More_masters_than_the_limit_are_refused()
    {
        string masters = $"<Masters xmlns='{MainNamespace}'>" + string.Concat(Enumerable.Range(1, VisioLimits.MaxMasters + 1).Select(i => $"<Master ID='{i + 10}' NameU='M{i}'/>")) + "</Masters>";

        Assert.Contains("5,000 masters", Refused(Replaced("visio/masters/masters.xml", masters)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void More_connections_than_the_limit_are_refused()
    {
        string connects = string.Concat(Enumerable.Repeat("<Connect FromSheet='1' FromCell='BeginX' ToSheet='2' ToCell='PinX'/>", VisioLimits.MaxConnects + 1));
        byte[] file = new VisioDocBuilder().Page("P", Shape(1, 2, 6, 1, 1), connects).Build();

        Assert.Contains("50,000 connections", Refused(file).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_element_with_a_flood_of_attributes_is_refused()
    {
        string attributes = string.Concat(Enumerable.Range(0, VisioLimits.MaxAttributesPerElement + 1).Select(i => $" k{i}='v'"));
        byte[] file = new VisioDocBuilder().Page("P", Shape(1, 2, 6, 1, 1).Replace("<Cell N='PinX'", $"<Cell{attributes} N='PinX'", StringComparison.Ordinal)).Build();

        Assert.Contains("attributes", Refused(file).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void More_relationships_than_the_limit_are_refused()
    {
        string rels = $"<Relationships xmlns='{PackageRelationshipsNamespace}'>" + string.Concat(Enumerable.Range(1, 2_001).Select(i => $"<Relationship Id='r{i}' Type='http://example.test/x' Target='x{i}.xml'/>")) + "</Relationships>";

        Assert.Contains("2,000 relationships", Refused(Replaced("visio/_rels/document.xml.rels", rels)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_huge_label_is_cut_not_held()
    {
        byte[] file = new VisioDocBuilder().Page("P", Shape(1, 2, 6, 1, 1, new string('y', 8_000_000))).Build();

        ImportResult result = VisioImporter.Import(file);

        Assert.Equal(10_000, result.Document.Nodes[0].Label.Length);
    }

    // ---- ZIP: size claims and bombs ------------------------------------------------------------------------------------------------

    [Fact]
    public void A_file_over_the_size_limit_is_refused_before_it_is_opened_as_a_zip()
    {
        byte[] file = new byte[VisioImporter.MaxFileBytes + 1];
        file[0] = (byte)'P';
        file[1] = (byte)'K';
        file[2] = 3;
        file[3] = 4;
        long before = GC.GetAllocatedBytesForCurrentThread();

        SourceRejectedException error = Refused(file);

        Assert.Contains("larger than 3,000,000 bytes", error.Message, StringComparison.Ordinal);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1024 * 1024, "the importer read a file it should have refused by its size");
    }

    [Fact]
    public void A_part_that_claims_and_really_is_more_than_the_limit_is_refused_from_the_directory_without_inflating_it()
    {
        // 65 MiB of one letter compresses to about 65 KiB: a small file that expands past the 64 MiB a part may be.
        byte[] bomb = ZipPadded(Parts(), new Dictionary<string, (string, long, string)> { [Page] = ("<PageContents><!--", 65L * 1024 * 1024, "--></PageContents>") });
        Assert.True(bomb.Length < VisioImporter.MaxFileBytes, $"the bomb is {bomb.Length:N0} bytes: small enough to be sent");
        long before = GC.GetAllocatedBytesForCurrentThread();

        SourceRejectedException error = Refused(bomb);

        Assert.Contains("claims to be 68,157,", error.Message, StringComparison.Ordinal);
        Assert.Contains("more than the 64 MiB Draughtsman reads from one part", error.Message, StringComparison.Ordinal);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 8 * 1024 * 1024, "the importer inflated a part whose declared size was already past the limit");
    }

    [Fact]
    public void Parts_that_add_up_to_more_than_the_total_limit_are_refused_from_the_directory()
    {
        // Five parts each claiming 60 MiB: each is inside the 64 MiB per part, together they are 300 MiB.
        byte[] file = Good();
        foreach (string part in new[] { Page, "visio/pages/pages.xml", "visio/document.xml", "visio/masters/masters.xml", "visio/masters/master1.xml" })
        {
            file = ZipTamper.ClaimUncompressedSize(file, part, 60u * 1024 * 1024);
        }

        SourceRejectedException error = Refused(file);

        Assert.Contains("in all", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_parts_the_reader_inflates_are_counted_together_so_several_large_ones_cannot_pass_one_at_a_time()
    {
        // Three honest parts (valid XML around a comment of padding): the page and the master 63 MiB each and the page list 3 MiB, each under the 64 MiB a part may be and
        // together 129 MiB, over the 128 MiB the reader will inflate in all. Declared sizes add up to far less than the 256 MiB total, so only the running count of what actually
        // comes out of the decompressor can refuse it.
        byte[] file = ZipPadded(Parts(), new Dictionary<string, (string, long, string)>
        {
            ["visio/pages/pages.xml"] = ($"<Pages xmlns='{MainNamespace}' xmlns:r='{RelationshipsNamespace}'><Page ID='0' NameU='P'><Rel r:id='rId1'/></Page><!--", 3L * 1024 * 1024, "--></Pages>"),
            [Page] = ($"<PageContents xmlns='{MainNamespace}'><Shapes>{Instance(1, 2, 3, 3, "Box")}</Shapes><!--", 63L * 1024 * 1024, "--></PageContents>"),
            ["visio/masters/master1.xml"] = ($"<MasterContents xmlns='{MainNamespace}'><Shapes>{Shape(1, 1, 1, 1, 1)}</Shapes><!--", 63L * 1024 * 1024, "--></MasterContents>"),
        });

        SourceRejectedException error = Refused(file);

        Assert.Contains("expands to more than 128 MiB when its parts are read", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_large_but_honest_package_inside_the_limits_is_still_read()
    {
        // A real page padded out to a third of the per part limit: the limits must not bite a legitimate file.
        byte[] file = ZipPadded(Parts(), new Dictionary<string, (string, long, string)>
        {
            [Page] = ($"<PageContents xmlns='{MainNamespace}'><Shapes>{Instance(1, 2, 3, 3, "Big but honest")}</Shapes><!--", 20L * 1024 * 1024, "--></PageContents>"),
        });

        ImportResult result = VisioImporter.Import(file);

        Assert.Equal("Big but honest", Assert.Single(result.Document.Nodes).Label);
    }

    [Fact]
    public void A_size_header_that_claims_more_than_the_part_has_is_refused_as_lying()
    {
        byte[] file = ZipTamper.ClaimUncompressedSize(Good(), Page, 1_000_000);

        SourceRejectedException error = Refused(file);

        Assert.Contains("not the 1,000,000 its header claims", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_size_header_that_claims_less_than_the_part_has_is_refused_by_its_checksum_not_believed()
    {
        // The page is valid XML with a megabyte of trailing whitespace; the header claims the size without the whitespace, so the part reads as a complete document and
        // is only found out by its CRC. .NET stops inflating at the claimed size, so what the importer must not do is believe a short read.
        string xml = $"<PageContents xmlns='{MainNamespace}'><Shapes>{Instance(1, 2, 3, 3, "Box")}</Shapes></PageContents>";
        byte[] page = Utf8(xml + new string(' ', 1_000_000));
        byte[] file = Zip(Parts().Select(p => p.Key == Page ? new KeyValuePair<string, byte[]>(p.Key, page) : p));
        file = ZipTamper.ClaimUncompressedSize(file, Page, (uint)Utf8(xml).Length);

        SourceRejectedException error = Refused(file);

        Assert.Contains("checksum", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reader_that_stops_early_does_not_let_a_lying_part_through()
    {
        // The importer's readers all run to the end of a part, but the rule is the package's own: whatever reads a part, the rest is read and checked against the ZIP's header.
        using VisioPackage lying = VisioPackage.Open(ZipTamper.ClaimUncompressedSize(Good(), Page, 1_000_000), new VisioBudget());
        SourceRejectedException error = Assert.Throws<SourceRejectedException>(() => lying.ReadXml(Page, "The page", reader => reader.MoveToContent()));
        Assert.Contains("not the 1,000,000 its header claims", error.Message, StringComparison.Ordinal);

        using VisioPackage honest = VisioPackage.Open(Good(), new VisioBudget());
        honest.ReadXml(Page, "The page", reader => reader.MoveToContent());
    }

    [Fact]
    public void A_compressed_size_larger_than_the_file_itself_is_refused_as_lying()
    {
        byte[] file = ZipTamper.ClaimCompressedSize(Good(), Page, 50_000_000);

        Assert.Contains("compressed size larger than the file itself", Refused(file).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_zip64_claim_of_eight_gigabytes_is_refused_from_the_directory()
    {
        byte[] file = ZipTamper.ClaimZip64(Good(), Page, 8L * 1024 * 1024 * 1024, 1_000);

        SourceRejectedException error = Refused(file);

        Assert.Contains("claims to be 8,589,934,592 bytes", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_encrypted_entry_is_refused_naming_what_to_do()
    {
        byte[] file = ZipTamper.MarkEncrypted(Good(), Page);

        SourceRejectedException error = Refused(file);

        Assert.Contains("encrypted entry", error.Message, StringComparison.Ordinal);
        Assert.Contains("Remove the password", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_truncated_archive_is_refused_as_damaged()
    {
        byte[] good = Good();
        byte[] truncated = good[..(good.Length * 6 / 10)];

        SourceRejectedException error = Refused(truncated);

        Assert.Contains("damaged or cut short", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_part_whose_compressed_data_is_damaged_is_refused_in_words_without_framework_text()
    {
        // A page big enough that its deflate stream is long, and one flipped byte in the middle of it.
        string shapes = string.Concat(Enumerable.Range(1, 400).Select(i => Shape(i, 1 + (i % 9), 1 + (i % 7), 0.5, 0.5, $"Box {i}")));
        byte[] file = ZipTamper.Corrupt(new VisioDocBuilder().Page("P", shapes).Build(), Page, 200);

        SourceRejectedException error = Refused(file);

        Assert.DoesNotContain("System.", error.Message, StringComparison.Ordinal);
        Assert.Contains("Visio file", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A stream that yields the letter a, <paramref name="length"/> times, and counts how many it was asked for.</summary>
    private sealed class Letters(long length) : Stream
    {
        public long Produced { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position
        {
            get => Produced;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int take = (int)Math.Min(count, length - Produced);
            Array.Fill(buffer, (byte)'a', offset, take);
            Produced += take;
            return take;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void A_part_that_inflates_past_the_limit_is_refused_while_it_is_still_being_read_whatever_its_header_said()
    {
        // The ZIP's own reader stops at the declared length, so this is the defence behind it: a stream that does not stop is cut off at the limit, not read to its end.
        var source = new Letters(long.MaxValue);
        using var stream = new VisioInflatedStream(source, 100, 0, new VisioBudget(), "A part");

        SourceRejectedException error = Assert.Throws<SourceRejectedException>(stream.Complete);

        Assert.Contains("expands to more than 64 MiB", error.Message, StringComparison.Ordinal);
        Assert.True(source.Produced <= VisioLimits.MaxEntryBytes + 8192, $"{source.Produced:N0} bytes were taken from a part that should have been cut off at 64 MiB");
    }

    [Theory]
    [InlineData(500, 100)]
    [InlineData(5, 100)]
    public void A_part_whose_length_is_not_the_length_its_header_claims_is_refused_at_its_end(long actual, long claimed)
    {
        using var stream = new VisioInflatedStream(new Letters(actual), claimed, 0, new VisioBudget(), "A part");

        SourceRejectedException error = Assert.Throws<SourceRejectedException>(stream.Complete);

        Assert.Contains($"is {actual} bytes, not the {claimed} its header claims", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_part_with_the_right_length_and_the_wrong_checksum_is_refused_and_one_with_the_right_checksum_is_read()
    {
        using var wrong = new VisioInflatedStream(new Letters(5), 5, 123, new VisioBudget(), "A part");
        Assert.Contains("does not match its checksum", Assert.Throws<SourceRejectedException>(wrong.Complete).Message, StringComparison.Ordinal);

        byte[] zip = Zip([new("a.bin", Utf8("aaaaa"))]);
        using var archive = new ZipArchive(new MemoryStream(zip));
        using VisioInflatedStream honest = VisioInflatedStream.Of(archive.Entries[0], new VisioBudget(), "A part");
        honest.Complete();
    }

    [Fact]
    public void A_master_the_page_does_not_use_is_never_opened_even_if_it_is_a_bomb()
    {
        List<KeyValuePair<string, byte[]>> parts = new VisioDocBuilder()
            .Master(2, "Process", Shape(1, 1, 1, 1.5, 0.75))
            .Master(3, "Unused", Shape(1, 1, 1, 1, 1))
            .Page("P", Instance(1, 2, 3, 3, "Box"))
            .Parts();
        byte[] file = VisioDocBuilder.Replace(parts, "visio/masters/master2.xml", BillionLaughs(9));

        ImportResult result = VisioImporter.Import(file);

        Assert.Equal("Box", Assert.Single(result.Document.Nodes).Label);
    }

    // ---- ZIP: names and relationships ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("../evil.xml")]
    [InlineData("visio/../../evil.xml")]
    [InlineData("/etc/passwd")]
    [InlineData("\\windows\\evil.xml")]
    [InlineData("C:/evil.xml")]
    [InlineData("C:\\evil.xml")]
    [InlineData("..\\evil.xml")]
    [InlineData("visio\\..\\..\\evil.xml")]
    public void An_entry_name_that_could_write_outside_its_folder_when_unpacked_refuses_the_whole_file(string name)
    {
        List<KeyValuePair<string, byte[]>> parts = Parts();
        parts.Add(new KeyValuePair<string, byte[]>(name, Utf8("<x/>")));

        SourceRejectedException error = Refused(Zip(parts));

        Assert.Contains("could write outside its own folder", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("visio/pages/page1.xml")]
    [InlineData("VISIO/Pages/PAGE1.XML")]
    public void Two_entries_of_one_name_refuse_the_file_because_two_readers_could_pick_different_ones(string second)
    {
        List<KeyValuePair<string, byte[]>> parts = Parts();
        parts.Add(new KeyValuePair<string, byte[]>(second, Utf8("<x/>")));

        SourceRejectedException error = Refused(Zip(parts));

        Assert.Contains("two entries named", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void More_entries_than_the_limit_are_refused()
    {
        List<KeyValuePair<string, byte[]>> parts = Parts();
        parts.AddRange(Enumerable.Range(0, VisioLimits.MaxEntries).Select(i => new KeyValuePair<string, byte[]>($"visio/extra/{i}.bin", [])));

        SourceRejectedException error = Refused(Zip(parts, CompressionLevel.NoCompression));

        Assert.Contains("5,000 entries", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../../outside.xml")]
    [InlineData("/../outside.xml")]
    [InlineData("pages/../../../outside.xml")]
    [InlineData("..\\outside.xml")]
    [InlineData("C:/outside.xml")]
    public void A_relationship_that_climbs_out_of_the_package_refuses_the_file(string target)
    {
        string rels = Relationships(("rId1", "document", target));

        SourceRejectedException error = Refused(VisioDocBuilder.Replace(Parts(), "_rels/.rels", rels));

        Assert.Contains("relationship to", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_page_relationship_that_climbs_out_of_the_package_refuses_the_file()
    {
        string rels = Relationships(("rId1", "page", "../../../../outside.xml"));

        Assert.Contains("climbs out of the package", Refused(VisioDocBuilder.Replace(Parts(), "visio/pages/_rels/pages.xml.rels", rels)).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("visio/document.xml", "pages/pages.xml", "visio/pages/pages.xml")]
    [InlineData("visio/pages/pages.xml", "page1.xml", "visio/pages/page1.xml")]
    [InlineData("visio/pages/pages.xml", "../masters/master1.xml", "visio/masters/master1.xml")]
    [InlineData("visio/pages/pages.xml", "./page1.xml", "visio/pages/page1.xml")]
    [InlineData("visio/pages/pages.xml", "/visio/document.xml", "visio/document.xml")]
    [InlineData("", "visio/document.xml", "visio/document.xml")]
    public void Relationship_targets_resolve_against_the_part_that_holds_them(string source, string target, string expected)
    {
        Assert.Equal(expected, VisioPackage.Resolve(source, target));
    }

    // ---- what is and is not read ---------------------------------------------------------------------------------------------------

    [Fact]
    public void A_part_nothing_points_at_is_never_opened_even_if_it_is_a_bomb()
    {
        List<KeyValuePair<string, byte[]>> parts = Parts();
        parts.Add(new KeyValuePair<string, byte[]>("visio/unused.xml", Utf8(BillionLaughs(9))));
        parts.Add(new KeyValuePair<string, byte[]>("docProps/thumbnail.emf", new byte[10_000]));

        ImportResult result = VisioImporter.Import(Zip(parts));

        Assert.Equal("Box", Assert.Single(result.Document.Nodes).Label);
    }

    [Fact]
    public void Only_the_chosen_page_is_read_so_a_hostile_other_page_does_not_stop_the_good_one()
    {
        List<KeyValuePair<string, byte[]>> parts = new VisioDocBuilder().Page("Bad", Shape(1, 2, 6, 1, 1, "x")).Page("Good", Shape(1, 2, 6, 1, 1, "Fine")).Parts();
        byte[] withBadFirst = VisioDocBuilder.Replace(parts, "visio/pages/page1.xml", BillionLaughs(9));

        ImportResult second = VisioImporter.Import(withBadFirst, new VisioImportOptions { Page = 2 });

        Assert.Equal("Fine", Assert.Single(second.Document.Nodes).Label);
        Assert.Contains("DOCTYPE", Assert.Throws<SourceRejectedException>(() => VisioImporter.Import(withBadFirst)).Message, StringComparison.Ordinal);
    }

    // ---- not a Visio file ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void An_old_binary_visio_file_is_named_as_one_and_told_to_save_as_vsdx()
    {
        byte[] ole = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0];

        SourceRejectedException error = Refused(ole);

        Assert.Contains(".vsd", error.Message, StringComparison.Ordinal);
        Assert.Contains("Save As", error.Message, StringComparison.Ordinal);
        Assert.Contains(".vsdx", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_is_not_a_vsdx_and_a_drawio_file_is_pointed_at_the_right_importer()
    {
        SourceRejectedException error = Refused(Encoding.UTF8.GetBytes("<mxfile><diagram name='x'/></mxfile>"));

        Assert.Contains("Import draw.io", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new byte[0], "empty")]
    [InlineData(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, "not a ZIP package")]
    public void Bytes_that_are_not_a_zip_are_refused_in_words(byte[] bytes, string expected)
    {
        Assert.Contains(expected, Refused(bytes).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_zip_that_is_not_a_visio_drawing_is_refused_as_one()
    {
        byte[] docx = Zip([new("[Content_Types].xml", Utf8("<Types/>")), new("word/document.xml", Utf8("<w:document xmlns:w='x'/>"))]);

        Assert.Contains("not a Visio drawing", Refused(docx).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_document_part_that_is_not_a_visio_document_is_refused()
    {
        byte[] file = Replaced("visio/document.xml", "<Other/>");

        Assert.Contains("not a VisioDocument", Refused(file).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_part_that_is_not_well_formed_xml_is_refused_in_words()
    {
        SourceRejectedException error = Refused(Replaced(Page, "<PageContents><Shapes>"));

        Assert.Contains("is not well-formed XML", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("System.", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Entry_names_are_shown_in_messages_without_control_characters()
    {
        List<KeyValuePair<string, byte[]>> parts = Parts();
        parts.Add(new KeyValuePair<string, byte[]>("../evil\u0007\n.xml", Utf8("<x/>")));

        SourceRejectedException error = Refused(Zip(parts));

        Assert.DoesNotContain('\u0007', error.Message);
        Assert.DoesNotContain('\n', error.Message);
    }
}
