using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace FaCT.DocDesigner.POC.Review;

[JsonConverter(typeof(JsonStringEnumConverter<ReviewStatus>))]
public enum ReviewStatus { Approved, Rejected }

/// <summary>A reviewer's decision on one case, pinned to the render it was made on (Fingerprint).</summary>
public sealed record ReviewDecision(ReviewStatus Status, string Fingerprint, double Score, string Reviewer, string Note, DateTimeOffset ReviewedUtc);

public sealed record ReviewRequest(ReviewStatus Status, string? Reviewer, string? Note);

/// <summary>One case as the review page lists it. Stale = decided on a render that has since changed.
/// Overlays = page images of original vs render (import review).</summary>
public sealed record ReviewCase(
	string Case, double Score, bool Match, int GoldenPages, int HtmlPages, int Missing, int Extra,
	bool HasGoldenPdf, bool HasHtmlPdf, ReviewDecision? Decision, bool Stale, int Overlays = 0);

/// <summary>
/// Where a review's cases live: {ResultsRoot}/{case}/ holds Html.pdf, Diff.txt, Template.html, HtmlSnapshot.txt (and
/// optional Overlay-p{n}.png); <see cref="OriginalPdf"/> finds the reference PDF; decisions go to DecisionsPath.
/// </summary>
public sealed record ReviewSource(string Name, string ResultsRoot, Func<string, string> OriginalPdf, string DecisionsPath);

/// <summary>
/// The review page's case sources: "ghostdraft" (GhostDraft -> HTML golden cases, the default) and "import"
/// (PDF / Word imports from the import proof run that scored low).
/// </summary>
public sealed class ReviewStores
{
	public ReviewStores(IConfiguration configuration, IWebHostEnvironment env)
	{
		var repo = Path.GetFullPath(Path.Combine(env.ContentRootPath, ".."));

		var gdRoot = configuration["GhostDraftReview:OutputRoot"] is { Length: > 0 } r ? r : Path.Combine(repo, "output", "ghostdraft");
		var serverXml = Path.Combine(gdRoot, "serverxml");
		GhostDraft = new ReviewStore(new ReviewSource(
			"ghostdraft",
			Path.Combine(gdRoot, "html-snapshots"),
			name => Path.Combine(serverXml, name, "GhostDraft.pdf"),
			configuration["GhostDraftReview:DecisionsPath"] is { Length: > 0 } d ? d : Path.Combine(repo, "ghostdraft-review.json")));
		Conversions = new GhostDraftConversions(
			configuration["GhostDraftReview:FormsCsv"] is { Length: > 0 } csv ? csv : Path.Combine(repo, "ghostdraft-forms.csv"),
			configuration["GhostDraftReview:TemplatesRoot"] is { Length: > 0 } t ? t : Path.Combine(gdRoot, "batch"));

		var importRoot = configuration["ImportReview:OutputRoot"] is { Length: > 0 } ir ? ir : Path.Combine(repo, "output", "import-proof", "review");
		Import = new ReviewStore(new ReviewSource(
			"import",
			importRoot,
			name => Path.Combine(importRoot, name, "Original.pdf"),
			configuration["ImportReview:DecisionsPath"] is { Length: > 0 } id ? id : Path.Combine(repo, "import-review.json")));
	}

	public ReviewStore GhostDraft { get; }
	public ReviewStore Import { get; }

	/// <summary>The designer conversions of the GhostDraft cases (Add to Designer Library).</summary>
	public GhostDraftConversions Conversions { get; }

	/// <summary>The store for a ?source= value (none = ghostdraft), or null for an unknown source.</summary>
	public ReviewStore? Get(string? source) => source switch
	{
		null or "" or "ghostdraft" => GhostDraft,
		"import" => Import,
		_ => null
	};
}

