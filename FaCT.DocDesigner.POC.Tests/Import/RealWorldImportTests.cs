using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FaCT.DocDesigner.POC.Import;
using FaCT.DocDesigner.POC.Legacy;
using UglyToad.PdfPig;
using UglyToad.PdfPig.AcroForms.Fields;

namespace FaCT.DocDesigner.POC.Tests.Import;

/// <summary>The real-world sample files (Samples/RealWorld, see SOURCES.txt).</summary>
internal static class RealWorld
{
	public static string Folder => Path.Combine(GoldenCases.RepoRoot, "FaCT.DocDesigner.POC.Tests", "Samples", "RealWorld");

	public static byte[] Docx(string name) => File.ReadAllBytes(Path.Combine(Folder, "docx", name + ".docx"));

	public static byte[] Pdf(string name) => File.ReadAllBytes(Path.Combine(Folder, "pdf", name + ".pdf"));

	public static string PdfPath(string name) => Path.Combine(Folder, "pdf", name + ".pdf");

	public static string DocxPath(string name) => Path.Combine(Folder, "docx", name + ".docx");
}

/// <summary>
/// Word mail-merge documents saved by real Word versions (the docx-mailmerge project's test files): merge fields split
/// across runs, spaces in names, stale shown results, fields in tables, headers and footers.
/// </summary>
public sealed class RealWorldDocxImportTests : IDisposable
{
	private readonly TempAssets _assets = new();

	public void Dispose() => _assets.Dispose();

	private DocumentImportResult Import(string name) => new DocxImporter(_assets.Store).Import(RealWorld.Docx(name));

	private static List<string> FieldSpans(string html) =>
		Regex.Matches(html, "data-field=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();

	[Theory]
	[InlineData("test_winword2010", new[] { "Titel", "Voornaam", "Achternaam", "Adresregel_1", "Postcode", "Plaats", "Provincie", "Land_of_regio" })]
	[InlineData("test_macword2011", new[] { "first_name", "last_name", "address_line", "postal_code", "city", "state", "country", "date" })]
	[InlineData("test_spaces", new[] { "Singleword", "Hello_world", "More_than_one_space" })]
	[InlineData("test_issue8", new[] { "testfield" })]
	[InlineData("test_multiple_elements", new[] { "foo", "bar", "gak" })]
	[InlineData("test_merge_table_rows", new[] { "student_name", "study_name", "class_code", "class_name", "class_grade", "thesis_grade" })]
	[InlineData("test_merge_table_multipart", new[] { "student_name", "study_name", "class_code", "class_name", "class_grade", "thesis_grade" })]
	[InlineData("test_merge_pages", new[] { "fieldname" })]
	[InlineData("test_merge_pages_paged", new[] { "fieldname" })]
	[InlineData("test_merge_templates_simple", new[] { "fieldname" })]
	public void Every_merge_field_becomes_a_data_field_in_the_model(string file, string[] expected)
	{
		var result = Import(file);
		Assert.Equal("docx", result.Format);
		Assert.Equal(expected.Order(), result.Fields.Distinct().Order());
		Assert.Equal(expected.Order(), FieldSpans(result.Html).Distinct().Order());
		Assert.Equal(result.Counts["fields"], FieldSpans(result.Html).Count);
		Assert.NotNull(result.Model);
		foreach (var field in expected)
		{
			Assert.True(result.Model!.ContainsKey(field), $"{field} not in the model");
		}
		// merge-field codes never leak into the text
		Assert.DoesNotContain("MERGEFIELD", result.Html);
		Assert.DoesNotContain("MERGEFORMAT", result.Html);
	}

	[Fact]
	public void A_field_split_over_many_runs_with_extra_spaces_is_one_field()
	{
		// " MERGEFIELD  testfield " typed in pieces by Word, with stray spaces
		var result = Import("test_issue8");
		Assert.Equal(["testfield"], FieldSpans(result.Html));
	}

	[Fact]
	public void The_field_code_wins_over_a_stale_shown_result()
	{
		// The third field's code says "gak" but Word last showed «boo».
		var result = Import("test_multiple_elements");
		Assert.Equal(["foo", "bar", "gak"], FieldSpans(result.Html));
		Assert.DoesNotContain("boo", result.Html);
	}

	[Fact]
	public void Spaces_in_field_names_become_underscores()
	{
		var result = Import("test_spaces");
		Assert.Equal(["Singleword", "Hello_world", "More_than_one_space"], FieldSpans(result.Html));
	}

	[Fact]
	public void Fields_in_table_cells_stay_in_the_table()
	{
		var html = Import("test_merge_table_rows").Html;
		var table = html[html.IndexOf("<table", StringComparison.Ordinal)..(html.IndexOf("</table>", StringComparison.Ordinal) + 8)];
		foreach (var field in new[] { "class_code", "class_name", "class_grade" })
		{
			Assert.Matches($"<td[^>]*>(?:(?!</td>).)*data-field=\"{field}\"", table);
		}
	}

	[Fact]
	public void Headers_footers_and_page_breaks_are_kept()
	{
		var pages = Import("test_merge_pages");
		Assert.Equal(1, pages.Counts["headers"]);
		Assert.Equal(1, pages.Counts["footers"]);
		var paged = Import("test_merge_pages_paged");
		Assert.Equal(1, paged.Counts["pageBreaks"]);
		Assert.Equal(2, paged.Pages);
	}

	[Theory]
	[InlineData("test_winword2010")]
	[InlineData("test_macword2011")]
	public void A_whole_letter_keeps_its_wording(string file)
	{
		var result = Import(file);
		Assert.True(result.Counts["paragraphs"] >= 10, result.Counts["paragraphs"].ToString(CultureInfo.InvariantCulture));
		Assert.Empty(result.Notes.Where(n => n.Contains("could not", StringComparison.OrdinalIgnoreCase)));
	}
}

/// <summary>
/// IRS fillable forms: real AcroForms made with Adobe LiveCycle, so every field is nested under one parent field and the
/// PDF also carries an XFA form. Every fill-in field must arrive, at its place on the page.
/// </summary>
public sealed class RealWorldPdfImportTests : IDisposable
{
	private readonly TempAssets _assets = new();

