using System.Text.RegularExpressions;

namespace FaCT.DocDesigner.POC.Templates;

/// <summary>
/// What a template is, for the template library: its printed title, form number and edition, the kind of document and
/// the library category it is listed under. Saved with each version (a new edition is a new version). All optional.
/// </summary>
public sealed partial record TemplateDetails(
	string? Title = null,
	string? FormCode = null,
	string? Edition = null,
	string? Type = null,
	string? Category = null)
{
	public const int MaxTitle = 120;
	public const int MaxFormCode = 24;
	public const int MaxCategory = 40;

	/// <summary>Kinds of document the designer makes.</summary>
	public static readonly IReadOnlyList<string> Types = ["Policy form", "Correspondence", "Notice", "Report"];

	/// <summary>Where a policy form is listed in the library (the forms schedule groups them the same way).</summary>
	public static readonly IReadOnlyList<string> Categories =
		["Declarations", "Coverage forms", "Endorsements", "Exclusions", "State amendatory", "Notices", "Letters", "Reports", "Other"];

	/// <summary>Why the details can't be saved, or null when they are fine.</summary>
	public string? Problem()
	{
		if (Title is { Length: > MaxTitle }) return $"The title can be at most {MaxTitle} characters.";
		if (FormCode is { Length: > 0 } code && (code.Length > MaxFormCode || !FormCodePattern().IsMatch(code)))
		{
			return $"The form code may contain letters, numbers, spaces, \"-\" and \".\" (at most {MaxFormCode}), e.g. CA 00 01 or MCP 1001.";
		}
		if (Edition is { Length: > 0 } edition && !EditionPattern().IsMatch(edition))
		{
			return "The edition is a month and year: MM/YY or MM YY, e.g. 10/26.";
		}
		if (Type is { Length: > 0 } type && !Types.Contains(type)) return "The type must be one of: " + string.Join(", ", Types) + ".";
		if (Category is { Length: > 0 } category && !Categories.Contains(category))
		{
			return "The category must be one of: " + string.Join(", ", Categories) + ".";
		}
		return null;
	}

	/// <summary>Trimmed, with empty values dropped; null when nothing is set.</summary>
	public TemplateDetails? Normalized()
	{
		var details = new TemplateDetails(Clean(Title), Clean(FormCode), Clean(Edition), Clean(Type), Clean(Category));
		return details == new TemplateDetails() ? null : details;
	}

	private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

	[GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 .\-]*$")]
	private static partial Regex FormCodePattern();

	[GeneratedRegex(@"^(0[1-9]|1[0-2])[/ ][0-9]{2}$")]
	private static partial Regex EditionPattern();
}
