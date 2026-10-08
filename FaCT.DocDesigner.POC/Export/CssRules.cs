using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;

namespace FaCT.DocDesigner.POC.Export;

/// <summary>
/// Just enough CSS to read the formatting the designer's Styles panel writes for an element: rules for <c>#id</c>,
/// <c>.class</c> and tag selectors, plus the element's own inline style. Only a short list of properties is ever used
/// (alignment, bold, italic, colour, size, column width); everything else is ignored.
/// </summary>
internal sealed partial class CssRules
{
	public static readonly CssRules Empty = new([]);

	private static readonly HashSet<string> Used =
		["text-align", "font-weight", "font-style", "font-size", "color", "flex-basis", "width", "text-decoration"];

	private readonly List<(string Selector, Dictionary<string, string> Declarations)> _rules;

	private CssRules(List<(string, Dictionary<string, string>)> rules) => _rules = rules;

	public static CssRules Parse(string? css)
	{
		if (string.IsNullOrWhiteSpace(css))
		{
			return Empty;
		}

		var text = Comments().Replace(css, string.Empty);
		text = AtRules().Replace(text, string.Empty);

		var rules = new List<(string, Dictionary<string, string>)>();
		foreach (Match rule in Rule().Matches(text))
		{
			var declarations = Declarations(rule.Groups["body"].Value);
			if (declarations.Count == 0)
			{
				continue;
			}

			foreach (var selector in rule.Groups["selector"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
			{
				if (SimpleSelector().IsMatch(selector))
				{
					rules.Add((selector, declarations));
				}
			}
		}

		return new CssRules(rules);
	}

	/// <summary>What applies to the element: tag rules, then class rules, then its id rule, then its inline style.</summary>
	public Dictionary<string, string> For(IElement element)
	{
		var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		void Apply(string selector)
		{
			foreach (var rule in _rules.Where(r => r.Selector == selector))
			{
				foreach (var (property, value) in rule.Declarations)
				{
					result[property] = value;
				}
			}
		}

		Apply(element.LocalName);
		foreach (var cssClass in element.ClassList)
		{
			Apply("." + cssClass);
		}

		if (!string.IsNullOrEmpty(element.Id))
		{
			Apply("#" + element.Id);
		}

		foreach (var (property, value) in Declarations(element.GetAttribute("style") ?? string.Empty))
		{
			result[property] = value;
		}

		return result;
	}

	private static Dictionary<string, string> Declarations(string body)
	{
		var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (var declaration in body.Split(';', StringSplitOptions.RemoveEmptyEntries))
		{
			var colon = declaration.IndexOf(':');
			if (colon <= 0)
			{
				continue;
			}

			var property = declaration[..colon].Trim().ToLowerInvariant();
			if (Used.Contains(property))
			{
				result[property] = declaration[(colon + 1)..].Replace("!important", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
			}
		}

		return result;
	}

	/// <summary>A length in points (px are 0.75 pt), or null.</summary>
	public static double? Points(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return null;
		}

		var m = Length().Match(value.Trim());
		if (!m.Success || !double.TryParse(m.Groups["n"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
		{
			return null;
		}

		return m.Groups["unit"].Value.ToLowerInvariant() switch
		{
			"px" or "" => n * 0.75,
			"pt" => n,
			"in" => n * 72,
			"cm" => n * 72 / 2.54,
			"mm" => n * 72 / 25.4,
			_ => null
		};
	}

	/// <summary>A percentage such as "33.33%" as 33.33, or null.</summary>
	public static double? Percent(string? value) =>
		value is not null && value.Trim().EndsWith('%')
		&& double.TryParse(value.Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;

	/// <summary>"#144835" or "#fff" as "144835" / "FFFFFF", or null for anything else (named colours, rgb()).</summary>
	public static string? HexColor(string? value)
	{
		if (value is null)
		{
			return null;
		}

		var m = Hex().Match(value.Trim());
		if (!m.Success)
		{
			return null;
		}

		var hex = m.Groups["hex"].Value.ToUpperInvariant();
		return hex.Length == 3 ? string.Concat(hex.Select(c => new string(c, 2))) : hex;
	}

	[GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
	private static partial Regex Comments();

	// @media and similar blocks hold rules for other situations; they are not read
	[GeneratedRegex(@"@[a-z-]+[^{;]*\{(?:[^{}]*\{[^{}]*\})*[^{}]*\}", RegexOptions.IgnoreCase)]
	private static partial Regex AtRules();

	[GeneratedRegex(@"(?<selector>[^{}]+)\{(?<body>[^{}]*)\}")]
	private static partial Regex Rule();

	[GeneratedRegex(@"^[#.]?[A-Za-z0-9_-]+$")]
	private static partial Regex SimpleSelector();

	[GeneratedRegex(@"^(?<n>-?\d+(?:\.\d+)?)(?<unit>px|pt|in|cm|mm)?$", RegexOptions.IgnoreCase)]
	private static partial Regex Length();

	[GeneratedRegex(@"^#(?<hex>[0-9a-fA-F]{6}|[0-9a-fA-F]{3})$")]
	private static partial Regex Hex();
}