	public void Dispose() => _assets.Dispose();

	private DocumentImportResult Import(string name) => new PdfImporter(new LegacyFormImporter(_assets.Store)).Import(RealWorld.Pdf(name));

	/// <summary>The fill-in fields (terminal fields) in the PDF itself.</summary>
	private static List<AcroFieldBase> Terminals(string name)
	{
		using var document = PdfDocument.Open(RealWorld.PdfPath(name));
		Assert.True(document.TryGetForm(out var form));
		return PdfImporter.FormFields(form).Select(f => f.Field).ToList();
	}

	[Theory]
	[InlineData("fw9", 23, 6)]
	[InlineData("fw4", 48, 5)]
	[InlineData("fw7", 69, 1)]
	[InlineData("fss4", 89, 2)]
	[InlineData("f1040", 199, 2)]
	[InlineData("f8822", 25, 2)]
	public void Every_fill_in_field_is_imported(string file, int fields, int pages)
	{
		var result = Import(file);
		Assert.Equal(pages, result.Pages);
		Assert.Equal(fields, result.Counts["fields"]);
		Assert.Equal(fields, result.Fields.Count);
		Assert.Equal(fields, result.Fields.Distinct().Count());
		Assert.Equal(fields, Regex.Matches(result.Html, "data-legacy-field=\"").Count);
		Assert.Contains("The PDF also carries an XFA (Adobe LiveCycle) form; its standard form fields were imported.", result.Notes);
		Assert.Contains(result.Notes, n => n.StartsWith("Form fields are not mapped yet", StringComparison.Ordinal));
	}

	[Fact]
	public void Fields_keep_their_names_from_the_form()
	{
		var result = Import("fw9");
		Assert.Contains("f1_01[0]", result.Fields);
		Assert.Contains("c1_1[0]", result.Fields);
		Assert.DoesNotContain(result.Fields, f => f.StartsWith("topmostSubform", StringComparison.Ordinal));
	}

