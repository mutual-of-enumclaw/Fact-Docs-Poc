using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using FaCT.DocDesigner.POC.Import;
using FaCT.DocDesigner.POC.Rendering;
using Microsoft.Extensions.DependencyInjection;
using UglyToad.PdfPig;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace FaCT.DocDesigner.POC.Tests.Import;

/// <summary>One document of a proof run. Visual is null for Word originals (nothing to compare pages with).</summary>
public sealed record ProofOutcome(
	string File, string Format, string Status, int Pages, double? Recall, double? Precision, double? Visual,
	IReadOnlyList<double> PageVisuals, int OriginalWords, int RenderedWords, string Folder, string Notes, string Missing,
	bool SentToReview)
{
	/// <summary>The case's score: the weaker of word recall and (for PDFs) visual likeness.</summary>
	public double? Score => Recall is null ? null : Visual is null ? Recall : Math.Min(Recall.Value, Visual.Value);
}

public sealed record ProofRun(IReadOnlyList<ProofOutcome> Outcomes, IReadOnlyList<string> Crashes);

/// <summary>
/// Import proof engine: each document is imported, merged with its sample model, rendered by the designer's renderer,
/// and compared with the original by words (recall / precision) and, for PDFs, by page images (ink overlap with a small
/// position tolerance). Per document: Rendered.pdf, Composed.html, Template.html, Report.txt and page overlays; for the
/// run summary.csv and index.html. Documents scoring below <see cref="ReviewScore"/> are written as review cases
/// (Original.pdf, Html.pdf, Diff.txt, Template.html, HtmlSnapshot.txt, Overlay-p{n}.png) for review.html?source=import.
/// </summary>
public static partial class ImportProof
{
	public const double ReviewScore = 0.9;
	public const int MaxOverlays = 5;

	public static async Task<ProofRun> RunAsync(IServiceProvider services, IEnumerable<string> files, string outRoot, string reviewRoot, Action<string>? log = null)
	{
		Directory.CreateDirectory(outRoot);
		// Review cases are this run's low scorers; decisions are kept separately (import-review.json).
		if (Directory.Exists(reviewRoot)) Directory.Delete(reviewRoot, recursive: true);
		Directory.CreateDirectory(reviewRoot);

		var pdfImporter = services.GetRequiredService<PdfImporter>();
		var docxImporter = services.GetRequiredService<DocxImporter>();
		var composer = services.GetRequiredService<DocumentComposer>();
		var renderer = services.GetRequiredService<PdfRenderer>();

		var outcomes = new List<ProofOutcome>();
		var crashes = new List<string>();
		foreach (var file in files)
		{
			var caseName = CaseName(file);
			var folder = Path.Combine(outRoot, caseName);
			Directory.CreateDirectory(folder);
			var bytes = await File.ReadAllBytesAsync(file);
			var kind = DocumentImport.Detect(bytes);
			var format = kind == DocumentKind.Pdf ? "pdf" : kind == DocumentKind.Docx ? "docx" : "?";
			try
			{
				if (kind == DocumentKind.Unknown) throw new DocumentImportException("Not a PDF or .docx file.");
				DocumentImportResult result;
				var pageLimit = int.MaxValue;
				try
				{
					result = kind == DocumentKind.Pdf ? pdfImporter.Import(bytes) : docxImporter.Import(bytes);
				}
				catch (PdfTooLongException)
				{
					// As a user would: import the first pages.
					result = pdfImporter.Import(bytes, new PdfImportOptions(1, PdfImporter.MaxPages));
					pageLimit = PdfImporter.MaxPages;
				}
				await File.WriteAllTextAsync(Path.Combine(folder, "Template.html"), result.Html);

				var data = result.Model is null ? JsonSerializer.SerializeToElement(new { }) : JsonSerializer.SerializeToElement(result.Model);
				var composed = await composer.ComposeAsync(result.Html, result.Css, data);
				if (composed.Error is not null) throw new InvalidOperationException("Liquid: " + composed.Error);
				await File.WriteAllTextAsync(Path.Combine(folder, "Composed.html"), composed.Html);

				var rendered = await renderer.RenderAsync(composed.Html!);
				await File.WriteAllBytesAsync(Path.Combine(folder, "Rendered.pdf"), rendered);

				var original = OriginalWords(kind, bytes, pageLimit);
				var renderedText = PdfWords(rendered);
				var render = Words(renderedText);
				var (recall, precision, missing) = Compare(original, render);

				// Pages side by side (PDF originals): the imported pages scale down to fit Letter like the importer does.
				var pageVisuals = new List<double>();
				var overlays = new List<byte[]>();
				if (kind == DocumentKind.Pdf)
				{
					var renderedPages = PageImages.Render(rendered);
					var originalPages = PageImages.Render(bytes, 1, renderedPages.Count,
						(w, h) => Math.Min(1, Math.Min(612 / w, 792 / h)));
					for (var i = 0; i < renderedPages.Count; i++)
					{
						var originalPage = i < originalPages.Count ? originalPages[i] : new InkMap(1, 1, [false]);
						pageVisuals.Add(PageImages.Similarity(originalPage, renderedPages[i]));
						if (overlays.Count < MaxOverlays) overlays.Add(PageImages.OverlayPng(originalPage, renderedPages[i]));
					}
					for (var i = 0; i < overlays.Count; i++)
					{
						await File.WriteAllBytesAsync(Path.Combine(folder, $"Overlay-p{i + 1}.png"), overlays[i]);
					}
				}
				double? visual = pageVisuals.Count == 0 ? null : pageVisuals.Average();

				var outcome = new ProofOutcome(file, format, "imported", result.Pages, recall, precision, visual, pageVisuals,
					original.Count, render.Count, folder, string.Join(" | ", result.Notes), string.Join(' ', missing.Take(40)), false);
				if (outcome.Score < ReviewScore)
				{
					await WriteReviewCaseAsync(Path.Combine(reviewRoot, caseName), caseName, kind == DocumentKind.Pdf ? bytes : null,
						rendered, result.Html, renderedText, outcome, missing, overlays);
					outcome = outcome with { SentToReview = true };
				}
				outcomes.Add(outcome);
				await File.WriteAllTextAsync(Path.Combine(folder, "Report.txt"), Report(outcome, result));
				log?.Invoke($"{Pct(recall),6} recall {Pct(precision),6} precision {Pct(visual),6} visual  {format}  {file}");
			}
			catch (DocumentImportException ex)
			{
				outcomes.Add(new ProofOutcome(file, format, "rejected: " + ex.Message, 0, null, null, null, [], 0, 0, folder, "", "", false));
				log?.Invoke($"rejected  {file}: {ex.Message}");
			}
			catch (Exception ex)
			{
				crashes.Add($"{file}: {ex.GetType().Name}: {ex.Message}");
				outcomes.Add(new ProofOutcome(file, format, "CRASH: " + ex.GetType().Name + ": " + ex.Message, 0, null, null, null, [], 0, 0, folder, "", "", false));
				log?.Invoke($"CRASH     {file}: {ex}");
			}
		}

		WriteSummary(outRoot, outcomes);
		return new ProofRun(outcomes, crashes);
	}

