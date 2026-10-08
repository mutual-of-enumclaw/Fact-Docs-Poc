using System.Globalization;
using AngleSharp.Dom;
using FaCT.DocDesigner.POC.Import;
using FaCT.DocDesigner.POC.Legacy;
using static FaCT.DocDesigner.POC.Tests.Import.Pdf;

namespace FaCT.DocDesigner.POC.Tests.Import;

public sealed class PdfImporterTests : IDisposable
{
	private const double PxPerPt = 96.0 / 72.0;

	private readonly TempAssets _assets = new();

	public void Dispose() => _assets.Dispose();

	private DocumentImportResult Import(byte[] pdf) =>
		new PdfImporter(new LegacyFormImporter(_assets.Store)).Import(pdf);

	private (DocumentImportResult Result, IDocument Dom) ImportDom(byte[] pdf)
	{
		var result = Import(pdf);
		return (result, Html.Parse(result.Html));
	}

	private static Dictionary<string, string> Style(IElement element) =>
		(element.GetAttribute("style") ?? string.Empty)
			.Split(';', StringSplitOptions.RemoveEmptyEntries)
			.Select(d => d.Split(':', 2))
			.ToDictionary(p => p[0].Trim(), p => p[1].Trim());

	private static double Px(IElement element, string property)
	{
		var value = Style(element)[property];
		Assert.EndsWith("px", value);
		return double.Parse(value[..^2], CultureInfo.InvariantCulture);
	}

	private static IElement[] Texts(IDocument dom) => dom.QuerySelectorAll("section.form-page > span.abs:not(.field)").ToArray();

	// ---- Pages ------------------------------------------------------------------------------------------------------

	[Fact]
	public void Each_pdf_page_becomes_a_form_page()
	{
		var pdf = Pages(3, (page, fonts, n) => page.AddText($"Page {n}", 12, At(72, 720), fonts.Helvetica));

		var (result, dom) = ImportDom(pdf);

		Assert.Equal("pdf", result.Format);
		Assert.Equal(3, result.Pages);
		var pages = dom.QuerySelectorAll("section.form-page");
		Assert.Equal(["Page 1", "Page 2", "Page 3"], pages.Select(p => p.TextContent));
		Assert.Equal(3, result.Counts["texts"]);
		Assert.Null(result.Model);
	}

	[Fact]
	public void An_empty_page_is_kept_as_a_blank_sheet()
	{
		var (result, dom) = ImportDom(Letter((_, _) => { }));
		Assert.Equal(1, result.Pages);
		Assert.Empty(dom.QuerySelector("section.form-page")!.Children);
	}

	[Fact]
	public void Letter_pages_are_not_scaled()
	{
		var (result, dom) = ImportDom(Letter((page, fonts) => page.AddText("Edge", 10, At(0, 700), fonts.Helvetica)));
		Assert.DoesNotContain(result.Notes, n => n.Contains("scaled"));
		Assert.Equal(0, Px(Texts(dom)[0], "left"));
	}

	[Fact]
	public void Pages_larger_than_letter_are_scaled_to_fit()
	{
		var (result, dom) = ImportDom(A4((page, fonts) => page.AddText("A4 text", 12, At(72, 770), fonts.Helvetica)));

		Assert.Contains(result.Notes, n => n.Contains("Page(s) 1") && n.Contains("scaled to fit"));
		var scale = 792.0 / 842.0;
		var span = Texts(dom)[0];
		Assert.Equal(72 * scale * PxPerPt, Px(span, "left"), 1);
		Assert.Equal(Math.Round(12 * scale, 2).ToString(CultureInfo.InvariantCulture) + "pt", Style(span)["font-size"]);
	}

	[Fact]
	public void Pages_smaller_than_letter_keep_their_size()
	{
		var pdf = Pages(1, (page, fonts, _) => page.AddText("Small", 12, At(36, 300), fonts.Helvetica), width: 400, height: 400);
		var (result, dom) = ImportDom(pdf);
		Assert.Empty(result.Notes);
		Assert.Equal(36 * PxPerPt, Px(Texts(dom)[0], "left"), 1);
	}