/// <summary>
/// Human review of converted documents beside their reference PDF. For GhostDraft: the golden-snapshot test output
/// (output/ghostdraft/html-snapshots/{case}) and GhostDraft PDFs (output/ghostdraft/serverxml/{case}/GhostDraft.pdf),
/// decisions in ghostdraft-review.json, which the golden-snapshot tests honour (approval passes, rejection fails, while
/// the render is unchanged). For imports: the import proof's low scorers, decisions in import-review.json.
/// </summary>
public sealed partial class ReviewStore(ReviewSource source)
{
	private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
	private readonly SemaphoreSlim _lock = new(1, 1);
	private readonly string _results = source.ResultsRoot;

	public string Name => source.Name;

	/// <summary>Whether a case of that exact name is in the results folder.</summary>
	public bool HasCase(string caseName) => CaseNames().Any(n => string.Equals(n, caseName, StringComparison.Ordinal));

	public async Task<IReadOnlyList<ReviewCase>> ListAsync()
	{
		var decisions = await LoadAsync();
		var list = new List<ReviewCase>();
		foreach (var name in CaseNames())
		{
			var d = await ReadDiffAsync(name);
			decisions.TryGetValue(name, out var decision);
			var fingerprint = await FingerprintAsync(name);
			list.Add(new ReviewCase(
				name,
				d.Score,
				d.Match,
				d.GoldenPages,
				d.HtmlPages,
				d.Missing,
				d.Extra,
				File.Exists(source.OriginalPdf(name)),
				File.Exists(Path.Combine(_results, name, "Html.pdf")),
				decision,
				decision is not null && decision.Fingerprint != fingerprint,
				OverlayCount(name)));
		}
		return list.OrderBy(c => c.Score).ThenBy(c => c.Case, StringComparer.Ordinal).ToList();
	}

