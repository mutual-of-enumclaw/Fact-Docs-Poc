using System.Text.Json;
using AngleSharp.Dom;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using FaCT.DocDesigner.POC.Import;
using FaCT.DocDesigner.POC.Legacy;
using static FaCT.DocDesigner.POC.Tests.Import.Docx;

namespace FaCT.DocDesigner.POC.Tests.Import;

public sealed class DocxImporterTests : IDisposable
{
	private readonly TempAssets _assets = new();

	public void Dispose() => _assets.Dispose();

	private DocumentImportResult Import(byte[] docx, DocxImportOptions? options = null) => new DocxImporter(_assets.Store).Import(docx, options);

	private static readonly DocxImportOptions NoPlaceholders = new(PlaceholderStyles.None);

	private (DocumentImportResult Result, IDocument Dom) ImportDom(byte[] docx, DocxImportOptions? options = null)
	{
		var result = Import(docx, options);
		return (result, Html.Parse(result.Html));
	}

	// ---- Paragraphs and headings ------------------------------------------------------------------------------------

	[Fact]
	public void Plain_paragraphs_become_p_elements()
	{
		var (result, dom) = ImportDom(WithBody(P("First paragraph."), P("Second paragraph.")));

		Assert.Equal("docx", result.Format);
		var paragraphs = dom.QuerySelectorAll("p");
		Assert.Equal(["First paragraph.", "Second paragraph."], paragraphs.Select(p => p.TextContent));
		Assert.Equal(2, result.Counts["paragraphs"]);
		Assert.Null(result.Model);
		Assert.Empty(result.Fields);
	}

	[Fact]
	public void Title_and_heading_styles_become_h1_to_h3()
	{
		var docx = Create((main, body) =>
		{
			AddStyles(main,
				ParagraphStyle("Title", "Title"),
				ParagraphStyle("Heading1", "heading 1"),
				ParagraphStyle("Heading2", "heading 2"),
				ParagraphStyle("Heading3", "heading 3"),
				ParagraphStyle("Heading4", "heading 4"),
				ParagraphStyle("CoverageHeading", "Coverage Heading", basedOn: "Heading2"),
				ParagraphStyle("BodyText", "Body Text"));
			body.Append(
				Styled("Title", "Policy Summary"),
				Styled("Heading1", "Section One"),
				Styled("Heading2", "Coverage"),
				Styled("Heading3", "Details"),
				Styled("Heading4", "Deeper"),
				Styled("CoverageHeading", "Custom"),
				Styled("BodyText", "Body"));
		});

		var (result, dom) = ImportDom(docx);

		Assert.Equal(["h1", "h1", "h2", "h3", "h3", "h2", "p"], dom.Body!.Children.Select(e => e.LocalName));
		Assert.Equal("Custom", dom.QuerySelectorAll("h2")[1].TextContent);
		Assert.Equal(6, result.Counts["headings"]);
		Assert.Equal(1, result.Counts["paragraphs"]);
	}

	[Fact]
	public void Heading_style_ids_without_a_styles_part_still_map()
	{
		var (_, dom) = ImportDom(WithBody(Styled("Heading2", "No styles part")));
		Assert.Equal("No styles part", dom.QuerySelector("h2")!.TextContent);
	}

	[Theory]
	[InlineData("center", "text-align:center")]
	[InlineData("right", "text-align:right")]
	[InlineData("both", "text-align:justify")]
	public void Paragraph_alignment_is_kept(string justification, string expected)
	{
		var value = justification switch
		{
			"center" => JustificationValues.Center,
			"right" => JustificationValues.Right,
			_ => JustificationValues.Both
		};
		var (_, dom) = ImportDom(WithBody(P(new ParagraphProperties(new Justification { Val = value }), R("Aligned"))));
		Assert.Contains(expected, dom.QuerySelector("p")!.GetAttribute("style"));
	}

	[Fact]
	public void Left_indentation_becomes_a_margin_in_points()
	{
		var (_, dom) = ImportDom(WithBody(P(new ParagraphProperties(new Indentation { Left = "720" }), R("Indented"))));
		Assert.Equal("margin-left:36pt;", dom.QuerySelector("p")!.GetAttribute("style"));
	}

	[Fact]
	public void Empty_paragraphs_keep_their_vertical_space()
	{
		var (result, _) = ImportDom(WithBody(P(), P("After")));
		Assert.StartsWith("<p>&nbsp;</p>", result.Html);
	}

	// ---- Run formatting ---------------------------------------------------------------------------------------------

	[Fact]
	public void Character_formatting_becomes_semantic_tags()
	{
		var (_, dom) = ImportDom(WithBody(P(
			R("bold", new RunProperties(new Bold())),
			R("italic", new RunProperties(new Italic())),
			R("under", new RunProperties(new Underline { Val = UnderlineValues.Single })),
			R("struck", new RunProperties(new Strike())),
			R("2", new RunProperties(new VerticalTextAlignment { Val = VerticalPositionValues.Superscript })),
			R("i", new RunProperties(new VerticalTextAlignment { Val = VerticalPositionValues.Subscript })),
			R("plain"))));

		Assert.Equal("bold", dom.QuerySelector("strong")!.TextContent);
		Assert.Equal("italic", dom.QuerySelector("em")!.TextContent);
		Assert.Equal("under", dom.QuerySelector("u")!.TextContent);
		Assert.Equal("struck", dom.QuerySelector("s")!.TextContent);
		Assert.Equal("2", dom.QuerySelector("sup")!.TextContent);
		Assert.Equal("i", dom.QuerySelector("sub")!.TextContent);
		Assert.EndsWith("plain", dom.QuerySelector("p")!.TextContent);
	}

