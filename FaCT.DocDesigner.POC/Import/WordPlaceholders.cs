using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Wordprocessing;

namespace FaCT.DocDesigner.POC.Import;

/// <summary>Which kinds of typed placeholder text a Word import turns into Data Fields.</summary>
[Flags]
public enum PlaceholderStyles
{
	None = 0,
	/// <summary><c>{{ name }}</c>, optionally with a designer format: <c>{{ premium | currency }}</c>.</summary>
	Braces = 1,
	/// <summary><c>«Name»</c> (merge-field results pasted as text, or typed chevrons).</summary>
	Chevrons = 2,
	/// <summary><c>[Insured Name]</c>. Off by default: ordinary bracketed wording looks the same.</summary>
	Brackets = 4,
	Default = Braces | Chevrons,
	All = Braces | Chevrons | Brackets
}

public sealed record DocxImportOptions(PlaceholderStyles Placeholders = PlaceholderStyles.Default)
{
	public static readonly DocxImportOptions Default = new();

	/// <summary>Parses "braces,chevrons,brackets" / "none" / "all" (the import endpoint's ?placeholders=).</summary>
	public static bool TryParsePlaceholders(string? value, out PlaceholderStyles styles)
	{
		styles = PlaceholderStyles.Default;
		if (string.IsNullOrWhiteSpace(value)) return true;
		styles = PlaceholderStyles.None;
		foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			switch (part.ToLowerInvariant())
			{
				case "none": break;
				case "all": styles |= PlaceholderStyles.All; break;
				case "braces": styles |= PlaceholderStyles.Braces; break;
				case "chevrons": styles |= PlaceholderStyles.Chevrons; break;
				case "brackets": styles |= PlaceholderStyles.Brackets; break;
				default: return false;
			}
		}
		return true;
	}
}

/// <summary>
/// Finds typed placeholders in a Word document's paragraphs and replaces each with a merge field (a w:fldSimple carrying
/// the placeholder's run formatting), so the converter turns it into a Data Field like any MERGEFIELD.
/// Word splits text into runs at will (spell check, edits, formatting), so a placeholder is found in the joined text of
/// consecutive runs and cut out of however many runs it spans. Text inside other fields is never touched.
/// The document is changed in memory only.
/// </summary>
internal sealed partial class WordPlaceholders(PlaceholderStyles styles)
{
	/// <summary>Formats a <c>{{ x | format }}</c> placeholder may carry: the designer's Data Field formats.</summary>
	public static readonly IReadOnlySet<string> Formats = new HashSet<string>(StringComparer.Ordinal)
	{
		"currency", "dollars", "percent", "number", "decimal", "shortdate", "upcase"
	};

	private readonly Dictionary<SimpleField, (string Path, string? Format)> _fields = new(ReferenceEqualityComparer.Instance);

	public int Converted { get; private set; }

	/// <summary>[Bracketed] phrases that look like placeholders but were left as text (Brackets not enabled).</summary>
	public int BracketCandidates { get; private set; }

	public bool TryGetField(SimpleField field, out (string Path, string? Format) binding) => _fields.TryGetValue(field, out binding);

	public void Apply(OpenXmlElement root)
	{
		foreach (var paragraph in root.Descendants<Paragraph>().ToList())
		{
			foreach (var segment in Segments(paragraph).ToList())
			{
				Replace(segment);
			}
		}
	}

	private sealed record Match(int Start, int Length, string Path, string? Format);

	private void Replace(List<Text> segment)
	{
		var text = string.Concat(segment.Select(t => t.Text));
		if (text.Length == 0) return;

		var matches = Find(text);
		// Right to left: a replacement never moves the text of the ones still to do.
		foreach (var match in Enumerable.Reverse(matches))
		{
			var (i, startOffset) = Locate(segment, match.Start);
			var (j, lastOffset) = Locate(segment, match.Start + match.Length - 1);
			var first = segment[i];
			var run = (Run)first.Parent!;
			var original = text.Substring(match.Start, match.Length);

			string? after = null;
			if (i == j)
			{
				after = first.Text[(lastOffset + 1)..];
			}
			else
			{
				var last = segment[j];
				last.Text = last.Text[(lastOffset + 1)..];
				last.Space = SpaceProcessingModeValues.Preserve;
				for (var k = i + 1; k < j; k++) segment[k].Text = string.Empty;
			}
			first.Text = first.Text[..startOffset];
			first.Space = SpaceProcessingModeValues.Preserve;

			// Whatever follows the placeholder in its first run moves to a new run after the field.
			var tail = new Run();
			if (run.RunProperties is { } properties) tail.Append(properties.CloneNode(true));
			if (!string.IsNullOrEmpty(after)) tail.Append(new Text(after) { Space = SpaceProcessingModeValues.Preserve });
			foreach (var sibling in first.ElementsAfter().ToList())
			{
				sibling.Remove();
				tail.Append(sibling);
			}

			var shown = new Run();
			if (run.RunProperties is { } format) shown.Append(format.CloneNode(true));
			shown.Append(new Text(original) { Space = SpaceProcessingModeValues.Preserve });
			var field = new SimpleField(shown) { Instruction = " MERGEFIELD \"" + match.Path + "\" " };
			_fields[field] = (match.Path, match.Format);
			Converted++;

			run.InsertAfterSelf(field);
			if (tail.ChildElements.Any(c => c is not RunProperties)) field.InsertAfterSelf(tail);
		}
	}

