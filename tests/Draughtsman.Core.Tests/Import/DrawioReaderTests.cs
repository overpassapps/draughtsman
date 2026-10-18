using System.Text;
using Draughtsman.Core.Import;
using Draughtsman.Core.Import.Drawio;

namespace Draughtsman.Core.Tests.Import;

/// <summary>Reading the file: plain and compressed pages, one page chosen out of many, and the cells in them.</summary>
public class DrawioReaderTests
{
    private static (double X, double Y, double W, double H, bool Relative, int Points) Shape(DrawioGeometry? geometry)
    {
        Assert.NotNull(geometry);
        return (geometry.X, geometry.Y, geometry.Width, geometry.Height, geometry.Relative, geometry.Points.Count);
    }

    private static readonly string s_oneBox = DrawioXml.Model(DrawioXml.Vertex("2", "Box", "rounded=1;", 10, 20, 120, 60));

    [Fact]
    public void A_plain_file_gives_its_page_and_cells()
    {
        DrawioFile file = DrawioReader.Read(DrawioXml.File(s_oneBox));

        DrawioPageInfo page = Assert.Single(file.Pages);
        Assert.Equal("Page-1", page.Name);
        Assert.Equal("page1", page.Id);
        Assert.Equal(0, file.SelectedPage);
        Assert.Equal(["0", "1", "2"], file.Cells.Select(c => c.Id));

        DrawioCell box = file.Cells.Single(c => c.Id == "2");
        Assert.True(box.Vertex);
        Assert.False(box.Edge);
        Assert.Equal("Box", box.Value);
        Assert.Equal("rounded=1;", box.Style);
        Assert.Equal("1", box.Parent);
        Assert.Equal((10.0, 20.0, 120.0, 60.0, false, 0), Shape(box.Geometry));
    }

    [Fact]
    public void A_bare_model_is_one_unnamed_page()
    {
        DrawioFile file = DrawioReader.Read(s_oneBox);

        DrawioPageInfo page = Assert.Single(file.Pages);
        Assert.Equal(string.Empty, page.Name);
        Assert.Contains(file.Cells, c => c.Id == "2");
    }

    [Fact]
    public void A_compressed_page_is_decoded_to_the_same_cells_as_a_plain_one()
    {
        DrawioFile plain = DrawioReader.Read(DrawioXml.File(s_oneBox));
        DrawioFile compressed = DrawioReader.Read(DrawioXml.CompressedFile(s_oneBox));

        Assert.Equal(plain.Cells.Select(c => (c.Id, c.Value, c.Style, c.Geometry?.X, c.Geometry?.Width)), compressed.Cells.Select(c => (c.Id, c.Value, c.Style, c.Geometry?.X, c.Geometry?.Width)));
        Assert.Equal("Page-1", Assert.Single(compressed.Pages).Name);
    }

    [Fact]
    public void A_compressed_page_that_carries_a_zlib_header_is_read_too()
    {
        string payload = Convert.ToBase64String(DrawioXml.ZLib(Encoding.UTF8.GetBytes(Uri.EscapeDataString(s_oneBox))));

        DrawioFile file = DrawioReader.Read($"<mxfile><diagram name=\"z\">{payload}</diagram></mxfile>");

        Assert.Contains(file.Cells, c => c.Id == "2");
    }

    [Fact]
    public void A_compressed_payload_with_line_breaks_in_it_is_read()
    {
        string payload = DrawioXml.Compress(s_oneBox);
        string wrapped = string.Join("\n  ", Enumerable.Range(0, (payload.Length + 39) / 40).Select(i => payload.Substring(i * 40, Math.Min(40, payload.Length - (i * 40)))));

        DrawioFile file = DrawioReader.Read($"<mxfile><diagram name=\"w\">\n  {wrapped}\n</diagram></mxfile>");

        Assert.Contains(file.Cells, c => c.Id == "2");
    }

    [Fact]
    public void A_bare_compressed_payload_with_no_file_around_it_is_read()
    {
        DrawioFile file = DrawioReader.Read(DrawioXml.Compress(s_oneBox));

        Assert.Contains(file.Cells, c => c.Id == "2");
    }

