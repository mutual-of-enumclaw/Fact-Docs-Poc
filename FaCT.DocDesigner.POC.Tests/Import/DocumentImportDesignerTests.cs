using System.Text.Json.Nodes;
using DocumentFormat.OpenXml;
using static FaCT.DocDesigner.POC.Tests.Import.Docx;

namespace FaCT.DocDesigner.POC.Tests.Import;

/// <summary>
/// "Import PDF / Word" as a user drives it in the designer (headless browser, see <see cref="DesignerFixture"/>):
/// the canvas must recognise the imported Data Fields and form fields, load the sample model, and save/render.
/// The documents have no images, so nothing is written to the designer's App_Data.
/// </summary>
[Collection("Designer UI")]
public sealed class DocumentImportDesignerTests(DesignerFixture designer) : IClassFixture<DesignerFixture>, IDisposable
{
	private readonly string _folder = Directory.CreateTempSubdirectory("docdesigner-ui-import-").FullName;

	public void Dispose() => Directory.Delete(_folder, recursive: true);

	private string Save(string name, byte[] bytes)
	{
		var path = Path.Combine(_folder, name);
		File.WriteAllBytes(path, bytes);
		return path;
	}

	[Fact]
	public async Task A_word_document_imports_as_bound_data_fields_with_its_model()
	{
		var docx = WithBody(
			Styled("Heading1", "Renewal Notice"),
			P(new OpenXmlElement[] { R("Policy ") }
				.Concat(MergeField(" MERGEFIELD policy.number ", "«policy.number»"))
				.Append(R(" renews on "))
				.Concat(MergeField(" MERGEFIELD \"Renewal Date\" ", "«Renewal Date»"))
				.Append(R("."))
				.ToArray()),
			P("Keep {{ braces }} as text."));

		var status = await designer.ImportDocumentAsync(Save("Renewal Notice.docx", docx), placeholders: []);

		Assert.StartsWith("Imported Renewal Notice.docx", status);
		Assert.Contains("Model built from 2 merge field(s)", status);
		Assert.Equal("Renewal-Notice", await designer.EvaluateAsync<string>("document.getElementById('templateName').value"));

		// The spans became real Data Fields bound to the imported model, with no binding problems.
		var fields = await designer.EvaluateAsync<string[]>(
			"grapesjs.editors[0].getWrapper().findType('data-field').map(c => c.get('field'))");
		Assert.Equal(["policy.number", "Renewal_Date"], fields);
		Assert.Equal(0, await designer.EvaluateAsync<int>(
			"grapesjs.editors[0].Canvas.getDocument().querySelectorAll('.binding-bad').length"));
		Assert.Contains("policy", await designer.EvaluateAsync<string>("document.getElementById('modelTree').textContent"));

		var html = await designer.EvaluateAsync<string>("grapesjs.editors[0].getHtml()");
		var css = await designer.EvaluateAsync<string>("grapesjs.editors[0].getCss()");
		Assert.Contains("{{ policy.number }}", html);
		Assert.Contains("{{ Renewal_Date }}", html);
		// The document's own braces stay apart in what the designer saves.
		Assert.DoesNotContain("{{ braces", html);

		// What Save Draft would store renders with message data.
		var pdf = await designer.RenderPdfAsync(html, css, new JsonObject
		{
			["policy"] = new JsonObject { ["number"] = "CPP5550001" },
			["Renewal_Date"] = "10/01/2026"
		});
		var text = string.Join(' ', PdfText.Extract(pdf).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
		Assert.Contains("Policy CPP5550001 renews on 10/01/2026.", text);
		Assert.Contains("Keep {{ braces }} as text.", text);
	}

	[Fact]
	public async Task A_pdf_imports_as_form_pages_with_unmapped_fields()
	{
		var status = await designer.ImportDocumentAsync(Save("acord-form.pdf", Pdf.WithForm()));

		Assert.StartsWith("Imported acord-form.pdf (1 page(s))", status);
		Assert.Contains("not mapped yet", status);
		Assert.Equal(1, await designer.EvaluateAsync<int>("grapesjs.editors[0].getWrapper().findType('legacy-page').length"));
		Assert.Equal(["PolicyNumber", "Agree"], await designer.EvaluateAsync<string[]>(
			"grapesjs.editors[0].getWrapper().findType('legacy-field').map(c => c.get('legacyName'))"));
		Assert.Equal(1, await designer.EvaluateAsync<int>("grapesjs.editors[0].getWrapper().findType('legacy-text').length"));
		Assert.Contains("2 form fields not mapped yet", await designer.EvaluateAsync<string>("document.getElementById('modelProblems').textContent"));

		var html = await designer.EvaluateAsync<string>("grapesjs.editors[0].getHtml()");
		var css = await designer.EvaluateAsync<string>("grapesjs.editors[0].getCss()");
		var pdf = await designer.RenderPdfAsync(html, css, new JsonObject());
		Assert.Contains("Policy Number:", string.Join(' ', PdfText.Extract(pdf).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
	}

	[Fact]
	public async Task Word_headers_and_footers_survive_the_designer_and_print_page_numbers()
	{
		var docx = Create((main, body) =>
		{
			var header = AddHeader(main, P("Designer Header"));
			var footer = AddFooter(main, PageXOfY());
			body.Append(P("Page one."), P(PageBreak()), P("Page two."), Section(header, footer));
		});

		var status = await designer.ImportDocumentAsync(Save("header-footer.docx", docx));
		Assert.StartsWith("Imported header-footer.docx", status);

		// What Save Draft stores: the running layout and its page geometry.
		var html = await designer.EvaluateAsync<string>("grapesjs.editors[0].getHtml()");
		var css = await designer.EvaluateAsync<string>("grapesjs.editors[0].getCss()");
		Assert.Contains("gd-pdffoot", html);
		Assert.Contains("gd-pageno", html);
		Assert.Contains("@page gdrun", css);

		var text = string.Join(' ', PdfText.Extract(await designer.RenderPdfAsync(html, css, new JsonObject()))
			.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
		Assert.Contains("Page 1 of 2", text);
		Assert.Contains("Page 2 of 2", text);
		Assert.Equal(2, text.Split("Designer Header").Length - 1);
	}

	[Fact]
	public async Task Typed_placeholders_become_bound_fields_with_their_format()
	{
		var docx = WithBody(P("Dear {{ insured.name }}, premium {{ premium | currency }}, agent [Agent Name]."));

		var status = await designer.ImportDocumentAsync(Save("placeholders.docx", docx));

		Assert.Contains("[bracketed] phrase(s) look like placeholders", status);
		var fields = await designer.EvaluateAsync<string[]>(
			"grapesjs.editors[0].getWrapper().findType('data-field').map(c => c.get('field') + '|' + c.get('format'))");
		Assert.Equal(["insured.name|", "premium|currency"], fields);
		Assert.Equal(0, await designer.EvaluateAsync<int>(
			"grapesjs.editors[0].Canvas.getDocument().querySelectorAll('.binding-bad').length"));

		var html = await designer.EvaluateAsync<string>("grapesjs.editors[0].getHtml()");
		var css = await designer.EvaluateAsync<string>("grapesjs.editors[0].getCss()");
		var text = string.Join(' ', PdfText.Extract(await designer.RenderPdfAsync(html, css, new JsonObject
		{
			["insured"] = new JsonObject { ["name"] = "Acme Farms" },
			["premium"] = 1500
		})).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
		Assert.Contains("Dear Acme Farms, premium $1,500.00, agent [Agent Name].", text);
	}

	[Fact]
	public async Task Ticking_brackets_in_the_import_dialog_converts_them_too()
	{
		var docx = WithBody(P("Agent [Agent Name], producer \u00ABProducer\u00BB."));

		await designer.ImportDocumentAsync(Save("brackets.docx", docx), placeholders: ["brackets"]);

		var fields = await designer.EvaluateAsync<string[]>(
			"grapesjs.editors[0].getWrapper().findType('data-field').map(c => c.get('field'))");
		// Only brackets were ticked, so the chevrons stay text.
		Assert.Equal(["Agent_Name"], fields);
	}

	[Fact]
	public async Task Closing_the_import_dialog_cancels_the_import()
	{
		await designer.EvaluateAsync<bool>("document.getElementById('docFile').value = ''; true");
		var path = Save("cancel.docx", WithBody(P("x")));
		var status = await designer.CancelDocumentImportAsync(path);
		Assert.Equal("Import cancelled.", status);
	}

	[Fact]
	public async Task Suggest_mappings_maps_confident_pdf_form_fields_in_one_click()
	{
		// A model with policy.number and terms.accepted (from a Word document's merge fields), then a PDF form with fields
		// "PolicyNumber" (clearly policy.number) and "Agree" (no property with a similar name).
		var docx = WithBody(P(MergeField(" MERGEFIELD policy.number ", "x")
			.Concat(MergeField(" MERGEFIELD terms.accepted ", "y")).ToArray<DocumentFormat.OpenXml.OpenXmlElement>()));
		await designer.ImportDocumentAsync(Save("model.docx", docx));
		await designer.ImportDocumentAsync(Save("form.pdf", Pdf.WithForm()));

		await designer.EvaluateAsync<bool>("document.getElementById('mapSuggest').click(), true");
		await designer.WaitForAsync("() => !!document.getElementById('mapApply')");

		var rows = await designer.EvaluateAsync<string[]>(
			"[...document.querySelectorAll('.map-suggest tr[data-field]')].map(r => r.getAttribute('data-field') + '|' + " +
			"r.querySelector('.map-accept').checked + '|' + r.querySelector('.map-path').value)");
		Assert.Contains("PolicyNumber|true|policy.number", rows);
		Assert.Contains(rows, r => r.StartsWith("Agree|false", StringComparison.Ordinal));

		await designer.EvaluateAsync<bool>("document.getElementById('mapApply').click(), true");
		await designer.WaitForAsync("() => document.getElementById('status').textContent.startsWith('Mapped')");

		Assert.Equal("Mapped 1 form field(s).", await designer.EvaluateAsync<string>("document.getElementById('status').textContent"));
		var mapped = await designer.EvaluateAsync<string[]>(
			"grapesjs.editors[0].getWrapper().findType('legacy-field').map(c => c.get('legacyName') + '=' + c.get('field'))");
		Assert.Equal(["PolicyNumber=policy.number", "Agree="], mapped);

		// The mapped field now prints the model value.
		var html = await designer.EvaluateAsync<string>("grapesjs.editors[0].getHtml()");
		var css = await designer.EvaluateAsync<string>("grapesjs.editors[0].getCss()");
		var text = PdfText.Extract(await designer.RenderPdfAsync(html, css, new JsonObject { ["policy"] = new JsonObject { ["number"] = "CPP4242" } }));
		Assert.Contains("CPP4242", text);
	}

	[Fact]
	public async Task A_long_pdf_asks_for_pages_and_imports_the_chosen_ones()
	{
		var pdf = Pdf.Pages(105, (page, fonts, n) => page.AddText($"Sheet {n}", 12, Pdf.At(72, 720), fonts.Helvetica));

		var status = await designer.ImportDocumentAsync(Save("long.pdf", pdf), pdfPages: (103, 105));

		Assert.StartsWith("Imported long.pdf (3 page(s))", status);
		Assert.Contains("Imported pages 103-105 of 105.", status);
		Assert.Equal(["Sheet 103", "Sheet 104", "Sheet 105"], await designer.EvaluateAsync<string[]>(
			"grapesjs.editors[0].getWrapper().findType('legacy-page').map(p => p.getEl().textContent)"));
	}

	[Fact]
	public async Task The_page_dialog_refuses_a_range_over_the_limit()
	{
		var pdf = Pdf.Pages(105, (_, _, _) => { });
		await designer.EvaluateAsync<bool>("document.getElementById('status').textContent = ''; document.getElementById('docFile').value = ''; true");
		await designer.UploadAsync("#docFile", Save("long2.pdf", pdf));
		await designer.WaitForAsync("() => !!document.getElementById('pdfPagesGo')");

		// Defaults to the first 100 pages.
		Assert.Equal("1", await designer.EvaluateAsync<string>("document.getElementById('pdfFrom').value"));
		Assert.Equal("100", await designer.EvaluateAsync<string>("document.getElementById('pdfTo').value"));

		await designer.EvaluateAsync<bool>("document.getElementById('pdfTo').value = '105', document.getElementById('pdfPagesGo').click(), true");
		Assert.Contains("choose at most 100", await designer.EvaluateAsync<string>("document.querySelector('.pdf-range-error').textContent"));
		Assert.True(await designer.EvaluateAsync<bool>("!!document.getElementById('pdfPagesGo')"));

		await designer.EvaluateAsync<bool>("grapesjs.editors[0].Modal.close(), true");
		await designer.WaitForAsync("() => document.getElementById('status').textContent === 'Import cancelled.'");
	}

	[Fact]
	public async Task Liquid_separators_are_hidden_from_the_author_but_kept_in_the_template()
	{
		var docx = WithBody(P("Literal {{ braces }} and {% tags %} stay words."));

		await designer.ImportDocumentAsync(Save("separators.docx", docx), placeholders: []);

		var separators = await designer.EvaluateAsync<string[]>(
			"grapesjs.editors[0].getWrapper().find('.no-liquid').map(c => [c.get('type'), c.get('layerable'), c.get('selectable'), c.get('hoverable'), c.get('draggable')].join('|'))");
		Assert.Equal(2, separators.Length);
		Assert.All(separators, s => Assert.Equal("no-liquid|false|false|false|false", s));

		// Not shown in the Layer Manager: the paragraph's layer children skip them.
		var layerChildren = await designer.EvaluateAsync<string[]>(
			"(() => { const lm = grapesjs.editors[0].Layers; const p = grapesjs.editors[0].getWrapper().find('p')[0];" +
			" return lm.getComponents(p).map(c => c.get('type') || 'default'); })()");
		Assert.DoesNotContain("no-liquid", layerChildren);

		// Still in the saved template, so the text never forms Liquid.
		var html = await designer.EvaluateAsync<string>("grapesjs.editors[0].getHtml()");
		Assert.Equal(2, html.Split("<span class=\"no-liquid\"></span>").Length - 1);
		Assert.DoesNotMatch(@"\{[{%]", html);
	}

	[Fact]
	public async Task Liquid_separators_in_pdf_form_text_are_hidden_too()
	{
		var pdf = Pdf.Letter((page, fonts) => page.AddText("Premium {{ premium }}", 12, Pdf.At(72, 720), fonts.Helvetica));

		await designer.ImportDocumentAsync(Save("separators.pdf", pdf));

		Assert.Equal(["no-liquid|false"], await designer.EvaluateAsync<string[]>(
			"grapesjs.editors[0].getWrapper().find('.no-liquid').map(c => c.get('type') + '|' + c.get('layerable'))"));
		Assert.Equal("Premium {{ premium }}", await designer.EvaluateAsync<string>(
			"grapesjs.editors[0].getWrapper().findType('legacy-text')[0].getEl().textContent"));
	}

	[Fact]
	public async Task The_ui_tests_drive_this_build_of_the_designer()
	{
		Assert.Equal(DesignerServer.ExpectedBuild, await DesignerServer.RunningBuildAsync(designer.Http, designer.BaseUri));
	}

	[Fact]
	public async Task Unsupported_files_show_an_error()
	{
		var status = await designer.ImportDocumentAsync(Save("notes.pdf", "just text, not a pdf"u8.ToArray()));
		Assert.Contains("Import failed (400)", status);
		Assert.Contains("not a PDF", status);
	}
}
