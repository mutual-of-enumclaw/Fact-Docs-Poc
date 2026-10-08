using System.Text;
using System.Text.RegularExpressions;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using UglyToad.PdfPig;

namespace FaCT.DocDesigner.POC.Templates;

/// <summary>One line of a diff: "same", "added", "removed", or "skip" (Count unchanged lines left out).</summary>
public sealed record DiffLine(string Type, string? Text, int Count = 0);

/// <summary>A line diff with unchanged stretches collapsed to a few lines of context.</summary>
public sealed record TextDiff(IReadOnlyList<DiffLine> Lines, int Added, int Removed)
{
	public bool Identical => Added == 0 && Removed == 0;
}

/// <summary>
/// What publishing would change: line diffs of the template HTML, its CSS and the printed text of both versions rendered
/// with the same data. Markup and CSS are put one tag / rule per line first so a diff line is a meaningful unit.
/// </summary>
public static partial class VersionDiff
{
	/// <summary>Unchanged lines kept around each change.</summary>
	public const int Context = 3;

	public static TextDiff Lines(string before, string after)
	{
		var model = InlineDiffBuilder.Diff(before, after, ignoreWhiteSpace: false);
		var lines = new List<(string Type, string Text)>();
		foreach (var line in model.Lines)
		{
			switch (line.Type)
			{
				case ChangeType.Inserted: lines.Add(("added", line.Text)); break;
				case ChangeType.Deleted: lines.Add(("removed", line.Text)); break;
				case ChangeType.Unchanged: lines.Add(("same", line.Text)); break;
			}
		}
		var added = lines.Count(l => l.Type == "added");
		var removed = lines.Count(l => l.Type == "removed");
		return new TextDiff(added + removed == 0 ? [] : Collapse(lines), added, removed);
	}

	// Long runs of unchanged lines become "skip" entries; Context lines stay next to each change.
	private static List<DiffLine> Collapse(List<(string Type, string Text)> lines)
	{
		var result = new List<DiffLine>();
		var i = 0;
		while (i < lines.Count)
		{
			if (lines[i].Type != "same")
			{
				result.Add(new DiffLine(lines[i].Type, lines[i].Text));
				i++;
				continue;
			}
			var start = i;
			while (i < lines.Count && lines[i].Type == "same") i++;
			var run = lines.GetRange(start, i - start);
			var keepHead = start == 0 ? 0 : Context;
			var keepTail = i == lines.Count ? 0 : Context;
			if (run.Count <= keepHead + keepTail)
			{
				result.AddRange(run.Select(l => new DiffLine("same", l.Text)));
				continue;
			}
			result.AddRange(run.Take(keepHead).Select(l => new DiffLine("same", l.Text)));
			result.Add(new DiffLine("skip", null, run.Count - keepHead - keepTail));
			result.AddRange(run.Skip(run.Count - keepTail).Select(l => new DiffLine("same", l.Text)));
		}
		return result;
	}

	/// <summary>Template HTML one tag (with its text) per line.</summary>
	public static string HtmlLines(string html) => Tidy(BetweenTags().Replace(html ?? string.Empty, ">\n<"));

	/// <summary>CSS one rule per line.</summary>
	public static string CssLines(string css) => Tidy(RuleEnd().Replace(css ?? string.Empty, "}\n"));

	/// <summary>The printed text of a PDF, line by line, with a marker line before each page after the first.</summary>
	public static string PdfLines(byte[] pdf)
	{
		using var document = PdfDocument.Open(pdf);
		var sb = new StringBuilder();
		foreach (var page in document.GetPages())
		{
			if (page.Number > 1) sb.Append("--- Page ").Append(page.Number).Append(" ---\n");
			var lines = new List<List<UglyToad.PdfPig.Content.Word>>();
			foreach (var word in page.GetWords().OrderByDescending(w => w.BoundingBox.Bottom).ThenBy(w => w.BoundingBox.Left))
			{
				var line = lines.LastOrDefault();
				if (line is not null && Math.Abs(line[0].BoundingBox.Bottom - word.BoundingBox.Bottom) <= 2.0) line.Add(word);
				else lines.Add([word]);
			}
			foreach (var line in lines)
			{
				sb.Append(string.Join(' ', line.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text))).Append('\n');
			}
		}
		return sb.ToString();
	}

	public static int PageCount(byte[] pdf)
	{
		using var document = PdfDocument.Open(pdf);
		return document.NumberOfPages;
	}

	private static string Tidy(string text) =>
		string.Join('\n', text.Replace("\r", string.Empty).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));

	[GeneratedRegex(@">\s*<")]
	private static partial Regex BetweenTags();

	[GeneratedRegex(@"\}\s*")]
	private static partial Regex RuleEnd();
}
