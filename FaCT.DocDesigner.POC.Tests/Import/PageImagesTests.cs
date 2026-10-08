using System.Buffers.Binary;
using System.IO.Compression;
using FaCT.DocDesigner.POC.Import;
using FaCT.DocDesigner.POC.Legacy;
using static FaCT.DocDesigner.POC.Tests.Import.Pdf;

namespace FaCT.DocDesigner.POC.Tests.Import;

public sealed class PageImagesTests
{
	private static InkMap Map(int width, int height, params (int X, int Y)[] ink)
	{
		var cells = new bool[width * height];
		foreach (var (x, y) in ink) cells[y * width + x] = true;
		return new InkMap(width, height, cells);
	}

	private static InkMap Block(int width, int height, int left, int top, int w, int h) =>
		Map(width, height, Cells(left, top, w, h).ToArray());

	private static IEnumerable<(int X, int Y)> Cells(int left, int top, int w, int h) =>
		from y in Enumerable.Range(top, h) from x in Enumerable.Range(left, w) select (x, y);

	// ---- Ink ----------------------------------------------------------------------------------------------------------

	[Theory]
	[InlineData(0, 0, 0, 0, false)]        // transparent: paper
	[InlineData(0, 0, 0, 255, true)]       // opaque black
	[InlineData(200, 200, 200, 255, false)] // light grey shading
	[InlineData(120, 120, 120, 255, true)] // mid grey text
	[InlineData(0, 0, 0, 128, true)]       // half-covered black
	[InlineData(0, 0, 0, 40, false)]       // faint anti-aliasing
	[InlineData(0, 0, 255, 255, true)]     // pure red (dark luminance)
	[InlineData(0, 255, 255, 255, false)]  // yellow (bright)
	public void Ink_is_anything_clearly_darker_than_paper(byte b, byte g, byte r, byte a, bool ink) =>
		Assert.Equal(ink, PageImages.FromBgra([b, g, r, a], 1, 1).Ink[0]);

	[Fact]
	public void Out_of_bounds_reads_as_paper()
	{
		var map = Map(2, 2, (0, 0));
		Assert.True(map[0, 0]);
		Assert.False(map[-1, 0]);
		Assert.False(map[2, 0]);
		Assert.False(map[0, 5]);
	}

	[Fact]
	public void Dilation_grows_ink_by_the_radius_in_every_direction()
	{
		var grown = PageImages.Dilate(Map(7, 7, (3, 3)), 2);
		Assert.Equal(25, grown.Count);
		Assert.True(grown[1, 1]);
		Assert.True(grown[5, 5]);
		Assert.False(grown[0, 3]);
		var single = Map(1, 1);
		Assert.Same(single, PageImages.Dilate(single, 0));
	}

	[Fact]
	public void Resizing_down_keeps_every_mark()
	{
		var small = PageImages.Resize(Map(100, 100, (99, 99), (0, 0), (50, 51)), 0.1);
		Assert.Equal(10, small.Width);
		Assert.Equal(10, small.Height);
		Assert.True(small[9, 9]);
		Assert.True(small[0, 0]);
		Assert.True(small[5, 5]);
		Assert.Equal(3, small.Count);
	}

	// ---- Similarity -------------------------------------------------------------------------------------------------

	[Fact]
	public void Identical_pages_are_fully_alike()
	{
		var page = Block(50, 50, 10, 10, 20, 5);
		Assert.Equal(1.0, PageImages.Similarity(page, page));
	}

	[Fact]
	public void Two_blank_pages_are_alike_and_ink_against_blank_is_not()
	{
		Assert.Equal(1.0, PageImages.Similarity(Map(10, 10), Map(10, 10)));
		Assert.Equal(0.0, PageImages.Similarity(Block(10, 10, 1, 1, 3, 3), Map(10, 10)));
		Assert.Equal(0.0, PageImages.Similarity(Map(10, 10), Block(10, 10, 1, 1, 3, 3)));
	}