	[Fact]
	public void Too_many_pages_are_refused()
	{
		var pdf = Pages(PdfImporter.MaxPages + 1, (_, _, _) => { });
		var error = Assert.ThrowsAny<DocumentImportException>(() => Import(pdf));
		Assert.Contains($"at most {PdfImporter.MaxPages}", error.Message);
	}

	// ---- Text -------------------------------------------------------------------------------------------------------

	[Fact]
	public void Text_is_placed_at_its_position_on_the_sheet()
	{
		var (_, dom) = ImportDom(Letter((page, fonts) => page.AddText("Declarations", 12, At(72, 720), fonts.Helvetica)));

		var span = Assert.Single(Texts(dom));
		var style = Style(span);
		Assert.Equal("Declarations", span.TextContent);
		Assert.Equal("absolute", style["position"]);
		Assert.Equal(96, Px(span, "left"), 2);
		// Top of a 12pt line box whose baseline is at y=720 (72pt from the top of the page).
		Assert.Equal((72 - 12 * 0.845) * PxPerPt, Px(span, "top"), 1);
		Assert.Equal("12pt", style["font-size"]);
		Assert.Equal("12pt", style["line-height"]);
		Assert.Equal(LegacyFormImporter.SansFontStack, style["font-family"]);
		Assert.False(style.ContainsKey("font-weight"));
		Assert.False(style.ContainsKey("color"));
	}

	[Fact]
	public void Words_on_a_line_become_one_text_run()
	{
		var (_, dom) = ImportDom(Letter((page, fonts) => page.AddText("Named Insured and Mailing Address", 10, At(72, 700), fonts.Helvetica)));
		Assert.Equal("Named Insured and Mailing Address", Assert.Single(Texts(dom)).TextContent);
	}

	[Fact]
	public void Separately_drawn_words_with_a_normal_gap_are_joined_with_a_space()
	{
		var pdf = Letter((page, fonts) =>
		{
			var first = page.AddText("Policy", 10, At(72, 700), fonts.Helvetica);
			page.AddText("Number", 10, At(first[^1].EndBaseLine.X + 3, 700), fonts.Helvetica);
		});

		Assert.Equal("Policy Number", Assert.Single(Texts(ImportDom(pdf).Dom)).TextContent);
	}

	[Fact]
	public void Widely_separated_text_on_one_line_stays_in_separate_runs()
	{
		var pdf = Letter((page, fonts) =>
		{
			page.AddText("Coverage", 10, At(72, 700), fonts.Helvetica);
			page.AddText("Limit", 10, At(400, 700), fonts.Helvetica);
		});

		var texts = Texts(ImportDom(pdf).Dom);
		Assert.Equal(["Coverage", "Limit"], texts.Select(t => t.TextContent));
		Assert.Equal(400 * PxPerPt, Px(texts[1], "left"), 1);
	}

	[Fact]
	public void Lines_are_written_top_to_bottom_whatever_the_drawing_order()
	{
		var pdf = Letter((page, fonts) =>
		{
			page.AddText("Bottom", 10, At(72, 100), fonts.Helvetica);
			page.AddText("Top", 10, At(72, 700), fonts.Helvetica);
			page.AddText("Middle", 10, At(72, 400), fonts.Helvetica);
		});

		Assert.Equal(["Top", "Middle", "Bottom"], Texts(ImportDom(pdf).Dom).Select(t => t.TextContent));
	}

	[Fact]
	public void Font_changes_within_a_line_start_a_new_run()
	{
		var pdf = Letter((page, fonts) =>
		{
			var label = page.AddText("Premium:", 10, At(72, 700), fonts.HelveticaBold);
			page.AddText("$1,250", 10, At(label[^1].EndBaseLine.X + 3, 700), fonts.Helvetica);
		});

		var texts = Texts(ImportDom(pdf).Dom);
		Assert.Equal(["Premium:", "$1,250"], texts.Select(t => t.TextContent));
		Assert.Equal("bold", Style(texts[0])["font-weight"]);
		Assert.False(Style(texts[1]).ContainsKey("font-weight"));
	}

