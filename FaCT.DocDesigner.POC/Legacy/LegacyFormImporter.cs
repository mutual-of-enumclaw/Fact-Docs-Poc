using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using FaCT.DocDesigner.POC.Import;

namespace FaCT.DocDesigner.POC.Legacy;

public sealed record LegacyImportResult(
	string Html,
	int Pages,
	int Texts,
	int Fields,
	int Shapes,
	int Images,
	IReadOnlyList<string> Fonts,
	IReadOnlyList<string> MissingFonts,
	int Skipped,
	int CheckBoxes = 0);

/// <summary>
/// Imports a legacy form converted by fact-pdf-tools (<c>demo emit-html</c>, "Layer A": fixed Letter pages with every
/// element at its Documaker position) into designer HTML.
/// The input is rebuilt from an allow-list rather than passed through: only the known element kinds, attributes and
/// style properties survive. Coordinates move from pt to px because the designer's drag tool works in px.
/// Embedded fonts and images are moved to the <see cref="LegacyAssetStore"/>.
/// </summary>
public sealed partial class LegacyFormImporter(LegacyAssetStore assets)
{
	private const double PxPerPt = 96.0 / 72.0;
	private static readonly string[] ShapeClasses = ["rule", "box", "shade", "bullet"];

	// Generic families for forms without embedded fonts (e.g. converted PDFs). Only these exact values are accepted.
	public const string SansFontStack = "Arial, Helvetica, sans-serif";
	public const string SerifFontStack = "'Times New Roman', Times, serif";
	public const string MonoFontStack = "'Courier New', Courier, monospace";
	private static readonly HashSet<string> StandardFontStacks = [SansFontStack, SerifFontStack, MonoFontStack];

	public LegacyImportResult Import(string source)
	{
		var document = new HtmlParser().ParseDocument(source);

		var fonts = new List<string>();
		foreach (var style in document.QuerySelectorAll("style"))
		{
			foreach (Match m in FontFace().Matches(style.TextContent))
			{
				byte[] ttf;
				try { ttf = Convert.FromBase64String(m.Groups["data"].Value); }
				catch (FormatException) { continue; }
				if (assets.RegisterFont(m.Groups["family"].Value, m.Groups["weight"].Value, m.Groups["style"].Value, ttf))
				{
					fonts.Add(m.Groups["family"].Value);
				}
			}
		}

		var html = new StringBuilder();
		var usedFamilies = new HashSet<string>();
		int pages = 0, texts = 0, fields = 0, shapes = 0, images = 0, skipped = 0, checkBoxes = 0;

		foreach (var page in document.QuerySelectorAll("section.form-page"))
		{
			pages++;
			html.Append("<section class=\"form-page\">");
			foreach (var element in page.Children)
			{
				var style = CleanStyle(element.GetAttribute("style"), usedFamilies);
				var classes = element.ClassList;

				if (element.LocalName == "span" && classes.Contains("abs") && classes.Contains("field"))
				{
					fields++;
					// check boxes (from fillable PDFs) are fields too, shown and printed as a mark
					var check = element.GetAttribute("data-kind") == "checkbox";
					if (check) checkBoxes++;
					html.Append(check ? "<span class=\"abs field field-check\"" : "<span class=\"abs field\"")
						.Append(" data-legacy-field=\"").Append(Encode(element.GetAttribute("data-field") ?? string.Empty)).Append('"');
					if (int.TryParse(element.GetAttribute("data-maxlen"), NumberStyles.None, CultureInfo.InvariantCulture, out var maxLength))
					{
						html.Append(" data-maxlen=\"").Append(maxLength).Append('"');
					}
					// the words printed next to the field on the form (for Suggest mappings)
					if (element.GetAttribute("data-label") is { Length: > 0 } rawLabel &&
						FieldLabels.Clean(rawLabel) is { } label)
					{
						html.Append(" data-label=\"").Append(Encode(label)).Append('"');
					}
					html.Append(" style=\"").Append(style).Append("\"></span>");
				}
				else if (element.LocalName == "span" && classes.Contains("abs"))
				{
					texts++;
					// Form wording can never be read as Liquid, even after the designer re-serialises it.
					html.Append("<span class=\"abs\" style=\"").Append(style).Append("\">")
						.Append(DocumentImport.EncodeText(element.TextContent)).Append("</span>");
				}
				else if (element.LocalName == "div" && ShapeClasses.FirstOrDefault(classes.Contains) is { } shape)
				{
					shapes++;
					html.Append("<div class=\"").Append(shape).Append("\" style=\"").Append(style).Append("\"></div>");
				}
				else if (element.LocalName == "img" && classes.Contains("img") && DataImage().Match(element.GetAttribute("src") ?? string.Empty) is { Success: true } image)
				{
					byte[] bytes;
					try { bytes = Convert.FromBase64String(image.Groups["data"].Value); }
					catch (FormatException) { skipped++; continue; }
					images++;
					var file = assets.Save(bytes, image.Groups["type"].Value == "jpeg" ? "jpg" : image.Groups["type"].Value);
					html.Append("<img class=\"img\" alt=\"\" src=\"").Append(LegacyAssetStore.UrlPrefix).Append(file)
						.Append("\" style=\"").Append(style).Append("\">");
				}
				else
				{
					skipped++;
				}
			}
			html.Append("</section>");
		}

		var registered = assets.FontFamilies;
		return new LegacyImportResult(
			html.ToString(), pages, texts, fields, shapes, images,
			fonts.Distinct().ToList(),
			usedFamilies.Where(f => !registered.Contains(f)).Order().ToList(),
			skipped,
			checkBoxes);
	}