	[Fact]
	public void Explicitly_switched_off_toggles_are_not_applied()
	{
		var (_, dom) = ImportDom(WithBody(P(
			R("not bold", new RunProperties(new Bold { Val = false })),
			R("no underline", new RunProperties(new Underline { Val = UnderlineValues.None })))));
		Assert.Null(dom.QuerySelector("strong"));
		Assert.Null(dom.QuerySelector("u"));
	}

	[Fact]
	public void Colour_size_highlight_and_caps_become_inline_styles()
	{
		var (_, dom) = ImportDom(WithBody(P(R("styled", new RunProperties(
			new Caps(),
			new Color { Val = "1F4E79" },
			new FontSize { Val = "28" },
			new Highlight { Val = HighlightColorValues.Yellow })))));

		var style = dom.QuerySelector("p > span")!.GetAttribute("style");
		Assert.Contains("color:#1F4E79;", style);
		Assert.Contains("font-size:14pt;", style);
		Assert.Contains("background:#FFFF00;", style);
		Assert.Contains("text-transform:uppercase;", style);
	}

	[Fact]
	public void Automatic_and_invalid_colours_are_ignored()
	{
		var (result, _) = ImportDom(WithBody(P(
			R("auto", new RunProperties(new Color { Val = "auto" })),
			R("bad", new RunProperties(new Color { Val = "red;background:url(x)" })))));
		Assert.DoesNotContain("color", result.Html);
		Assert.DoesNotContain("url(", result.Html);
	}

	[Fact]
	public void Consecutive_runs_with_the_same_formatting_share_one_element()
	{
		var (_, dom) = ImportDom(WithBody(P(
			R("Mutual ", new RunProperties(new Bold())),
			R("of ", new RunProperties(new Bold())),
			R("Enumclaw", new RunProperties(new Bold())),
			R(" Insurance"))));

		var strong = Assert.Single(dom.QuerySelectorAll("strong"));
		Assert.Equal("Mutual of Enumclaw", strong.TextContent);
	}

	[Fact]
	public void Tabs_and_line_breaks_are_kept()
	{
		var (result, dom) = ImportDom(WithBody(P(
			new Run(new Text("Name:"), new TabChar(), new Text("Value")),
			new Run(new Break(), new Text("Next line")))));
		Assert.Contains("&emsp;", result.Html);
		Assert.NotNull(dom.QuerySelector("p > br"));
		Assert.EndsWith("Next line", dom.QuerySelector("p")!.TextContent);
	}

	[Fact]
	public void Repeated_spaces_are_preserved()
	{
		var (result, _) = ImportDom(WithBody(P("A   B")));
		Assert.Contains("A &nbsp;&nbsp;B", result.Html);
	}

	// ---- Safety -----------------------------------------------------------------------------------------------------

	[Fact]
	public void Markup_in_document_text_is_encoded()
	{
		var (result, dom) = ImportDom(WithBody(P("<script>alert('x')</script> & <img src=x onerror=alert(1)>")));
		Assert.Null(dom.QuerySelector("script"));
		Assert.Null(dom.QuerySelector("img"));
		Assert.Contains("&lt;script&gt;", result.Html);
		Assert.Equal("<script>alert('x')</script> & <img src=x onerror=alert(1)>", dom.QuerySelector("p")!.TextContent);
	}

	[Fact]
	public void Liquid_syntax_in_document_text_is_never_live()
	{
		var (result, dom) = ImportDom(WithBody(P("Total: {{ premium }} {% if x %}yes{% endif %} {{{ triple }}}")), NoPlaceholders);
		Assert.DoesNotMatch(@"\{[{%]", result.Html);
		Assert.Equal("Total: {{ premium }} {% if x %}yes{% endif %} {{{ triple }}}", dom.QuerySelector("p")!.TextContent);
	}

	[Fact]
	public void Braces_split_across_runs_do_not_join_into_liquid()
	{
		var (result, dom) = ImportDom(WithBody(P(R("Use {"), R("{ name }} or {"), R("% tag %}"))), NoPlaceholders);
		Assert.DoesNotMatch(@"\{[{%]", result.Html);
		Assert.Equal("Use {{ name }} or {% tag %}", dom.QuerySelector("p")!.TextContent);
	}

	[Fact]
	public void Braces_are_dropped_from_attribute_values()
	{
		var docx = Create((main, body) =>
		{
			var id = AddImage(main, ImagePartType.Png, Html.Png);
			body.Append(P(Image(id, 10, 10, alt: "{{ brand.name }}")));
		});
		var (result, dom) = ImportDom(docx);
		Assert.Equal(" brand.name ", dom.QuerySelector("img")!.GetAttribute("alt"));
		Assert.DoesNotContain("{", result.Html);
	}

	// ---- Lists ------------------------------------------------------------------------------------------------------

	[Fact]
	public void Bulleted_and_numbered_paragraphs_become_lists()
	{
		var docx = Create((main, body) =>
		{
			AddNumbering(main, NumberFormatValues.Bullet, NumberFormatValues.Decimal);
			body.Append(
				ListItem(1, 0, "Apple"),
				ListItem(1, 0, "Banana"),
				P("Between"),
				ListItem(2, 0, "Step one"),
				ListItem(2, 0, "Step two"),
				ListItem(2, 0, "Step three"));
		});

		var (result, dom) = ImportDom(docx);

		Assert.Equal(["ul", "p", "ol"], dom.Body!.Children.Select(e => e.LocalName));
		Assert.Equal(["Apple", "Banana"], dom.QuerySelectorAll("ul > li").Select(li => li.TextContent));
		Assert.Equal(["Step one", "Step two", "Step three"], dom.QuerySelectorAll("ol > li").Select(li => li.TextContent));
		Assert.Equal(5, result.Counts["listItems"]);
	}