	[Fact]
	public void Fonts_map_to_the_closest_standard_family()
	{
		var pdf = Letter((page, fonts) =>
		{
			page.AddText("Serif", 10, At(72, 700), fonts.Times);
			page.AddText("Italic", 10, At(72, 680), fonts.TimesItalic);
			page.AddText("Mono", 10, At(72, 660), fonts.Courier);
		});

		var texts = Texts(ImportDom(pdf).Dom);
		Assert.Equal(LegacyFormImporter.SerifFontStack, Style(texts[0])["font-family"]);
		Assert.Equal(LegacyFormImporter.SerifFontStack, Style(texts[1])["font-family"]);
		Assert.Equal("italic", Style(texts[1])["font-style"]);
		Assert.Equal(LegacyFormImporter.MonoFontStack, Style(texts[2])["font-family"]);
	}

	[Fact]
	public void Text_colour_is_kept()
	{
		var pdf = Letter((page, fonts) =>
		{
			page.SetTextAndFillColor(20, 72, 53);
			page.AddText("MOE Green", 10, At(72, 700), fonts.Helvetica);
		});

		Assert.Equal("rgb(20, 72, 53)", Style(Assert.Single(Texts(ImportDom(pdf).Dom)))["color"]);
	}

	[Fact]
	public void Markup_and_liquid_in_pdf_text_are_encoded()
	{
		var pdf = Letter((page, fonts) => page.AddText("{{ premium }} <b>bold</b> {% raw %}", 10, At(72, 700), fonts.Helvetica));

		var (result, dom) = ImportDom(pdf);

		Assert.DoesNotMatch(@"\{[{%]", result.Html);
		Assert.Null(dom.QuerySelector("b"));
		Assert.Equal("{{ premium }} <b>bold</b> {% raw %}", Assert.Single(Texts(dom)).TextContent);
	}

	// ---- Shapes -----------------------------------------------------------------------------------------------------

	[Fact]
	public void Horizontal_and_vertical_lines_become_rules()
	{
		var pdf = Letter((page, _) =>
		{
			page.DrawLine(At(72, 600), At(540, 600), 1);
			page.DrawLine(At(300, 100), At(300, 500), 2);
		});

		var (result, dom) = ImportDom(pdf);

		var rules = dom.QuerySelectorAll("div.rule");
		Assert.Equal(2, rules.Length);
		var horizontal = rules[0];
		Assert.Equal(72 * PxPerPt, Px(horizontal, "left"), 1);
		Assert.Equal((192 - 0.5) * PxPerPt, Px(horizontal, "top"), 1);
		Assert.Equal(468 * PxPerPt, Px(horizontal, "width"), 1);
		Assert.Equal(1 * PxPerPt, Px(horizontal, "height"), 1);
		var vertical = rules[1];
		Assert.Equal((300 - 1) * PxPerPt, Px(vertical, "left"), 1);
		Assert.Equal(292 * PxPerPt, Px(vertical, "top"), 1);
		Assert.Equal(2 * PxPerPt, Px(vertical, "width"), 1);
		Assert.Equal(400 * PxPerPt, Px(vertical, "height"), 1);
		Assert.Equal(2, result.Counts["shapes"]);
	}

	[Fact]
	public void Stroked_rectangles_become_boxes()
	{
		var (_, dom) = ImportDom(Letter((page, _) => page.DrawRectangle(At(72, 400), 200, 100, 2)));

		var box = Assert.Single(dom.QuerySelectorAll("div.box"));
		Assert.Equal(72 * PxPerPt, Px(box, "left"), 1);
		Assert.Equal(292 * PxPerPt, Px(box, "top"), 1);
		Assert.Equal(200 * PxPerPt, Px(box, "width"), 1);
		Assert.Equal(100 * PxPerPt, Px(box, "height"), 1);
		Assert.Equal("2pt", Style(box)["border-width"]);
	}

