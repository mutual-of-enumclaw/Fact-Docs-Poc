using Fluid;
using Fluid.Values;

namespace FaCT.DocDesigner.POC.Rendering;

/// <summary>
/// Check boxes on form pages: <c>{{ path | checkmark }}</c> prints an X when the value means "yes" (true, a number other
/// than 0, or text such as Yes / Y / X / On / 1), and <c>{{ path | checkmark: "Corporation" }}</c> when the value is that
/// text (case and surrounding spaces ignored; for a list, when the list holds it), which suits a group of boxes where only
/// one applies. Anything else, including a missing value, leaves the box empty.
/// </summary>
public static class CheckMarks
{
	public const string Mark = "X";

	public const int MaxWhenLength = 100;

	private static readonly HashSet<string> YesWords = new(StringComparer.OrdinalIgnoreCase)
	{
		"true", "t", "yes", "y", "x", "on", "checked", "1", "s\u00ed", "si", "oui"
	};

	public static bool IsChecked(FluidValue value, string? when, TemplateContext context)
	{
		if (value.IsNil()) return false;
		var target = when?.Trim();
		if (value.Type == FluidValues.Array)
		{
			var items = value.Enumerate(context);
			return string.IsNullOrEmpty(target) ? items.Any(i => IsChecked(i, null, context)) : items.Any(i => Matches(i, target));
		}
		if (string.IsNullOrEmpty(target))
		{
			return value.Type switch
			{
				FluidValues.Boolean => value.ToBooleanValue(),
				FluidValues.Number => value.ToNumberValue() != 0,
				_ => YesWords.Contains(value.ToStringValue().Trim())
			};
		}
		return Matches(value, target);
	}

	private static bool Matches(FluidValue value, string target)
	{
		if (value.IsNil()) return false;
		// true / false match "true" / "false" as well as the words they print (Yes / No)
		if (value.Type == FluidValues.Boolean && bool.TryParse(target, out var flag)) return value.ToBooleanValue() == flag;
		if (value.Type == FluidValues.Number && decimal.TryParse(target, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var number))
		{
			return value.ToNumberValue() == number;
		}
		return string.Equals(value.ToStringValue().Trim(), target, StringComparison.OrdinalIgnoreCase);
	}
}