	[Fact]
	public void Nested_list_levels_become_nested_lists()
	{
		var docx = Create((main, body) =>
		{
			AddNumbering(main, NumberFormatValues.Bullet);
			body.Append(
				ListItem(1, 0, "Property"),
				ListItem(1, 1, "Building"),
				ListItem(1, 1, "Contents"),
				ListItem(1, 2, "Stock"),
				ListItem(1, 0, "Liability"));
		});

		var (result, dom) = ImportDom(docx);

		var top = Assert.Single(dom.Body!.Children);
		Assert.Equal("ul", top.LocalName);
		var items = top.Children.Where(c => c.LocalName == "li").ToList();
		Assert.Equal(2, items.Count);
		Assert.StartsWith("Property", items[0].TextContent);
		Assert.Equal(["Building", "ContentsStock"], items[0].QuerySelector("ul")!.Children.Select(li => li.TextContent));
		Assert.Equal("Stock", dom.QuerySelector("ul ul ul > li")!.TextContent);
		Assert.Equal("Liability", items[1].TextContent);
		// Well-formed: every opened list/item is closed.
		Assert.Equal(Count(result.Html, "<ul>"), Count(result.Html, "</ul>"));
		Assert.Equal(Count(result.Html, "<li>"), Count(result.Html, "</li>"));
	}

	[Fact]
	public void A_list_that_starts_below_level_zero_is_still_well_formed()
	{
		var docx = Create((main, body) =>
		{
			AddNumbering(main, NumberFormatValues.Decimal);
			body.Append(ListItem(1, 2, "Deep first"), ListItem(1, 0, "Back to top"));
		});

		var (result, dom) = ImportDom(docx);

		Assert.Equal(Count(result.Html, "<ol>"), Count(result.Html, "</ol>"));
		Assert.Equal(Count(result.Html, "<li"), Count(result.Html, "</li>"));
		Assert.Equal("Deep first", dom.QuerySelector("ol ol ol > li")!.TextContent);
		// The items holding the nested list have no text, so they show no number.
		Assert.Equal(2, dom.QuerySelectorAll("li[style='list-style:none']").Length);
	}

	[Fact]
	public void Numbering_continues_after_an_interrupting_paragraph_like_word()
	{
		var docx = Create((main, body) =>
		{
			AddNumbering(main, NumberFormatValues.Decimal, NumberFormatValues.Decimal);
			body.Append(
				ListItem(1, 0, "One"),
				ListItem(1, 0, "Two"),
				P("Screenshot goes here"),
				ListItem(1, 0, "Three"),
				ListItem(1, 1, "Three-a"),
				P("Note"),
				ListItem(1, 1, "Three-b"),
				ListItem(1, 0, "Four"),
				ListItem(1, 1, "Four-a"),
				ListItem(2, 0, "Other list starts at one"));
		});

		var (_, dom) = ImportDom(docx);

		var lists = dom.Body!.Children.Where(e => e.LocalName == "ol").ToList();
		Assert.Equal(4, lists.Count);
		Assert.Null(lists[0].GetAttribute("start"));
		Assert.Equal("3", lists[1].GetAttribute("start"));
		// Re-opened to hold the resumed sub-list, the outer list continues Three, so Four is numbered 4.
		Assert.Equal("3", lists[2].GetAttribute("start"));
		// The sub-list resumed after "Note" continues at b; the sub-list under Four restarts.
		Assert.Equal("2", lists[2].QuerySelector("ol")!.GetAttribute("start"));
		Assert.Null(lists[2].QuerySelectorAll("ol")[1].GetAttribute("start"));
		Assert.Null(lists[3].GetAttribute("start"));
	}

	[Fact]
	public void Switching_between_bullets_and_numbers_at_one_level_starts_a_new_list()
	{
		var docx = Create((main, body) =>
		{
			AddNumbering(main, NumberFormatValues.Bullet, NumberFormatValues.LowerLetter);
			body.Append(ListItem(1, 0, "Bullet"), ListItem(2, 0, "Letter"));
		});

		var (_, dom) = ImportDom(docx);

		Assert.Equal(["ul", "ol"], dom.Body!.Children.Select(e => e.LocalName));
	}

	[Fact]
	public void List_styles_carry_their_numbering()
	{
		var docx = Create((main, body) =>
		{
			AddNumbering(main, NumberFormatValues.Bullet);
			AddStyles(main,
				ParagraphStyle("ListBullet", "List Bullet", numbering: new NumberingProperties(new NumberingId { Val = 1 })),
				ParagraphStyle("ListBullet2", "List Bullet 2", basedOn: "ListBullet"));
			body.Append(Styled("ListBullet", "From style"), Styled("ListBullet2", "Inherited"));
		});

		var (_, dom) = ImportDom(docx);

		Assert.Equal(["From style", "Inherited"], dom.QuerySelectorAll("ul > li").Select(li => li.TextContent));
	}

