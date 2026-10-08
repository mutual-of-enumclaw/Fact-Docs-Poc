using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Wordprocessing;
using FaCT.DocDesigner.POC.Import;
using FaCT.DocDesigner.POC.Legacy;
using FaCT.DocDesigner.POC.Rendering;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using static FaCT.DocDesigner.POC.Tests.Import.Docx;

namespace FaCT.DocDesigner.POC.Tests.Import;

/// <summary>The designer app in memory, with imported assets written to a temp folder instead of App_Data.</summary>
public sealed class ImportAppFactory : WebApplicationFactory<Program>
{
	private readonly TempAssets _assets = new();

	internal TempAssets Assets => _assets;

	protected override void ConfigureWebHost(IWebHostBuilder builder)
	{
		builder.UseEnvironment("Development");
		builder.ConfigureTestServices(services => services.AddSingleton(_assets.Store));
	}

	protected override void Dispose(bool disposing)
	{
		base.Dispose(disposing);
		if (disposing) _assets.Dispose();
	}
}

/// <summary>POST /api/import end to end: validation, both formats, assets, and imported templates rendering with data.</summary>
public sealed class ImportEndpointTests(ImportAppFactory factory) : IClassFixture<ImportAppFactory>
{
	private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

	private async Task<HttpResponseMessage> PostAsync(byte[] body, string? contentType)
	{
		var content = new ByteArrayContent(body);
		if (contentType is not null) content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
		return await factory.CreateClient().PostAsync("/api/import", content);
	}

	private static async Task<string> ErrorAsync(HttpResponseMessage response) =>
		(await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!;

	private static byte[] SampleDocx() => Create((main, body) =>
	{
		var image = AddImage(main, DocumentFormat.OpenXml.Packaging.ImagePartType.Png, Html.Png);
		body.Append(
			Styled("Heading1", "Policy Summary"),
			P(new OpenXmlElement[] { R("Policy ") }
				.Concat(MergeField(" MERGEFIELD policy.number ", "«policy.number»"))
				.Append(R(" for "))
				.Append(SimpleField(" MERGEFIELD \"Insured Name\" ", "«Insured Name»"))
				.ToArray()),
			P("Literal braces stay text: {{ 1 + 2 }}"),
			P(Image(image, 20, 20, "logo")));
	});

	private static byte[] SamplePdf() => Pdf.Letter((page, fonts) =>
	{
		page.AddText("Commercial Property Declarations", 14, Pdf.At(72, 720), fonts.HelveticaBold);
		page.AddText("Named Insured: Acme Farms", 10, Pdf.At(72, 690), fonts.Helvetica);
		page.DrawLine(Pdf.At(72, 680), Pdf.At(540, 680), 1);
	});

	// ---- Request validation -----------------------------------------------------------------------------------------

	[Theory]
	[InlineData("text/plain")]
	[InlineData("multipart/form-data; boundary=abc")]
	[InlineData("application/x-www-form-urlencoded")]
	[InlineData("application/msword")]
	[InlineData(null)]
	public async Task Other_content_types_are_rejected(string? contentType)
	{
		using var response = await PostAsync(SamplePdf(), contentType);
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Contains("Send a PDF", await ErrorAsync(response));
	}

	[Fact]
	public async Task The_declared_type_must_match_the_file()
	{
		using var docxAsPdf = await PostAsync(SampleDocx(), DocumentImport.PdfContentType);
		Assert.Equal(HttpStatusCode.BadRequest, docxAsPdf.StatusCode);
		Assert.Equal("The file is not a PDF.", await ErrorAsync(docxAsPdf));

		using var pdfAsDocx = await PostAsync(SamplePdf(), DocumentImport.DocxContentType);
		Assert.Equal(HttpStatusCode.BadRequest, pdfAsDocx.StatusCode);
		Assert.Equal("The file is not a Word (.docx) document.", await ErrorAsync(pdfAsDocx));
	}

	[Fact]
	public async Task Old_doc_files_get_a_save_as_docx_message()
	{
		byte[] ole = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0];
		using var response = await PostAsync(ole, DocumentImport.DocxContentType);
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Contains("save it as .docx", await ErrorAsync(response));
	}

