using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Filters;
using UglyToad.PdfPig.Tokens;

namespace FaCT.DocDesigner.POC.Import;

/// <summary>
/// The font programs embedded in a PDF (TrueType FontFile2, OpenType FontFile3), offered as web fonts for the text they
/// print. A font is used only when <see cref="TrueTypeFont"/> finds outlines and a Unicode map covering all of that
/// font's characters on the page; otherwise the text keeps its standard family. Type 1 and bare CFF fonts aren't used
/// (browsers can't load them). Each program becomes its own family (F_{name}_{hash}): PDF fonts are often subsets, so
/// two documents' "Arial" can hold different glyphs.
/// </summary>
public sealed partial class EmbeddedFonts(PdfDocument document)
{
	public const int MaxFontBytes = 10 * 1024 * 1024;

	private readonly Dictionary<string, byte[]?> _programs = new(StringComparer.Ordinal);
	private readonly Dictionary<string, TrueTypeFont?> _parsed = new(StringComparer.Ordinal);
	private readonly Dictionary<string, (string Family, string Weight, string Style, byte[] Data)> _used = new(StringComparer.Ordinal);
	private readonly HashSet<string> _rejected = new(StringComparer.Ordinal);

	/// <summary>Fonts whose program is used by some text.</summary>
	public int Used => _used.Count;

	/// <summary>Embedded fonts that couldn't be used (no Unicode map, or characters missing).</summary>
	public int Rejected => _rejected.Count(name => !_used.ContainsKey(name));

	/// <summary>Finds the embedded programs of the fonts in a page's resources (once per font name).</summary>
	public void Collect(Page page)
	{
		if (!page.Dictionary.TryGet(NameToken.Create("Resources"), out var resourcesToken)) return;
		var resources = Get<DictionaryToken>(resourcesToken);
		if (resources is null || !resources.TryGet(NameToken.Create("Font"), out var fontsToken)) return;
		var fonts = Get<DictionaryToken>(fontsToken);
		if (fonts is null) return;

		foreach (var entry in fonts.Data.Values)
		{
			try
			{
				var font = Get<DictionaryToken>(entry);
				if (font is null || !font.TryGet(NameToken.Create("BaseFont"), out var baseToken)) continue;
				if (Get<NameToken>(baseToken) is not { } baseName || _programs.ContainsKey(baseName.Data)) continue;
				_programs[baseName.Data] = Program(font);
			}
			catch (Exception ex) when (ex is not OutOfMemoryException)
			{
				// A damaged font entry: its text uses a standard family.
			}
		}
	}

	/// <summary>The object a token stands for: indirect references are followed (a few levels at most).</summary>
	private T? Get<T>(IToken? token) where T : class, IToken
	{
		for (var depth = 0; depth < 8 && token is IndirectReferenceToken reference; depth++)
		{
			token = document.Structure.GetObject(reference.Data)?.Data;
		}
		return token as T;
	}

	/// <summary>The web font family for text in <paramref name="fontName"/>, or null to use a standard family.</summary>
	public string? Resolve(string fontName, string characters)
	{
		if (!_programs.TryGetValue(fontName, out var data) || data is null) return null;
		if (!_parsed.TryGetValue(fontName, out var font)) _parsed[fontName] = font = TrueTypeFont.TryRead(data);
		if (font is null || !font.Covers(characters))
		{
			_rejected.Add(fontName);
			return null;
		}
		if (_used.TryGetValue(fontName, out var known)) return known.Family;

		var (_, bold, italic) = PdfImporter.MapFont(fontName);
		var name = fontName.Length > 7 && fontName[6] == '+' ? fontName[7..] : fontName;
		name = NonName().Replace(name, "_").Trim('_');
		if (name.Length > 40) name = name[..40];
		var family = "F_" + (name.Length > 0 ? name + "_" : "") + Convert.ToHexString(SHA256.HashData(data))[..8];
		_used[fontName] = (family, bold ? "bold" : "normal", italic ? "italic" : "normal", data);
		return family;
	}

	/// <summary>@font-face rules (base64 TrueType) for the fonts in use, as a style block.</summary>
	public string FaceCss()
	{
		if (_used.Count == 0) return string.Empty;
		var css = new StringBuilder("<style>");
		foreach (var (family, weight, style, data) in _used.Values)
		{
			css.Append("@font-face { font-family: '").Append(family).Append("'; font-weight: ").Append(weight)
				.Append("; font-style: ").Append(style).Append("; src: url(data:font/ttf;base64,")
				.Append(Convert.ToBase64String(data)).Append(") }\n");
		}
		return css.Append("</style>").ToString();
	}

	private byte[]? Program(DictionaryToken font)
	{
		var owner = font;
		// Composite (Type0) fonts keep the program on their descendant CIDFont.
		if (font.TryGet(NameToken.Create("DescendantFonts"), out var descendantsToken) &&
			Get<ArrayToken>(descendantsToken) is { Length: > 0 } descendants)
		{
			owner = Get<DictionaryToken>(descendants[0]) ?? font;
		}
		if (!owner.TryGet(NameToken.Create("FontDescriptor"), out var descriptorToken)) return null;
		var descriptor = Get<DictionaryToken>(descriptorToken);
		if (descriptor is null) return null;

		StreamToken? stream = null;
		if (descriptor.TryGet(NameToken.Create("FontFile2"), out var trueType))
		{
			stream = Get<StreamToken>(trueType);
		}
		else if (descriptor.TryGet(NameToken.Create("FontFile3"), out var fontFile3) &&
			Get<StreamToken>(fontFile3) is { } candidate &&
			candidate.StreamDictionary.TryGet(NameToken.Create("Subtype"), out var subtype) &&
			Get<NameToken>(subtype) is { Data: "OpenType" })
		{
			stream = candidate;
		}
		if (stream is null) return null;

		var bytes = stream.Decode(DefaultFilterProvider.Instance).ToArray();
		return bytes.Length is > 0 and <= MaxFontBytes ? bytes : null;
	}

	[GeneratedRegex("[^A-Za-z0-9_]+")]
	private static partial Regex NonName();
}
