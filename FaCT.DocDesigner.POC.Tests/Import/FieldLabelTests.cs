using FaCT.DocDesigner.POC.Import;
using FaCT.DocDesigner.POC.Legacy;
using FaCT.DocDesigner.POC.Mapping;
using UglyToad.PdfPig.Core;

namespace FaCT.DocDesigner.POC.Tests.Import;

/// <summary>Finding the printed label of a form field (for fields with meaningless names).</summary>
public sealed class FieldLabelTests
{
	// PDF coordinates (y up). A 200 x 14 text field at (200, 600).
	private static readonly PdfRectangle Field = new(200, 600, 400, 614);
	private static readonly PdfRectangle Box = new(200, 600, 208, 608);

	private static TextRun Run(string text, double left, double baseline, double size = 9, double? width = null) =>
		new(text, left, left + (width ?? text.Length * size * 0.5), baseline - size * 0.2, baseline + size * 0.8);

	[Fact]
	public void The_text_before_a_field_on_its_line_is_its_label()
	{
		var runs = new[] { Run("Policy Number:", 120, 603, width: 70) };
		Assert.Equal("Policy Number", FieldLabels.Find(runs, Field, checkBox: false));
	}

	[Fact]
	public void A_caption_just_above_a_field_is_its_label()
	{
		var runs = new[] { Run("Named insured", 202, 617) };
		Assert.Equal("Named insured", FieldLabels.Find(runs, Field, checkBox: false));
	}

	[Fact]
	public void The_closer_of_left_and_above_wins()
	{
		var farLeft = Run("Section 2", 60, 603, width: 50);
		var above = Run("Mailing address", 202, 617);
		Assert.Equal("Mailing address", FieldLabels.Find([farLeft, above], Field, checkBox: false));
		var nearLeft = Run("City:", 175, 603, width: 22);
		var farAbove = Run("Mailing address", 202, 625);
		Assert.Equal("City", FieldLabels.Find([nearLeft, farAbove], Field, checkBox: false));
	}

	[Fact]
	public void A_check_box_is_labelled_by_the_text_to_its_right()
	{
		var runs = new[] { Run("Tax classification", 120, 601, width: 70), Run("Individual/sole proprietor", 212, 601) };
		Assert.Equal("Individual/sole proprietor", FieldLabels.Find(runs, Box, checkBox: true));
	}

	[Fact]
	public void A_check_box_without_text_to_its_right_takes_the_text_before_it()
	{
		var runs = new[] { Run("Renewal?", 150, 601, width: 40) };
		Assert.Equal("Renewal?", FieldLabels.Find(runs, Box, checkBox: true));
	}

	[Fact]
	public void A_label_paragraph_above_is_read_from_its_first_line()
	{
		var runs = new[]
		{
			Run("Line 1 is explained in the instructions.", 202, 641, width: 180),
			Run("1 Name of entity/individual. An entry is required. (For a sole proprietor, enter the owner's", 202, 629, width: 350),
			Run("name on line 2.)", 202, 617, width: 70)
		};
		Assert.Equal("Name of entity/individual", FieldLabels.Find(runs, Field, checkBox: false));
	}

	[Fact]
	public void Nothing_near_means_no_label()
	{
		Assert.Null(FieldLabels.Find([], Field, checkBox: false));
		Assert.Null(FieldLabels.Find([Run("Far away", 10, 603, width: 30)], Field, checkBox: false));
		Assert.Null(FieldLabels.Find([Run("Far above", 202, 660)], Field, checkBox: false));
		Assert.Null(FieldLabels.Find([Run("Below", 202, 580)], Field, checkBox: false));
	}

	[Fact]
	public void Text_without_a_label_is_skipped_for_the_next_candidate()
	{
		var runs = new[] { Run("3a", 185, 603, width: 10), Run("Spouse's name", 202, 617) };
		Assert.Equal("Spouse's name", FieldLabels.Find(runs, Field, checkBox: false));
	}