    [Fact]
    public void The_first_page_is_chosen_unless_another_is_asked_for_and_every_page_is_listed()
    {
        string source = DrawioXml.File(
            DrawioXml.Model(DrawioXml.Vertex("a", "On page one", string.Empty, 0, 0, 10, 10)),
            DrawioXml.Model(DrawioXml.Vertex("b", "On page two", string.Empty, 0, 0, 10, 10)));
        source = source.Replace("</mxfile>", $"<diagram id=\"page3\" name=\"Third\">{DrawioXml.Compress(DrawioXml.Model(DrawioXml.Vertex("c", "On page three", string.Empty, 0, 0, 10, 10)))}</diagram></mxfile>", StringComparison.Ordinal);

        DrawioFile first = DrawioReader.Read(source);
        DrawioFile second = DrawioReader.Read(source, page: 2);
        DrawioFile third = DrawioReader.Read(source, page: 3);

        Assert.Equal(["Page-1", "Page-2", "Third"], first.Pages.Select(p => p.Name));
        Assert.Equal(0, first.SelectedPage);
        Assert.Equal(1, second.SelectedPage);
        Assert.Equal(2, third.SelectedPage);
        Assert.Equal("On page one", first.Cells.Single(c => c.Vertex).Value);
        Assert.Equal("On page two", second.Cells.Single(c => c.Vertex).Value);
        Assert.Equal("On page three", third.Cells.Single(c => c.Vertex).Value);
    }

