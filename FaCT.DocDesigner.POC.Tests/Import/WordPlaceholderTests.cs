using System.Text.Json;
using AngleSharp.Dom;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Wordprocessing;
using FaCT.DocDesigner.POC.Import;
using static FaCT.DocDesigner.POC.Tests.Import.Docx;

namespace FaCT.DocDesigner.POC.Tests.Import;

/// <summary>Typed placeholders in Word documents ({{ name }}, «Name», [Name]) becoming Data Fields.</summary>
public sealed class WordPlaceholderTests : IDisposable
{
	private readonly TempAssets _assets = new();

	public void Dispose() => _assets.Dispose();

	private (DocumentImportResult Result, IDocument Dom) Import(byte[] docx, PlaceholderStyles styles = PlaceholderStyles.Default)
	{
		var result = new DocxImporter(_assets.Store).Import(docx, new DocxImportOptions(styles));
		return (result, Html.Parse(result.Html));
	}

	private static string[] Fields(IDocument dom) => dom.QuerySelectorAll("span.df").Select(f => f.GetAttribute("data-field")!).ToArray();

	private static RunProperties Bold() => new(new Bold());

	// ---- Each style -------------------------------------------------------------------------------------------------

	[Fact]
	public void Brace_placeholders_become_data_fields()
	{
		var (result, dom) = Import(WithBody(P("Dear {{ insured.name }}, your policy {{policyNumber}} renews.")));

		Assert.Equal(["insured.name", "policyNumber"], Fields(dom));
		Assert.Equal("Dear {{ insured.name }}, your policy {{ policyNumber }} renews.", dom.QuerySelector("p")!.TextContent);
		Assert.Equal(2, result.Counts["placeholders"]);
		Assert.Equal(["insured.name", "policyNumber"], result.Fields);
		Assert.Equal("\u00ABname\u00BB", ((Dictionary<string, object?>)result.Model!["insured"]!)["name"]);
	}

	[Theory]
	[InlineData("currency")]
	[InlineData("dollars")]
	[InlineData("percent")]
	[InlineData("number")]
	[InlineData("decimal")]
	[InlineData("shortdate")]
	[InlineData("upcase")]
	public void Brace_placeholders_keep_a_designer_format(string format)
	{
		var (_, dom) = Import(WithBody(P($"Value: {{{{ amount | {format} }}}}")));

		var field = dom.QuerySelector("span.df")!;
		Assert.Equal("amount", field.GetAttribute("data-field"));
		Assert.Equal(format, field.GetAttribute("data-format"));
		Assert.Equal($"{{{{ amount | {format} }}}}", field.TextContent);
	}

	[Fact]
	public void Unknown_filters_are_dropped_but_the_field_is_kept()
	{
		var (_, dom) = Import(WithBody(P("{{ amount | money }}")));
		var field = dom.QuerySelector("span.df")!;
		Assert.Equal("amount", field.GetAttribute("data-field"));
		Assert.Null(field.GetAttribute("data-format"));
		Assert.Equal("{{ amount }}", field.TextContent);
	}

	[Theory]
	[InlineData("currency", 1234.5)]
	[InlineData("dollars", 1234.5)]
	[InlineData("number", 1234.5)]
	[InlineData("decimal", 1234.5)]
	[InlineData("percent", 0.125)]
	public void Formatted_fields_get_numeric_sample_values(string format, double expected)
	{
		var (result, _) = Import(WithBody(P($"{{{{ amount | {format} }}}}")));
		Assert.Equal((decimal)expected, result.Model!["amount"]);
	}

	[Fact]
	public void Date_fields_get_a_date_sample_value()
	{
		var (result, _) = Import(WithBody(P("{{ effective | shortdate }}")));
		Assert.Equal("2026-01-15", result.Model!["effective"]);
	}

	[Fact]
	public void Chevron_placeholders_become_data_fields()
	{
		var (_, dom) = Import(WithBody(P("Named insured: \u00ABInsured Name\u00BB at \u00AB Address.City \u00BB")));

		Assert.Equal(["Insured_Name", "Address.City"], Fields(dom));
		Assert.DoesNotContain("\u00AB", dom.Body!.TextContent);
	}