	[Fact]
	public void Small_shifts_are_tolerated()
	{
		var original = Block(60, 60, 10, 10, 20, 3);
		var shifted = Block(60, 60, 12, 11, 20, 3);
		Assert.Equal(1.0, PageImages.Similarity(original, shifted));
	}

	[Fact]
	public void Content_in_the_wrong_place_is_not_alike()
	{
		var original = Block(60, 60, 5, 5, 20, 3);
		var moved = Block(60, 60, 5, 40, 20, 3);
		Assert.Equal(0.0, PageImages.Similarity(original, moved));
	}

	[Fact]
	public void Missing_half_the_content_scores_about_two_thirds()
	{
		var original = Block(100, 100, 0, 0, 40, 10);
		var half = Block(100, 100, 0, 0, 20, 10);
		// recall ~0.55 (20 cols + 2 tolerance of 40), precision 1 => F1 ~0.71
		Assert.InRange(PageImages.Similarity(original, half), 0.65, 0.75);
	}

	[Fact]
	public void Extra_content_in_the_render_lowers_the_score()
	{
		var original = Block(100, 100, 0, 0, 40, 10);
		var extra = Map(100, 100, Cells(0, 0, 40, 10).Concat(Cells(0, 60, 40, 10)).ToArray());
		// recall 1, precision 0.5 => F1 0.67
		Assert.InRange(PageImages.Similarity(original, extra), 0.6, 0.72);
	}

	// ---- Real PDFs --------------------------------------------------------------------------------------------------

	[Fact]
	public void Pdf_pages_rasterize_at_half_scale()
	{
		var pdf = Pages(2, (page, fonts, n) => { if (n == 1) page.AddText("Ink here", 24, At(72, 700), fonts.Helvetica); });

		var maps = PageImages.Render(pdf);

		Assert.Equal(2, maps.Count);
		Assert.Equal(306, maps[0].Width);
		Assert.Equal(396, maps[0].Height);
		Assert.True(maps[0].Count > 50);
		Assert.Equal(0, maps[1].Count);
		Assert.Equal(2, PageImages.PageCount(pdf));
	}

	[Fact]
	public void A_page_range_rasterizes_only_those_pages()
	{
		var pdf = Pages(5, (page, fonts, n) => page.AddText(new string('X', n * 5), 20, At(72, 700), fonts.Helvetica));
		var maps = PageImages.Render(pdf, first: 2, count: 2);
		Assert.Equal(2, maps.Count);
		Assert.True(maps[1].Count > maps[0].Count);
	}

	[Fact]
	public async Task An_imported_and_rendered_pdf_looks_like_the_original()
	{
		var original = Letter((page, fonts) =>
		{
			page.AddText("COMMERCIAL PROPERTY DECLARATIONS", 16, At(72, 720), fonts.HelveticaBold);
			for (var i = 0; i < 20; i++) page.AddText("Line of policy wording number " + i, 10, At(72, 680 - i * 14), fonts.Times);
			page.DrawRectangle(At(60, 380), 490, 360, 1);
		});

		var rendered = await RenderImportAsync(original);

		var similarity = PageImages.Similarity(PageImages.Render(original)[0], PageImages.Render(rendered)[0]);
		Assert.True(similarity >= 0.9, $"similarity {similarity:P1}");
	}

	[Fact]
	public void A_different_page_does_not_look_like_the_original()
	{
		var original = Letter((page, fonts) =>
		{
			for (var i = 0; i < 20; i++) page.AddText("Upper half wording " + i, 10, At(72, 720 - i * 14), fonts.Helvetica);
		});
		var other = Letter((page, fonts) =>
		{
			for (var i = 0; i < 20; i++) page.AddText("Lower half wording " + i, 10, At(300, 300 - i * 14), fonts.Helvetica);
		});

		Assert.True(PageImages.Similarity(PageImages.Render(original)[0], PageImages.Render(other)[0]) < 0.2);
	}

