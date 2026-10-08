using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using FaCT.DocDesigner.POC.Images;
using FaCT.DocDesigner.POC.Legacy;
using FaCT.DocDesigner.POC.Templates;
using Fluid;
using Fluid.Values;
using Ganss.Xss;

namespace FaCT.DocDesigner.POC.Rendering;

/// <summary>
/// Turns a designer template (HTML + Liquid) and a JSON data payload into a complete, sanitized HTML document.
/// This is the piece fact-docgen would own: template + message data => HTML.
/// </summary>
public sealed partial class DocumentComposer
{
	private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

	private readonly FluidParser _parser = new();
	// One set of options per document language: its culture (month names, Yes/No, numbers) and its clause translations.
	private readonly Dictionary<string, TemplateOptions> _languageOptions;
	private readonly HtmlSanitizer _sanitizer;

	// Brand stylesheet every document gets (same file the designer canvas loads), and the approved logos as
	// data: URIs exposed to templates as {{ brand.logos.horizontal_4color }} etc. The PDF renderer blocks all
	// network requests, so images must be inline.
	private readonly string _brandCss;
	private readonly Dictionary<string, object?> _brand;
	private readonly LegacyAssetStore _legacyAssets;
	private readonly DocumentImageResolver _images;
	private readonly ClauseStore _clauses;
	private readonly Themes.ThemeStore _themes;

	public DocumentComposer(IWebHostEnvironment environment, LegacyAssetStore legacyAssets, DocumentImageResolver images, ClauseStore clauses, Themes.ThemeStore themes)
	{
		_legacyAssets = legacyAssets;
		_images = images;
		_clauses = clauses;
		_themes = themes;
		var brandRoot = Path.Combine(environment.WebRootPath, "brand");
		_brandCss = EmbedFonts(Path.Combine(brandRoot, "fonts", "figtree.css")) + File.ReadAllText(Path.Combine(brandRoot, "moe-document.css"));
		_brand = new Dictionary<string, object?>
		{
			["name"] = "Mutual Of Enumclaw",
			["logos"] = Directory.EnumerateFiles(Path.Combine(brandRoot, "logos"), "moe-logo-*.svg").ToDictionary(
				f => Path.GetFileNameWithoutExtension(f)["moe-logo-".Length..].Replace('-', '_'),
				f => (object?)("data:image/svg+xml;base64," + Convert.ToBase64String(File.ReadAllBytes(f))))
		};

		_languageOptions = DocumentLanguages.All.ToDictionary(l => l.Code, CreateOptions);
		_sanitizer = CreateSanitizer();
	}