	[Fact]
	public void Bracket_placeholders_are_off_by_default_and_offered_in_a_note()
	{
		var (result, dom) = Import(WithBody(P("Signed by [Agent Name] on [Signature Date] (see [1] and [sic]).")));

		Assert.Empty(Fields(dom));
		Assert.Equal("Signed by [Agent Name] on [Signature Date] (see [1] and [sic]).", dom.QuerySelector("p")!.TextContent);
		// [1] doesn't look like a name; [sic] does, which is why brackets are opt-in.
		Assert.Contains(result.Notes, n => n.StartsWith("3 [bracketed] phrase(s)"));
	}

	[Fact]
	public void Bracket_placeholders_convert_when_turned_on()
	{
		var (result, dom) = Import(WithBody(P("Signed by [Agent Name] on [Signature Date] (see [1]).")), PlaceholderStyles.All);

		Assert.Equal(["Agent_Name", "Signature_Date"], Fields(dom));
		Assert.EndsWith("(see [1]).", dom.QuerySelector("p")!.TextContent);
		Assert.DoesNotContain(result.Notes, n => n.Contains("[bracketed]"));
	}

	[Fact]
	public void Only_the_chosen_styles_convert()
	{
		var docx = WithBody(P("{{ a }} \u00ABb\u00BB [c]"));

		Assert.Equal(["a"], Fields(Import(docx, PlaceholderStyles.Braces).Dom));
		Assert.Equal(["b"], Fields(Import(docx, PlaceholderStyles.Chevrons).Dom));
		Assert.Equal(["c"], Fields(Import(docx, PlaceholderStyles.Brackets).Dom));
		Assert.Equal(["a", "b", "c"], Fields(Import(docx, PlaceholderStyles.All).Dom));
	}

	[Fact]
	public void None_leaves_all_placeholder_text_alone_and_safe()
	{
		var (result, dom) = Import(WithBody(P("{{ a }} \u00ABb\u00BB [c]")), PlaceholderStyles.None);

		Assert.Empty(Fields(dom));
		Assert.Equal("{{ a }} \u00ABb\u00BB [c]", dom.QuerySelector("p")!.TextContent);
		Assert.DoesNotMatch(@"\{[{%]", result.Html);
		Assert.Equal(0, result.Counts["placeholders"]);
	}

	[Theory]
	[InlineData("{{ 123 }}")]
	[InlineData("{{ }}")]
	[InlineData("{{ a | }}")]
	[InlineData("{{ a }")]
	[InlineData("{ a }}")]
	[InlineData("\u00AB\u00BB")]
	[InlineData("\u00AB***\u00BB")]
	[InlineData("{% if x %}yes{% endif %}")]
	[InlineData("[1] [2.5] [ ]")]
	public void Text_that_only_resembles_a_placeholder_stays_text(string text)
	{
		var (result, dom) = Import(WithBody(P(text)), PlaceholderStyles.All);
		Assert.Empty(Fields(dom));
		Assert.Equal(text, dom.QuerySelector("p")!.TextContent);
		Assert.DoesNotMatch(@"\{[{%]", result.Html);
	}

	// ---- Word's run splitting ---------------------------------------------------------------------------------------

	[Fact]
	public void A_placeholder_split_over_several_runs_is_found()
	{
		// Word splits runs after spell-check and edits: "{{ Ins" + "ured" + "Name }}".
		var (result, dom) = Import(WithBody(P(R("Dear {{ Ins"), R("ured"), R("Name }}, welcome."))));

		Assert.Equal(["InsuredName"], Fields(dom));
		Assert.Equal("Dear {{ InsuredName }}, welcome.", dom.QuerySelector("p")!.TextContent);
		Assert.Equal(1, result.Counts["placeholders"]);
	}

	[Fact]
	public void A_placeholder_split_over_text_elements_of_one_run_is_found()
	{
		var run = new Run(new Text("A {{ first") { Space = SpaceProcessingModeValues.Preserve }, new Text("Name }} B") { Space = SpaceProcessingModeValues.Preserve });
		var (_, dom) = Import(WithBody(P(run)));

		Assert.Equal(["firstName"], Fields(dom));
		Assert.Equal("A {{ firstName }} B", dom.QuerySelector("p")!.TextContent);
	}