	[Fact]
	public void Numbering_id_zero_means_not_a_list()
	{
		var docx = Create((main, body) =>
		{
			AddNumbering(main, NumberFormatValues.Bullet);
			body.Append(ListItem(0, 0, "Numbering removed"));
		});

		var (_, dom) = ImportDom(docx);

		Assert.Null(dom.QuerySelector("ul"));
		Assert.Equal("Numbering removed", dom.QuerySelector("p")!.TextContent);
	}

	// ---- Tables -----------------------------------------------------------------------------------------------------

	[Fact]
	public void Tables_keep_rows_cells_and_header_rows()
	{
		var docx = WithBody(Table(
			new TableProperties(AllBorders()),
			HeaderRow(Cell("Coverage"), Cell("Limit")),
			Row(Cell("Building"), Cell("$500,000")),
			Row(Cell("Contents"), Cell("$100,000"))));

		var (result, dom) = ImportDom(docx);

		var table = dom.QuerySelector("table")!;
		Assert.Equal("doc-table tbl-b-all", table.ClassName);
		Assert.Equal(["Coverage", "Limit"], table.QuerySelectorAll("thead th").Select(c => c.TextContent));
		Assert.Equal(2, table.QuerySelectorAll("tbody tr").Length);
		Assert.Equal("$100,000", table.QuerySelectorAll("tbody td")[3].TextContent);
		Assert.Equal(1, result.Counts["tables"]);
	}

	[Fact]
	public void Tables_without_header_rows_have_only_a_body()
	{
		var (_, dom) = ImportDom(WithBody(Table(null, Row(Cell("a"), Cell("b")))));
		Assert.Null(dom.QuerySelector("thead"));
		Assert.Equal(2, dom.QuerySelectorAll("tbody td").Length);
	}

	[Fact]
	public void Horizontally_merged_cells_get_colspan()
	{
		var docx = WithBody(Table(null,
			Row(Cell("Spans two", new TableCellProperties(new GridSpan { Val = 2 })), Cell("c")),
			Row(Cell("a"), Cell("b"), Cell("c"))));

		var (_, dom) = ImportDom(docx);

		Assert.Equal("2", dom.QuerySelector("td")!.GetAttribute("colspan"));
	}

	[Fact]
	public void Vertically_merged_cells_get_rowspan_and_the_continuations_are_dropped()
	{
		static TableCellProperties Merge(MergedCellValues? value) =>
			new(value is null ? new VerticalMerge() : new VerticalMerge { Val = value.Value });

		var docx = WithBody(Table(null,
			Row(Cell("Location 1", Merge(MergedCellValues.Restart)), Cell("Building")),
			Row(Cell("", Merge(MergedCellValues.Continue)), Cell("Contents")),
			Row(Cell("", Merge(null)), Cell("Stock")),
			Row(Cell("Location 2"), Cell("Building"))));

		var (_, dom) = ImportDom(docx);

		var rows = dom.QuerySelectorAll("tr");
		Assert.Equal("3", rows[0].Children[0].GetAttribute("rowspan"));
		Assert.Single(rows[1].Children);
		Assert.Single(rows[2].Children);
		Assert.Equal(2, rows[3].Children.Length);
		Assert.Null(rows[3].Children[0].GetAttribute("rowspan"));
	}

	[Fact]
	public void Cell_shading_and_vertical_alignment_are_kept()
	{
		var docx = WithBody(Table(null, Row(Cell("Shaded", new TableCellProperties(
			new Shading { Fill = "D9D9D9", Val = ShadingPatternValues.Clear },
			new TableCellVerticalAlignment { Val = TableVerticalAlignmentValues.Center })))));

		var (_, dom) = ImportDom(docx);

		var style = dom.QuerySelector("td")!.GetAttribute("style");
		Assert.Contains("background:#D9D9D9;", style);
		Assert.Contains("vertical-align:middle;", style);
	}

	[Fact]
	public void Tables_without_borders_are_marked_borderless()
	{
		var (_, dom) = ImportDom(WithBody(Table(new TableProperties(NoBorders()), Row(Cell("x")))));
		Assert.Equal("doc-table tbl-b-none", dom.QuerySelector("table")!.ClassName);
	}

	[Fact]
	public void Table_Grid_style_means_borders()
	{
		var docx = Create((main, body) =>
		{
			AddStyles(main, TableStyle("TableGrid", "Table Grid", AllBorders()), TableStyle("Plain", "Plain Table"));
			body.Append(
				Table(new TableProperties(new TableStyle { Val = "TableGrid" }), Row(Cell("grid"))),
				Table(new TableProperties(new TableStyle { Val = "Plain" }), Row(Cell("plain"))));
		});

		var (_, dom) = ImportDom(docx);

		Assert.Equal(["doc-table tbl-b-all", "doc-table tbl-b-none"], dom.QuerySelectorAll("table").Select(t => t.ClassName));
	}

	[Fact]
	public void Nested_tables_are_imported_inside_their_cell()
	{
		var inner = Table(null, Row(Cell("inner")));
		var outerCell = new TableCell(P("outer"), inner, P());
		var (result, dom) = ImportDom(WithBody(Table(null, Row(outerCell))));

		Assert.Equal("inner", dom.QuerySelector("td table td")!.TextContent);
		Assert.Equal(2, result.Counts["tables"]);
	}

	// ---- Merge fields and content controls --------------------------------------------------------------------------