	[Fact]
	public async Task Empty_uploads_are_rejected()
	{
		using var response = await PostAsync([], DocumentImport.PdfContentType);
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task Uploads_over_the_limit_are_rejected()
	{
		var big = new byte[DocumentImport.MaxUploadBytes + 1];
		"%PDF-1.7"u8.CopyTo(big);
		using var response = await PostAsync(big, DocumentImport.PdfContentType);
		Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
		Assert.Contains("larger than 25 MB", await ErrorAsync(response));
	}

	[Fact]
	public async Task Damaged_files_return_a_readable_error()
	{
		var docx = SampleDocx();
		var damaged = docx[..(docx.Length / 2)];
		using var response = await PostAsync(damaged, DocumentImport.DocxContentType);
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.False(string.IsNullOrWhiteSpace(await ErrorAsync(response)));
	}

	[Fact]
	public async Task Get_is_not_allowed()
	{
		using var response = await factory.CreateClient().GetAsync("/api/import");
		Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
	}

	// ---- Successful imports -----------------------------------------------------------------------------------------

	[Fact]
	public async Task A_word_document_imports_with_fields_model_and_assets()
	{
		using var response = await PostAsync(SampleDocx(), DocumentImport.DocxContentType + "; charset=binary");
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);

		var result = (await response.Content.ReadFromJsonAsync<DocumentImportResult>(Web))!;
		Assert.Equal("docx", result.Format);
		Assert.Contains("<h1>Policy Summary</h1>", result.Html);
		Assert.Equal(["policy.number", "Insured_Name"], result.Fields);
		var model = JsonSerializer.SerializeToElement(result.Model);
		Assert.Equal("«number»", model.GetProperty("policy").GetProperty("number").GetString());
		Assert.Equal("«Insured_Name»", model.GetProperty("Insured_Name").GetString());

		// The image asset the HTML points at is served by the designer.
		var src = Html.Parse(result.Html).QuerySelector("img")!.GetAttribute("src")!;
		using var asset = await factory.CreateClient().GetAsync(src);
		Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
		Assert.Equal("image/png", asset.Content.Headers.ContentType!.MediaType);
		Assert.Equal(Html.Png, await asset.Content.ReadAsByteArrayAsync());
	}

	[Fact]
	public async Task A_pdf_imports_as_form_pages()
	{
		using var response = await PostAsync(SamplePdf(), DocumentImport.PdfContentType);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);