	[Fact]
	public void Each_field_sits_exactly_where_it_is_on_the_form()
	{
		var result = Import("fw9");
		var page1 = result.Html[..result.Html.IndexOf("</section>", StringComparison.Ordinal)];
		var boxes = Regex.Matches(page1, "<span class=\"abs field(?: field-check)?\" data-legacy-field=\"([^\"]+)\"[^>]*style=\"([^\"]+)\"")
			.ToDictionary(m => m.Groups[1].Value, m => Style(m.Groups[2].Value));
		using var document = PdfDocument.Open(RealWorld.PdfPath("fw9"));
		var crop = document.GetPage(1).CropBox.Bounds;
		var checkedFields = 0;
		Assert.True(document.TryGetForm(out var form));
		foreach (var (field, name) in PdfImporter.FormFields(form).Where(f => f.Field.PageNumber == 1))
		{
			var b = field.Bounds!.Value;
			var box = boxes[name];
			// emit-html points become CSS pixels (96 / 72)
			Assert.Equal((b.Left - crop.Left) * 96 / 72, box["left"], 1.0);
			Assert.Equal((crop.Top - b.Top) * 96 / 72, box["top"], 1.0);
			Assert.Equal(b.Width * 96 / 72, box["width"], 1.0);
			Assert.Equal(b.Height * 96 / 72, box["height"], 1.0);
			checkedFields++;
		}
		Assert.Equal(boxes.Count, checkedFields);
		Assert.True(checkedFields >= 15, checkedFields.ToString(CultureInfo.InvariantCulture));
	}

	private static Dictionary<string, double> Style(string style) =>
		style.Split(';', StringSplitOptions.RemoveEmptyEntries)
			.Select(p => p.Split(':', 2))
			.Where(p => p.Length == 2 && p[1].Trim().EndsWith("px", StringComparison.Ordinal))
			.ToDictionary(p => p[0].Trim(), p => double.Parse(p[1].Trim()[..^2], CultureInfo.InvariantCulture));

	[Fact]
	public void Character_limits_of_fields_are_kept()
	{
		var withLimit = Terminals("fw9").OfType<AcroTextField>().Count(f => f.MaxLength > 0);
		Assert.True(withLimit > 0);
		Assert.Equal(withLimit, Regex.Matches(Import("fw9").Html, "data-maxlen=\"\\d+\"").Count);
	}

	[Theory]
	[InlineData("fw9", 8)]
	[InlineData("fw4", 5)]
	[InlineData("fw7", 21)]
	[InlineData("fss4", 44)]
	[InlineData("f1040", 73)]
	[InlineData("f8822", 3)]
	public void Check_boxes_are_imported_as_check_box_fields(string file, int checkBoxes)
	{
		Assert.Equal(checkBoxes, Terminals(file).Count(f => f.FieldType == AcroFieldType.Checkbox));
		var result = Import(file);
		Assert.Equal(checkBoxes, result.Counts["checkboxes"]);
		Assert.Equal(checkBoxes, Regex.Matches(result.Html, "<span class=\"abs field field-check\" data-legacy-field=").Count);
		Assert.Contains($"{checkBoxes} check box(es) print an X when the data they are mapped to says so (Checked when, in the field's settings).", result.Notes);
		// still fields, counted with the text fields
		Assert.Equal(result.Fields.Count, result.Counts["fields"]);
	}

	[Fact]
	public void A_check_box_mark_fills_most_of_its_box()
	{
		var html = Import("fw9").Html;
		var check = Regex.Match(html, "<span class=\"abs field field-check\" data-legacy-field=\"c1_1\\[0\\]\"[^>]*style=\"([^\"]+)\"");
		Assert.True(check.Success);
		// 8pt box: 8 x 0.9 = 7.2pt
		Assert.Contains("font-size:7.2pt", check.Groups[1].Value.Replace(" ", ""));
		Assert.DoesNotContain("field-check", Regex.Match(html, "<span[^>]*data-legacy-field=\"f1_01\\[0\\]\"[^>]*>").Value);
	}

	[Fact]
	public void The_wording_of_the_form_is_imported_too()
	{
		var result = Import("fw9");
		Assert.True(result.Counts["texts"] > 500, result.Counts["texts"].ToString(CultureInfo.InvariantCulture));
		Assert.Contains("Request for Taxpayer", result.Html);
	}

