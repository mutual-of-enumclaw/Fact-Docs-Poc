using System.Globalization;
using FaCT.DocDesigner.POC.Review;
using FaCT.DocDesigner.POC.Tests.Import;

namespace FaCT.DocDesigner.POC.Tests.Review;

/// <summary>The import proof engine end to end: scores, artifacts, and review cases for low scorers.</summary>
public sealed class ImportProofEngineTests(ImportAppFactory factory) : IClassFixture<ImportAppFactory>, IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("import-proof-engine-").FullName;

	public void Dispose()
	{
		try { Directory.Delete(_root, recursive: true); }
		catch (IOException) { /* best effort */ }
	}

	private string In(string name, byte[] bytes)
	{
		var folder = Path.Combine(_root, "in");
		Directory.CreateDirectory(folder);
		var path = Path.Combine(folder, name);
		File.WriteAllBytes(path, bytes);
		return path;
	}

	private string Out => Path.Combine(_root, "out");
	private string ReviewRoot => Path.Combine(Out, "review");

	private static byte[] GoodPdf() => Pdf.Letter((page, fonts) =>
	{
		page.AddText("DECLARATIONS", 16, Pdf.At(72, 720), fonts.HelveticaBold);
		for (var i = 0; i < 15; i++) page.AddText("Policy wording line " + i, 10, Pdf.At(72, 690 - i * 14), fonts.Helvetica);
	});

	// A big filled circle: curves are skipped by the importer, so the render is missing most of the page's ink.
	private static byte[] LossyPdf() => Pdf.RawPage(
		"BT /F1 12 Tf 72 720 Td (Logo page) Tj ET " +
		"0 0 0 rg 400 400 m 400 455.2 355.2 500 300 500 c 244.8 500 200 455.2 200 400 c " +
		"200 344.8 244.8 300 300 300 c 355.2 300 400 344.8 400 400 c f");

	private static byte[] Letter() => Docx.WithBody(Docx.P("Dear customer,"), Docx.P("Your policy renews soon."));

	[Fact]
	public async Task Each_document_is_scored_by_words_and_by_page_images()
	{
		var files = new[] { In("good.pdf", GoodPdf()), In("lossy.pdf", LossyPdf()), In("letter.docx", Letter()) };

		var run = await ImportProof.RunAsync(factory.Services, files, Out, ReviewRoot);

		Assert.Empty(run.Crashes);
		var good = run.Outcomes.Single(o => o.File.EndsWith("good.pdf"));
		var lossy = run.Outcomes.Single(o => o.File.EndsWith("lossy.pdf"));
		var letter = run.Outcomes.Single(o => o.File.EndsWith("letter.docx"));

		Assert.Equal(1.0, good.Recall);
		Assert.True(good.Visual >= 0.9, $"good visual {good.Visual:P1}");
		Assert.False(good.SentToReview);

		Assert.Equal(1.0, lossy.Recall);
		Assert.True(lossy.Visual < 0.5, $"lossy visual {lossy.Visual:P1}");
		Assert.Equal(lossy.Visual, lossy.Score);
		Assert.True(lossy.SentToReview);

		Assert.Null(letter.Visual);
		Assert.Empty(letter.PageVisuals);
		Assert.Equal(letter.Recall, letter.Score);
	}

	[Fact]
	public async Task Artifacts_and_summaries_are_written()
	{
		var files = new[] { In("good.pdf", GoodPdf()), In("letter.docx", Letter()) };

		var run = await ImportProof.RunAsync(factory.Services, files, Out, ReviewRoot);

		var good = run.Outcomes.Single(o => o.File.EndsWith("good.pdf"));
		foreach (var artifact in new[] { "Template.html", "Composed.html", "Rendered.pdf", "Report.txt", "Overlay-p1.png" })
		{
			Assert.True(File.Exists(Path.Combine(good.Folder, artifact)), artifact);
		}
		Assert.Contains("visual", await File.ReadAllTextAsync(Path.Combine(good.Folder, "Report.txt")));
		var letter = run.Outcomes.Single(o => o.File.EndsWith("letter.docx"));
		Assert.False(File.Exists(Path.Combine(letter.Folder, "Overlay-p1.png")));

		var csv = await File.ReadAllLinesAsync(Path.Combine(Out, "summary.csv"));
		Assert.Equal("file,format,status,pages,score,recall,precision,visual,original_words,rendered_words,review,notes", csv[0]);
		Assert.Equal(3, csv.Length);
		var index = await File.ReadAllTextAsync(Path.Combine(Out, "index.html"));
		Assert.Contains("review.html?source=import", index);
		Assert.Contains("Overlay-p1.png", index);
	}

	[Fact]
	public async Task Low_scorers_become_review_cases_the_review_page_can_read()
	{
		var lossyPath = In("lossy.pdf", LossyPdf());
		var run = await ImportProof.RunAsync(factory.Services, [lossyPath, In("good.pdf", GoodPdf())], Out, ReviewRoot);
		var lossy = run.Outcomes.Single(o => o.File == lossyPath);

		var caseName = ImportProof.CaseName(lossyPath);
		var folder = Path.Combine(ReviewRoot, caseName);
		foreach (var artifact in new[] { "Original.pdf", "Html.pdf", "Diff.txt", "Template.html", "HtmlSnapshot.txt", "Overlay-p1.png" })
		{
			Assert.True(File.Exists(Path.Combine(folder, artifact)), artifact);
		}
		Assert.Equal(LossyPdf(), await File.ReadAllBytesAsync(Path.Combine(folder, "Original.pdf")));
		Assert.Single(Directory.GetDirectories(ReviewRoot));

		var store = new ReviewStore(new ReviewSource("import", ReviewRoot, n => Path.Combine(ReviewRoot, n, "Original.pdf"), Path.Combine(_root, "decisions.json")));
		var listed = Assert.Single(await store.ListAsync());
		Assert.Equal(caseName, listed.Case);
		Assert.Equal(Math.Round(lossy.Score!.Value, 3), listed.Score, 3);
		Assert.False(listed.Match);
		Assert.Equal(1, listed.GoldenPages);
		Assert.Equal(1, listed.HtmlPages);
		Assert.True(listed.HasGoldenPdf);
		Assert.True(listed.HasHtmlPdf);
		Assert.Equal(1, listed.Overlays);

		var diff = await File.ReadAllTextAsync(Path.Combine(folder, "Diff.txt"));
		Assert.StartsWith(caseName + ": DIFFERENT  score ", diff);
		Assert.Contains("page 1  " + (lossy.PageVisuals[0] * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%", diff);
	}

	[Fact]
	public async Task Each_run_replaces_the_previous_runs_review_cases()
	{
		await ImportProof.RunAsync(factory.Services, [In("lossy.pdf", LossyPdf())], Out, ReviewRoot);
		Assert.Single(Directory.GetDirectories(ReviewRoot));

		await ImportProof.RunAsync(factory.Services, [In("good.pdf", GoodPdf())], Out, ReviewRoot);
		Assert.Empty(Directory.GetDirectories(ReviewRoot));
	}

	[Fact]
	public async Task Files_that_are_not_documents_are_rejected_not_crashed()
	{
		var run = await ImportProof.RunAsync(factory.Services, [In("notes.pdf", "plain text"u8.ToArray())], Out, ReviewRoot);
		Assert.Empty(run.Crashes);
		Assert.StartsWith("rejected", Assert.Single(run.Outcomes).Status);
	}

	[Fact]
	public void Sampling_spreads_evenly_and_skips_office_lock_files()
	{
		var folder = Path.Combine(_root, "sample");
		Directory.CreateDirectory(folder);
		for (var i = 0; i < 10; i++) File.WriteAllText(Path.Combine(folder, $"doc{i:D2}.pdf"), "x");
		File.WriteAllText(Path.Combine(folder, "~$doc.docx"), "lock");
		File.WriteAllText(Path.Combine(folder, "readme.txt"), "x");

		Assert.Equal(["doc00.pdf", "doc02.pdf", "doc05.pdf", "doc07.pdf"], ImportProof.Sample(folder, 4).Select(Path.GetFileName));
		Assert.Equal(10, ImportProof.Sample(folder, 50).Count());
	}

	[Fact]
	public void Case_names_are_safe_and_unique_per_path()
	{
		var a = ImportProof.CaseName(@"C:\docs\A form (v2).pdf");
		var b = ImportProof.CaseName(@"C:\other\A form (v2).pdf");
		Assert.Matches("^[A-Za-z0-9_-]+$", a);
		Assert.StartsWith("A-form-v2-", a);
		Assert.NotEqual(a, b);
	}

	[Theory]
	[InlineData(new[] { "a", "b" }, new[] { "a", "b" }, 1.0, 1.0)]
	[InlineData(new[] { "a", "b" }, new[] { "a" }, 0.5, 1.0)]
	[InlineData(new[] { "a" }, new[] { "a", "x" }, 1.0, 0.5)]
	[InlineData(new[] { "a" }, new[] { "a", "mutual", "of", "enumclaw", "page", "1" }, 1.0, 1.0)]
	public void Word_recall_and_precision(string[] original, string[] rendered, double recall, double precision)
	{
		var (r, p, _) = ImportProof.Compare([.. original], [.. rendered]);
		Assert.Equal(recall, r);
		Assert.Equal(precision, p);
	}
}
