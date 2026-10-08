using System.Globalization;
using System.Text.Encodings.Web;
using Fluid;
using Fluid.Values;

namespace FaCT.DocDesigner.POC.Rendering;

/// <summary>
/// A true/false value from the message data. It behaves as a boolean in conditions ({% if x %}, x == true) but prints
/// "Yes" / "No", the way DocGen's Word filler prints booleans, so the PDF and the Word path show the same words.
/// </summary>
public sealed class YesNoValue(bool value) : FluidValue
{
	public static readonly YesNoValue Yes = new(true);
	public static readonly YesNoValue No = new(false);

	public static YesNoValue Of(bool value) => value ? Yes : No;

	public override FluidValues Type => FluidValues.Boolean;

	public override bool Equals(FluidValue other) =>
		other.Type == FluidValues.Boolean ? value == other.ToBooleanValue()
		// Liquid treats false as blank: {% if x == blank %} holds for false, as it does for a plain boolean.
		: ReferenceEquals(other, BlankValue.Instance) && !value;

	public override bool ToBooleanValue() => value;

	public override decimal ToNumberValue() => value ? 1 : 0;

	public override string ToStringValue() => value ? "Yes" : "No";

	public override object ToObjectValue() => value;

	public override ValueTask WriteToAsync(TextWriter writer, TextEncoder encoder, CultureInfo cultureInfo) =>
		new(writer.WriteAsync(encoder.Encode(Word(value, cultureInfo))));

	/// <summary>Yes / No in the document's language (Sí / No, Oui / Non); English for any other culture.</summary>
	public static string Word(bool value, CultureInfo culture)
	{
		var language = Templates.DocumentLanguages.All.FirstOrDefault(l => l.Code == culture.TwoLetterISOLanguageName)
			?? Templates.DocumentLanguages.English;
		return value ? language.Yes : language.No;
	}

	public override bool Equals(object? obj) => obj is FluidValue other && Equals(other);

	public override int GetHashCode() => value.GetHashCode();
}