	[Fact]
	public void Filled_rectangles_become_shading_in_their_colour()
	{
		var pdf = Letter((page, _) =>
		{
			page.SetTextAndFillColor(217, 217, 217);
			page.SetStrokeColor(217, 217, 217);
			page.DrawRectangle(At(72, 400), 200, 100, 1, fill: true);
		});

		var (_, dom) = ImportDom(pdf);

		var shade = dom.QuerySelector("div.shade")!;
		Assert.Equal("rgb(217, 217, 217)", Style(shade)["background"]);
		Assert.Equal(200 * PxPerPt, Px(shade, "width"), 1);
	}

	[Fact]
	public void Thin_filled_rectangles_become_rules()
	{
		var (_, dom) = ImportDom(RawPage("0 0 0 rg 72 600 468 1 re f"));

		var rule = Assert.Single(dom.QuerySelectorAll("div.rule"));
		Assert.Equal(468 * PxPerPt, Px(rule, "width"), 1);
		Assert.Equal(1 * PxPerPt, Px(rule, "height"), 1);
		Assert.Empty(dom.QuerySelectorAll("div.shade"));
	}

	[Fact]
	public void White_fills_are_ignored()
	{
		var (_, dom) = ImportDom(RawPage("1 1 1 rg 0 0 612 792 re f"));
		Assert.Empty(dom.QuerySelector("section.form-page")!.Children);
	}

	[Fact]
	public void One_path_drawing_a_whole_grid_becomes_one_rule_per_line()
	{
		var (_, dom) = ImportDom(RawPage("1 w 72 700 m 540 700 l 72 680 m 540 680 l 72 660 m 540 660 l 72 700 m 72 660 l 540 700 m 540 660 l S"));
		Assert.Equal(5, dom.QuerySelectorAll("div.rule").Length);
	}

	[Fact]
	public void Small_filled_curves_are_bullets_and_other_curves_are_skipped_with_a_note()
	{
		// A ~6pt filled circle (bullet), a large stroked arc, and a diagonal line.
		var content =
			"100 700 m 100 701.7 98.7 703 97 703 c 95.3 703 94 701.7 94 700 c 94 698.3 95.3 697 97 697 c 98.7 697 100 698.3 100 700 c f " +
			"100 400 m 150 500 250 500 300 400 c S " +
			"72 72 m 200 200 l S";

		var (result, dom) = ImportDom(RawPage(content));

		var bullet = Assert.Single(dom.QuerySelectorAll("div.bullet"));
		Assert.Equal(6 * PxPerPt, Px(bullet, "width"), 1);
		Assert.Empty(dom.QuerySelectorAll("div.rule"));
		Assert.Contains(result.Notes, n => n.StartsWith("2 curved or diagonal"));
	}

	// ---- Images -----------------------------------------------------------------------------------------------------

	[Fact]
	public void Images_are_stored_as_assets_at_their_position()
	{
		var pdf = Letter((page, fonts) =>
		{
			page.AddText("Photo", 10, At(72, 720), fonts.Helvetica);
			page.AddPng(Html.Png, new UglyToad.PdfPig.Core.PdfRectangle(72, 500, 272, 650));
		});

		var (result, dom) = ImportDom(pdf);

		var img = Assert.Single(dom.QuerySelectorAll("img.img"));
		var src = img.GetAttribute("src")!;
		Assert.StartsWith(LegacyAssetStore.UrlPrefix, src);
		Assert.True(File.Exists(Path.Combine(_assets.AssetFolder, src[LegacyAssetStore.UrlPrefix.Length..])));
		Assert.Equal(72 * PxPerPt, Px(img, "left"), 1);
		Assert.Equal(142 * PxPerPt, Px(img, "top"), 1);
		Assert.Equal(200 * PxPerPt, Px(img, "width"), 1);
		Assert.Equal(150 * PxPerPt, Px(img, "height"), 1);
		Assert.Equal(1, result.Counts["images"]);
		Assert.DoesNotContain(result.Notes, n => n.Contains("scanned"));
	}