	/// <summary>Keeps only the layout/typography properties the converter emits, with values of the expected shape.</summary>
	private static string CleanStyle(string? style, HashSet<string> usedFamilies)
	{
		var result = new StringBuilder("position:absolute;");
		foreach (var declaration in (style ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
		{
			var colon = declaration.IndexOf(':');
			if (colon <= 0) continue;
			var property = declaration[..colon].Trim().ToLowerInvariant();
			var value = declaration[(colon + 1)..].Trim();

			string? clean = property switch
			{
				"left" or "top" or "width" or "height" => ToPx(value),
				"font-size" or "line-height" or "letter-spacing" or "border-width" => Length().IsMatch(value) ? value : null,
				"font-weight" => Weight().IsMatch(value) ? value : null,
				"font-style" => value is "normal" or "italic" ? value : null,
				"white-space" => value is "pre" or "nowrap" ? value : null,
				"background" or "color" => Color().IsMatch(value) ? value : null,
				"font-family" when StandardFontStacks.Contains(value) => value,
				// A form font with a standard fallback (fonts embedded in an imported PDF).
				"font-family" when FamilyWithFallback().Match(value) is { Success: true } withFallback &&
					LegacyAssetStore.IsValidFamily(withFallback.Groups[1].Value) && StandardFontStacks.Contains(withFallback.Groups[2].Value)
					=> Remember(usedFamilies, withFallback.Groups[1].Value) + ", " + withFallback.Groups[2].Value,
				"font-family" => Family().Match(value) is { Success: true } family && LegacyAssetStore.IsValidFamily(family.Groups[1].Value)
					? Remember(usedFamilies, family.Groups[1].Value)
					: null,
				_ => null
			};
			if (clean is not null)
			{
				result.Append(property).Append(':').Append(clean).Append(';');
			}
		}
		return result.ToString();
	}

	private static string Remember(HashSet<string> usedFamilies, string family)
	{
		usedFamilies.Add(family);
		return "'" + family + "'";
	}

	private static string? ToPx(string value)
	{
		var match = Length().Match(value);
		if (!match.Success) return null;
		var number = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
		if (match.Groups[2].Value == "pt") number *= PxPerPt;
		return Math.Round(number, 2).ToString(CultureInfo.InvariantCulture) + "px";
	}

	private static string Encode(string text) => WebUtility.HtmlEncode(text);

	[GeneratedRegex(@"@font-face\s*\{\s*font-family:\s*'(?<family>[^']+)'\s*;\s*font-weight:\s*(?<weight>\w+)\s*;\s*font-style:\s*(?<style>\w+)\s*;\s*src:\s*url\(data:font/ttf;base64,(?<data>[A-Za-z0-9+/=]+)\)")]
	private static partial Regex FontFace();

	[GeneratedRegex(@"^data:image/(?<type>png|jpeg|gif);base64,(?<data>[A-Za-z0-9+/=]+)$")]
	private static partial Regex DataImage();

	[GeneratedRegex(@"^(-?\d+(?:\.\d+)?)(pt|px)$")]
	private static partial Regex Length();

	[GeneratedRegex(@"^(normal|bold|[1-9]00)$")]
	private static partial Regex Weight();

	[GeneratedRegex(@"^(rgb\(\s*\d{1,3}\s*,\s*\d{1,3}\s*,\s*\d{1,3}\s*\)|#[0-9a-fA-F]{3,8})$")]
	private static partial Regex Color();

	[GeneratedRegex(@"^'([^']+)'$")]
	private static partial Regex Family();

	[GeneratedRegex(@"^'([^']+)',\s*(.+)$")]
	private static partial Regex FamilyWithFallback();
}
