using FaCT.DocDesigner.POC.Import;
using FaCT.DocDesigner.POC.Legacy;
using static FaCT.DocDesigner.POC.Tests.Import.Pdf;

namespace FaCT.DocDesigner.POC.Tests.Import;

/// <summary>Importing part of a PDF: page ranges and the page limit.</summary>
public sealed class PdfPageRangeTests : IDisposable
{
	private readonly TempAssets _assets = new();

	public void Dispose() => _assets.Dispose();

	private DocumentImportResult Import(byte[] pdf, PdfImportOptions? options = null) =>
		new PdfImporter(new LegacyFormImporter(_assets.Store)).Import(pdf, options);

	private static byte[] Numbered(int pages) =>
		Pages(pages, (page, fonts, n) => page.AddText($"Sheet {n}", 12, At(72, 720), fonts.Helvetica));

	private static string[] SheetTexts(DocumentImportResult result) =>
		Html.Parse(result.Html).QuerySelectorAll("section.form-page").Select(s => s.TextContent).ToArray();

	// ---- Parsing ----------------------------------------------------------------------------------------------------

	[Theory]
	[InlineData(null, null, null)]
	[InlineData("", null, null)]
	[InlineData("   ", null, null)]
	[InlineData("7", 7, 7)]
	[InlineData("5-40", 5, 40)]
	[InlineData(" 5 - 40 ", 5, 40)]
	[InlineData("0", 0, 0)]
	[InlineData("40-5", 40, 5)]
	public void Page_ranges_parse(string? value, int? first, int? last)
	{
		Assert.True(PdfImportOptions.TryParsePages(value, out var options));
		Assert.Equal(first, options.FirstPage);
		Assert.Equal(last, options.LastPage);
	}

	[Theory]
	[InlineData("a")]
	[InlineData("5-")]
	[InlineData("-5")]
	[InlineData("1-2-3")]
	[InlineData("1,2")]
	[InlineData("+3")]
	[InlineData("1.5")]
	[InlineData("99999999999")]
	public void Malformed_page_ranges_are_rejected(string value) =>
		Assert.False(PdfImportOptions.TryParsePages(value, out _));

	// ---- The limit --------------------------------------------------------------------------------------------------

	[Fact]
	public void A_pdf_over_the_limit_without_a_range_reports_its_page_count()
	{
		var error = Assert.Throws<PdfTooLongException>(() => Import(Numbered(PdfImporter.MaxPages + 1)));
		Assert.Equal(PdfImporter.MaxPages + 1, error.PageCount);
		Assert.Contains("Choose a page range", error.Message);
		Assert.IsAssignableFrom<DocumentImportException>(error);
	}

	[Fact]
	public void A_pdf_at_the_limit_imports_whole()
	{
		var result = Import(Numbered(PdfImporter.MaxPages));
		Assert.Equal(PdfImporter.MaxPages, result.Pages);
		Assert.DoesNotContain(result.Notes, n => n.StartsWith("Imported pages", StringComparison.Ordinal));
	}

	[Fact]
	public void The_first_hundred_pages_of_a_long_pdf_import()
	{
		var result = Import(Numbered(150), new PdfImportOptions(1, 100));

		Assert.Equal(100, result.Pages);
		Assert.Equal("Sheet 1", SheetTexts(result)[0]);
		Assert.Equal("Sheet 100", SheetTexts(result)[^1]);
		Assert.Contains("Imported pages 1-100 of 150.", result.Notes);
	}

	[Fact]
	public void A_range_in_the_middle_imports_just_those_pages()
	{
		var result = Import(Numbered(150), new PdfImportOptions(120, 125));
		Assert.Equal(["Sheet 120", "Sheet 121", "Sheet 122", "Sheet 123", "Sheet 124", "Sheet 125"], SheetTexts(result));
	}

	[Fact]
	public void A_single_page_can_be_imported()
	{
		var result = Import(Numbered(5), new PdfImportOptions(3, 3));
		Assert.Equal(["Sheet 3"], SheetTexts(result));
		Assert.Contains("Imported pages 3-3 of 5.", result.Notes);
	}

	[Fact]
	public void The_last_page_can_be_imported()
	{
		var result = Import(Numbered(101), new PdfImportOptions(101, 101));
		Assert.Equal(["Sheet 101"], SheetTexts(result));
	}

	[Theory]
	[InlineData(0, 5)]
	[InlineData(3, 2)]
	[InlineData(1, 11)]
	[InlineData(11, 11)]
	[InlineData(-1, 3)]
	public void Ranges_outside_the_pdf_are_rejected(int first, int last)
	{
		var error = Assert.Throws<DocumentImportException>(() => Import(Numbered(10), new PdfImportOptions(first, last)));
		Assert.Contains("10 page(s)", error.Message);
	}

	[Fact]
	public void A_range_longer_than_the_limit_is_rejected()
	{
		var error = Assert.Throws<DocumentImportException>(() => Import(Numbered(150), new PdfImportOptions(10, 110)));
		Assert.Contains("are 101", error.Message);
	}

	[Fact]
	public void Only_a_first_page_means_to_the_end()
	{
		var result = Import(Numbered(8), new PdfImportOptions(FirstPage: 6));
		Assert.Equal(["Sheet 6", "Sheet 7", "Sheet 8"], SheetTexts(result));
	}

	[Fact]
	public void Only_a_last_page_means_from_the_start()
	{
		var result = Import(Numbered(8), new PdfImportOptions(LastPage: 2));
		Assert.Equal(["Sheet 1", "Sheet 2"], SheetTexts(result));
	}

	[Fact]
	public void Notes_about_pages_use_the_pdfs_page_numbers()
	{
		// Page 4 is only an image: reported as page 4, not as the 2nd imported page.
		var pdf = Pages(5, (page, fonts, n) =>
		{
			if (n == 4) page.AddPng(Html.Png, new UglyToad.PdfPig.Core.PdfRectangle(0, 0, 612, 792));
			else page.AddText($"Sheet {n}", 12, At(72, 720), fonts.Helvetica);
		});

		var result = Import(pdf, new PdfImportOptions(3, 5));

		Assert.Contains(result.Notes, n => n.Contains("Page(s) 4 have no text layer"));
	}

	[Fact]
	public void Form_fields_come_from_the_imported_pages_only()
	{
		// WithForm() has its fields on page 1; importing page 1 of a one-page form keeps them.
		var result = Import(WithForm(), new PdfImportOptions(1, 1));
		Assert.Equal(["PolicyNumber", "Agree"], result.Fields);
	}
}
