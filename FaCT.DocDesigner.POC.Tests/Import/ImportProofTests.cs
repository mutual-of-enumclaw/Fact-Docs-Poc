using Xunit.Abstractions;

namespace FaCT.DocDesigner.POC.Tests.Import;

/// <summary>
/// Proof run on real documents (opt-in): every .pdf/.docx under IMPORT_PROOF_DIR (folders separated by ';') goes through
/// <see cref="ImportProof"/>. Artifacts go to output/import-proof (or IMPORT_PROOF_OUT), low scorers to
/// output/import-proof/review for review.html?source=import. IMPORT_PROOF_MAX limits documents per folder (default 25,
/// spread evenly over the sorted file list). Without IMPORT_PROOF_DIR the test does nothing.
///
///   $env:IMPORT_PROOF_DIR = "C:\docs\forms;C:\docs\letters"; dotnet test --filter ImportProofTests
/// </summary>
public sealed class ImportProofTests(ImportAppFactory factory, ITestOutputHelper output) : IClassFixture<ImportAppFactory>
{
	[Fact]
	public async Task Import_real_documents_and_compare_them_with_the_originals()
	{
		var folders = (Environment.GetEnvironmentVariable("IMPORT_PROOF_DIR") ?? string.Empty)
			.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (folders.Length == 0)
		{
			output.WriteLine("IMPORT_PROOF_DIR is not set; nothing to prove.");
			return;
		}
		var max = int.TryParse(Environment.GetEnvironmentVariable("IMPORT_PROOF_MAX"), out var m) && m > 0 ? m : 25;
		var outRoot = Environment.GetEnvironmentVariable("IMPORT_PROOF_OUT") is { Length: > 0 } o
			? o
			: Path.Combine(GoldenCases.RepoRoot, "output", "import-proof");

		var files = folders.SelectMany(folder => ImportProof.Sample(folder, max)).ToList();
		Assert.NotEmpty(files);

		var run = await ImportProof.RunAsync(factory.Services, files, outRoot, Path.Combine(outRoot, "review"), output.WriteLine);

		var scored = run.Outcomes.Where(x => x.Score is not null).ToList();
		output.WriteLine($"\n{run.Outcomes.Count} documents, {scored.Count} imported, " +
			$"{run.Outcomes.Count(x => x.Status.StartsWith("rejected", StringComparison.Ordinal))} rejected, {run.Crashes.Count} crashed, " +
			$"{run.Outcomes.Count(x => x.SentToReview)} sent to review." +
			(scored.Count > 0 ? $" Median score {ImportProof.Pct(ImportProof.Median(scored.Select(x => x.Score!.Value)))}." : string.Empty) +
			$"\nReport: {Path.Combine(outRoot, "index.html")}");

		// Poor fidelity is reported (and sent to review), not failed; an importer or renderer crash is a bug.
		Assert.True(run.Crashes.Count == 0, "Crashes:\n" + string.Join('\n', run.Crashes));
	}
}
