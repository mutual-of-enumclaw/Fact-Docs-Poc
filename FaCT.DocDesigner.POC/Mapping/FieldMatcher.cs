using System.Text.RegularExpressions;

namespace FaCT.DocDesigner.POC.Mapping;

public sealed record MappingPath(string Path, string? Kind);

public sealed record MappingCandidate(string Path, double Score);

/// <summary>
/// Model paths that may fit one form field, best first; <see cref="Recommended"/> is safe to accept in one click.
/// <see cref="Label"/> is the field's printed label when one was sent.
/// </summary>
public sealed record MappingSuggestion(string Field, IReadOnlyList<MappingCandidate> Candidates, string? Recommended, string? Label = null);

/// <summary>
/// Suggests model paths for form fields by name: "POLICY_NO", "Policy #" and "PolicyNumber" all fit policy.number.
/// Names are split into words (camelCase, separators, digits), common form abbreviations are expanded, and the words
/// are compared with the path's words (all segments, and the last segment on its own), allowing near-misses.
/// A field whose name says nothing ("f1_01[0]") is matched by the label printed next to it on the form instead.
/// </summary>
public static partial class FieldMatcher
{
	public const double MinimumScore = 0.5;
	public const double RecommendScore = 0.75;
	public const double RecommendMargin = 0.1;

	private static readonly Dictionary<string, string> Abbreviations = new(StringComparer.Ordinal)
	{
		["no"] = "number", ["num"] = "number", ["nbr"] = "number", ["nmbr"] = "number",
		["dt"] = "date", ["eff"] = "effective", ["exp"] = "expiration", ["expiry"] = "expiration",
		["amt"] = "amount", ["prem"] = "premium", ["ins"] = "insured", ["insd"] = "insured",
		["addr"] = "address", ["postal"] = "zip", ["postcode"] = "zip", ["zipcode"] = "zip",
		["tel"] = "phone", ["ph"] = "phone", ["telephone"] = "phone", ["nm"] = "name", ["desc"] = "description",
		["qty"] = "quantity", ["pol"] = "policy", ["agt"] = "agent", ["co"] = "company", ["yr"] = "year",
		["veh"] = "vehicle", ["bldg"] = "building", ["loc"] = "location", ["lim"] = "limit", ["ded"] = "deductible",
		["cov"] = "coverage", ["dob"] = "birthdate", ["st"] = "state", ["fname"] = "firstname", ["lname"] = "lastname"
	};

	private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
	{
		"the", "of", "a", "an", "and", "or", "for", "to", "in", "on", "by", "at", "field", "text", "txt", "fld"
	};

	private static readonly HashSet<string> NumberWords = new(StringComparer.Ordinal)
	{
		"amount", "premium", "limit", "deductible", "total", "tax", "fee", "value", "quantity", "count"
	};

	/// <summary>The words a name is compared by: lower case, abbreviations expanded, plurals and numbering dropped.</summary>
	public static IReadOnlyList<string> Words(string? name)
	{
		if (string.IsNullOrWhiteSpace(name)) return [];
		var spaced = CamelBoundary().Replace(name.Replace("#", " number "), " ");
		var words = new List<string>();
		foreach (var raw in NonWord().Split(spaced.ToLowerInvariant()))
		{
			if (raw.Length == 0 || raw.All(char.IsAsciiDigit) || StopWords.Contains(raw)) continue;
			var word = Abbreviations.GetValueOrDefault(raw, raw);
			if (word.Length > 3 && word.EndsWith('s') && !word.EndsWith("ss", StringComparison.Ordinal) && !word.EndsWith("us", StringComparison.Ordinal))
			{
				word = word[..^1];
			}
			// "zip code" / "postal code" are one idea
			if (word == "code" && words.Count > 0 && words[^1] == "zip") continue;
			words.Add(word);
		}
		return words;
	}

	/// <summary>How well a form field name fits a model path, 0..1.</summary>
	public static double Score(string field, MappingPath path)
	{
		var fieldWords = Words(field);
		if (fieldWords.Count == 0) return 0;
		var segments = path.Path.Split('.');
		var pathWords = Words(string.Join(' ', segments));
		var leafWords = Words(segments[^1]);
		if (pathWords.Count == 0) return 0;

		double score;
		if (string.Concat(fieldWords) == string.Concat(pathWords)) score = 1;
		else score = Math.Max(SoftDice(fieldWords, pathWords), 0.95 * SoftDice(fieldWords, leafWords));

		// A date/amount name fitting a date/number property is more likely the intended one.
		if (score > 0 && score < 1 &&
			(fieldWords.Contains("date") && path.Kind == "date" || fieldWords.Any(NumberWords.Contains) && path.Kind == "number"))
		{
			score = Math.Min(1, score + 0.05);
		}
		return Math.Round(score, 3);
	}

	public static IReadOnlyList<MappingSuggestion> Suggest(IEnumerable<string> fields, IReadOnlyCollection<MappingPath> paths, int candidates = 3) =>
		Suggest(fields.Select(f => (f, (string?)null)), paths, candidates);