	private List<Match> Find(string text)
	{
		var found = new List<Match>();
		if (styles.HasFlag(PlaceholderStyles.Braces))
		{
			foreach (System.Text.RegularExpressions.Match m in BracePattern().Matches(text))
			{
				var format = m.Groups["format"].Success && Formats.Contains(m.Groups["format"].Value) ? m.Groups["format"].Value : null;
				Add(m, format);
			}
		}
		if (styles.HasFlag(PlaceholderStyles.Chevrons))
		{
			foreach (System.Text.RegularExpressions.Match m in ChevronPattern().Matches(text)) Add(m, null);
		}
		foreach (System.Text.RegularExpressions.Match m in BracketPattern().Matches(text))
		{
			if (styles.HasFlag(PlaceholderStyles.Brackets)) Add(m, null);
			else if (DocxImporter.ToFieldPath(m.Groups["name"].Value) is not null) BracketCandidates++;
		}

		// Earliest first; a placeholder overlapping one already taken is left as text.
		var result = new List<Match>();
		foreach (var match in found.OrderBy(m => m.Start))
		{
			if (result.Count == 0 || match.Start >= result[^1].Start + result[^1].Length) result.Add(match);
		}
		return result;

		void Add(System.Text.RegularExpressions.Match m, string? format)
		{
			if (DocxImporter.ToFieldPath(m.Groups["name"].Value) is { } path) found.Add(new Match(m.Index, m.Length, path, format));
		}
	}

	private static (int Index, int Offset) Locate(List<Text> segment, int position)
	{
		var start = 0;
		for (var i = 0; i < segment.Count; i++)
		{
			var length = segment[i].Text.Length;
			if (position < start + length) return (i, position - start);
			start += length;
		}
		throw new InvalidOperationException("Position outside the segment.");
	}

	/// <summary>
	/// Runs of plain text in a paragraph that a placeholder may span: consecutive w:t in the paragraph's own runs.
	/// Tabs, breaks, fields, links and content controls end a segment; bookmarks and proofing marks don't.
	/// </summary>
	private static IEnumerable<List<Text>> Segments(Paragraph paragraph)
	{
		var segment = new List<Text>();
		var fieldDepth = 0;
		foreach (var child in paragraph.ChildElements)
		{
			switch (child)
			{
				case Run run:
					foreach (var part in run.ChildElements)
					{
						switch (part)
						{
							case RunProperties or LastRenderedPageBreak:
								break;
							case Text text when fieldDepth == 0:
								segment.Add(text);
								break;
							case FieldChar fieldChar:
								if (fieldChar.FieldCharType?.InnerText == "begin") fieldDepth++;
								else if (fieldChar.FieldCharType?.InnerText == "end" && fieldDepth > 0) fieldDepth--;
								if (segment.Count > 0) { yield return segment; segment = []; }
								break;
							default:
								if (segment.Count > 0) { yield return segment; segment = []; }
								break;
						}
					}
					break;
				case ParagraphProperties or BookmarkStart or BookmarkEnd or ProofError or PermStart or PermEnd:
					break;
				default:
					if (segment.Count > 0) { yield return segment; segment = []; }
					break;
			}
		}
		if (segment.Count > 0) yield return segment;
	}

	[GeneratedRegex(@"\{\{\s*(?<name>[A-Za-z_][A-Za-z0-9_. \-]{0,80}?)\s*(?:\|\s*(?<format>[a-z]+)\s*)?\}\}")]
	private static partial Regex BracePattern();

	[GeneratedRegex(@"«\s*(?<name>[^«»\r\n]{1,80}?)\s*»")]
	private static partial Regex ChevronPattern();

	[GeneratedRegex(@"\[\s*(?<name>[A-Za-z][A-Za-z0-9_. \-]{0,60}?)\s*\]")]
	private static partial Regex BracketPattern();
}
