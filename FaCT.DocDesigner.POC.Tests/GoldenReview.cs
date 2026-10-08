using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FaCT.DocDesigner.POC.Tests;

/// <summary>
/// Pass rules for the golden snapshot tests. A case passes when its word score is at least <see cref="PassScore"/>
/// (GD_PASS_SCORE, default 0.90), unless a person decided otherwise on the designer's review page (/review.html):
/// an approval passes the case and a rejection fails it, as long as the render is the one they looked at
/// (same fingerprint). Decisions live in ghostdraft-review.json at the repo root.
/// </summary>
public static class GoldenReview
{
	public static double PassScore =>
		double.TryParse(Environment.GetEnvironmentVariable("GD_PASS_SCORE"), NumberStyles.Float, CultureInfo.InvariantCulture, out var s)
			? s : 0.90;

	private static string DecisionsPath => Environment.GetEnvironmentVariable("GD_REVIEW_FILE") is { Length: > 0 } p
		? p : Path.Combine(GoldenCases.RepoRoot, "ghostdraft-review.json");

	public sealed record Decision(string Status, string Fingerprint, string Reviewer, string Note);

	public static Decision? Get(string caseName)
	{
		if (!File.Exists(DecisionsPath)) return null;
		using var doc = JsonDocument.Parse(File.ReadAllText(DecisionsPath));
		if (!doc.RootElement.TryGetProperty(caseName, out var d)) return null;
		string Str(string name) => d.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";
		return new Decision(Str("status"), Str("fingerprint"), Str("reviewer"), Str("note"));
	}

	/// <summary>Must match GhostDraftReviewStore.FingerprintAsync: SHA-256 of Template.html (random editor ids
	/// renumbered in document order) + "\n" + HtmlSnapshot.txt.</summary>
	public static string Fingerprint(string templateHtml, string renderedText) =>
		Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(StableIds(templateHtml) + "\n" + renderedText)));

	private static string StableIds(string template)
	{
		var ids = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (Match m in Regex.Matches(template, "\\bid=\"(i[a-z0-9]{5})\""))
			ids.TryAdd(m.Groups[1].Value, "gd" + ids.Count);
		return Regex.Replace(template, "\\bi[a-z0-9]{5}\\b", m => ids.TryGetValue(m.Value, out var id) ? id : m.Value);
	}
}