	private TemplateOptions CreateOptions(DocumentLanguage language)
	{
		var options = new TemplateOptions
		{
			CultureInfo = language.CultureInfo,
			// Shared clauses: {% include 'name' %} / {% include 'name@3' %} read the clause store (never the file system),
			// in the document's language when the clause has a translation.
			FileProvider = _clauses.FileProviderFor(language)
		};

		// Formats offered in the designer's Data Field "Format" dropdown.
		options.Filters.AddFilter("currency", (input, _, _) =>
			Text(input.IsNil() ? string.Empty : input.ToNumberValue().ToString("C2", Us)));
		options.Filters.AddFilter("dollars", (input, _, _) =>
			Text(input.IsNil() ? string.Empty : input.ToNumberValue().ToString("C0", Us)));
		options.Filters.AddFilter("percent", (input, _, _) =>
			Text(input.IsNil() ? string.Empty : input.ToNumberValue().ToString("P2", Us)));
		options.Filters.AddFilter("number", (input, _, _) =>
			Text(input.IsNil() ? string.Empty : input.ToNumberValue().ToString("N0", Us)));
		options.Filters.AddFilter("decimal", (input, _, _) =>
			Text(input.IsNil() ? string.Empty : input.ToNumberValue().ToString("N2", Us)));
		options.Filters.AddFilter("shortdate", (input, _, _) =>
		{
			var raw = input.ToStringValue();
			return Text(DateTime.TryParse(raw, Us, DateTimeStyles.None, out var date) ? date.ToString("MM/dd/yyyy", Us) : raw);
		});

		// Custom formats (designer: Format > Custom...): {{ x | format: "#,##0.00;(#,##0.00);'None'" }}, a date pattern,
		// optionally in another language ({{ d | format: "d 'de' MMMM 'de' yyyy", "es-US" }}). Same rules as DocGen's filler.
		// Without a language the format uses the document's (English: en-US, as before).
		options.Filters.AddFilter("format", (input, arguments, _) =>
		{
			if (input.IsNil()) return input;
			var format = arguments.At(0).ToStringValue();
			var culture = arguments.Count > 1 ? arguments.At(1).ToStringValue() : language.Culture;
			if (ValueFormats.FormatProblem(format) is { } problem) throw new InvalidOperationException(problem);
			if (ValueFormats.CultureProblem(culture) is { } cultureProblem) throw new InvalidOperationException(cultureProblem);
			return input is NumberValue
				? new StringValue(ValueFormats.FormatNumber(input.ToNumberValue(), format, culture))
				: new StringValue(ValueFormats.FormatText(input.ToStringValue(), format, culture));
		});
		// Masks: {{ policy.phone | mask: "(###) ###-####" }} (# a digit, * a hidden digit).
		options.Filters.AddFilter("mask", (input, arguments, _) =>
		{
			if (input.IsNil()) return input;
			var mask = arguments.At(0).ToStringValue();
			if (ValueFormats.MaskProblem(mask) is { } problem) throw new InvalidOperationException(problem);
			var text = input is NumberValue ? input.ToNumberValue().ToString("0", Us) : input.ToStringValue();
			return new StringValue(ValueFormats.Mask(text, mask));
		});

		// Calculated fields (ExpressionCompiler): business rounding (half away from zero, 2.5 -> 3), and results without
		// trailing zeros (1200.0 -> 1200). Text passes through.
		options.Filters.AddFilter("calc_round", (input, arguments, _) =>
		{
			if (input.IsNil()) return input;
			var digits = (int)Math.Clamp(arguments.At(0).ToNumberValue(), 0, 6);
			return NumberValue.Create(Math.Round(input.ToNumberValue(), digits, MidpointRounding.AwayFromZero));
		});
		options.Filters.AddFilter("calc_value", (input, _, _) =>
			input is NumberValue ? NumberValue.Create(input.ToNumberValue() / 1.000000000000000000000000000000000m) : input);

		// Check boxes on form pages: {{ x | checkmark }} / {{ x | checkmark: "Corporation" }} print an X or nothing.
		options.Filters.AddFilter("checkmark", (input, arguments, context) =>
		{
			var when = arguments.Count > 0 ? arguments.At(0).ToStringValue() : null;
			if (when is { Length: > CheckMarks.MaxWhenLength }) throw new InvalidOperationException($"A check box value is at most {CheckMarks.MaxWhenLength} characters.");
			return new StringValue(CheckMarks.IsChecked(input, when, context) ? CheckMarks.Mark : string.Empty);
		});

		// Data Table totals row: {{ lossRatio.claims | sum: "totalLoss" }}. The argument may be a dotted path.
		options.Filters.AddFilter("sum", async (input, arguments, context) =>
		{
			var path = arguments.At(0).ToStringValue();
			var total = 0m;
			foreach (var item in input.Enumerate(context))
			{
				var value = item;
				foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
				{
					value = await value.GetValueAsync(segment, context);
				}
				if (!value.IsNil())
				{
					total += value.ToNumberValue();
				}
			}
			return NumberValue.Create(total);
		});

		// Barcodes and QR codes: {{ policy.number | barcode: "qr" }} (qr, code128, code39, datamatrix, pdf417) => inline SVG.
		// A value the symbology can't encode prints nothing.
		options.Filters.AddFilter("barcode", (input, arguments, _) =>
		{
			var kind = arguments.At(0).ToStringValue();
			if (!Barcodes.Kinds.ContainsKey(kind)) throw new InvalidOperationException($"'{kind}' isn't a barcode kind ({string.Join(", ", Barcodes.Kinds.Keys)}).");
			return new StringValue(input.IsNil() ? string.Empty : Barcodes.Svg(input.ToStringValue(), kind) ?? string.Empty, false);
		});

		// Charts from a list: {{ claims | chart: "column", "year", "amount", "currency" }} (column, bar, line, pie, donut) =>
		// inline SVG; label and value are paths inside each item.
		options.Filters.AddFilter("chart", async (input, arguments, context) =>
		{
			var type = arguments.At(0).ToStringValue();
			if (!Charts.Types.Contains(type)) throw new InvalidOperationException($"'{type}' isn't a chart type ({string.Join(", ", Charts.Types)}).");
			var labelPath = arguments.At(1).ToStringValue();
			var valuePath = arguments.At(2).ToStringValue();
			var format = arguments.Count > 3 ? arguments.At(3).ToStringValue() : null;
			async Task<FluidValue> At(FluidValue item, string path)
			{
				var value = item;
				foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
				{
					value = await value.GetValueAsync(segment, context);
				}
				return value;
			}
			var points = new List<ChartPoint>();
			foreach (var item in input.Enumerate(context))
			{
				var value = await At(item, valuePath);
				if (value.IsNil() || (value is StringValue && !decimal.TryParse(value.ToStringValue(), NumberStyles.Number, Us, out _))) continue;
				points.Add(new ChartPoint((await At(item, labelPath)).ToStringValue(), value.ToNumberValue()));
			}
			return new StringValue(Charts.Svg(type, points, format, language.NoData), false);
		});

		return options;
	}

