using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace FaCT.DocDesigner.POC.Tests;

/// <summary>
/// Golden snapshot tests for GhostDraft forms converted to designer (HTML/Liquid) templates.
///
/// For every fact-docgen FormRenderTests golden case (TestData/Snapshots/{case}/GoldenSnapshot.txt, rendered by
/// GhostDraft TST) whose GhostDraft template has been converted by tools/gd2designer.py:
///   1. import the converted template into the designer (headless) and take its HTML + CSS,
///   2. convert the case's Server XML (the same input GhostDraft got) to the designer's JSON message shape,
///   3. render the PDF with the designer's /api/render, extract its text,
///   4. compare the text with the GhostDraft golden (words, order-insensitive; see SnapshotComparison).
/// Artifacts per case go to output/ghostdraft/html-snapshots/{case}/ (HtmlSnapshot.txt, Html.pdf, Diff.txt,
/// Data.json, Template.html) plus summary.csv for the whole run.
///
/// Inputs: run fact-docgen FormRenderTests with SERVERXML_DUMP_DIR=&lt;fact-poc&gt;/output/ghostdraft/serverxml
/// (tst.runsettings) to capture the Server XML, and tools/gd2designer.py --forms-csv for the templates.
/// </summary>
[Collection("Designer UI")]
public sealed class GhostDraftGoldenSnapshotTests(DesignerFixture designer) : IClassFixture<DesignerFixture>
{
	private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

	public static TheoryData<string> Cases => GoldenCases.Names();

	[Theory]
	[MemberData(nameof(Cases))]
	public async Task Html_render_matches_GhostDraft_golden(string caseName)
	{
		var testCase = GoldenCases.Get(caseName);
		var outDir = Path.Combine(GoldenCases.ResultsRoot, caseName);
		Directory.CreateDirectory(outDir);

		using var template = JsonDocument.Parse(await File.ReadAllTextAsync(testCase.TemplatePath));
		var schema = JsonNode.Parse(template.RootElement.GetProperty("model").GetRawText());
		var data = ServerXmlData.Convert(XDocument.Load(testCase.ServerXmlPath).Root!, schema);
		await File.WriteAllTextAsync(Path.Combine(outDir, "Data.json"), data.ToJsonString(Indented));

		var (html, css) = await designer.ImportGhostDraftAsync(testCase.TemplatePath);
		var templateHtml = $"<style>{css}</style>\n{html}";
		await File.WriteAllTextAsync(Path.Combine(outDir, "Template.html"), templateHtml);

		var pdf = await designer.RenderPdfAsync(html, css, data);
		await File.WriteAllBytesAsync(Path.Combine(outDir, "Html.pdf"), pdf);

		var rendered = PdfText.Extract(pdf);
		await File.WriteAllTextAsync(Path.Combine(outDir, "HtmlSnapshot.txt"), rendered);

		var comparison = SnapshotComparison.Compare(await File.ReadAllTextAsync(testCase.GoldenPath), rendered);
		var report = comparison.Report(caseName) +
			$"\ngolden   {testCase.GoldenPath}\ntemplate {testCase.TemplatePath}\nserverxml {testCase.ServerXmlPath}\n";
		await File.WriteAllTextAsync(Path.Combine(outDir, "Diff.txt"), report);
		designer.Results.Add((caseName, comparison));

		// a person's decision on this exact render (review.html) overrides the score
		var decision = GoldenReview.Get(caseName);
		if (decision is not null && decision.Fingerprint == GoldenReview.Fingerprint(templateHtml, rendered))
		{
			Assert.True(decision.Status == "Approved",
				$"Rejected in review by {decision.Reviewer}: {decision.Note}\n\n{report}");
			return;
		}

		Assert.True(comparison.Score >= GoldenReview.PassScore,
			$"Score {comparison.Score:P1} is below {GoldenReview.PassScore:P0} and the render has not been approved on /review.html.\n\n{report}");
	}

	/// <summary>
	/// A Word column break (RTF \column) starts the next newspaper column: in EA 99 02 the second column starts at
	/// "b. Financial penalties", level with the first line of the first column (CSS balancing alone moved "a." over).
	/// </summary>
	[Fact]
	public async Task A_column_break_starts_the_next_column()
	{
		const string caseName = "EA9902_1113";
		if (!GoldenCases.Names().Any(row => (string)row[0] == caseName)) return;   // conversions not on this machine
		var testCase = GoldenCases.Get(caseName);
		using var template = JsonDocument.Parse(await File.ReadAllTextAsync(testCase.TemplatePath));
		var data = ServerXmlData.Convert(XDocument.Load(testCase.ServerXmlPath).Root!,
			JsonNode.Parse(template.RootElement.GetProperty("model").GetRawText()));
		var (html, css) = await designer.ImportGhostDraftAsync(testCase.TemplatePath);

		using var pdf = UglyToad.PdfPig.PdfDocument.Open(await designer.RenderPdfAsync(html, css, data));
		var words = pdf.GetPage(1).GetWords().ToList();
		var columnTop = words.First(w => w.Text == "Physical").BoundingBox;
		var financial = words.First(w => w.Text == "Financial").BoundingBox;
		var overdue = words.First(w => w.Text == "Overdue").BoundingBox;
		Assert.Equal(columnTop.Bottom, financial.Bottom, 2.0);
		Assert.True(financial.Left > columnTop.Right, "\"Financial\" should be in the right-hand column");
		Assert.True(overdue.Left < financial.Left && overdue.Bottom < columnTop.Bottom, "\"a. Overdue\" should stay at the foot of the left-hand column");
	}
}