	[Fact]
	public void Several_placeholders_in_one_run_keep_their_order_and_the_text_between()
	{
		var (_, dom) = Import(WithBody(P("{{ a }}-{{ b }}\u00ABc\u00BB{{ d }} end")));

		Assert.Equal(["a", "b", "c", "d"], Fields(dom));
		Assert.Equal("{{ a }}-{{ b }}{{ c }}{{ d }} end", dom.QuerySelector("p")!.TextContent);
	}

	[Fact]
	public void Placeholders_split_over_runs_and_sharing_runs_with_others_all_convert()
	{
		var (_, dom) = Import(WithBody(P(R("x {{ a }} {{ b"), R("c }} y {{"), R(" d }} z"))));

		Assert.Equal(["a", "bc", "d"], Fields(dom));
		Assert.Equal("x {{ a }} {{ bc }} y {{ d }} z", dom.QuerySelector("p")!.TextContent);
	}

	[Fact]
	public void Bookmarks_and_proofing_marks_between_runs_do_not_break_a_placeholder()
	{
		var (_, dom) = Import(WithBody(P(
			R("{{ policy"),
			new BookmarkStart { Id = "0", Name = "b" },
			new ProofError { Type = ProofingErrorValues.SpellStart },
			R(".number }}"),
			new BookmarkEnd { Id = "0" })));

		Assert.Equal(["policy.number"], Fields(dom));
	}

	[Fact]
	public void A_tab_inside_a_placeholder_ends_it()
	{
		var (_, dom) = Import(WithBody(P(new Run(new Text("{{ a"), new TabChar(), new Text("b }}")))));
		Assert.Empty(Fields(dom));
	}

	// ---- Formatting -------------------------------------------------------------------------------------------------

	[Fact]
	public void The_field_keeps_the_placeholders_formatting_and_so_does_the_text_around_it()
	{
		var (_, dom) = Import(WithBody(P(R("Insured: {{ insured }} (primary)", Bold()), R(" plain"))));

		var strongs = dom.QuerySelectorAll("strong");
		Assert.Equal(["Insured: ", "{{ insured }}", " (primary)"], strongs.Select(s => s.TextContent));
		Assert.NotNull(strongs[1].QuerySelector("span.df"));
		Assert.EndsWith(" plain", dom.QuerySelector("p")!.TextContent);
	}

	[Fact]
	public void Merge_fields_keep_their_formatting_too()
	{
		var fieldRuns = MergeField(" MERGEFIELD PolicyNumber ", "\u00ABPolicyNumber\u00BB");
		foreach (var run in fieldRuns) run.PrependChild(new RunProperties(new Italic()));
		var (_, dom) = Import(WithBody(P(fieldRuns.ToArray<OpenXmlElement>())));

		Assert.NotNull(dom.QuerySelector("em > span.df"));
	}

	// ---- Where placeholders are looked for --------------------------------------------------------------------------

	[Fact]
	public void Placeholders_in_tables_text_boxes_and_list_items_convert()
	{
		var docx = Create((main, body) =>
		{
			AddNumbering(main, NumberFormatValues.Bullet);
			body.Append(
				Table(null, Row(Cell("{{ cell }}"))),
				P(R("Anchor"), TextBox(P("{{ boxed }}"))),
				ListItem(1, 0, "{{ item }}"));
		});

		var (_, dom) = Import(docx);

		Assert.Equal("cell", dom.QuerySelector("td span.df")!.GetAttribute("data-field"));
		Assert.Equal("boxed", dom.QuerySelector(".doc-box span.df")!.GetAttribute("data-field"));
		Assert.Equal("item", dom.QuerySelector("li span.df")!.GetAttribute("data-field"));
	}

	[Fact]
	public void Placeholders_in_headers_and_footers_convert()
	{
		var docx = Create((main, body) =>
		{
			var header = AddHeader(main, P("Insured: {{ insured.name }}"));
			var footer = AddFooter(main, P(R("Policy \u00ABPolicyNumber\u00BB \u2013 ")), PageXOfY());
			body.Append(P("Body"), Section(header, footer));
		});

		var (result, dom) = Import(docx);

		Assert.Equal("insured.name", dom.QuerySelector(".gd-header span.df")!.GetAttribute("data-field"));
		Assert.Equal("PolicyNumber", dom.QuerySelector(".gd-pdffoot span.df")!.GetAttribute("data-field"));
		Assert.NotNull(dom.QuerySelector(".gd-pdffoot .gd-pageno"));
		Assert.Equal(2, result.Counts["placeholders"]);
	}

