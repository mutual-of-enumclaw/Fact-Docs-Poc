using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FaCT.DocDesigner.POC.Templates;

namespace FaCT.DocDesigner.POC.Themes;

/// <summary>
/// A brand theme (per brand or line of business): overrides of the brand stylesheet's color tokens and its body and
/// heading fonts. Templates choose one; templates without one use the standard MOE look.
/// </summary>
public sealed record Theme(
	string Name,
	string Label,
	string? Description,
	IReadOnlyDictionary<string, string> Colors,
	string? BodyFont,
	string? HeadingFont,
	DateTimeOffset SavedUtc,
	string? SavedBy);

/// <summary>An uploaded font file (one weight/style of a family).</summary>
public sealed record ThemeFont(string Id, string Family, int Weight, string Style, string Format, long Bytes, DateTimeOffset UploadedUtc, string? UploadedBy);

/// <summary>A color token of the brand stylesheet (":root { --moe-green: #144835 }") and its standard value.</summary>
public sealed record ThemeToken(string Name, string Default);

public sealed record ThemeInput(string? Label, string? Description, Dictionary<string, string>? Colors, string? BodyFont, string? HeadingFont);

/// <summary>Themes (one JSON file each) and uploaded fonts (fonts/{id}.{ext} + fonts/fonts.json) in {root}.</summary>
public sealed partial class ThemeStore
{
	/// <summary>The standard MOE look: not stored, can't be changed or deleted.</summary>
	public const string StandardTheme = "moe";

	public const int MaxThemes = 50;
	public const int MaxFonts = 100;
	public const int MaxFontBytes = 5 * 1024 * 1024;

	/// <summary>Fonts every renderer machine has (Figtree is embedded by the brand stylesheet).</summary>
	public static readonly IReadOnlyList<string> SystemFonts = ["Figtree", "Segoe UI", "Arial", "Calibri", "Georgia", "Times New Roman", "Verdana"];

	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
	private static readonly string[] ReservedNames = [StandardTheme, "fonts"];

	private readonly SemaphoreSlim _writeLock = new(1, 1);
	private readonly string _root;
	private readonly string _fontRoot;

	public ThemeStore(string root, string brandCssPath)
	{
		_root = root;
		_fontRoot = Path.Combine(root, "fonts");
		Directory.CreateDirectory(_fontRoot);
		ColorTokens = ColorToken().Matches(File.ReadAllText(brandCssPath))
			.Select(m => new ThemeToken(m.Groups[1].Value, m.Groups[2].Value.ToLowerInvariant()))
			.DistinctBy(t => t.Name)
			.ToList();
	}

	public IReadOnlyList<ThemeToken> ColorTokens { get; }

	public static bool IsValidName(string name) =>
		TemplateStore.IsValidName(name) && !ReservedNames.Contains(name, StringComparer.OrdinalIgnoreCase);

	public static bool IsValidFamily(string family) => FamilyName().IsMatch(family);

	// ---- Themes ------------------------------------------------------------------------------------------------------

	public async Task<IReadOnlyList<Theme>> ListAsync()
	{
		var themes = new List<Theme>();
		foreach (var file in Directory.EnumerateFiles(_root, "*.json"))
		{
			if (!IsValidName(Path.GetFileNameWithoutExtension(file))) continue;
			if (await ReadAsync(file) is { } theme) themes.Add(theme);
		}
		return themes.OrderBy(t => t.Label, StringComparer.OrdinalIgnoreCase).ToList();
	}

	public async Task<Theme?> GetAsync(string name) =>
		IsValidName(name) && File.Exists(PathFor(name)) ? await ReadAsync(PathFor(name)) : null;