	// Output of the Liquid render is sanitized before Chromium ever sees it:
	// no <script>, no event handlers, no javascript: URLs.
	private static HtmlSanitizer CreateSanitizer()
	{
		var sanitizer = new HtmlSanitizer();
		sanitizer.AllowedAttributes.Add("class");
		sanitizer.AllowedAttributes.Add("id");
		sanitizer.AllowedSchemes.Add("data");
		// Material Symbols icons are inline <svg><path d="..."/></svg> (no fonts, no network).
		sanitizer.AllowedTags.Add("svg");
		sanitizer.AllowedTags.Add("path");
		sanitizer.AllowedAttributes.Add("viewBox");
		sanitizer.AllowedAttributes.Add("d");
		sanitizer.AllowedAttributes.Add("aria-hidden");
		// Barcodes and charts: SVG shapes and text, geometry only (colors come from CSS classes).
		foreach (var tag in new[] { "g", "text", "line", "circle", "polyline" })
		{
			sanitizer.AllowedTags.Add(tag);
		}
		foreach (var attribute in new[] { "preserveAspectRatio", "x", "y", "x1", "y1", "x2", "y2", "cx", "cy", "r", "points", "text-anchor" })
		{
			sanitizer.AllowedAttributes.Add(attribute);
		}
		return sanitizer;
	}

	public async Task<ComposeResult> ComposeAsync(string templateHtml, string? css, JsonElement data, string? stamp = null)
	{
		if (stamp is not null && !IsValidStamp(stamp))
		{
			return ComposeResult.Failed(StampRule);
		}
		if (!_parser.TryParse(templateHtml, out var template, out var error))
		{
			return ComposeResult.Failed(error);
		}

		// The template's theme (designer: Theme...), applied over the brand stylesheet.
		var themeCss = string.Empty;
		if (ThemeOf(templateHtml) is { } theme)
		{
			if (await _themes.CssAsync(theme) is not { } themeRules)
			{
				return ComposeResult.Failed($"The template uses the theme '{theme}', which doesn't exist.");
			}
			themeCss = themeRules;
		}

		var language = DocumentLanguages.Of(templateHtml);
		var (context, contextError) = await CreateContextAsync(data, language);
		if (context is null)
		{
			return ComposeResult.Failed(contextError!);
		}

		// HtmlEncoder.Default encodes every {{ value }} so user-entered data (e.g. location notes) can't inject markup.
		string body;
		try
		{
			body = await template.RenderAsync(context, HtmlEncoder.Default);
		}
		catch (FileNotFoundException)
		{
			var missing = _clauses.Used(templateHtml, language).Where(u => u.Version is null).Select(u => "'" + u.Reference + "'").ToList();
			return ComposeResult.Failed(missing.Count > 0
				? $"Clause {string.Join(", ", missing)} has no published version (or the pinned version doesn't exist)."
				: "A clause the template includes could not be found.");
		}
		catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
		{
			// e.g. clauses including each other (too deep), or a clause whose Liquid doesn't parse.
			return ComposeResult.Failed("The template could not be rendered: " + ex.Message);
		}
		// Legacy form images/fonts are referenced by URL in templates; the renderer blocks network, so inline them.
		var safeBody = _sanitizer.Sanitize(_legacyAssets.InlineImages(body));
		var safeCss = StyleCloseTag().Replace((css ?? string.Empty) + "\n" + _clauses.CssFor(templateHtml, language), string.Empty);
		var legacyFonts = _legacyAssets.FontCss(family =>
			safeCss.Contains(family, StringComparison.Ordinal) || safeBody.Contains(family, StringComparison.Ordinal));

		var html = $"<!DOCTYPE html><html lang=\"{language.Code}\"><head><meta charset=\"utf-8\"><style>{_brandCss}{themeCss}{legacyFonts}{safeCss}</style></head>" +
			(stamp is null
				? $"<body>{safeBody}</body></html>"
				: $"<body class=\"doc-watermark-stamped\">{safeBody}{StampHtml(stamp)}</body></html>");
		return ComposeResult.Ok(html);
	}