	[Fact]
	public void Merge_fields_become_data_fields_and_a_sample_model()
	{
		var docx = WithBody(P(
			new OpenXmlElement[] { R("Policy ") }
				.Concat(MergeField(" MERGEFIELD PolicyNumber \\* MERGEFORMAT ", "«PolicyNumber»"))
				.Append(R(" for "))
				.Append(SimpleField(" MERGEFIELD \"Insured Name\" ", "«Insured Name»"))
				.Append(R("."))
				.ToArray()));

		var (result, dom) = ImportDom(docx);

		var fields = dom.QuerySelectorAll("span.df");
		Assert.Equal(["PolicyNumber", "Insured_Name"], fields.Select(f => f.GetAttribute("data-field")));
		Assert.Equal(["{{ PolicyNumber }}", "{{ Insured_Name }}"], fields.Select(f => f.TextContent));
		// The cached «...» results are replaced, not kept beside the field.
		Assert.DoesNotContain("«", dom.Body!.TextContent);
		Assert.Equal("Policy {{ PolicyNumber }} for {{ Insured_Name }}.", dom.QuerySelector("p")!.TextContent);
		Assert.Equal(["PolicyNumber", "Insured_Name"], result.Fields);
		Assert.Equal(2, result.Counts["fields"]);
		Assert.Equal("«PolicyNumber»", result.Model!["PolicyNumber"]);
		Assert.Equal("«Insured_Name»", result.Model["Insured_Name"]);
	}

	[Fact]
	public void Merge_fields_split_over_many_runs_are_read_whole()
	{
		var docx = WithBody(P(
			new Run(new FieldChar { FieldCharType = FieldCharValues.Begin }),
			new Run(new FieldCode(" MERGE") { Space = SpaceProcessingModeValues.Preserve }),
			new Run(new FieldCode("FIELD Effective") { Space = SpaceProcessingModeValues.Preserve }),
			new Run(new FieldCode("Date ") { Space = SpaceProcessingModeValues.Preserve }),
			new Run(new FieldChar { FieldCharType = FieldCharValues.Separate }),
			R("«Effective"),
			R("Date»"),
			new Run(new FieldChar { FieldCharType = FieldCharValues.End })));

		var (result, dom) = ImportDom(docx);

		Assert.Equal("EffectiveDate", dom.QuerySelector("span.df")!.GetAttribute("data-field"));
		Assert.Equal(["EffectiveDate"], result.Fields);
	}

	[Fact]
	public void A_merge_field_without_a_cached_result_still_becomes_a_data_field()
	{
		var docx = WithBody(P(
			new Run(new FieldChar { FieldCharType = FieldCharValues.Begin }),
			new Run(new FieldCode(" MERGEFIELD AgentName ") { Space = SpaceProcessingModeValues.Preserve }),
			new Run(new FieldChar { FieldCharType = FieldCharValues.End })));

		var (_, dom) = ImportDom(docx);

		Assert.Equal("AgentName", dom.QuerySelector("span.df")!.GetAttribute("data-field"));
	}

	[Fact]
	public void Other_fields_keep_their_displayed_result()
	{
		var docx = WithBody(P(
			new OpenXmlElement[] { R("Page ") }.Concat(MergeField(" PAGE ", "3")).Append(SimpleField(" DATE ", "9/30/2026")).ToArray()));

		var (result, dom) = ImportDom(docx);

		Assert.Equal("Page 39/30/2026", dom.QuerySelector("p")!.TextContent);
		Assert.Empty(result.Fields);
		Assert.Null(dom.QuerySelector("span.df"));
	}

	[Fact]
	public void Dotted_merge_fields_build_a_nested_model()
	{
		var docx = WithBody(P(
			MergeField(" MERGEFIELD insured.name ", "«insured.name»")
				.Concat(MergeField(" MERGEFIELD insured.address.city ", "«insured.address.city»"))
				.Concat(MergeField(" MERGEFIELD insured.name ", "«insured.name»"))
				.ToArray<OpenXmlElement>()));

		var result = Import(docx);

		Assert.Equal(["insured.name", "insured.address.city"], result.Fields);
		Assert.Equal(3, result.Counts["fields"]);
		var json = JsonSerializer.Serialize(result.Model);
		Assert.Equal("""{"insured":{"name":"\u00ABname\u00BB","address":{"city":"\u00ABcity\u00BB"}}}""", json);
	}

	[Fact]
	public void Clashing_field_paths_are_reported()
	{
		var docx = WithBody(P(
			MergeField(" MERGEFIELD policy ", "«policy»")
				.Concat(MergeField(" MERGEFIELD policy.number ", "«policy.number»"))
				.ToArray<OpenXmlElement>()));

		var result = Import(docx);

		Assert.Equal("«policy»", result.Model!["policy"]);
		Assert.Contains(result.Notes, n => n.Contains("policy.number") && n.Contains("clashes"));
	}

	[Fact]
	public void Tagged_content_controls_become_data_fields()
	{
		var docx = WithBody(P(
			R("Agent: "),
			ContentControl("agent.name", "Agent Name", "Click to enter"),
			R(" / "),
			ContentControl(null, "Producer Code", "Code"),
			R(" / "),
			ContentControl(null, null, "Just text")));

		var (result, dom) = ImportDom(docx);

		Assert.Equal(["agent.name", "Producer_Code"], dom.QuerySelectorAll("span.df").Select(f => f.GetAttribute("data-field")));
		Assert.EndsWith("Just text", dom.QuerySelector("p")!.TextContent);
		Assert.DoesNotContain("Click to enter", result.Html);
	}