	/// <summary>Adds or replaces a theme. Returns the error when the input isn't valid.</summary>
	public async Task<(Theme? Theme, string? Error)> SaveAsync(string name, ThemeInput input, string? user)
	{
		if (!IsValidName(name)) return (null, "Theme names may contain letters, numbers, \"-\" and \"_\" (\"moe\" and \"fonts\" are taken).");
		var label = input.Label?.Trim() ?? string.Empty;
		if (label.Length is 0 or > 60) return (null, "Give the theme a name of up to 60 characters.");
		var description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
		if (description is { Length: > 200 }) return (null, "The description is at most 200 characters.");

		var colors = new SortedDictionary<string, string>(StringComparer.Ordinal);
		foreach (var (token, value) in input.Colors ?? [])
		{
			if (ColorTokens.All(t => t.Name != token)) return (null, $"'{token}' is not a brand color.");
			if (!HexColor().IsMatch(value ?? string.Empty)) return (null, $"The color for {token} must look like #1A2B3C.");
			colors[token] = value!.ToLowerInvariant();
		}

		var fonts = await ListFontsAsync();
		string? Font(string? family) => string.IsNullOrWhiteSpace(family) ? null : family.Trim();
		var body = Font(input.BodyFont);
		var heading = Font(input.HeadingFont);
		foreach (var family in new[] { body, heading }.OfType<string>())
		{
			if (!SystemFonts.Contains(family, StringComparer.Ordinal) && fonts.All(f => f.Family != family))
			{
				return (null, $"The font '{family}' isn't available. Upload it first.");
			}
		}

		await _writeLock.WaitAsync();
		try
		{
			var path = PathFor(name);
			if (!File.Exists(path) && Directory.EnumerateFiles(_root, "*.json").Count() >= MaxThemes)
			{
				return (null, $"There can be at most {MaxThemes} themes.");
			}
			var theme = new Theme(name, label, description, colors, body, heading, DateTimeOffset.UtcNow, user);
			await AtomicFile.WriteAllTextAsync(path, JsonSerializer.Serialize(theme, JsonOptions));
			return (theme, null);
		}
		finally
		{
			_writeLock.Release();
		}
	}

	public async Task<bool> DeleteAsync(string name)
	{
		if (!IsValidName(name)) return false;
		await _writeLock.WaitAsync();
		try
		{
			var path = PathFor(name);
			if (!File.Exists(path)) return false;
			File.Delete(path);
			return true;
		}
		finally
		{
			_writeLock.Release();
		}
	}

	/// <summary>
	/// The stylesheet that turns the standard look into the theme's: @font-face for its uploaded fonts (inline data, the
	/// PDF renderer allows no network) and the token overrides. Empty for the standard theme; null if it doesn't exist.
	/// </summary>
	public async Task<string?> CssAsync(string name)
	{
		if (string.Equals(name, StandardTheme, StringComparison.OrdinalIgnoreCase)) return string.Empty;
		if (await GetAsync(name) is not { } theme) return null;

		var css = new StringBuilder();
		var uploaded = await ListFontsAsync();
		foreach (var font in uploaded.Where(f => f.Family == theme.BodyFont || f.Family == theme.HeadingFont))
		{
			var file = FontPath(font);
			if (!File.Exists(file)) continue;
			css.Append("@font-face{font-family:\"").Append(font.Family).Append("\";src:url(data:")
				.Append(MimeOf(font.Format)).Append(";base64,").Append(Convert.ToBase64String(await File.ReadAllBytesAsync(file)))
				.Append(") format(\"").Append(font.Format).Append("\");font-weight:").Append(font.Weight)
				.Append(";font-style:").Append(font.Style).Append(";}\n");
		}
		css.Append(":root{");
		foreach (var (token, value) in theme.Colors)
		{
			css.Append("--").Append(token).Append(':').Append(value).Append(';');
		}
		if (theme.BodyFont is not null) css.Append("--moe-font-sans:").Append(Stack(theme.BodyFont)).Append(';');
		if (theme.HeadingFont is not null) css.Append("--moe-font-heading:").Append(Stack(theme.HeadingFont)).Append(';');
		css.Append("}\n");
		return css.ToString();
	}

	private static string Stack(string family) => $"\"{family}\", \"Segoe UI\", Arial, sans-serif";

	// ---- Fonts -------------------------------------------------------------------------------------------------------

	public async Task<IReadOnlyList<ThemeFont>> ListFontsAsync()
	{
		var index = Path.Combine(_fontRoot, "fonts.json");
		if (!File.Exists(index)) return [];
		await using var stream = File.OpenRead(index);
		return await JsonSerializer.DeserializeAsync<List<ThemeFont>>(stream, JsonOptions) ?? [];
	}

