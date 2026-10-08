using System.Globalization;
using System.Text.RegularExpressions;

namespace FaCT.DocDesigner.POC.Templates;

/// <summary>
/// A language a template or clause can be written in. <see cref="Culture"/> is used when the document prints values
/// (month names, Yes/No, numbers without an explicit format language).
/// </summary>
public sealed record DocumentLanguage(string Code, string Name, string Culture, string Yes, string No, string Page, string Of, string NoData)
{
	public CultureInfo CultureInfo => CultureInfo.GetCultureInfo(Culture);
}

/// <summary>
/// Language variants: a template (or clause) is written in English and may have versions in other languages that use
/// the same data fields. Each language has its own Draft -> Published -> Retired versions
/// (<see cref="TemplateStore.Language"/>). A variant's HTML carries a hidden marker, <c>div.doc-language.lang-es</c>, so
/// the document prints in its language wherever it is rendered (preview, publish, diff, scenarios).
/// </summary>
public static partial class DocumentLanguages
{
	public static readonly DocumentLanguage English = new("en", "English", "en-US", "Yes", "No", "Page", "of", "No data");

	public static readonly IReadOnlyList<DocumentLanguage> All =
	[
		English,
		new("es", "Spanish", "es-US", "S\u00ed", "No", "P\u00e1gina", "de", "Sin datos"),
		new("fr", "French (Canada)", "fr-CA", "Oui", "Non", "Page", "de", "Aucune donn\u00e9e")
	];

	/// <summary>The language for a code; null or empty (or "en") is English.</summary>
	public static DocumentLanguage? Find(string? code) =>
		string.IsNullOrEmpty(code) ? English : All.FirstOrDefault(l => l.Code == code);

	/// <summary>Whether <paramref name="code"/> is English (the language every template starts in).</summary>
	public static bool IsBase(string? code) => string.IsNullOrEmpty(code) || code == English.Code;

	public static string UnknownLanguage(string? code) =>
		$"'{code}' isn't a document language ({string.Join(", ", All.Select(l => l.Code + " = " + l.Name))}).";

	/// <summary>The language a template's HTML is written in (its marker), English when it has none.</summary>
	public static DocumentLanguage Of(string? html) =>
		html is not null && Marker().Match(html) is { Success: true } m && Find(m.Groups[1].Value) is { } language ? language : English;

	/// <summary>The marker the designer writes (and the server keeps in step with the language a version is saved in).</summary>
	public static string MarkerHtml(DocumentLanguage language) => $"<div class=\"doc-language lang-{language.Code}\"></div>";

	/// <summary>
	/// The HTML with exactly the marker of <paramref name="language"/> (none for English): any other marker is removed,
	/// so a version always says the language it is stored under.
	/// </summary>
	public static string WithMarker(string html, DocumentLanguage language)
	{
		var clean = MarkerElement().Replace(html, string.Empty);
		if (language == English) return clean;
		var body = BodyOpen().Match(clean);
		return body.Success
			? clean.Insert(body.Index + body.Length, MarkerHtml(language))
			: MarkerHtml(language) + clean;
	}

	/// <summary>The HTML without any language marker (clauses: the template they are placed in decides the language).</summary>
	public static string WithoutMarker(string html) => MarkerElement().Replace(html, string.Empty);

	[GeneratedRegex("class=\"doc-language lang-([a-z]{2})\"")]
	private static partial Regex Marker();

	[GeneratedRegex("<div[^>]*class=\"doc-language lang-[a-z]{2}\"[^>]*>\\s*</div>")]
	private static partial Regex MarkerElement();

	[GeneratedRegex("<body[^>]*>", RegexOptions.IgnoreCase)]
	private static partial Regex BodyOpen();
}