	/// <summary>Physical path and content type of a case artifact, or null when unknown. Case names come only from the
	/// results folder listing and artifacts from a fixed set, so request values never reach the file system unchecked.</summary>
	public (string Path, string ContentType)? Artifact(string caseName, string artifact)
	{
		var name = CaseNames().FirstOrDefault(n => string.Equals(n, caseName, StringComparison.Ordinal));
		if (name is null) return null;
		(string, string)? found = artifact switch
		{
			"golden.pdf" => (source.OriginalPdf(name), "application/pdf"),
			"html.pdf" => (Path.Combine(_results, name, "Html.pdf"), "application/pdf"),
			"diff" => (Path.Combine(_results, name, "Diff.txt"), "text/plain; charset=utf-8"),
			_ when OverlayArtifact().Match(artifact) is { Success: true } m =>
				(Path.Combine(_results, name, "Overlay-p" + int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) + ".png"), "image/png"),
			_ => null
		};
		return found is { } f && File.Exists(f.Item1) ? f : null;
	}

	/// <summary>Kept for callers that only need the path.</summary>
	public string? ArtifactPath(string caseName, string artifact) => Artifact(caseName, artifact)?.Path;

	public async Task<ReviewDecision?> DecideAsync(string caseName, ReviewRequest request)
	{
		var name = CaseNames().FirstOrDefault(n => string.Equals(n, caseName, StringComparison.Ordinal));
		if (name is null) return null;
		var fingerprint = await FingerprintAsync(name);
		var score = (await ReadDiffAsync(name)).Score;
		var decision = new ReviewDecision(request.Status, fingerprint, score,
			Clip(request.Reviewer, 100) is { Length: > 0 } who ? who : Environment.UserName,
			Clip(request.Note, 2000), DateTimeOffset.UtcNow);

		await _lock.WaitAsync();
		try
		{
			var all = await LoadAsync();
			all[name] = decision;
			var sorted = new SortedDictionary<string, ReviewDecision>(all, StringComparer.Ordinal);
			await File.WriteAllTextAsync(source.DecisionsPath, JsonSerializer.Serialize(sorted, Json));
		}
		finally
		{
			_lock.Release();
		}
		return decision;
	}

	public async Task<bool> ClearAsync(string caseName)
	{
		await _lock.WaitAsync();
		try
		{
			var all = await LoadAsync();
			if (!all.Remove(caseName)) return false;
			var sorted = new SortedDictionary<string, ReviewDecision>(all, StringComparer.Ordinal);
			await File.WriteAllTextAsync(source.DecisionsPath, JsonSerializer.Serialize(sorted, Json));
			return true;
		}
		finally
		{
			_lock.Release();
		}
	}

	/// <summary>What the reviewer looked at: the template and the text it rendered. Must match
	/// GhostDraftGoldenSnapshotTests' fingerprint so a decision is honoured only for the render it was made on.</summary>
	private async Task<string> FingerprintAsync(string name)
	{
		var template = Path.Combine(_results, name, "Template.html");
		var text = Path.Combine(_results, name, "HtmlSnapshot.txt");
		if (!File.Exists(template) || !File.Exists(text)) return "";
		var bytes = Encoding.UTF8.GetBytes(StableIds(await File.ReadAllTextAsync(template)) + "\n" + await File.ReadAllTextAsync(text));
		return Convert.ToHexString(SHA256.HashData(bytes));
	}

	/// <summary>The editor gives elements random ids (i + 5 chars) on every import; number them in document order so
	/// the same conversion always fingerprints the same.</summary>
	private static string StableIds(string template)
	{
		var ids = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (Match m in Regex.Matches(template, "\\bid=\"(i[a-z0-9]{5})\""))
			ids.TryAdd(m.Groups[1].Value, "gd" + ids.Count);
		return Regex.Replace(template, "\\bi[a-z0-9]{5}\\b", m => ids.TryGetValue(m.Value, out var id) ? id : m.Value);
	}

	private IEnumerable<string> CaseNames() =>
		Directory.Exists(_results)
			? Directory.EnumerateDirectories(_results).Select(Path.GetFileName).OfType<string>()
				.Where(n => File.Exists(Path.Combine(_results, n, "Diff.txt")))
			: [];

	private int OverlayCount(string name)
	{
		var count = 0;
		while (File.Exists(Path.Combine(_results, name, "Overlay-p" + (count + 1) + ".png"))) count++;
		return count;
	}

	private async Task<Dictionary<string, ReviewDecision>> LoadAsync()
	{
		if (!File.Exists(source.DecisionsPath)) return new(StringComparer.Ordinal);
		return JsonSerializer.Deserialize<Dictionary<string, ReviewDecision>>(await File.ReadAllTextAsync(source.DecisionsPath), Json)
			?? new(StringComparer.Ordinal);
	}

	private sealed record DiffHead(double Score, bool Match, int GoldenPages, int HtmlPages, int Missing, int Extra);

	/// <summary>The head of the case's Diff.txt ("{case}: MATCH|DIFFERENT  score 97.5%", "pages golden 2  html 2",
	/// "words ... missing 8  extra 14"). Per case, so a filtered test run doesn't lose the other cases' scores.</summary>
	private async Task<DiffHead> ReadDiffAsync(string name)
	{
		var path = Path.Combine(_results, name, "Diff.txt");
		var head = string.Join("\n", (await File.ReadAllLinesAsync(path)).Take(3));
		double Num(string pattern) =>
			Regex.Match(head, pattern) is { Success: true } m
				? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
		return new DiffHead(
			Math.Round(Num(@"score ([\d.]+)%") / 100, 4),
			head.Contains(": MATCH", StringComparison.Ordinal),
			(int)Num(@"pages\s+golden (\d+)"),
			(int)Num(@"pages\s+golden \d+\s+html (\d+)"),
			(int)Num(@"missing (\d+)"),
			(int)Num(@"extra (\d+)"));
	}

	private static string Clip(string? s, int max)
	{
		var t = (s ?? "").Trim();
		return t.Length > max ? t[..max] : t;
	}

	[GeneratedRegex(@"^overlay-([1-9]|1[0-9]|20)\.png$")]
	private static partial Regex OverlayArtifact();
}