	/// <summary>Stores an uploaded font file (WOFF2, WOFF, TTF or OTF, checked by its first bytes).</summary>
	public async Task<(ThemeFont? Font, string? Error)> AddFontAsync(byte[] data, string? family, int weight, string? style, string? user)
	{
		family = family?.Trim() ?? string.Empty;
		if (!IsValidFamily(family)) return (null, "Font family names may contain letters, numbers, spaces and \"-\" (at most 60).");
		if (SystemFonts.Contains(family, StringComparer.OrdinalIgnoreCase)) return (null, $"'{family}' is already available.");
		if (weight is < 100 or > 900 || weight % 100 != 0) return (null, "The weight must be 100, 200, ... 900.");
		style = string.IsNullOrEmpty(style) ? "normal" : style;
		if (style is not ("normal" or "italic")) return (null, "The style must be normal or italic.");
		if (data.Length == 0 || data.Length > MaxFontBytes) return (null, $"The font file must be at most {MaxFontBytes / 1024 / 1024} MB.");
		if (FormatOf(data) is not { } format) return (null, "That isn't a font file (WOFF2, WOFF, TTF or OTF).");

		await _writeLock.WaitAsync();
		try
		{
			var fonts = (await ListFontsAsync()).ToList();
			var existing = fonts.FirstOrDefault(f => f.Family == family && f.Weight == weight && f.Style == style);
			if (existing is null && fonts.Count >= MaxFonts) return (null, $"There can be at most {MaxFonts} fonts.");
			var font = new ThemeFont(Guid.NewGuid().ToString("N")[..12], family, weight, style, format, data.Length, DateTimeOffset.UtcNow, user);
			await File.WriteAllBytesAsync(FontPath(font), data);
			if (existing is not null)
			{
				fonts.Remove(existing);
				File.Delete(FontPath(existing));
			}
			fonts.Add(font);
			await WriteFontsAsync(fonts);
			return (font, null);
		}
		finally
		{
			_writeLock.Release();
		}
	}

	public async Task<ThemeFont?> GetFontAsync(string id) => (await ListFontsAsync()).FirstOrDefault(f => f.Id == id);

	/// <summary>Removes a font file; false when it doesn't exist.</summary>
	public async Task<bool> DeleteFontAsync(string id)
	{
		await _writeLock.WaitAsync();
		try
		{
			var fonts = (await ListFontsAsync()).ToList();
			var font = fonts.FirstOrDefault(f => f.Id == id);
			if (font is null) return false;
			fonts.Remove(font);
			await WriteFontsAsync(fonts);
			if (File.Exists(FontPath(font))) File.Delete(FontPath(font));
			return true;
		}
		finally
		{
			_writeLock.Release();
		}
	}

	/// <summary>Themes using a font family.</summary>
	public async Task<IReadOnlyList<string>> ThemesUsingFamilyAsync(string family) =>
		(await ListAsync()).Where(t => t.BodyFont == family || t.HeadingFont == family).Select(t => t.Name).ToList();

	public string FontPath(ThemeFont font) => Path.Combine(_fontRoot, font.Id + "." + ExtensionOf(font.Format));

	/// <summary>The CSS format name for a font file's first bytes, or null if it isn't a font.</summary>
	public static string? FormatOf(byte[] data)
	{
		if (data.Length < 12) return null;
		var tag = Encoding.ASCII.GetString(data, 0, 4);
		return tag switch
		{
			"wOF2" => "woff2",
			"wOFF" => "woff",
			"OTTO" => "opentype",
			"true" => "truetype",
			_ when data[0] == 0 && data[1] == 1 && data[2] == 0 && data[3] == 0 => "truetype",
			_ => null
		};
	}

	public static string MimeOf(string format) => format switch
	{
		"woff2" => "font/woff2",
		"woff" => "font/woff",
		"opentype" => "font/otf",
		_ => "font/ttf"
	};

	private static string ExtensionOf(string format) => format switch
	{
		"woff2" => "woff2",
		"woff" => "woff",
		"opentype" => "otf",
		_ => "ttf"
	};

	private Task WriteFontsAsync(List<ThemeFont> fonts) =>
		AtomicFile.WriteAllTextAsync(Path.Combine(_fontRoot, "fonts.json"),
			JsonSerializer.Serialize(fonts.OrderBy(f => f.Family).ThenBy(f => f.Weight).ThenBy(f => f.Style).ToList(), JsonOptions));

	private static async Task<Theme?> ReadAsync(string file)
	{
		await using var stream = File.OpenRead(file);
		return await JsonSerializer.DeserializeAsync<Theme>(stream, JsonOptions);
	}

	// Names are allow-listed so a theme file can never be written outside the folder.
	private string PathFor(string name) => Path.Combine(_root, name + ".json");

	[GeneratedRegex(@"--(moe-[a-z0-9-]+)\s*:\s*(#[0-9A-Fa-f]{6})\b")]
	private static partial Regex ColorToken();

	[GeneratedRegex("^#([0-9A-Fa-f]{3}|[0-9A-Fa-f]{6})$")]
	private static partial Regex HexColor();

	[GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 \-]{0,59}$")]
	private static partial Regex FamilyName();
}