	/// <summary>Up to <paramref name="max"/> documents per folder, spread evenly over the sorted list (deterministic).</summary>
	public static IEnumerable<string> Sample(string folder, int max)
	{
		var all = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
			.Where(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
			.Where(f => !Path.GetFileName(f).StartsWith("~$", StringComparison.Ordinal))
			.Order(StringComparer.OrdinalIgnoreCase)
			.ToList();
		if (all.Count <= max) return all;
		return Enumerable.Range(0, max).Select(i => all[(int)((long)i * all.Count / max)]);
	}

	public static string CaseName(string file)
	{
		var name = Regex.Replace(Path.GetFileNameWithoutExtension(file), "[^A-Za-z0-9_-]+", "-").Trim('-');
		var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(file)))[..6].ToLowerInvariant();
		return (name.Length > 60 ? name[..60] : name) + "-" + hash;
	}

	// ---- Review cases -----------------------------------------------------------------------------------------------

	/// <summary>A case in the review page's format (see ReviewStore): the Diff.txt head carries score, pages and words.</summary>
	private static async Task WriteReviewCaseAsync(string folder, string caseName, byte[]? originalPdf, byte[] rendered,
		string template, string renderedText, ProofOutcome outcome, List<string> missing, List<byte[]> overlays)
	{
		Directory.CreateDirectory(folder);
		if (originalPdf is not null) await File.WriteAllBytesAsync(Path.Combine(folder, "Original.pdf"), originalPdf);
		await File.WriteAllBytesAsync(Path.Combine(folder, "Html.pdf"), rendered);
		await File.WriteAllTextAsync(Path.Combine(folder, "Template.html"), template);
		await File.WriteAllTextAsync(Path.Combine(folder, "HtmlSnapshot.txt"), renderedText);
		for (var i = 0; i < overlays.Count; i++)
		{
			await File.WriteAllBytesAsync(Path.Combine(folder, $"Overlay-p{i + 1}.png"), overlays[i]);
		}

		var originalPages = originalPdf is null ? outcome.Pages : PageImages.PageCount(originalPdf);
		var diff = new StringBuilder()
			.Append(caseName).Append(": DIFFERENT  score ").Append(Percent(outcome.Score)).Append('\n')
			.Append("pages golden ").Append(originalPages).Append("  html ").Append(PageImages.PageCount(rendered)).Append('\n')
			.Append("words golden ").Append(outcome.OriginalWords).Append("  html ").Append(outcome.RenderedWords)
			.Append("  missing ").Append(missing.Count).Append("  extra ").Append(Math.Max(0, outcome.RenderedWords - outcome.OriginalWords)).Append('\n')
			.Append('\n')
			.Append("file       ").Append(outcome.File).Append('\n')
			.Append("recall     ").Append(Percent(outcome.Recall)).Append('\n')
			.Append("precision  ").Append(Percent(outcome.Precision)).Append('\n')
			.Append("visual     ").Append(Percent(outcome.Visual)).Append('\n');
		for (var i = 0; i < outcome.PageVisuals.Count; i++)
		{
			diff.Append("  page ").Append(i + 1).Append("  ").Append(Percent(outcome.PageVisuals[i])).Append('\n');
		}
		diff.Append("notes      ").Append(outcome.Notes).Append('\n')
			.Append("missing    ").Append(string.Join(' ', missing.Take(200))).Append('\n');
		await File.WriteAllTextAsync(Path.Combine(folder, "Diff.txt"), diff.ToString());
	}

	private static string Percent(double? value) =>
		value is null ? "-" : (value.Value * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";

	// ---- Text comparison --------------------------------------------------------------------------------------------

	private static List<string> OriginalWords(DocumentKind kind, byte[] bytes, int pageLimit)
	{
		if (kind == DocumentKind.Pdf) return Words(PdfWords(bytes, pageLimit));
		using var document = WordprocessingDocument.Open(new MemoryStream(bytes, false), false);
		var body = document.MainDocumentPart!.Document.Body!;
		// Visible text only: w:t (not field codes, not deleted text), paragraph by paragraph.
		var text = string.Join(' ', body.Descendants<W.Paragraph>()
			.Where(p => !p.Ancestors<W.Paragraph>().Any())
			.Select(p => string.Concat(p.Descendants<W.Text>().Select(t => t.Text))));
		return Words(text);
	}

	private static string PdfWords(byte[] pdf, int pageLimit = int.MaxValue)
	{
		using var document = PdfDocument.Open(pdf);
		return string.Join(' ', document.GetPages().Take(pageLimit).SelectMany(p => p.GetWords()).Select(w => w.Text));
	}

	private static List<string> Words(string text) =>
		Token().Matches(text).Select(m => m.Value.ToLowerInvariant()).ToList();

	public static (double? Recall, double? Precision, List<string> Missing) Compare(List<string> original, List<string> rendered)
	{
		// The renderer adds the MOE footer to flowing documents; it isn't counted against precision.
		var footer = new[] { "mutual", "of", "enumclaw", "page" };
		var renderedCounts = rendered.GroupBy(w => w).ToDictionary(g => g.Key, g => g.Count());
		var originalCounts = original.GroupBy(w => w).ToDictionary(g => g.Key, g => g.Count());
		var matched = originalCounts.Sum(kv => Math.Min(kv.Value, renderedCounts.GetValueOrDefault(kv.Key)));
		var missing = originalCounts.Where(kv => kv.Value > renderedCounts.GetValueOrDefault(kv.Key)).Select(kv => kv.Key).ToList();
		var renderedTotal = rendered.Count(w => originalCounts.ContainsKey(w) || !footer.Contains(w) && !w.All(char.IsDigit));
		return (
			original.Count == 0 ? null : (double)matched / original.Count,
			renderedTotal == 0 ? null : Math.Min(1, (double)matched / renderedTotal),
			missing);
	}

	[GeneratedRegex(@"[\p{L}\p{N}]+")]
	private static partial Regex Token();

	// ---- Reports ----------------------------------------------------------------------------------------------------

	public static string Pct(double? value) => value is null ? "-" : value.Value.ToString("P0", CultureInfo.InvariantCulture);

	public static double Median(IEnumerable<double> values)
	{
		var sorted = values.Order().ToList();
		return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
	}

	private static string Report(ProofOutcome outcome, DocumentImportResult result) =>
		$"""
		file       {outcome.File}
		format     {outcome.Format}
		pages      {outcome.Pages}
		recall     {Pct(outcome.Recall)} of {outcome.OriginalWords} original words appear in the render
		precision  {Pct(outcome.Precision)} of {outcome.RenderedWords} rendered words come from the original
		visual     {Pct(outcome.Visual)} page likeness (ink within ~4pt of the original){(outcome.PageVisuals.Count > 0 ? " [" + string.Join(", ", outcome.PageVisuals.Select(v => Pct(v))) + "]" : "")}
		review     {(outcome.SentToReview ? "sent to review (score below " + Pct(ReviewScore) + ")" : "-")}
		counts     {string.Join(", ", result.Counts.Select(kv => kv.Value + " " + kv.Key))}
		fields     {string.Join(", ", result.Fields)}
		notes      {string.Join(Environment.NewLine + "           ", result.Notes)}
		missing    {outcome.Missing}
		""";

	private static void WriteSummary(string outRoot, List<ProofOutcome> outcomes)
	{
		var ordered = outcomes.OrderBy(o => o.Score ?? -1).ThenBy(o => o.File, StringComparer.OrdinalIgnoreCase).ToList();

		var csv = new StringBuilder("file,format,status,pages,score,recall,precision,visual,original_words,rendered_words,review,notes\n");
		foreach (var o in ordered)
		{
			csv.AppendLine(string.Join(',', Csv(o.File), o.Format, Csv(o.Status), o.Pages,
				F3(o.Score), F3(o.Recall), F3(o.Precision), F3(o.Visual),
				o.OriginalWords, o.RenderedWords, o.SentToReview ? "yes" : "", Csv(o.Notes)));
		}
		File.WriteAllText(Path.Combine(outRoot, "summary.csv"), csv.ToString());

		static string F3(double? v) => v?.ToString("F3", CultureInfo.InvariantCulture) ?? "";

		static string Link(string path, string text) =>
			$"<a href=\"{WebUtility.HtmlEncode(new Uri(path).AbsoluteUri)}\" target=\"_blank\">{WebUtility.HtmlEncode(text)}</a>";

		var reviewCount = outcomes.Count(o => o.SentToReview);
		var html = new StringBuilder("""
			<!DOCTYPE html><html><head><meta charset="utf-8"><title>Import proof</title><style>
			body{font:13px Segoe UI,Arial,sans-serif;margin:16px}table{border-collapse:collapse}
			td,th{border:1px solid #ccc;padding:4px 6px;text-align:left;vertical-align:top}th{background:#144835;color:#fff}
			.bad{background:#fde2e2}.mid{background:#fff4d6}.good{background:#e6f4ea}.n{color:#555;font-size:12px;max-width:520px}
			img.ov{height:120px;border:1px solid #ccc;margin-right:4px}
			</style></head><body><h1>PDF / Word import proof</h1>
			<p>Each document was imported, rendered by the designer's PDF renderer, and compared with the original.
			<b>Recall</b>: share of the original's words present in the render. <b>Precision</b>: share of the render's words
			that come from the original. <b>Visual</b> (PDFs): how much ink sits within ~4pt of where it is on the original
			page. Overlays: original red, render blue, both black.</p>
			""");
		html.Append("<p>").Append(reviewCount).Append(" document(s) scored below ").Append(Pct(ReviewScore))
			.Append(" and were sent to review: <a href=\"http://localhost:5199/review.html?source=import\" target=\"_blank\">review.html?source=import</a>.</p>")
			.Append("<table><tr><th>Document</th><th>Type</th><th>Pages</th><th>Score</th><th>Recall</th><th>Precision</th><th>Visual</th><th>Render</th><th>Overlays</th><th>Status / notes</th></tr>\n");
		foreach (var o in ordered)
		{
			var css = o.Score is null ? "bad" : o.Score >= 0.95 ? "good" : o.Score >= 0.8 ? "mid" : "bad";
			var render = o.Recall is null ? string.Empty
				: Link(Path.Combine(o.Folder, "Rendered.pdf"), "PDF") + " · " + Link(Path.Combine(o.Folder, "Composed.html"), "HTML") +
				  " · " + Link(Path.Combine(o.Folder, "Report.txt"), "report");
			var overlays = new StringBuilder();
			for (var i = 1; i <= MaxOverlays && File.Exists(Path.Combine(o.Folder, $"Overlay-p{i}.png")); i++)
			{
				overlays.Append("<img class=\"ov\" src=\"").Append(WebUtility.HtmlEncode(new Uri(Path.Combine(o.Folder, $"Overlay-p{i}.png")).AbsoluteUri)).Append("\">");
			}
			html.Append($"<tr class=\"{css}\"><td>{Link(o.File, Path.GetFileName(o.File))}</td><td>{o.Format}</td><td>{o.Pages}</td>" +
				$"<td>{Pct(o.Score)}{(o.SentToReview ? " ⚑" : "")}</td><td>{Pct(o.Recall)}</td><td>{Pct(o.Precision)}</td><td>{Pct(o.Visual)}</td><td>{render}</td><td>{overlays}</td>" +
				$"<td class=\"n\">{WebUtility.HtmlEncode(o.Status == "imported" ? o.Notes : o.Status)}</td></tr>\n");
		}
		html.Append("</table></body></html>");
		File.WriteAllText(Path.Combine(outRoot, "index.html"), html.ToString());
	}

	private static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
}
