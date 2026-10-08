using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FaCT.DocDesigner.POC.Legacy;

/// <summary>
/// Fonts and images pulled out of converted legacy forms. Each file is stored once by content hash and shared by
/// every template, so templates hold short URLs instead of megabytes of base64.
/// The designer canvas loads them by URL; the PDF composer inlines them as data: URIs (the renderer blocks network).
/// </summary>
public sealed partial class LegacyAssetStore
{
	public const string UrlPrefix = "/api/legacy/assets/";

	private static readonly Dictionary<string, string> ContentTypes = new()
	{
		["png"] = "image/png",
		["jpg"] = "image/jpeg",
		["gif"] = "image/gif",
		["ttf"] = "font/ttf"
	};

	private readonly string _root;
	private readonly string _registryPath;
	private readonly Lock _lock = new();
	private readonly ConcurrentDictionary<string, string> _base64 = new();
	private Dictionary<string, LegacyFont> _fonts;

	public sealed record LegacyFont(string Family, string Weight, string Style, string File);

	public LegacyAssetStore(IWebHostEnvironment environment)
	{
		_root = Path.Combine(environment.ContentRootPath, "App_Data", "legacy-assets");
		Directory.CreateDirectory(_root);
		_registryPath = Path.Combine(_root, "fonts.json");
		var fonts = File.Exists(_registryPath)
			? JsonSerializer.Deserialize<List<LegacyFont>>(File.ReadAllText(_registryPath)) ?? []
			: [];
		_fonts = fonts.Where(IsValid).ToDictionary(Key);
	}

	public static bool IsValidFamily(string family) => FamilyPattern().IsMatch(family);

	public IReadOnlySet<string> FontFamilies
	{
		get { lock (_lock) return _fonts.Values.Select(f => f.Family).ToHashSet(); }
	}

	/// <summary>Stores the bytes (if new) and returns the asset file name: {hash}.{ext}.</summary>
	public string Save(byte[] bytes, string extension)
	{
		var name = Convert.ToHexString(SHA256.HashData(bytes))[..32].ToLowerInvariant() + "." + extension;
		var path = Path.Combine(_root, name);
		if (!File.Exists(path))
		{
			File.WriteAllBytes(path, bytes);
		}
		return name;
	}

	/// <summary>Registers a font face. The first registration of a family/weight/style wins, so forms share one file.</summary>
	public bool RegisterFont(string family, string weight, string style, byte[] ttf)
	{
		var font = new LegacyFont(family, weight, style, Save(ttf, "ttf"));
		if (!IsValid(font)) return false;
		lock (_lock)
		{
			if (_fonts.ContainsKey(Key(font))) return true;
			_fonts = new Dictionary<string, LegacyFont>(_fonts) { [Key(font)] = font };
			File.WriteAllText(_registryPath, JsonSerializer.Serialize(_fonts.Values.OrderBy(Key).ToList()));
		}
		return true;
	}

	public bool TryGetFile(string file, out string path, out string contentType)
	{
		path = string.Empty;
		contentType = string.Empty;
		var match = AssetName().Match(file);
		if (!match.Success) return false;
		path = Path.Combine(_root, file);
		contentType = ContentTypes[match.Groups[1].Value];
		return File.Exists(path);
	}

	/// <summary>
	/// @font-face rules for the registered fonts. Without <paramref name="isUsed"/>: every font, by URL (designer canvas).
	/// With it: only the families the document uses, inlined as data: URIs (PDF render).
	/// </summary>
	public string FontCss(Func<string, bool>? isUsed = null)
	{
		List<LegacyFont> fonts;
		lock (_lock) fonts = _fonts.Values.OrderBy(Key).ToList();

		var css = new StringBuilder();
		foreach (var font in fonts)
		{
			if (isUsed is not null && !isUsed(font.Family)) continue;
			var src = isUsed is null ? UrlPrefix + font.File : "data:font/ttf;base64," + Base64(font.File);
			css.Append("@font-face{font-family:'").Append(font.Family).Append("';font-weight:").Append(font.Weight)
				.Append(";font-style:").Append(font.Style).Append(";src:url(").Append(src).Append(") format('truetype');}\n");
		}
		return css.ToString();
	}

	/// <summary>Replaces image asset URLs with data: URIs. Unknown files are left as-is (and blocked by the renderer).</summary>
	public string InlineImages(string html) => ImageUrl().Replace(html, match =>
	{
		var file = match.Groups[1].Value;
		return TryGetFile(file, out _, out var contentType)
			? "data:" + contentType + ";base64," + Base64(file)
			: match.Value;
	});

	private string Base64(string file) =>
		_base64.GetOrAdd(file, f => Convert.ToBase64String(File.ReadAllBytes(Path.Combine(_root, f))));

	private static string Key(LegacyFont font) => font.Family + "|" + font.Weight + "|" + font.Style;

	// The values are written into CSS, so only known-safe shapes are accepted.
	private static bool IsValid(LegacyFont font) =>
		IsValidFamily(font.Family) && WeightPattern().IsMatch(font.Weight) && StylePattern().IsMatch(font.Style) && AssetName().IsMatch(font.File);

	[GeneratedRegex(@"^F_[A-Za-z0-9_]{1,60}$")]
	private static partial Regex FamilyPattern();

	[GeneratedRegex(@"^(normal|bold|[1-9]00)$")]
	private static partial Regex WeightPattern();

	[GeneratedRegex(@"^(normal|italic|oblique)$")]
	private static partial Regex StylePattern();

	[GeneratedRegex(@"^[a-f0-9]{32}\.(png|jpg|gif|ttf)$")]
	private static partial Regex AssetName();

	[GeneratedRegex(@"/api/legacy/assets/([a-f0-9]{32}\.(?:png|jpg|gif))")]
	private static partial Regex ImageUrl();
}