    [Fact]
    public void A_page_past_the_end_is_an_argument_error_that_says_how_many_pages_the_file_has()
    {
        string source = DrawioXml.File(s_oneBox, s_oneBox);

        ArgumentException error = Assert.Throws<ArgumentException>(() => DrawioReader.Read(source, page: 3));

        Assert.Contains("2 pages", error.Message, StringComparison.Ordinal);
        Assert.Contains("page 3", error.Message, StringComparison.Ordinal);
        // The message goes to a person as it is: no "(Parameter 'page')" framework suffix after it.
        Assert.DoesNotContain("Parameter", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_of_one_page_says_page_not_pages()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() => DrawioReader.Read(DrawioXml.File(s_oneBox), page: 2));

        Assert.Contains("1 page;", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Pages_are_numbered_from_one(int page)
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() => DrawioReader.Read(DrawioXml.File(s_oneBox), page));

        Assert.Contains("numbered from 1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_page_that_is_not_the_chosen_one_is_never_decoded()
    {
        // Page 1 is garbage; choosing page 2 must not trip over it, and choosing page 1 must say so.
        string source = $"<mxfile><diagram name=\"Broken\">!!!not base64!!!</diagram><diagram name=\"Fine\">{DrawioXml.Compress(s_oneBox)}</diagram></mxfile>";

        DrawioFile fine = DrawioReader.Read(source, page: 2);
        SourceRejectedException broken = Assert.Throws<SourceRejectedException>(() => DrawioReader.Read(source, page: 1));

        Assert.Contains(fine.Cells, c => c.Id == "2");
        Assert.Contains("Broken", broken.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_with_no_pages_has_no_cells()
    {
        DrawioFile file = DrawioReader.Read("<mxfile host=\"x\"/>");

        Assert.Empty(file.Pages);
        Assert.Equal(-1, file.SelectedPage);
        Assert.Empty(file.Cells);
    }

    [Fact]
    public void An_object_wrapper_gives_its_label_and_custom_data_to_the_cell_inside()
    {
        string model = DrawioXml.Model(
            "<object label=\"Order &lt;b&gt;API&lt;/b&gt;\" id=\"7\" owner=\"payments\" link=\"https://example.test/wiki\" placeholders=\"1\"><mxCell style=\"rounded=1;html=1;\" vertex=\"1\" parent=\"1\"><mxGeometry x=\"5\" y=\"6\" width=\"100\" height=\"40\" as=\"geometry\"/></mxCell></object>",
            "<UserObject label=\"Legacy\" id=\"8\"><mxCell style=\"\" vertex=\"1\" parent=\"1\"><mxGeometry as=\"geometry\"/></mxCell></UserObject>");

        DrawioFile file = DrawioReader.Read(DrawioXml.File(model));

        DrawioCell api = file.Cells.Single(c => c.Id == "7");
        Assert.True(api.Vertex);
        Assert.Equal("Order <b>API</b>", api.Value);
        Assert.Equal("1", api.Parent);
        Assert.Equal("payments", api.Attributes["owner"]);
        Assert.Equal("https://example.test/wiki", api.Attributes["link"]);
        Assert.Equal("1", api.Attributes["placeholders"]);
        Assert.False(api.Attributes.ContainsKey("id"));
        Assert.False(api.Attributes.ContainsKey("label"));
        Assert.Equal((5.0, 6.0, 100.0, 40.0, false, 0), Shape(api.Geometry));
        Assert.Equal("Legacy", file.Cells.Single(c => c.Id == "8").Value);
    }

    [Fact]
    public void An_edge_gives_its_ends_and_the_points_it_was_bent_through()
    {
        string model = DrawioXml.Model(
            DrawioXml.Vertex("2", "A", string.Empty, 0, 0, 40, 40),
            DrawioXml.Vertex("3", "B", string.Empty, 200, 0, 40, 40),
            DrawioXml.Edge("4", "2", "3", "go", "edgeStyle=orthogonalEdgeStyle;", "<Array as=\"points\"><mxPoint x=\"100\" y=\"-20\"/><mxPoint x=\"150.5\" y=\"-20\"/></Array><mxPoint x=\"1\" y=\"2\" as=\"sourcePoint\"/>"));

        DrawioCell edge = DrawioReader.Read(DrawioXml.File(model)).Cells.Single(c => c.Id == "4");

        Assert.True(edge.Edge);
        Assert.Equal("2", edge.Source);
        Assert.Equal("3", edge.Target);
        Assert.Equal("go", edge.Value);
        Assert.Equal([new DrawioPoint(100, -20), new DrawioPoint(150.5, -20)], edge.Geometry!.Points);
        Assert.True(edge.Geometry.Relative);
    }

    [Fact]
    public void Attributes_the_reader_does_not_use_do_not_matter_and_hidden_cells_are_marked()
    {
        string model = DrawioXml.Model("<mxCell id=\"2\" value=\"h\" style=\"\" vertex=\"1\" visible=\"0\" collapsed=\"1\" connectable=\"0\" parent=\"1\"><mxGeometry x=\"1\" y=\"1\" width=\"1\" height=\"1\" as=\"geometry\"/></mxCell>");

        DrawioCell hidden = DrawioReader.Read(DrawioXml.File(model)).Cells.Single(c => c.Id == "2");

        Assert.False(hidden.Visible);
    }

    [Theory]
    [InlineData("<mxGeometry x=\"abc\" y=\"1e999\" width=\"NaN\" height=\"-5\" as=\"geometry\"/>", 0.0, 0.0, 0.0, -5.0)]
    [InlineData("<mxGeometry as=\"geometry\"/>", 0.0, 0.0, 0.0, 0.0)]
    [InlineData("<mxGeometry x=\"-12.5\" y=\"7\" width=\"3\" height=\"4\" as=\"geometry\"/>", -12.5, 7.0, 3.0, 4.0)]
    public void A_geometry_number_that_is_missing_or_not_finite_reads_as_zero(string geometry, double x, double y, double w, double h)
    {
        string model = DrawioXml.Model($"<mxCell id=\"2\" value=\"\" style=\"\" vertex=\"1\" parent=\"1\">{geometry}</mxCell>");

        DrawioGeometry? read = DrawioReader.Read(DrawioXml.File(model)).Cells.Single(c => c.Id == "2").Geometry;

        Assert.Equal((x, y, w, h, false, 0), Shape(read));
    }

    [Fact]
    public void Source_with_a_byte_order_mark_and_an_xml_declaration_is_read()
    {
        string source = "﻿<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + DrawioXml.File(s_oneBox);

        Assert.Contains(DrawioReader.Read(source).Cells, c => c.Id == "2");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n ")]
    [InlineData("<html><body>Not draw.io</body></html>")]
    [InlineData("<mxfile><diagram>")]
    [InlineData("<mxGraphModel><root></mxGraphModel>")]
    [InlineData("just some words")]
    [InlineData("flowchart TD\n  A --> B")]
    public void Something_that_is_not_a_draw_io_file_is_refused_in_words_a_person_can_act_on(string source)
    {
        SourceRejectedException error = Assert.Throws<SourceRejectedException>(() => DrawioReader.Read(source));

        Assert.Contains("draw.io", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_edge_label_cell_keeps_the_edge_it_hangs_off()
    {
        string model = DrawioXml.Model(
            DrawioXml.Vertex("2", "A", string.Empty, 0, 0, 40, 40),
            DrawioXml.Vertex("3", "B", string.Empty, 200, 0, 40, 40),
            DrawioXml.Edge("4", "2", "3"),
            "<mxCell id=\"5\" value=\"yes\" style=\"edgeLabel;html=1;align=center;\" vertex=\"1\" connectable=\"0\" parent=\"4\"><mxGeometry x=\"-0.1\" relative=\"1\" as=\"geometry\"><mxPoint as=\"offset\"/></mxGeometry></mxCell>");

        DrawioCell label = DrawioReader.Read(DrawioXml.File(model)).Cells.Single(c => c.Id == "5");

        Assert.Equal("4", label.Parent);
        Assert.True(label.Geometry!.Relative);
    }

    [Fact]
    public void Characters_that_need_escaping_come_back_as_the_author_typed_them()
    {
        string model = DrawioXml.Model(DrawioXml.Vertex("2", "Tom & Jerry <3 \"quotes\"", string.Empty, 0, 0, 1, 1));

        Assert.Equal("Tom & Jerry <3 \"quotes\"", DrawioReader.Read(DrawioXml.File(model)).Cells.Single(c => c.Id == "2").Value);
    }
}