	[Fact]
	public void Pages_that_are_only_an_image_are_reported_as_scanned()
	{
		var pdf = Pages(2, (page, fonts, n) =>
		{
			if (n == 1) page.AddText("Cover", 10, At(72, 720), fonts.Helvetica);
			else page.AddPng(Html.Png, new UglyToad.PdfPig.Core.PdfRectangle(0, 0, 612, 792));
		});

		var (result, _) = ImportDom(pdf);

		Assert.Contains(result.Notes, n => n.Contains("Page(s) 2 have no text layer"));
	}

	// ---- Form fields ------------------------------------------------------------------------------------------------

	[Fact]
	public void AcroForm_fields_become_unmapped_form_fields()
	{
		var (result, dom) = ImportDom(WithForm());

		var fields = dom.QuerySelectorAll("span.abs.field");
		Assert.Equal(["PolicyNumber", "Agree"], fields.Select(f => f.GetAttribute("data-legacy-field")));
		Assert.Equal(["PolicyNumber", "Agree"], result.Fields);
		Assert.Equal(2, result.Counts["fields"]);

		var text = fields[0];
		Assert.Equal("12", text.GetAttribute("data-maxlen"));
		Assert.Equal(100 * PxPerPt, Px(text, "left"), 1);
		Assert.Equal(172 * PxPerPt, Px(text, "top"), 1);
		Assert.Equal(200 * PxPerPt, Px(text, "width"), 1);
		Assert.Equal(20 * PxPerPt, Px(text, "height"), 1);
		Assert.Empty(text.TextContent);
		Assert.Null(fields[1].GetAttribute("data-maxlen"));

		Assert.Equal("Policy Number:", Assert.Single(Texts(dom)).TextContent);
		Assert.Contains(result.Notes, n => n.Contains("1 signature/button"));
		Assert.Contains(result.Notes, n => n.Contains("not mapped yet"));
	}

	[Fact]
	public void Pdfs_without_a_form_have_no_fields()
	{
		var result = Import(Letter((page, fonts) => page.AddText("No form", 10, At(72, 720), fonts.Helvetica)));
		Assert.Empty(result.Fields);
		Assert.Equal(0, result.Counts["fields"]);
		Assert.DoesNotContain(result.Notes, n => n.Contains("not mapped"));
	}

	// ---- Robustness -------------------------------------------------------------------------------------------------

	[Theory]
	[InlineData("%PDF-1.7\nthis is not a pdf at all")]
	[InlineData("not even a pdf header")]
	public void Unreadable_files_are_rejected_with_a_readable_message(string content)
	{
		var bytes = System.Text.Encoding.Latin1.GetBytes(content);
		var error = Record.Exception(() => Import(bytes));
		// Either the parser rejects the file, or nothing is found in it; never an unhandled parser exception.
		if (error is not null)
		{
			var importError = Assert.IsType<DocumentImportException>(error);
			Assert.Contains("PDF", importError.Message);
		}
		else
		{
			Assert.Equal(0, Import(bytes).Pages);
		}
	}

	[Fact]
	public void The_output_only_contains_allow_listed_elements()
	{
		var pdf = Letter((page, fonts) =>
		{
			page.AddText("Text", 10, At(72, 720), fonts.Helvetica);
			page.DrawLine(At(72, 600), At(540, 600), 1);
			page.DrawRectangle(At(72, 400), 200, 100, 1);
			page.AddPng(Html.Png, new UglyToad.PdfPig.Core.PdfRectangle(72, 100, 172, 200));
		});

		var dom = ImportDom(pdf).Dom;

		var allowed = new[] { "section", "span", "div", "img" };
		Assert.All(dom.Body!.QuerySelectorAll("*"), e => Assert.Contains(e.LocalName, allowed));
		Assert.All(dom.Body!.QuerySelectorAll("[style]"), e => Assert.StartsWith("position:absolute;", e.GetAttribute("style")));
	}
}
