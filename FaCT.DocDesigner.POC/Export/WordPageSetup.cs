using System.Globalization;
using AngleSharp.Dom;

namespace FaCT.DocDesigner.POC.Export;

/// <summary>
/// A template's page setup (the designer's hidden div.doc-setup) read for the Word export, with the same rules and
/// fallbacks as the PDF renderer: Letter / Legal / A4, portrait or landscape, margins 0-3 inches (standard 0.5, 0.5,
/// 0.6, 0.5), header/footer text 6-16 pt (standard 10). Slots: "header:default", "footer:first", ... => their div.doc-hf.
/// </summary>
internal sealed record WordPageSetup(string Size, bool Landscape, double[] Margins, double FontSize, IReadOnlyDictionary<string, IElement> Slots)
{
	private static readonly double[] StandardMargins = [0.5, 0.5, 0.6, 0.5];

	/// <summary>Reads the first page setup in the document and removes every one from it (they never print in the body).</summary>
	public static WordPageSetup? Take(IDocument document)
	{
		var all = document.QuerySelectorAll(".doc-setup").ToList();
		if (all.Count == 0) return null;
		var element = all[0];
		all.ForEach(e => e.Remove());

		var classes = element.ClassList.ToList();
		string? Value(string prefix) => classes.FirstOrDefault(c => c.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];

		var size = Value("ds-size-") is "legal" or "a4" ? Value("ds-size-")! : "letter";
		var landscape = Value("ds-orient-") == "landscape";
		var given = (Value("ds-margins-") ?? string.Empty).Split('_');
		var margins = StandardMargins.Select((standard, i) =>
			i < given.Length && double.TryParse(given[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var m) && m is >= 0 and <= 3 ? m : standard).ToArray();
		var font = double.TryParse(Value("ds-font-"), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f is >= 6 and <= 16 ? f : 10;

		var slots = new Dictionary<string, IElement>();
		foreach (var line in element.QuerySelectorAll(".doc-hf"))
		{
			var part = line.ClassList.Contains("doc-hf-header") ? "header" : line.ClassList.Contains("doc-hf-footer") ? "footer" : null;
			var variant = new[] { "first", "even", "default" }.FirstOrDefault(v => line.ClassList.Contains("doc-hf-" + v));
			if (part is not null && variant is not null) slots.TryAdd(part + ":" + variant, line);
		}
		return new WordPageSetup(size, landscape, margins, font, slots);
	}

	private (int Width, int Height) Paper => Size switch
	{
		"legal" => (12240, 20160),
		"a4" => (11906, 16838),
		_ => (12240, 15840)
	};

	public int WidthTwips => Landscape ? Paper.Height : Paper.Width;

	public int HeightTwips => Landscape ? Paper.Width : Paper.Height;

	public (int Top, int Right, int Bottom, int Left) MarginTwips =>
		(Twips(Margins[0]), Twips(Margins[1]), Twips(Margins[2]), Twips(Margins[3]));

	public int ContentWidth => WidthTwips - MarginTwips.Left - MarginTwips.Right;

	private static int Twips(double inches) => (int)Math.Round(inches * 1440);
}