	[Theory]
	[InlineData("Policy Number:", "Policy Number")]
	[InlineData("  Effective   date . . . . . . .", "Effective date")]
	[InlineData("3a Spouse's name", "Spouse's name")]
	[InlineData("(b) First name", "First name")]
	[InlineData("12. Total premium", "Total premium")]
	[InlineData("Exempt payee code (if any)", "Exempt payee code")]
	[InlineData("Address; see instructions", "Address")]
	[InlineData("Name {{ x }} here", "Name x here")]
	[InlineData("Rate 50%", "Rate 50")]
	[InlineData("No", "No")]
	[InlineData("Yes", "Yes")]
	public void Clean_keeps_the_phrase(string text, string expected) => Assert.Equal(expected, FieldLabels.Clean(text));

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("3a")]
	[InlineData("3(a)")]
	[InlineData("(b)")]
	[InlineData("12.")]
	[InlineData("$")]
	[InlineData(". . . . .")]
	[InlineData("or")]
	[InlineData("Note")]
	public void Clean_drops_text_that_is_no_label(string? text) => Assert.Null(FieldLabels.Clean(text));

	[Fact]
	public void A_long_label_is_cut_at_a_word()
	{
		var label = FieldLabels.Clean("Name of the person or organization that is to receive the certificate of insurance requested here");
		Assert.NotNull(label);
		Assert.True(label!.Length <= FieldLabels.MaxLength);
		Assert.EndsWith("the", label);
	}
}

/// <summary>Suggest mappings using the printed label when the field name says nothing.</summary>
public sealed class LabelMappingTests
{
	private static readonly MappingPath[] Paths =
	[
		new("policy.number", "text"), new("policy.effectiveDate", "date"), new("insured.name", "text"),
		new("agent.name", "text"), new("entity.name", "text"), new("premium.total", "number")
	];

	[Fact]
	public void A_label_that_names_a_property_is_recommended()
	{
		var s = Assert.Single(FieldMatcher.Suggest([("f1_01[0]", (string?)"Policy Number")], Paths));
		Assert.Equal("policy.number", s.Recommended);
		Assert.Equal(0.95, s.Candidates[0].Score);
		Assert.Equal("Policy Number", s.Label);
	}

	[Fact]
	public void Without_a_label_a_meaningless_name_matches_nothing()
	{
		var s = Assert.Single(FieldMatcher.Suggest([("f1_01[0]", (string?)null)], Paths));
		Assert.Empty(s.Candidates);
		Assert.Null(s.Label);
		Assert.Empty(Assert.Single(FieldMatcher.Suggest(["f1_01[0]"], Paths)).Candidates);
	}

	[Fact]
	public void A_long_label_offers_the_properties_it_names_but_ties_are_not_ticked()
	{
		var s = Assert.Single(FieldMatcher.Suggest([("f1_01[0]", (string?)"Name of insured/agent")], [new MappingPath("insured.name", null), new MappingPath("agent.name", null)]));
		Assert.Equal(2, s.Candidates.Count);
		Assert.Equal(s.Candidates[0].Score, s.Candidates[1].Score);
		Assert.Null(s.Recommended);
	}

	[Fact]
	public void A_label_naming_the_whole_path_beats_one_naming_the_last_part()
	{
		var s = Assert.Single(FieldMatcher.Suggest([("f1_01[0]", (string?)"Name of entity/individual")], Paths));
		Assert.Equal("entity.name", s.Candidates[0].Path);
		Assert.True(s.Candidates[0].Score > s.Candidates[1].Score);
	}

	[Fact]
	public void A_good_name_still_wins_over_its_label()
	{
		var s = Assert.Single(FieldMatcher.Suggest([("PolicyNumber", (string?)"Policy")], Paths));
		Assert.Equal("policy.number", s.Recommended);
		Assert.Equal(1, s.Candidates[0].Score);
	}

	[Fact]
	public void Dates_and_amounts_in_labels_favour_date_and_number_properties()
	{
		Assert.True(FieldMatcher.LabelScore("Effective date", new MappingPath("policy.effectiveDate", "date")) >
			FieldMatcher.LabelScore("Effective date", new MappingPath("policy.effectiveDate", "text")));
		Assert.True(FieldMatcher.LabelScore("Total premium amount", new MappingPath("premium.total", "number")) >
			FieldMatcher.LabelScore("Total premium amount", new MappingPath("premium.total", "text")));
	}

	[Theory]
	[InlineData("Policy Number", "policy.number", 1.0)]
	[InlineData("Telephone", "agent.phone", 0.9)]
	[InlineData("Colour", "policy.number", 0.0)]
	[InlineData("", "policy.number", 0.0)]
	public void LabelScore_examples(string label, string path, double expected) =>
		Assert.Equal(expected, FieldMatcher.LabelScore(label, new MappingPath(path, null)), 3);
}

/// <summary>Labels on real and generated PDFs, through the importer.</summary>
public sealed class FieldLabelImportTests : IDisposable
{
	private readonly TempAssets _assets = new();