	[Fact]
	public void Block_content_controls_are_unwrapped()
	{
		var sdt = new SdtBlock(new SdtProperties(new Tag { Val = "section" }), new SdtContentBlock(P("Inside a block control")));
		var (_, dom) = ImportDom(WithBody(sdt));
		Assert.Equal("Inside a block control", dom.QuerySelector("p")!.TextContent);
	}

	// ---- Links ------------------------------------------------------------------------------------------------------

	[Fact]
	public void Web_and_mail_links_are_kept()
	{
		var docx = Create((main, body) => body.Append(P(
			Link(main, "https://www.mutualofenumclaw.com/claims", "Report a claim"),
			R(" or "),
			Link(main, "mailto:service@example.com", "email us"))));

		var (result, dom) = ImportDom(docx);

		var links = dom.QuerySelectorAll("a");
		Assert.Equal(["https://www.mutualofenumclaw.com/claims", "mailto:service@example.com"], links.Select(a => a.GetAttribute("href")));
		Assert.Equal("Report a claim", links[0].TextContent);
		Assert.Equal(2, result.Counts["links"]);
	}

	[Fact]
	public void Unsafe_links_are_imported_as_text()
	{
		var docx = Create((main, body) => body.Append(P(
			Link(main, "javascript:alert(1)", "click me"),
			Link(main, "file:///c:/secret.txt", "local file"))));

		var (result, dom) = ImportDom(docx);

		Assert.Null(dom.QuerySelector("a"));
		Assert.Equal("click melocal file", dom.QuerySelector("p")!.TextContent);
		Assert.Contains(result.Notes, n => n.Contains("2 link(s)"));
	}

	// ---- Images and text boxes --------------------------------------------------------------------------------------

	[Fact]
	public void Png_images_are_stored_as_assets_and_sized_from_the_document()
	{
		var docx = Create((main, body) =>
		{
			var id = AddImage(main, ImagePartType.Png, Html.Png);
			body.Append(P(Image(id, 240, 120, alt: "Aerial \"photo\" <1>")));
		});

		var (result, dom) = ImportDom(docx);

		var img = dom.QuerySelector("img")!;
		var src = img.GetAttribute("src")!;
		Assert.StartsWith(LegacyAssetStore.UrlPrefix, src);
		Assert.EndsWith(".png", src);
		Assert.True(File.Exists(Path.Combine(_assets.AssetFolder, src[LegacyAssetStore.UrlPrefix.Length..])));
		Assert.Equal("width:240px;max-width:100%;height:auto;", img.GetAttribute("style"));
		Assert.Equal("Aerial \"photo\" <1>", img.GetAttribute("alt"));
		Assert.Equal(1, result.Counts["images"]);
		// The PDF composer inlines asset URLs, so the image reaches the render.
		Assert.StartsWith("data:image/png;base64,", _assets.Store.InlineImages(src));
	}

	[Fact]
	public void The_same_image_twice_is_stored_once()
	{
		var docx = Create((main, body) =>
		{
			var first = AddImage(main, ImagePartType.Png, Html.Png);
			var second = AddImage(main, ImagePartType.Png, Html.Png);
			body.Append(P(Image(first, 10, 10)), P(Image(second, 10, 10)));
		});

		var result = Import(docx);

		Assert.Equal(2, result.Counts["images"]);
		Assert.Single(Directory.GetFiles(_assets.AssetFolder, "*.png"));
	}

	[Fact]
	public void Floating_images_are_placed_inline_with_a_note()
	{
		var docx = Create((main, body) =>
		{
			var id = AddImage(main, ImagePartType.Png, Html.Png);
			body.Append(P(Image(id, 50, 50, floating: true)));
		});

		var (result, dom) = ImportDom(docx);

		Assert.NotNull(dom.QuerySelector("img"));
		Assert.Contains(result.Notes, n => n.Contains("floating image"));
	}

	[Fact]
	public void Unsupported_image_formats_are_skipped_with_a_note()
	{
		var docx = Create((main, body) =>
		{
			var id = AddImage(main, ImagePartType.Emf, [1, 0, 0, 0, 0x6C, 0, 0, 0]);
			body.Append(P(R("Logo: "), Image(id, 50, 50)));
		});

		var (result, dom) = ImportDom(docx);

		Assert.Null(dom.QuerySelector("img"));
		Assert.Equal(0, result.Counts["images"]);
		Assert.Contains(result.Notes, n => n.Contains("1 image(s)"));
		Assert.Empty(Directory.Exists(_assets.AssetFolder) ? Directory.GetFiles(_assets.AssetFolder, "*.png") : []);
	}

	[Fact]
	public void Images_whose_bytes_do_not_match_their_type_are_skipped()
	{
		var docx = Create((main, body) =>
		{
			var id = AddImage(main, ImagePartType.Png, "<svg onload=alert(1)>"u8.ToArray());
			body.Append(P(Image(id, 50, 50)));
		});

		var (result, dom) = ImportDom(docx);

		Assert.Null(dom.QuerySelector("img"));
		Assert.Contains(result.Notes, n => n.Contains("image(s)"));
	}

	[Fact]
	public void Text_boxes_are_imported_as_boxes_after_their_paragraph()
	{
		var docx = WithBody(P(R("Anchor paragraph"), TextBox(P("Boxed heading"), P("Boxed text"))), P("Next"));

		var (result, dom) = ImportDom(docx);

		Assert.Equal(["p", "div", "p"], dom.Body!.Children.Select(e => e.LocalName));
		var box = dom.QuerySelector("div.doc-box")!;
		Assert.Equal(["Boxed heading", "Boxed text"], box.QuerySelectorAll("p").Select(p => p.TextContent));
		Assert.Equal("Anchor paragraph", dom.QuerySelector("p")!.TextContent);
		Assert.Equal(1, result.Counts["textBoxes"]);
	}

