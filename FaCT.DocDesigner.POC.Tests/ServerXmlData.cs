using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace FaCT.DocDesigner.POC.Tests;

/// <summary>
/// Server XML -> designer message JSON, using the same mechanical rules as tools/gd2designer.py:
///   A/B/Leaf      -> A.B.Leaf
///   A/Items/Auto  -> A.Autos[]
/// Names go through gd2designer's ident() ("Snowmobiles-Wyoming" -> "Snowmobiles_Wyoming").
/// Server XML is all text, so the converted template's sample model decides each leaf's JSON type
/// (bool / number / string); leaves the template doesn't bind stay strings ("true"/"false" become bools).
/// </summary>
public static partial class ServerXmlData
{
	public static JsonObject Convert(XElement root, JsonNode? schema) => ConvertObject(root, schema as JsonObject);

	private static JsonObject ConvertObject(XElement element, JsonObject? schema)
	{
		var result = new JsonObject();
		var hadItems = false;
		foreach (var child in element.Elements())
		{
			var name = child.Name.LocalName;
			if (name == "Items")
			{
				hadItems = true;
				foreach (var group in child.Elements().GroupBy(e => e.Name.LocalName))
				{
					var key = Plural(group.Key);
					if (result.ContainsKey(key)) continue;
					var itemSchema = (schema?[key] as JsonArray)?.FirstOrDefault();
					var array = new JsonArray();
					foreach (var item in group)
					{
						array.Add(ConvertElement(item, itemSchema));
					}
					result[key] = array;
				}
				continue;
			}

			var ident = Ident(name);
			if (schema is not null && !schema.ContainsKey(ident))
			{
				ident = Suffixed(ident, element, schema) ?? ident;
			}
			if (!result.ContainsKey(ident))
			{
				result[ident] = ConvertElement(child, schema?[ident]);
			}
		}

		// An empty <Items/> still means "a list with no entries" for the lists the template iterates.
		if (hadItems && schema is not null)
		{
			foreach (var (key, value) in schema)
			{
				if (value is JsonArray && !result.ContainsKey(key))
				{
					result[key] = new JsonArray();
				}
			}
		}
		return result;
	}

	private static JsonNode? ConvertElement(XElement element, JsonNode? schema) =>
		element.HasElements ? ConvertObject(element, schema as JsonObject) : Leaf(element.Value, schema);

	private static JsonNode? Leaf(string text, JsonNode? schema)
	{
		var kind = schema is JsonValue value ? value.GetValueKind() : System.Text.Json.JsonValueKind.Undefined;
		switch (kind)
		{
			case System.Text.Json.JsonValueKind.True:
			case System.Text.Json.JsonValueKind.False:
				return JsonValue.Create(string.Equals(text.Trim(), "true", StringComparison.OrdinalIgnoreCase));
			case System.Text.Json.JsonValueKind.Number:
				return decimal.TryParse(text.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
					? JsonValue.Create(number)
					: null;
			case System.Text.Json.JsonValueKind.String:
				return JsonValue.Create(text);
			default:
				return text.Trim() switch
				{
					"true" => JsonValue.Create(true),
					"false" => JsonValue.Create(false),
					_ => JsonValue.Create(text)
				};
		}
	}

	// GhostDraft numbers model ids whose display names collide (package 2607 added state variants of "Lessor -
	// Additional Insured and Loss Payee Coverage", so the one fact-docgen sends as
	// Lessor-AdditionalInsuredandLossPayeeCoverage became ...Coverage1). When the template binds exactly one such
	// numbered key and the XML has no element of that name, the XML element fills it.
	private static string? Suffixed(string ident, XElement parent, JsonObject schema)
	{
		var candidates = schema
			.Select(p => p.Key)
			.Where(k => k.Length > ident.Length && k.StartsWith(ident, StringComparison.Ordinal) && k[ident.Length..].All(char.IsAsciiDigit))
			.ToList();
		if (candidates.Count != 1)
		{
			return null;
		}
		var key = candidates[0];
		return parent.Elements().Any(e => Ident(e.Name.LocalName) == key) ? null : key;
	}

	// gd2designer.ident / plural
	public static string Ident(string name)
	{
		var s = NotIdentChar().Replace(name ?? string.Empty, "_");
		return IdentStart().IsMatch(s) ? s : "_" + s;
	}

	public static string Plural(string name)
	{
		var s = Ident(name);
		return s.EndsWith('s') ? s + "es" : s + "s";
	}

	[GeneratedRegex("[^A-Za-z0-9_]")]
	private static partial Regex NotIdentChar();

	[GeneratedRegex("^[A-Za-z_]")]
	private static partial Regex IdentStart();
}