	[Fact]
	public async Task Larger_pages_are_compared_at_the_scale_the_importer_used()
	{
		var original = A4((page, fonts) =>
		{
			for (var i = 0; i < 25; i++) page.AddText("A4 wording line " + i, 11, At(60, 780 - i * 22), fonts.Helvetica);
		});
		var rendered = PageImages.Render(await RenderImportAsync(original))[0];

		var scaled = PageImages.Render(original, pageScale: (w, h) => Math.Min(1, Math.Min(612 / w, 792 / h)))[0];
		var unscaled = PageImages.Render(original)[0];

		var withScale = PageImages.Similarity(scaled, rendered);
		Assert.True(withScale >= 0.85, $"scaled {withScale:P1}");
		Assert.True(withScale > PageImages.Similarity(unscaled, rendered) + 0.1);
	}

	private static async Task<byte[]> RenderImportAsync(byte[] pdf)
	{
		using var assets = new TempAssets();
		var result = new PdfImporter(new LegacyFormImporter(assets.Store)).Import(pdf);
		// Standalone page document: the brand stylesheet's .form-page rules and zero page margins.
		var css = await File.ReadAllTextAsync(Path.Combine(GoldenCases.RepoRoot, "FaCT.DocDesigner.POC", "wwwroot", "brand", "moe-document.css"));
		var html = $"<!DOCTYPE html><html><head><style>{css}</style></head><body>{result.Html}</body></html>";
		await using var renderer = new POC.Rendering.PdfRenderer(
			new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
			Microsoft.Extensions.Logging.Abstractions.NullLogger<POC.Rendering.PdfRenderer>.Instance);
		return await renderer.RenderAsync(html);
	}

	// ---- PNG --------------------------------------------------------------------------------------------------------

	[Fact]
	public void Crc32_matches_the_standard_check_value() =>
		Assert.Equal(0xCBF43926u, Png.Crc("123456789"u8));

	[Fact]
	public void Png_files_are_well_formed()
	{
		var png = Png.Encode([255, 0, 0, 0, 255, 0, 0, 0, 255, 10, 20, 30], 2, 2);

		Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
		var chunks = Chunks(png);
		Assert.Equal(["IHDR", "IDAT", "IEND"], chunks.Select(c => c.Type));
		Assert.All(chunks, c => Assert.True(c.CrcOk, c.Type + " CRC"));

		var header = chunks[0].Data;
		Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(header));
		Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4)));
		Assert.Equal(8, header[8]);
		Assert.Equal(2, header[9]);

		Assert.Equal(new byte[] { 0, 255, 0, 0, 0, 255, 0, 0, 0, 0, 255, 10, 20, 30 }, Inflate(chunks[1].Data));
	}

	[Fact]
	public void Png_rejects_the_wrong_amount_of_pixels() =>
		Assert.Throws<ArgumentException>(() => Png.Encode(new byte[5], 2, 2));

	[Fact]
	public void Overlays_colour_original_render_and_shared_ink()
	{
		var original = Map(3, 1, (0, 0), (1, 0));
		var rendered = Map(3, 1, (1, 0), (2, 0));

		var png = PageImages.OverlayPng(original, rendered);

		var pixels = Inflate(Chunks(png).Single(c => c.Type == "IDAT").Data);
		Assert.Equal(new byte[] { 0, 220, 40, 40, 30, 30, 30, 40, 90, 220 }, pixels);
	}

	private sealed record PngChunk(string Type, byte[] Data, bool CrcOk);

	private static List<PngChunk> Chunks(byte[] png)
	{
		var chunks = new List<PngChunk>();
		var at = 8;
		while (at < png.Length)
		{
			var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at));
			var typed = png.AsSpan(at + 4, 4 + length);
			var crc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at + 8 + length));
			chunks.Add(new PngChunk(System.Text.Encoding.ASCII.GetString(typed[..4]), typed[4..].ToArray(), Png.Crc(typed) == crc));
			at += 12 + length;
		}
		return chunks;
	}

	private static byte[] Inflate(byte[] zlib)
	{
		using var input = new ZLibStream(new MemoryStream(zlib), CompressionMode.Decompress);
		using var output = new MemoryStream();
		input.CopyTo(output);
		return output.ToArray();
	}
}
