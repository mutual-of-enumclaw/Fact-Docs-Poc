using System.Globalization;
using System.Text;

namespace FaCT.DocDesigner.POC.Rendering;

/// <summary>
/// Custom formats for data fields: .NET format strings ("#,##0.00;(#,##0.00);'None'", "MMMM d, yyyy") with an optional
/// culture, and masks ("(###) ###-####"). The format filter follows DocGen's Word filler rule for rule (numbers via
/// decimal.ToString, text that reads as a date via DateTime.ToString, other text unchanged, "upper" / "lower"), so a
/// format prints the same in the designer's PDF and in a Word template DocGen fills.
/// </summary>
public static class ValueFormats
{
	public const string DefaultCulture = "en-US";
	public const int MaxFormatLength = 100;

	/// <summary>Cultures a format can use: US English plus the Spanish and Canadian variants MOE documents may need.</summary>
	public static readonly IReadOnlyList<string> Cultures = ["en-US", "es-US", "es-MX", "en-CA", "fr-CA"];

	private static readonly CultureInfo Us = CultureInfo.GetCultureInfo(DefaultCulture);

	/// <summary>Why a format or mask can't be used, or null. Braces and double quotes would break the Liquid and Word tokens.</summary>
	public static string? FormatProblem(string? format)
	{
		if (string.IsNullOrWhiteSpace(format) || format.Length > MaxFormatLength) return $"A format is 1 to {MaxFormatLength} characters.";
		if (format.IndexOfAny(['{', '}', '"']) >= 0) return "A format can't contain { } or \" (use 'single quotes' around text).";
		try
		{
			1234.5m.ToString(format, Us);
			return null;
		}
		catch (FormatException)
		{
			try
			{
				new DateTime(2026, 7, 1).ToString(format, Us);
				return null;
			}
			catch (FormatException)
			{
				return $"'{format}' isn't a number or date format.";
			}
		}
	}

	public static string? CultureProblem(string? culture) =>
		culture is null || Cultures.Contains(culture) ? null : $"The language '{culture}' isn't one of {string.Join(", ", Cultures)}.";

	public static string? MaskProblem(string? mask)
	{
		if (string.IsNullOrWhiteSpace(mask) || mask.Length > 40) return "A mask is 1 to 40 characters.";
		if (mask.IndexOfAny(['{', '}', '"']) >= 0) return "A mask can't contain { } or \".";
		if (!mask.Any(c => c is '#' or '*')) return "A mask needs at least one # (a digit) or * (a hidden digit).";
		return null;
	}

	public static string FormatNumber(decimal value, string format, string? culture = null) =>
		value.ToString(format, Culture(culture));

	/// <summary>Text: "upper" / "lower", or a date format when the text reads as a date (as DocGen does); otherwise unchanged.</summary>
	public static string FormatText(string text, string format, string? culture = null)
	{
		if (string.Equals(format, "upper", StringComparison.OrdinalIgnoreCase)) return text.ToUpper(Culture(culture));
		if (string.Equals(format, "lower", StringComparison.OrdinalIgnoreCase)) return text.ToLower(Culture(culture));
		return DateTime.TryParse(text, Us, DateTimeStyles.None, out var date) ? date.ToString(format, Culture(culture)) : text;
	}

	/// <summary>
	/// Fills the mask's # with the value's digits in order and * with a hidden digit; other characters are printed as they
	/// are. A value with a different number of digits than the mask expects is printed unchanged, so nothing is lost.
	/// </summary>
	public static string Mask(string text, string mask)
	{
		var digits = text.Where(char.IsAsciiDigit).ToArray();
		if (digits.Length != mask.Count(c => c is '#' or '*')) return text;
		var output = new StringBuilder(mask.Length);
		var next = 0;
		foreach (var c in mask)
		{
			output.Append(c switch
			{
				'#' => digits[next++],
				'*' => Hidden(ref next),
				_ => c
			});
		}
		return output.ToString();
	}

	private static char Hidden(ref int next)
	{
		next++;
		return '*';
	}

	private static CultureInfo Culture(string? culture) =>
		culture is not null && Cultures.Contains(culture) ? CultureInfo.GetCultureInfo(culture) : Us;
}