	[Fact]
	public void Link_text_and_other_fields_results_are_left_alone()
	{
		var docx = Create((main, body) => body.Append(P(
			new OpenXmlElement[] { Link(main, "https://example.com", "{{ in_link }}") }
				.Concat(MergeField(" DATE ", "{{ in_field_result }}"))
				.ToArray())));

		var (result, dom) = Import(docx);

		Assert.Empty(Fields(dom));
		Assert.Equal("{{ in_link }}{{ in_field_result }}", dom.QuerySelector("p")!.TextContent);
		Assert.DoesNotMatch(@"\{[{%]", result.Html);
	}

	[Fact]
	public void The_same_placeholder_twice_is_one_model_field()
	{
		var (result, dom) = Import(WithBody(P("{{ name }}"), P("Again: {{ name }}")));

		Assert.Equal(["name", "name"], Fields(dom));
		Assert.Equal(["name"], result.Fields);
		Assert.Equal(2, result.Counts["fields"]);
		Assert.Single(result.Model!);
	}

	[Fact]
	public void Placeholders_and_merge_fields_share_one_model()
	{
		var (result, _) = Import(WithBody(P(
			new OpenXmlElement[] { R("{{ policy.number }} ") }
				.Concat(MergeField(" MERGEFIELD policy.term ", "\u00ABpolicy.term\u00BB"))
				.ToArray())));

		Assert.Equal("""{"policy":{"number":"\u00ABnumber\u00BB","term":"\u00ABterm\u00BB"}}""", JsonSerializer.Serialize(result.Model));
	}

	// ---- Merge field switches ---------------------------------------------------------------------------------------

	[Theory]
	[InlineData(" MERGEFIELD Name \\* Upper ", "upcase")]
	[InlineData(" MERGEFIELD Name \\* upper \\* MERGEFORMAT ", "upcase")]
	[InlineData(" MERGEFIELD Effective \\@ \"MM/dd/yyyy\" ", "shortdate")]
	[InlineData(" MERGEFIELD Premium \\# \"$#,##0.00\" ", "currency")]
	[InlineData(" MERGEFIELD Premium \\# \"$#,##0\" ", "dollars")]
	[InlineData(" MERGEFIELD Rate \\# \"#,##0.00\" ", "decimal")]
	[InlineData(" MERGEFIELD Count \\# 0 ", "number")]
	[InlineData(" MERGEFIELD Name \\* MERGEFORMAT ", null)]
	[InlineData(" MERGEFIELD Name ", null)]
	[InlineData(null, null)]
	public void Merge_field_switches_map_to_designer_formats(string? instruction, string? expected) =>
		Assert.Equal(expected, DocxImporter.MergeFieldFormat(instruction));

	[Fact]
	public void A_formatted_merge_field_imports_with_its_format()
	{
		var (_, dom) = Import(WithBody(P(MergeField(" MERGEFIELD Premium \\# \"$#,##0.00\" ", "\u00ABPremium\u00BB").ToArray<OpenXmlElement>())));
		var field = dom.QuerySelector("span.df")!;
		Assert.Equal("currency", field.GetAttribute("data-format"));
		Assert.Equal("{{ Premium | currency }}", field.TextContent);
	}

	// ---- Options parsing --------------------------------------------------------------------------------------------

	[Theory]
	[InlineData(null, PlaceholderStyles.Default)]
	[InlineData("", PlaceholderStyles.Default)]
	[InlineData("none", PlaceholderStyles.None)]
	[InlineData("all", PlaceholderStyles.All)]
	[InlineData("braces", PlaceholderStyles.Braces)]
	[InlineData("Chevrons, BRACKETS", PlaceholderStyles.Chevrons | PlaceholderStyles.Brackets)]
	[InlineData("braces,chevrons,brackets", PlaceholderStyles.All)]
	public void Placeholder_options_parse(string? value, PlaceholderStyles expected)
	{
		Assert.True(DocxImportOptions.TryParsePlaceholders(value, out var styles));
		Assert.Equal(expected, styles);
	}

	[Theory]
	[InlineData("curly")]
	[InlineData("braces;brackets")]
	public void Unknown_placeholder_options_are_rejected(string value) =>
		Assert.False(DocxImportOptions.TryParsePlaceholders(value, out _));
}