	/// <summary>
	/// The template itself as one self-contained HTML file: the brand stylesheet, theme and the template's own CSS are inside it,
	/// shared clauses are copied in, and the {{ data }} and {% logic %} placeholders are left as they are, so the file
	/// can be handed to another system (or opened to see the layout) and filled with data there.
	/// </summary>
	public async Task<ComposeResult> ComposeTemplatePageAsync(string templateHtml, string? css)
	{
		var themeCss = string.Empty;
		if (ThemeOf(templateHtml) is { } theme)
		{
			if (await _themes.CssAsync(theme) is not { } themeRules)
			{
				return ComposeResult.Failed($"The template uses the theme '{theme}', which doesn't exist.");
			}
			themeCss = themeRules;
		}

		var language = DocumentLanguages.Of(templateHtml);
		var body = InlineClauses(templateHtml, language, 0, out var missing);
		if (missing.Count > 0)
		{
			return ComposeResult.Failed($"Clause {string.Join(", ", missing.Select(m => "'" + m + "'"))} has no published version (or the pinned version doesn't exist).");
		}

		var safeCss = StyleCloseTag().Replace((css ?? string.Empty) + "\n" + _clauses.CssFor(templateHtml, language), string.Empty);
		var legacyFonts = _legacyAssets.FontCss(family =>
			safeCss.Contains(family, StringComparison.Ordinal) || body.Contains(family, StringComparison.Ordinal));
		return ComposeResult.Ok($"<!DOCTYPE html><html lang=\"{language.Code}\"><head><meta charset=\"utf-8\"><style>{_brandCss}{themeCss}{legacyFonts}{safeCss}</style></head><body>{body}</body></html>");
	}

	private string InlineClauses(string html, DocumentLanguage language, int depth, out List<string> missing)
	{
		var notFound = new List<string>();
		var result = IncludeTag().Replace(html, m =>
		{
			var name = m.Groups["name"].Value;
			if (depth >= 5 || ClauseStore.Parse(name) is not { } reference || _clauses.Resolve(reference, language) is not { } clause)
			{
				notFound.Add(name);
				return string.Empty;
			}
			var inner = InlineClauses(clause.Html, language, depth + 1, out var innerMissing);
			notFound.AddRange(innerMissing);
			return inner;
		});
		missing = notFound;
		return result;
	}

	[GeneratedRegex(@"\{%-?\s*include\s+['""](?<name>[^'""]+)['""][^%]*-?%\}")]
	private static partial Regex IncludeTag();

	public const string StampRule = "A watermark is 1 to 40 letters, numbers, spaces, \".\" or \"-\".";

	/// <summary>The theme a template uses (its div.doc-theme marker), or null for the standard look.</summary>
	public static string? ThemeOf(string templateHtml) =>
		ThemeMarker().Match(templateHtml) is { Success: true } m ? m.Groups[1].Value : null;

	[GeneratedRegex("class=\"doc-theme theme-([A-Za-z0-9_-]{1,64})\"")]
	private static partial Regex ThemeMarker();

	/// <summary>Text that may be stamped over a document at render time (DRAFT, SPECIMEN, ...).</summary>
	public static bool IsValidStamp(string text) => StampText().IsMatch(text) && text.Trim().Length > 0;