	/// <summary>
	/// Suggestions for fields with their printed labels (null when unknown). A path scores the better of its fit with the
	/// name and its fit with the label; a label match counts a little less (<see cref="LabelWeight"/>), labels being
	/// read from the page rather than chosen by the form's author.
	/// </summary>
	public static IReadOnlyList<MappingSuggestion> Suggest(IEnumerable<(string Field, string? Label)> fields, IReadOnlyCollection<MappingPath> paths, int candidates = 3)
	{
		var suggestions = new List<MappingSuggestion>();
		foreach (var (field, label) in fields.DistinctBy(f => f.Field, StringComparer.Ordinal))
		{
			var ranked = paths
				.Select(p => new MappingCandidate(p.Path, Math.Max(Score(field, p), string.IsNullOrWhiteSpace(label) ? 0 : Math.Round(LabelWeight * LabelScore(label, p), 3))))
				.Where(c => c.Score >= MinimumScore)
				.OrderByDescending(c => c.Score)
				.ThenBy(c => c.Path.Length)
				.ThenBy(c => c.Path, StringComparer.Ordinal)
				.Take(candidates)
				.ToList();
			var recommended = ranked.Count > 0 && ranked[0].Score >= RecommendScore &&
				(ranked.Count == 1 || ranked[0].Score - ranked[1].Score >= RecommendMargin)
				? ranked[0].Path
				: null;
			suggestions.Add(new MappingSuggestion(field, ranked, recommended, string.IsNullOrWhiteSpace(label) ? null : label));
		}
		return suggestions;
	}

	public const double LabelWeight = 0.95;
	private const int MaxLabelWords = 12;

	/// <summary>
	/// How well a printed label fits a model path, 0..1. A label is often longer than a property name ("Name of
	/// entity/individual"), so this asks how much of the path the label covers (the whole path, or its last segment a little
	/// less), discounted by how much of the label is left unexplained.
	/// </summary>
	public static double LabelScore(string label, MappingPath path)
	{
		var labelWords = Words(label).Take(MaxLabelWords).ToList();
		if (labelWords.Count == 0) return 0;
		var segments = path.Path.Split('.');
		var pathWords = Words(string.Join(' ', segments));
		var leafWords = Words(segments[^1]);
		if (pathWords.Count == 0) return 0;

		double Covered(IReadOnlyList<string> wanted) => wanted.Count == 0 ? 0 : wanted.Average(w => labelWords.Max(l => WordMatch(l, w)));
		var coverage = Math.Max(Covered(pathWords), 0.9 * Covered(leafWords));
		if (coverage == 0) return 0;
		var explained = labelWords.Average(l => pathWords.Max(w => WordMatch(l, w)));
		var score = coverage * (0.75 + 0.25 * explained);
		if (score < 1 &&
			(labelWords.Contains("date") && path.Kind == "date" || labelWords.Any(NumberWords.Contains) && path.Kind == "number"))
		{
			score = Math.Min(1, score + 0.05);
		}
		return Math.Round(score, 3);
	}

	// Symmetric soft Dice: every word on each side counts its best match on the other side.
	private static double SoftDice(IReadOnlyList<string> a, IReadOnlyList<string> b)
	{
		if (a.Count == 0 || b.Count == 0) return 0;
		var sumA = a.Sum(x => b.Max(y => WordMatch(x, y)));
		var sumB = b.Sum(y => a.Max(x => WordMatch(x, y)));
		return (sumA + sumB) / (a.Count + b.Count);
	}

	/// <summary>1 for the same word, 0.8 for a one-letter slip in a long word, 0.7 for a prefix ("insur" / "insured").</summary>
	public static double WordMatch(string a, string b)
	{
		if (a == b) return 1;
		var shorter = a.Length <= b.Length ? a : b;
		var longer = ReferenceEquals(shorter, a) ? b : a;
		if (shorter.Length >= 5 && longer.Length - shorter.Length <= 1 && Levenshtein(a, b) <= 1) return 0.8;
		if (shorter.Length >= 3 && longer.StartsWith(shorter, StringComparison.Ordinal)) return 0.7;
		return 0;
	}

	private static int Levenshtein(string a, string b)
	{
		var previous = new int[b.Length + 1];
		var current = new int[b.Length + 1];
		for (var j = 0; j <= b.Length; j++) previous[j] = j;
		for (var i = 1; i <= a.Length; i++)
		{
			current[0] = i;
			for (var j = 1; j <= b.Length; j++)
			{
				var cost = a[i - 1] == b[j - 1] ? 0 : 1;
				current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
			}
			(previous, current) = (current, previous);
		}
		return previous[b.Length];
	}

	[GeneratedRegex(@"(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])|(?<=[A-Za-z])(?=[0-9])|(?<=[0-9])(?=[A-Za-z])")]
	private static partial Regex CamelBoundary();

	[GeneratedRegex(@"[^a-z0-9]+")]
	private static partial Regex NonWord();
}
