using System.Text;
using System.Text.RegularExpressions;

namespace FaCT.DocDesigner.POC.Tests;

/// <summary>
/// Compares rendered text with a GhostDraft golden snapshot by CONTENT, not layout.
/// The golden text comes from Spire's layout extraction of the GhostDraft PDF (two-column ISO wording is
/// interleaved line by line, overprinted footers appear twice); the HTML render is extracted by PdfPig in
/// content order. So both sides are reduced to words (letters / digit runs, case-insensitive, punctuation
/// dropped, immediately repeated runs collapsed) and compared as multisets:
///   missing = in the golden but not rendered, extra = rendered but not in the golden.
/// </summary>
public sealed partial class SnapshotComparison
{
	public required int GoldenPages { get; init; }
	public required int RenderedPages { get; init; }
	public required int GoldenWords { get; init; }
	public required int RenderedWords { get; init; }
	public required IReadOnlyList<(string Line, IReadOnlyList<string> Words)> Missing { get; init; }
	public required IReadOnlyList<(string Line, IReadOnlyList<string> Words)> Extra { get; init; }

	public int MissingCount => Missing.Sum(m => m.Words.Count);
	public int ExtraCount => Extra.Sum(m => m.Words.Count);

	/// <summary>Share of words on both sides that matched (Dice coefficient), 0..1.</summary>
	public double Score => GoldenWords + RenderedWords == 0
		? 1
		: (GoldenWords + RenderedWords - MissingCount - ExtraCount) / (double)(GoldenWords + RenderedWords);

	public bool Matches => MissingCount == 0 && ExtraCount == 0 && GoldenPages == RenderedPages;

	public static SnapshotComparison Compare(string golden, string rendered)
	{
		var goldenLines = Lines(golden);
		var renderedLines = Lines(rendered);
		var goldenWords = Collapse(goldenLines.SelectMany(l => l.Words).ToList());
		var renderedWords = Collapse(renderedLines.SelectMany(l => l.Words).ToList());

		return new SnapshotComparison
		{
			GoldenPages = PageCount(golden),
			RenderedPages = PageCount(rendered),
			GoldenWords = goldenWords.Count,
			RenderedWords = renderedWords.Count,
			Missing = Unmatched(goldenLines, goldenWords, renderedWords),
			Extra = Unmatched(renderedLines, renderedWords, goldenWords)
		};
	}

	public string Report(string caseName)
	{
		var sb = new StringBuilder();
		sb.AppendLine($"{caseName}: {(Matches ? "MATCH" : "DIFFERENT")}  score {Score:P1}");
		sb.AppendLine($"pages   golden {GoldenPages}  html {RenderedPages}  (pages with text)");
		sb.AppendLine($"words   golden {GoldenWords}  html {RenderedWords}  missing {MissingCount}  extra {ExtraCount}");
		Section(sb, "MISSING (in the GhostDraft golden, not in the HTML render)", Missing);
		Section(sb, "EXTRA (in the HTML render, not in the GhostDraft golden)", Extra);
		return sb.ToString();
	}

	private static void Section(StringBuilder sb, string title, IReadOnlyList<(string Line, IReadOnlyList<string> Words)> items)
	{
		if (items.Count == 0) return;
		sb.AppendLine().AppendLine(title);
		foreach (var (line, words) in items)
		{
			sb.AppendLine($"  [{string.Join(' ', words)}]  <=  {line}");
		}
	}

	// Walk the source lines in order and report, per line, the words the other side doesn't have (greedy multiset).
	private static List<(string, IReadOnlyList<string>)> Unmatched(
		List<(string Text, List<string> Words)> lines, List<string> kept, List<string> other)
	{
		var available = other.GroupBy(w => w).ToDictionary(g => g.Key, g => g.Count());
		var budget = kept.GroupBy(w => w).ToDictionary(g => g.Key, g => g.Count());
		var result = new List<(string, IReadOnlyList<string>)>();
		foreach (var (text, words) in lines)
		{
			var unmatched = new List<string>();
			foreach (var word in words)
			{
				// Words removed by Collapse (duplicate runs) are skipped.
				if (!budget.TryGetValue(word, out var left) || left == 0) continue;
				budget[word] = left - 1;
				if (available.TryGetValue(word, out var n) && n > 0) available[word] = n - 1;
				else unmatched.Add(word);
			}
			if (unmatched.Count > 0) result.Add((text.Trim(), unmatched));
		}
		return result;
	}

	private static List<(string Text, List<string> Words)> Lines(string text) =>
		Normalize(text).Split('\n')
			.Where(l => !PageMarker().IsMatch(l))
			.Select(l => (l, WordPattern().Matches(l).Select(m => Undouble(m.Value.ToUpperInvariant())).ToList()))
			.Where(l => l.Item2.Count > 0)
			.ToList();

	// Spire interleaves overprinted text character by character ("Page Page 2 2 of of 22"): "22" -> "2".
	// Applied to both sides, so a real "2020" or "11" still compares equal.
	private static string Undouble(string word) =>
		word.Length % 2 == 0 && word.Length > 0 && string.CompareOrdinal(word, 0, word, word.Length / 2, word.Length / 2) == 0
			? word[..(word.Length / 2)]
			: word;

	// Pages with text. GhostDraft sometimes emits a trailing blank page (e.g. a hidden overflow schedule).
	private static int PageCount(string text)
	{
		var pages = new List<bool> { false };
		foreach (var line in Normalize(text).Split('\n'))
		{
			if (PageMarker().IsMatch(line)) pages.Add(false);
			else if (WordPattern().IsMatch(line)) pages[^1] = true;
		}
		return pages.Count(p => p);
	}

	private static string Normalize(string text) => text
		.Replace("\r", string.Empty)
		.Replace('\u00A0', ' ')
		.Replace('\u2018', '\'').Replace('\u2019', '\'')
		.Replace('\u201C', '"').Replace('\u201D', '"')
		.Replace('\u2013', '-').Replace('\u2014', '-');

	// "A B C A B C" -> "A B C" (Spire extracts overprinted/bold-simulated text twice; a form sheet's footer printed
	// over the one in its picture repeats a whole footer line). Applied to both sides.
	private static List<string> Collapse(List<string> words)
	{
		var result = new List<string>(words.Count);
		var i = 0;
		while (i < words.Count)
		{
			var skipped = false;
			for (var run = Math.Min(24, words.Count - i); run >= 1; run--)
			{
				if (result.Count >= run && Enumerable.Range(0, run).All(k => result[result.Count - run + k] == words[i + k]))
				{
					i += run;
					skipped = true;
					break;
				}
			}
			if (!skipped) result.Add(words[i++]);
		}
		return result;
	}

	[GeneratedRegex(@"^\s*--- Page \d+ ---\s*$")]
	private static partial Regex PageMarker();

	[GeneratedRegex(@"[A-Za-z]+|[0-9]+")]
	private static partial Regex WordPattern();
}
