using System.Text.RegularExpressions;
using UglyToad.PdfPig.Core;

namespace FaCT.DocDesigner.POC.Import;

/// <summary>A run of text on a PDF page in PDF coordinates (y up).</summary>
public sealed record TextRun(string Text, double Left, double Right, double Bottom, double Top)
{
	public double Middle => (Bottom + Top) / 2;
}

/// <summary>
/// The printed label of a form field, for fields whose names say nothing (an IRS W-9's "f1_01[0]" is labelled "Name of
/// entity/individual"): a check box's label is the text just right of it, a text field's the text just left of it on the
/// same line, otherwise the text just above it. Line numbers ("1", "3a") and the explanation after the first sentence or
/// bracket are dropped, so the label is a short phrase that can be matched with model property names.
/// </summary>
public static partial class FieldLabels
{
	public const int MaxLength = 60;
	private const double MaxLeftGap = 150;
	private const double NearLeftGap = 30;
	private const double MaxRightGap = 24;
	private const double MaxAboveGap = 14;

	public static string? Find(IReadOnlyList<TextRun> runs, PdfRectangle field, bool checkBox)
	{
		bool SameLine(TextRun r) => r.Middle >= field.Bottom - 2 && r.Middle <= field.Top + 2;
		var height = field.Top - field.Bottom;

		// text on the field's line, nearest first
		var left = runs.Where(r => SameLine(r) && r.Right <= field.Left + 1 && field.Left - r.Right <= MaxLeftGap)
			.OrderByDescending(r => r.Right).ToList();
		var right = runs.Where(r => SameLine(r) && r.Left >= field.Right - 1 && r.Left - field.Right <= MaxRightGap)
			.OrderBy(r => r.Left).ToList();
		// the line just above, followed up to where its paragraph starts
		var aboveLine = runs.Where(r => r.Bottom >= field.Top - 3 && r.Bottom - field.Top <= MaxAboveGap && r.Left < field.Right && r.Right > field.Left - 4)
			.OrderBy(r => r.Bottom).ThenBy(r => r.Left).FirstOrDefault();
		var above = aboveLine is null ? [] : new List<TextRun> { ParagraphStart(runs, aboveLine) };

		// a text field takes whichever is closer: the text before it on its line, or the line above it
		var leftGap = left.Count > 0 ? field.Left - left[0].Right : double.MaxValue;
		var aboveGap = aboveLine is null ? double.MaxValue : Math.Max(0, aboveLine.Bottom - field.Top);
		var order = checkBox
			? new[] { right, left, above }
			: leftGap < aboveGap ? new[] { left, above } : new[] { above, left };
		return order.SelectMany(c => c).Select(r => Clean(r.Text)).FirstOrDefault(l => l is not null);
	}

	/// <summary>The first line of the paragraph a line belongs to: lines stacked above it at the same left edge.</summary>
	private static TextRun ParagraphStart(IReadOnlyList<TextRun> runs, TextRun line)
	{
		var current = line;
		for (var i = 0; i < 10; i++)
		{
			var lineHeight = current.Top - current.Bottom;
			var previous = runs.Where(r => !ReferenceEquals(r, current) && r.Bottom >= current.Top - lineHeight * 0.5 &&
					r.Bottom - current.Top <= lineHeight * 0.8 && Math.Abs(r.Left - current.Left) <= lineHeight * 1.5)
				.OrderBy(r => r.Bottom).FirstOrDefault();
			// a paragraph's first line usually ends a sentence-less phrase; stop at a line that closes a sentence
			if (previous is null || previous.Text.TrimEnd().EndsWith('.')) break;
			current = previous;
		}
		return current;
	}

	/// <summary>The label as a short phrase, or null when it has no letters.</summary>
	public static string? Clean(string? text)
	{
		if (string.IsNullOrWhiteSpace(text)) return null;
		// never anything Liquid could read
		var label = Spaces().Replace(text.Replace('{', ' ').Replace('}', ' ').Replace('%', ' '), " ").Trim();
		label = Enumerator().Replace(label, string.Empty);
		if (OnlyEnumerator().IsMatch(label)) return null;
		var stop = Stop().Match(label);
		if (stop.Success && stop.Index >= 3) label = label[..stop.Index];
		label = label.Trim().TrimEnd(':', '.', '-', ',', ';', ' ').Trim();
		if (label.Length > MaxLength)
		{
			var cut = label.LastIndexOf(' ', MaxLength);
			label = label[..(cut > 10 ? cut : MaxLength)].TrimEnd();
		}
		return label.Count(char.IsLetter) >= 2 && !Trivial.Contains(label) ? label : null;
	}

	// connecting words that end up alone on a line next to a field
	private static readonly HashSet<string> Trivial = new(StringComparer.OrdinalIgnoreCase) { "and", "or", "of", "to", "the", "for", "see", "note" };

	[GeneratedRegex(@"\s+")]
	private static partial Regex Spaces();

	// "1 ", "3a ", "(a) ", "12. ", "b " at the start
	[GeneratedRegex(@"^(?:\(?[0-9]{1,2}[a-z]?\)?\.?|\(?[a-z]\)|[a-z])\s+")]
	private static partial Regex Enumerator();

	// a line number on its own: "3a", "(b)", "12.", "3(a)"
	[GeneratedRegex(@"^\(?[0-9]{1,2}[a-z]?\)?\.?$|^\(?[a-z]\)$|^[0-9]{1,2}\([a-z]\)$")]
	private static partial Regex OnlyEnumerator();

	// where the explanation starts: ". " "(" ";"
	[GeneratedRegex(@"\.\s|\(|;")]
	private static partial Regex Stop();
}