	// ---- Page breaks, tracked changes, document notes ---------------------------------------------------------------

	[Fact]
	public void Page_breaks_become_designer_page_breaks()
	{
		var docx = WithBody(
			P("Page one"),
			P(PageBreak()),
			P("Page two"),
			P(new ParagraphProperties(new PageBreakBefore()), R("Page three")));

		var (result, dom) = ImportDom(docx);

		// As in Word, the paragraph holding the break leaves its (empty) paragraph mark at the top of the next page.
		Assert.Equal(["p", "div", "p", "p", "div", "p"], dom.Body!.Children.Select(e => e.LocalName));
		Assert.Equal(2, dom.QuerySelectorAll("div.page-break").Length);
		Assert.Equal(2, result.Counts["pageBreaks"]);
		Assert.Equal(3, result.Pages);
	}

	[Fact]
	public void A_break_at_the_start_of_a_paragraph_goes_before_it_and_at_the_end_after_it()
	{
		var docx = WithBody(
			P(PageBreak(), R("Starts page two")),
			P(R("Ends page two"), PageBreak()));

		var (_, dom) = ImportDom(docx);

		Assert.Equal(["div", "p", "p", "div"], dom.Body!.Children.Select(e => e.LocalName));
	}

	[Fact]
	public void Page_count_comes_from_the_document_properties_when_present()
	{
		var docx = Create((_, body) => body.Append(P("Only text")), package =>
		{
			var part = package.AddExtendedFilePropertiesPart();
			part.Properties = new DocumentFormat.OpenXml.ExtendedProperties.Properties(
				new DocumentFormat.OpenXml.ExtendedProperties.Pages("7"));
			part.Properties.Save();
		});

		Assert.Equal(7, Import(docx).Pages);
	}

	[Fact]
	public void Tracked_insertions_are_kept_and_deletions_dropped()
	{
		var docx = WithBody(P(
			R("Limit is "),
			new DeletedRun(new Run(new DeletedText("$100,000"))) { Id = "1", Author = "a" },
			new InsertedRun(R("$250,000")) { Id = "2", Author = "a" }));

		var (result, dom) = ImportDom(docx);

		Assert.Equal("Limit is $250,000", dom.QuerySelector("p")!.TextContent);
		Assert.Contains(result.Notes, n => n.Contains("Tracked changes"));
	}

	[Fact]
	public void Headers_and_footers_are_reported_as_not_imported()
	{
		var docx = Create((main, body) =>
		{
			var header = main.AddNewPart<HeaderPart>();
			header.Header = new Header(P("Confidential"));
			header.Header.Save();
			body.Append(P("Body"));
		});

		var result = Import(docx);

		Assert.DoesNotContain("Confidential", result.Html);
		Assert.Contains(result.Notes, n => n.Contains("headers and footers"));
	}

	[Fact]
	public void A_clean_document_has_no_notes()
	{
		Assert.Empty(Import(WithBody(P("Simple"))).Notes);
	}

	// ---- Running headers and footers --------------------------------------------------------------------------------

	private static byte[] WithHeaderAndFooter(bool titlePage = false) => Create((main, body) =>
	{
		var header = AddHeader(main, P("Mutual of Enumclaw \u2013 Renewal Notice"));
		var footer = AddFooter(main, P(R("Form RN-01")), PageXOfY());
		body.Append(P("Body text."), Section(header, footer, top: 1800, bottom: 1440, left: 1080, right: 1260, header: 600, footer: 500, titlePage: titlePage));
	});

	[Fact]
	public void Header_and_footer_become_a_running_layout_around_the_body()
	{
		var (result, dom) = ImportDom(WithHeaderAndFooter());

		var run = dom.QuerySelector("div.gd-doc.gd-flow.gd-run > table.gd-runt")!;
		Assert.Equal("Mutual of Enumclaw \u2013 Renewal Notice", run.QuerySelector("thead .gd-header")!.TextContent);
		Assert.NotNull(run.QuerySelector("thead .gd-pgmark"));
		var footer = run.QuerySelector("tfoot .gd-footer.gd-pdffoot")!;
		Assert.StartsWith("Form RN-01", footer.TextContent);
		Assert.Equal("Body text.", run.QuerySelector("tbody > tr > td")!.TextContent);
		Assert.Equal(1, result.Counts["headers"]);
		Assert.Equal(1, result.Counts["footers"]);
		Assert.Empty(result.Notes);
	}

	[Fact]
	public void Page_number_fields_in_the_footer_become_renderer_page_numbers()
	{
		var (result, dom) = ImportDom(WithHeaderAndFooter());

		var footer = dom.QuerySelector(".gd-pdffoot")!;
		Assert.NotNull(footer.QuerySelector(".gd-pageno"));
		Assert.NotNull(footer.QuerySelector(".gd-pagecount"));
		Assert.Equal(2, result.Counts["pageNumbers"]);
		Assert.Empty(result.Fields);
	}