	public void Dispose() => _assets.Dispose();

	private DocumentImportResult Import(byte[] pdf) => new PdfImporter(new LegacyFormImporter(_assets.Store)).Import(pdf);

	private static string? LabelOf(string html, string field) =>
		System.Text.RegularExpressions.Regex.Match(html, "data-legacy-field=\"" + System.Text.RegularExpressions.Regex.Escape(field) + "\"[^>]*?data-label=\"([^\"]*)\"") is { Success: true } m
			? System.Net.WebUtility.HtmlDecode(m.Groups[1].Value)
			: null;

	[Fact]
	public void A_generated_form_gets_its_labels()
	{
		// "Policy Number:" printed left of the PolicyNumber field (whose name happens to be good anyway)
		var html = Import(Pdf.Raw(
			"<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R 5 0 R] >> >>",
			"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
			"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R 5 0 R] /Contents 6 0 R /Resources << /Font << /F1 7 0 R >> >> >>",
			"<< /Type /Annot /Subtype /Widget /FT /Tx /T (Text1) /Rect [180 600 380 616] /P 3 0 R /F 4 >>",
			"<< /Type /Annot /Subtype /Widget /FT /Btn /T (Check1) /Rect [100 560 110 570] /P 3 0 R /V /Off /AS /Off /F 4 >>",
			Pdf.Stream("BT /F1 10 Tf 72 604 Td (Policy Number:) Tj ET BT /F1 10 Tf 116 562 Td (Renewal) Tj ET"),
			"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")).Html;
		Assert.Equal("Policy Number", LabelOf(html, "Text1"));
		Assert.Equal("Renewal", LabelOf(html, "Check1"));
	}

	[Fact]
	public void A_field_with_no_text_near_it_has_no_label()
	{
		Assert.DoesNotContain("data-label", Import(Pdf.Raw(
			"<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>",
			"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
			"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R] /Contents 5 0 R /Resources << /Font << /F1 6 0 R >> >> >>",
			"<< /Type /Annot /Subtype /Widget /FT /Tx /T (Lonely) /Rect [300 300 400 316] /P 3 0 R /F 4 >>",
			Pdf.Stream("BT /F1 10 Tf 72 700 Td (Heading far away) Tj ET"),
			"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")).Html);
	}

	[Theory]
	[InlineData("f1_01[0]", "Name of entity/individual")]
	[InlineData("c1_1[0]", "Individual/sole proprietor")]
	[InlineData("c1_1[1]", "C corporation")]
	[InlineData("c1_1[5]", "LLC")]
	[InlineData("f1_05[0]", "Exempt payee code")]
	[InlineData("f1_09[0]", "Requester\u2019s name and address")]
	public void The_w9_fields_get_their_printed_labels(string field, string label) =>
		Assert.Equal(label, LabelOf(Import(RealWorld.Pdf("fw9")).Html, field));

	[Fact]
	public void Most_fields_of_the_samples_get_a_label()
	{
		foreach (var file in new[] { "fw9", "fss4", "f8822" })
		{
			var result = Import(RealWorld.Pdf(file));
			var labelled = System.Text.RegularExpressions.Regex.Matches(result.Html, "data-label=\"").Count;
			Assert.True(labelled >= result.Fields.Count * 0.6, $"{file}: {labelled} of {result.Fields.Count} labelled");
		}
	}

	[Fact]
	public void The_legacy_importer_cleans_labels_from_converted_forms()
	{
		var result = new LegacyFormImporter(_assets.Store).Import("<html><body><section class=\"form-page\">" +
			"<span class=\"abs field\" data-field=\"A\" data-label=\"1 Insured {{ name }}: (see note)\" style=\"left:1pt\"></span>" +
			"<span class=\"abs field\" data-field=\"B\" data-label=\"3a\" style=\"left:1pt\"></span>" +
			"<span class=\"abs field\" data-field=\"C\" data-label=\"&lt;b&gt;Zip&quot; onclick=&quot;x\" style=\"left:1pt\"></span>" +
			"</section></body></html>");
		Assert.Contains("data-legacy-field=\"A\" data-label=\"Insured name\"", result.Html);
		Assert.DoesNotContain("data-legacy-field=\"B\" data-label", result.Html);
		Assert.DoesNotContain("{{", result.Html);
		Assert.DoesNotContain("onclick=\"", result.Html);
		Assert.DoesNotContain("<b>", result.Html);
	}
}