	/// <summary>
	/// LiveCycle-style nesting, built by hand: top[0] > Page1[0] > (name[0], unique[0]) and top[0] > Page2[0] > name[0],
	/// with an XFA entry in the AcroForm.
	/// </summary>
	private static byte[] NestedForm(bool xfa = true) => Pdf.Raw(
		"<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R]" + (xfa ? " /XFA 11 0 R" : "") + " >> >>",
		"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
		"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [7 0 R 8 0 R 9 0 R] /Contents 10 0 R /Resources << /Font << /F1 12 0 R >> >> >>",
		"<< /T (top[0]) /Kids [5 0 R 6 0 R] >>",
		"<< /T (Page1[0]) /Parent 4 0 R /Kids [7 0 R 8 0 R] >>",
		"<< /T (Page2[0]) /Parent 4 0 R /Kids [9 0 R] >>",
		"<< /Type /Annot /Subtype /Widget /FT /Tx /T (name[0]) /Parent 5 0 R /Rect [100 600 300 620] /P 3 0 R /F 4 >>",
		"<< /Type /Annot /Subtype /Widget /FT /Tx /T (unique[0]) /Parent 5 0 R /Rect [100 560 300 580] /P 3 0 R /F 4 >>",
		"<< /Type /Annot /Subtype /Widget /FT /Tx /T (name[0]) /Parent 6 0 R /Rect [100 520 300 540] /P 3 0 R /F 4 >>",
		Pdf.Stream("BT /F1 12 Tf 72 700 Td (Nested form) Tj ET"),
		Pdf.Stream("<xdp:xdp xmlns:xdp=\"http://ns.adobe.com/xdp/\"></xdp:xdp>"),
		"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

	[Fact]
	public void Nested_fields_are_found_and_repeated_names_are_told_apart_by_their_parents()
	{
		using (var document = PdfDocument.Open(NestedForm()))
		{
			Assert.True(document.TryGetForm(out var form));
			Assert.Equal(["Page1[0].name[0]", "unique[0]", "Page2[0].name[0]"], PdfImporter.FormFields(form).Select(f => f.Name));
		}

		var result = new PdfImporter(new LegacyFormImporter(_assets.Store)).Import(NestedForm());
		Assert.Equal(["Page1[0].name[0]", "unique[0]", "Page2[0].name[0]"], result.Fields);
		Assert.Contains("data-legacy-field=\"Page2[0].name[0]\"", result.Html);
		Assert.Contains("The PDF also carries an XFA (Adobe LiveCycle) form; its standard form fields were imported.", result.Notes);
	}

	[Fact]
	public void A_form_without_xfa_gets_no_xfa_note()
	{
		var result = new PdfImporter(new LegacyFormImporter(_assets.Store)).Import(NestedForm(xfa: false));
		Assert.Equal(3, result.Fields.Count);
		Assert.DoesNotContain(result.Notes, n => n.Contains("XFA", StringComparison.Ordinal));
	}
}

/// <summary>The real files through the designer, as a user imports them, and rendered with data.</summary>
[Collection("Designer UI")]
public sealed class RealWorldImportDesignerTests(DesignerFixture designer) : IClassFixture<DesignerFixture>
{
	private static string Words(byte[] pdf) => string.Join(' ', PdfText.Extract(pdf).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

	[Fact]
	public async Task A_word_2010_mail_merge_letter_renders_with_message_data()
	{
		var status = await designer.ImportDocumentAsync(RealWorld.DocxPath("test_winword2010"), placeholders: []);
		Assert.StartsWith("Imported test_winword2010.docx", status);
		var fields = await designer.EvaluateAsync<string[]>("grapesjs.editors[0].getWrapper().findType('data-field').map(c => c.get('field'))");
		Assert.Contains("Voornaam", fields);
		Assert.Contains("Achternaam", fields);
		Assert.Equal(0, await designer.EvaluateAsync<int>("grapesjs.editors[0].Canvas.getDocument().querySelectorAll('.binding-bad').length"));

		var html = await designer.EvaluateAsync<string>("grapesjs.editors[0].getHtml()");
		var css = await designer.EvaluateAsync<string>("grapesjs.editors[0].getCss()");
		var text = Words(await designer.RenderPdfAsync(html, css, new JsonObject
		{
			["Titel"] = "Mevrouw", ["Voornaam"] = "Anna", ["Achternaam"] = "Jansen", ["Adresregel_1"] = "Kerkstraat 1",
			["Postcode"] = "1234 AB", ["Plaats"] = "Utrecht", ["Provincie"] = "Utrecht", ["Land_of_regio"] = "Nederland"
		}));
		Assert.Contains("Anna", text);
		Assert.Contains("Jansen", text);
		Assert.Contains("Kerkstraat 1", text);
		Assert.Contains("1234 AB", text);
		Assert.DoesNotContain("«", text);
	}

	[Fact]
	public async Task A_mapped_w9_field_prints_its_value_inside_the_form_box()
	{
		var status = await designer.ImportDocumentAsync(RealWorld.PdfPath("fw9"));
		Assert.StartsWith("Imported fw9.pdf (6 page(s))", status);
		Assert.Equal(23, await designer.EvaluateAsync<int>("grapesjs.editors[0].getWrapper().findType('legacy-field').length"));

		// Map line 1 (name) to a data path, as the user does with the Model panel.
		await designer.EvaluateAsync<bool>(
			"(() => { const f = grapesjs.editors[0].getWrapper().findType('legacy-field').find(c => c.get('legacyName') === 'f1_01[0]'); f.set('field', 'insured.name'); return true; })()");
		var html = await designer.EvaluateAsync<string>("grapesjs.editors[0].getHtml()");
		var css = await designer.EvaluateAsync<string>("grapesjs.editors[0].getCss()");
		var pdf = await designer.RenderPdfAsync(html, css, new JsonObject { ["insured"] = new JsonObject { ["name"] = "ACME BAKERY LLC" } });

		using var rendered = PdfDocument.Open(pdf);
		var page = rendered.GetPage(1);
		var word = page.GetWords().First(w => w.Text == "ACME");
		using var original = PdfDocument.Open(RealWorld.PdfPath("fw9"));
		Assert.True(original.TryGetForm(out var form));
		var box = PdfImporter.FormFields(form).Single(f => f.Name == "f1_01[0]").Field.Bounds!.Value;
		Assert.InRange(word.BoundingBox.Left, box.Left - 2, box.Right);
		// PdfPig's glyph boxes include the font's descent, so compare the middle of the word.
		Assert.InRange((word.BoundingBox.Bottom + word.BoundingBox.Top) / 2, box.Bottom, box.Top);
		Assert.Contains("Request for Taxpayer", Words(pdf));
	}

	[Fact]
	public async Task A_mapped_w9_check_box_prints_an_X_in_its_box_only_for_its_value()
	{
		await designer.ImportDocumentAsync(RealWorld.PdfPath("fw9"));
		Assert.Equal(8, await designer.EvaluateAsync<int>("grapesjs.editors[0].getWrapper().findType('legacy-field').filter(c => c.get('check')).length"));
		Assert.Equal("Check box", await designer.EvaluateAsync<string>(
			"grapesjs.editors[0].getWrapper().findType('legacy-field').find(c => c.get('legacyName') === 'c1_1[0]').get('name')"));
		Assert.Equal(["field", "checkedWhen", "legacyName"], await designer.EvaluateAsync<string[]>(
			"grapesjs.editors[0].getWrapper().findType('legacy-field').find(c => c.get('legacyName') === 'c1_1[0]').get('traits').map(t => t.get('name'))"));

		// Line 3a: the first box is "Individual/sole proprietor", the second "C corporation".
		await designer.EvaluateAsync<bool>(
			"(() => { const all = grapesjs.editors[0].getWrapper().findType('legacy-field');" +
			" const a = all.find(c => c.get('legacyName') === 'c1_1[0]'); a.set('field', 'entity.type'); a.set('checkedWhen', 'Individual');" +
			" const b = all.find(c => c.get('legacyName') === 'c1_2[0]'); b.set('field', 'entity.type'); b.set('checkedWhen', 'C \"Corp\"');" +
			" return true; })()");
		var html = await designer.EvaluateAsync<string>("grapesjs.editors[0].getHtml()");
		Assert.Contains("{{ entity.type | checkmark: \"Individual\" }}", html);
		Assert.Contains("{{ entity.type | checkmark: \"C Corp\" }}", html);
		var css = await designer.EvaluateAsync<string>("grapesjs.editors[0].getCss()");
		var pdf = await designer.RenderPdfAsync(html, css, new JsonObject { ["entity"] = new JsonObject { ["type"] = "individual" } });
		var blank = await designer.RenderPdfAsync(html, css, new JsonObject { ["entity"] = new JsonObject { ["type"] = "trust" } });

		// The bold X is an unnamed Type3 font whose glyph boxes PdfPig misplaces, so find it by pixels: where the page with
		// the box checked differs from the page with nothing checked.
		const double scale = 4;
		var (on, width, height) = PageImages.Pixels(pdf, 1, scale);
		var (off, _, _) = PageImages.Pixels(blank, 1, scale);
		int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
		for (var y = 0; y < height; y++)
		{
			for (var x = 0; x < width; x++)
			{
				var i = (y * width + x) * 4;
				if (Math.Abs(on[i] - off[i]) + Math.Abs(on[i + 1] - off[i + 1]) + Math.Abs(on[i + 2] - off[i + 2]) + Math.Abs(on[i + 3] - off[i + 3]) < 60) continue;
				minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
			}
		}
		Assert.True(maxX >= 0, "nothing printed");
		var pageHeight = height / scale;
		var mark = (Left: minX / scale, Right: (maxX + 1) / scale, Top: pageHeight - minY / scale, Bottom: pageHeight - (maxY + 1) / scale);

		using var original = PdfDocument.Open(RealWorld.PdfPath("fw9"));
		Assert.True(original.TryGetForm(out var form));
		var individual = PdfImporter.FormFields(form).Single(f => f.Name == "c1_1[0]").Field.Bounds!.Value;
		// the only ink that changed is the X, inside the Individual box (the C corporation box stays empty)
		Assert.InRange(mark.Left, individual.Left, individual.Right);
		Assert.InRange(mark.Right, individual.Left, individual.Right);
		Assert.InRange(mark.Bottom, individual.Bottom - 0.5, individual.Top);
		Assert.InRange(mark.Top, individual.Bottom, individual.Top + 0.5);
		Assert.True(mark.Right - mark.Left > 3, $"the X is {mark.Right - mark.Left}pt wide");
		Assert.Contains("X", Words(pdf));
	}

	[Fact]
	public async Task A_check_box_shows_its_sample_on_the_canvas()
	{
		await designer.ImportDocumentAsync(RealWorld.PdfPath("fw9"));
		var mark = "(() => { const c = grapesjs.editors[0].getWrapper().findType('legacy-field').find(c => c.get('legacyName') === 'c1_1[0]');" +
			" return c.view.el.textContent; })()";
		// unmapped: no text of its own
		Assert.Equal("", await designer.EvaluateAsync<string>(mark));
		// the sample model has no such value: an empty (unchecked) box
		await designer.EvaluateAsync<bool>("(() => { grapesjs.editors[0].getWrapper().findType('legacy-field').find(c => c.get('legacyName') === 'c1_1[0]').set('field', 'nothing.here'); return true; })()");
		Assert.Equal("\u00a0", await designer.EvaluateAsync<string>(mark));
	}

	[Fact]
	public async Task Suggest_mappings_uses_the_printed_labels_of_a_w9()
	{
		// a model with entity.name and requester.address (from a Word letter's merge fields)
		var docx = Docx.WithBody(Docx.P(Docx.MergeField(" MERGEFIELD entity.name ", "x")
			.Concat(Docx.MergeField(" MERGEFIELD requester.address ", "y")).ToArray<DocumentFormat.OpenXml.OpenXmlElement>()));
		var folder = Directory.CreateTempSubdirectory("docdesigner-labels-").FullName;
		try
		{
			var model = Path.Combine(folder, "model.docx");
			File.WriteAllBytes(model, docx);
			await designer.ImportDocumentAsync(model);
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
		}
		await designer.ImportDocumentAsync(RealWorld.PdfPath("fw9"));

		// the label stays out of the template; it shows when hovering the unmapped field
		Assert.DoesNotContain("data-label", await designer.EvaluateAsync<string>("grapesjs.editors[0].getHtml()"));
		Assert.Equal("Form field f1_01[0] (\u201cName of entity/individual\u201d on the form) is not mapped to the model yet.", await designer.EvaluateAsync<string>(
			"grapesjs.editors[0].getWrapper().findType('legacy-field').find(c => c.get('legacyName') === 'f1_01[0]').view.el.getAttribute('title')"));

		await designer.EvaluateAsync<bool>("document.getElementById('mapSuggest').click(), true");
		await designer.WaitForAsync("() => !!document.getElementById('mapApply')");
		var row = "document.querySelector('.map-suggest tr[data-field=\"f1_01[0]\"]')";
		Assert.Equal("Name of entity/individual", await designer.EvaluateAsync<string>(row + ".querySelector('.map-label').textContent"));
		Assert.Equal("entity.name", await designer.EvaluateAsync<string>(row + ".querySelector('.map-path').value"));
		Assert.True(await designer.EvaluateAsync<bool>(row + ".querySelector('.map-accept').checked"));
		Assert.Equal("Requester\u2019s name and address", await designer.EvaluateAsync<string>(
			"document.querySelector('.map-suggest tr[data-field=\"f1_09[0]\"] .map-label').textContent"));

		await designer.EvaluateAsync<bool>("document.getElementById('mapApply').click(), true");
		await designer.WaitForAsync("() => document.getElementById('status').textContent.startsWith('Mapped')");
		Assert.Equal("entity.name", await designer.EvaluateAsync<string>(
			"grapesjs.editors[0].getWrapper().findType('legacy-field').find(c => c.get('legacyName') === 'f1_01[0]').get('field')"));
	}
}
