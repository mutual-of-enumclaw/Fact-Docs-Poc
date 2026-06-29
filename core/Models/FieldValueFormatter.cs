using System.Globalization;
using System.Text.RegularExpressions;

namespace FapPdfTools.Server.Models;

/// <summary>
/// Applies a per-field <see cref="FieldOverride.Format"/> to a raw value at render time, so values
/// from the policy map OR user input render consistently (money / date / percent). Text/no-format
/// values pass through unchanged.
/// </summary>
public static class FieldValueFormatter
{
	public static string Apply(string value, FieldOverride ov)
	{
		if (ov == null || string.IsNullOrWhiteSpace(ov.Format) || string.IsNullOrEmpty(value))
			return value;

		string format = ov.Format.Trim().ToLowerInvariant();
		switch (format)
		{
			case "money":
			case "number":
			{
				if (TryParseNumber(value, out decimal d))
				{
					int dec = ov.Decimals ?? (format == "money" ? 2 : 0);
					string num = d.ToString("N" + dec, CultureInfo.InvariantCulture);
					return (ov.Prefix ?? string.Empty) + num;
				}
				return value;
			}
			case "percent":
			{
				if (TryParseNumber(value, out decimal p))
				{
					int dec = ov.Decimals ?? 0;
					return p.ToString("N" + dec, CultureInfo.InvariantCulture) + "%";
				}
				return value;
			}
			case "date":
			{
				if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dt))
					return dt.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture);
				return value;
			}
			default:
				return value;
		}
	}

	// Parse a number from a value that may already carry separators / currency symbols.
	private static bool TryParseNumber(string value, out decimal result)
	{
		string cleaned = Regex.Replace(value, @"[^0-9.\-]", "");
		return decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out result);
	}
}