	[Fact]
	public void Word_page_margins_become_the_page_geometry()
	{
		var (result, dom) = ImportDom(WithHeaderAndFooter());

		// Header/footer distance is the printed page margin; the bands hold the body at Word's body margins.
		Assert.Contains("@page gdrun{margin:30pt 0 25pt 0;}", result.Css);
		Assert.Contains(".gd-flow.gd-run{page:gdrun;padding:0 63pt 0 54pt;}", result.Css);
		Assert.Contains("thead>tr>td{height:60pt;", result.Css);
		Assert.Contains("tfoot>tr>td{height:47pt;", result.Css);
		// The footer's position rides in class names for the renderer (fy = footer distance, mb/ml/mr = margins).
		var classes = dom.QuerySelector(".gd-pdffoot")!.ClassList;
		Assert.Contains("gd-fy-25", classes);
		Assert.Contains("gd-mb-72", classes);
		Assert.Contains("gd-ml-54", classes);
		Assert.Contains("gd-mr-63", classes);
	}

	[Fact]
	public void Page_fields_in_the_body_keep_their_displayed_value()
	{
		var docx = Create((main, body) =>
		{
			var footer = AddFooter(main, PageXOfY());
			body.Append(PageXOfY(), Section(null, footer));
		});

		var (_, dom) = ImportDom(docx);

		Assert.Equal("Page 1 of 1", dom.QuerySelector("tbody p")!.TextContent);
		Assert.Null(dom.QuerySelector("tbody .gd-pageno"));
	}

	[Fact]
	public void A_footer_alone_still_gets_the_running_layout()
	{
		var docx = Create((main, body) => body.Append(P("Body"), Section(null, AddFooter(main, P("Footer only")))));

		var (result, dom) = ImportDom(docx);

		Assert.Null(dom.QuerySelector(".gd-header"));
		Assert.Equal("Footer only", dom.QuerySelector(".gd-pdffoot")!.TextContent);
		Assert.Equal(0, result.Counts["headers"]);
		Assert.Equal(1, result.Counts["footers"]);
	}

	[Fact]
	public void Blank_headers_are_ignored()
	{
		var docx = Create((main, body) => body.Append(P("Body"), Section(AddHeader(main, P()), AddFooter(main, P(R("   "))))));

		var result = Import(docx);

		Assert.Equal("<p>Body</p>", result.Html);
		Assert.Empty(result.Css);
		Assert.Empty(result.Notes);
	}

	[Fact]
	public void Documents_without_headers_keep_the_plain_layout()
	{
		var result = Import(WithBody(P("Body"), Section(null, null)));
		Assert.Equal("<p>Body</p>", result.Html);
		Assert.Empty(result.Css);
	}

	[Fact]
	public void Header_images_come_from_the_header_part()
	{
		var docx = Create((main, body) =>
		{
			var header = main.AddNewPart<HeaderPart>();
			var imageId = header.GetIdOfPart(AddHeaderImage(header));
			header.Header = new Header(P(Image(imageId, 120, 40, alt: "logo")));
			header.Header.Save();
			body.Append(P("Body"), Section(main.GetIdOfPart(header), null));
		});

		var (result, dom) = ImportDom(docx);

		var img = dom.QuerySelector(".gd-header img")!;
		Assert.StartsWith(LegacyAssetStore.UrlPrefix, img.GetAttribute("src"));
		Assert.Equal(1, result.Counts["images"]);

		static ImagePart AddHeaderImage(HeaderPart header)
		{
			var part = header.AddImagePart(ImagePartType.Png);
			using var stream = new MemoryStream(Html.Png);
			part.FeedData(stream);
			return part;
		}
	}

	[Fact]
	public void Different_first_page_and_several_sections_are_reported()
	{
		var docx = Create((main, body) =>
		{
			var header = AddHeader(main, P("Header"));
			body.Append(
				P(new ParagraphProperties(Section(null, null)), R("Section one")),
				P("Section two"),
				Section(header, null, titlePage: true));
		});

		var result = Import(docx);

		Assert.Contains(result.Notes, n => n.Contains("first page"));
		Assert.Contains(result.Notes, n => n.Contains("several sections"));
	}

	[Fact]
	public void Headers_not_used_by_the_last_section_are_reported()
	{
		var docx = Create((main, body) =>
		{
			AddHeader(main, P("Unused header"));
			body.Append(P("Body"), Section(null, null));
		});

		var result = Import(docx);

		Assert.DoesNotContain("Unused header", result.Html);
		Assert.Contains(result.Notes, n => n.Contains("not imported"));
	}

	// ---- Robustness -------------------------------------------------------------------------------------------------

	[Fact]
	public void Deeply_nested_tables_stop_at_the_depth_limit()
	{
		OpenXmlElement content = P("core");
		for (var i = 0; i < 30; i++)
		{
			content = Table(null, Row(new TableCell(content is Table ? new OpenXmlElement[] { content, P() } : [content])));
		}

		var result = Import(WithBody(content));

		Assert.Contains(result.Notes, n => n.Contains("nested more than"));
		Assert.True(result.Counts["tables"] < 30);
	}

	[Fact]
	public void A_damaged_document_part_is_reported_as_unreadable()
	{
		var docx = Create((_, body) => body.Append(P("x")));
		using var stream = new MemoryStream();
		stream.Write(docx);
		using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Update, leaveOpen: true))
		{
			zip.GetEntry("word/document.xml")!.Delete();
			using var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open());
			writer.Write("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p>");
		}

		Assert.Throws<DocumentImportException>(() => Import(stream.ToArray()));
	}

	[Fact]
	public void Non_word_bytes_are_rejected()
	{
		Assert.Throws<DocumentImportException>(() => Import("%PDF-1.7 not a docx"u8.ToArray()));
	}

	private static int Count(string text, string value)
	{
		var count = 0;
		for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal)) count++;
		return count;
	}
}