		var result = (await response.Content.ReadFromJsonAsync<DocumentImportResult>(Web))!;
		Assert.Equal("pdf", result.Format);
		Assert.Equal(1, result.Pages);
		Assert.StartsWith("<section class=\"form-page\">", result.Html);
		Assert.Equal(2, result.Counts["texts"]);
		Assert.Equal(1, result.Counts["shapes"]);
		Assert.Null(result.Model);
	}

	// ---- PDF page ranges --------------------------------------------------------------------------------------------

	private static byte[] LongPdf() => Pdf.Pages(PdfImporter.MaxPages + 5, (page, fonts, n) => page.AddText($"Sheet {n}", 12, Pdf.At(72, 720), fonts.Helvetica));

	[Fact]
	public async Task A_long_pdf_without_pages_gets_its_page_count_back()
	{
		using var response = await PostAsync(LongPdf(), DocumentImport.PdfContentType);
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		var json = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal(PdfImporter.MaxPages + 5, json.GetProperty("pageCount").GetInt32());
		Assert.Equal(PdfImporter.MaxPages, json.GetProperty("maxPages").GetInt32());
		Assert.Contains("Choose a page range", json.GetProperty("error").GetString());
	}

	[Theory]
	[InlineData("1-100", 100)]
	[InlineData("101-105", 5)]
	[InlineData("42", 1)]
	public async Task A_long_pdf_imports_the_requested_pages(string pages, int expected)
	{
		var content = new ByteArrayContent(LongPdf());
		content.Headers.ContentType = MediaTypeHeaderValue.Parse(DocumentImport.PdfContentType);
		using var response = await factory.CreateClient().PostAsync("/api/import?pages=" + pages, content);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var result = (await response.Content.ReadFromJsonAsync<DocumentImportResult>(Web))!;
		Assert.Equal(expected, result.Pages);
	}

	[Theory]
	[InlineData("abc", "pages must be")]
	[InlineData("1-2-3", "pages must be")]
	[InlineData("200-210", "not in the PDF")]
	[InlineData("1-105", "At most")]
	public async Task Bad_page_ranges_are_rejected(string pages, string message)
	{
		var content = new ByteArrayContent(LongPdf());
		content.Headers.ContentType = MediaTypeHeaderValue.Parse(DocumentImport.PdfContentType);
		using var response = await factory.CreateClient().PostAsync("/api/import?pages=" + pages, content);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Contains(message, await ErrorAsync(response));
	}

	// ---- Placeholder options ------------------------------------------------------------------------------------------

	private static byte[] PlaceholderDocx() => WithBody(P("{{ insured }} \u00ABagent\u00BB [Producer Code] {{ premium | currency }}"));

	private async Task<DocumentImportResult> ImportWordAsync(byte[] docx, string query)
	{
		var content = new ByteArrayContent(docx);
		content.Headers.ContentType = MediaTypeHeaderValue.Parse(DocumentImport.DocxContentType);
		using var response = await factory.CreateClient().PostAsync("/api/import" + query, content);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		return (await response.Content.ReadFromJsonAsync<DocumentImportResult>(Web))!;
	}

	[Theory]
	[InlineData("", new[] { "insured", "agent", "premium" })]
	[InlineData("?placeholders=all", new[] { "insured", "agent", "Producer_Code", "premium" })]
	[InlineData("?placeholders=brackets", new[] { "Producer_Code" })]
	[InlineData("?placeholders=braces", new[] { "insured", "premium" })]
	[InlineData("?placeholders=none", new string[0])]
	public async Task The_placeholders_option_selects_what_converts(string query, string[] expected)
	{
		var result = await ImportWordAsync(PlaceholderDocx(), query);
		Assert.Equal(expected, result.Fields);
	}

	[Fact]
	public async Task An_invalid_placeholders_option_is_rejected()
	{
		var content = new ByteArrayContent(PlaceholderDocx());
		content.Headers.ContentType = MediaTypeHeaderValue.Parse(DocumentImport.DocxContentType);
		using var response = await factory.CreateClient().PostAsync("/api/import?placeholders=curly", content);
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Contains("placeholders", await ErrorAsync(response));
	}

	[Fact]
	public async Task Placeholder_fields_render_with_their_format_and_message_data()
	{
		var result = factory.Services.GetRequiredService<DocxImporter>().Import(PlaceholderDocx());
		var composer = factory.Services.GetRequiredService<DocumentComposer>();

		var preview = await composer.ComposeAsync(result.Html, result.Css, JsonSerializer.SerializeToElement(result.Model));
		Assert.Contains("\u00ABinsured\u00BB \u00ABagent\u00BB [Producer Code] $1,234.50", Html.Parse(preview.Html!).Body!.TextContent);

		var data = JsonSerializer.SerializeToElement(new { insured = "Acme Farms", agent = "J. Smith", premium = 987.6 });
		var merged = await composer.ComposeAsync(result.Html, result.Css, data);
		Assert.Contains("Acme Farms J. Smith [Producer Code] $987.60", Html.Parse(merged.Html!).Body!.TextContent);
	}

	// ---- Imported templates render --------------------------------------------------------------------------------

	[Fact]
	public async Task An_imported_word_template_merges_message_data()
	{
		var result = factory.Services.GetRequiredService<DocxImporter>().Import(SampleDocx());
		var composer = factory.Services.GetRequiredService<DocumentComposer>();
		var data = JsonSerializer.SerializeToElement(new
		{
			policy = new { number = "CPP1234567" },
			Insured_Name = "Acme <Farms> & Co"
		});

		var composed = await composer.ComposeAsync(result.Html, null, data);

		Assert.Null(composed.Error);
		var dom = Html.Parse(composed.Html!);
		Assert.Contains("Policy CPP1234567 for Acme <Farms> & Co", dom.Body!.TextContent);
		// Data is encoded, and the document's own braces were never evaluated.
		Assert.DoesNotContain("<Farms>", composed.Html);
		Assert.Contains("Literal braces stay text: {{ 1 + 2 }}", dom.Body.TextContent);
		// The asset URL was inlined for the renderer, which blocks network requests.
		Assert.StartsWith("data:image/png;base64,", dom.QuerySelector("img")!.GetAttribute("src"));
	}

	[Fact]
	public async Task Continued_list_numbering_survives_the_render_sanitizer()
	{
		var docx = Create((main, body) =>
		{
			AddNumbering(main, DocumentFormat.OpenXml.Wordprocessing.NumberFormatValues.Decimal);
			body.Append(ListItem(1, 0, "One"), P("Between"), ListItem(1, 0, "Two"));
		});
		var result = factory.Services.GetRequiredService<DocxImporter>().Import(docx);

		var composed = await factory.Services.GetRequiredService<DocumentComposer>()
			.ComposeAsync(result.Html, null, JsonSerializer.SerializeToElement(new { }));

		Assert.Equal("2", Html.Parse(composed.Html!).QuerySelectorAll("ol")[1].GetAttribute("start"));
	}

	[Fact]
	public async Task An_imported_word_template_previews_with_its_own_sample_model()
	{
		var result = factory.Services.GetRequiredService<DocxImporter>().Import(SampleDocx());
		var composer = factory.Services.GetRequiredService<DocumentComposer>();

		var composed = await composer.ComposeAsync(result.Html, null, JsonSerializer.SerializeToElement(result.Model));

		Assert.Null(composed.Error);
		Assert.Contains("Policy «number» for «Insured_Name»", Html.Parse(composed.Html!).Body!.TextContent);
	}

	[Fact]
	public async Task Imported_documents_render_to_pdf_with_their_text()
	{
		var renderer = factory.Services.GetRequiredService<PdfRenderer>();
		var composer = factory.Services.GetRequiredService<DocumentComposer>();
		var data = JsonSerializer.SerializeToElement(new { policy = new { number = "CPP7654321" }, Insured_Name = "Acme Farms" });

		var word = factory.Services.GetRequiredService<DocxImporter>().Import(SampleDocx());
		var wordHtml = (await composer.ComposeAsync(word.Html, null, data)).Html!;
		var wordText = Words(PdfText.Extract(await renderer.RenderAsync(wordHtml)));
		Assert.Contains("Policy CPP7654321 for Acme Farms", wordText);

		var pdf = factory.Services.GetRequiredService<PdfImporter>().Import(SamplePdf());
		var pdfHtml = (await composer.ComposeAsync(pdf.Html, null, data)).Html!;
		var pdfText = Words(PdfText.Extract(await renderer.RenderAsync(pdfHtml)));
		Assert.Contains("Commercial Property Declarations", pdfText);
		Assert.Contains("Named Insured: Acme Farms", pdfText);
	}

	// PdfText lays words out by their gaps; compare with single spaces.
	private static string Words(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

	[Fact]
	public async Task Word_headers_and_footers_print_on_every_page_with_page_numbers_and_word_margins()
	{
		var docx = Create((main, body) =>
		{
			var header = AddHeader(main, P("Renewal Notice Header"));
			var footer = AddFooter(main, PageXOfY());
			body.Append(
				P("First page body."),
				P(PageBreak()),
				P("Second page body."),
				Section(header, footer, left: 1440, right: 1440));
		});
		var result = factory.Services.GetRequiredService<DocxImporter>().Import(docx);
		var composer = factory.Services.GetRequiredService<DocumentComposer>();
		var html = (await composer.ComposeAsync(result.Html, result.Css, JsonSerializer.SerializeToElement(new { }))).Html!;

		var pdf = await factory.Services.GetRequiredService<PdfRenderer>().RenderAsync(html);

		using var document = UglyToad.PdfPig.PdfDocument.Open(pdf);
		Assert.Equal(2, document.NumberOfPages);
		foreach (var page in document.GetPages())
		{
			var text = Words(string.Join(' ', page.GetWords().Select(w => w.Text)));
			Assert.Contains("Renewal Notice Header", text);
			Assert.Contains($"Page {page.Number} of 2", text);
			// Word's footer replaces the MOE footer.
			Assert.DoesNotContain("Mutual Of Enumclaw", text);
			// Body text starts at Word's 1in left margin.
			var body = page.GetWords().First(w => w.Text == (page.Number == 1 ? "First" : "Second"));
			Assert.InRange(body.BoundingBox.Left, 70, 75);
		}
	}
}