	// Replaces the template's own watermark (brand CSS .doc-watermark-stamped).
	private static string StampHtml(string text) =>
		"<div class=\"doc-watermark doc-watermark-stamp wm-color-red wm-strength-medium wm-size-medium wm-angle-diagonal\">" +
		$"<span class=\"doc-watermark-text\">{HtmlEncoder.Default.Encode(text.Trim())}</span></div>";

	[GeneratedRegex(@"^[\p{L}\p{N} .\-]{1,40}$")]
	private static partial Regex StampText();

	/// <summary>
	/// Renders a Liquid fragment (e.g. a calculated field) with the data as plain text, for previews in the designer.
	/// The caller must treat the result as text, never as markup.
	/// </summary>
	public async Task<(string? Text, string? Error)> RenderTextAsync(string liquid, JsonElement data)
	{
		if (!_parser.TryParse(liquid, out var template, out var error)) return (null, error);
		var (context, contextError) = await CreateContextAsync(data);
		if (context is null) return (null, contextError);
		try
		{
			return ((await template.RenderAsync(context, NullEncoder.Default)).Trim(), null);
		}
		catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
		{
			return (null, ex.Message);
		}
	}

	private async Task<(TemplateContext? Context, string? Error)> CreateContextAsync(JsonElement data, DocumentLanguage? language = null)
	{
		var context = new TemplateContext(_languageOptions[(language ?? DocumentLanguages.English).Code]);
		if (data.ValueKind == JsonValueKind.Object)
		{
			// Vendor images arrive as "docimage:{blob}" references; embed them before Liquid sees the data.
			IReadOnlyDictionary<string, string> images;
			try
			{
				images = await _images.ResolveAsync(data);
			}
			catch (InvalidOperationException ex)
			{
				return (null, ex.Message);
			}
			foreach (var property in data.EnumerateObject())
			{
				context.SetValue(property.Name, ToPlainObject(property.Value, images));
			}
		}

		// Set last so message data can't replace brand assets.
		context.SetValue("brand", _brand);
		return (context, null);
	}

	private static ValueTask<FluidValue> Text(string value) => new(new StringValue(value));

	// The designer loads figtree.css with relative url()s; the PDF renderer blocks network requests, so the
	// same font files are inlined as data: URIs. Works without Figtree installed (e.g. the Linux container).
	private static string EmbedFonts(string cssPath)
	{
		var fontRoot = Path.GetDirectoryName(cssPath)!;
		return FontUrl().Replace(File.ReadAllText(cssPath), match =>
		{
			var file = Path.Combine(fontRoot, Path.GetFileName(match.Groups[1].Value));
			return "url(data:font/woff2;base64," + Convert.ToBase64String(File.ReadAllBytes(file)) + ")";
		});
	}

	// Fluid navigates dictionaries/lists natively, so JSON is converted to plain CLR shapes.
	// Image references are swapped for their resolved data: URIs. Values print the way DocGen's Word filler prints them:
	// numbers without trailing zeros (1200.0 => 1200, 1234.50 => 1234.5) and true/false as Yes/No.
	private static object? ToPlainObject(JsonElement element, IReadOnlyDictionary<string, string> images) => element.ValueKind switch
	{
		JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => ToPlainObject(p.Value, images)),
		JsonValueKind.Array => element.EnumerateArray().Select(e => ToPlainObject(e, images)).ToList(),
		JsonValueKind.String => images.TryGetValue(element.GetString()!, out var uri) ? uri : element.GetString(),
		JsonValueKind.Number => element.GetDecimal() / 1.000000000000000000000000000000000m,
		JsonValueKind.True => YesNoValue.Yes,
		JsonValueKind.False => YesNoValue.No,
		_ => null
	};

	[GeneratedRegex("</style", RegexOptions.IgnoreCase)]
	private static partial Regex StyleCloseTag();

	[GeneratedRegex(@"url\(([^)]+\.woff2)\)")]
	private static partial Regex FontUrl();
}

public sealed record ComposeResult(string? Html, string? Error)
{
	public static ComposeResult Ok(string html) => new(html, null);
	public static ComposeResult Failed(string error) => new(null, error);
}
