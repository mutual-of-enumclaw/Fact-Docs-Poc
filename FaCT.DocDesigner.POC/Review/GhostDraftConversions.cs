using System.Text;
using System.Text.Json;

namespace FaCT.DocDesigner.POC.Review;

/// <summary>
/// Finds the designer conversion (tools/gd2designer.py JSON in output/ghostdraft/batch) of a GhostDraft review case, so a
/// reviewed form can be added to the designer's library. A case is "{FormCode}_{Edition}[_{suffix}]";
/// ghostdraft-forms.csv maps FormCode + Edition to the GhostDraft form name, and each conversion records the .gd file it
/// was made from ("source").
/// </summary>
public sealed class GhostDraftConversions(string formsCsvPath, string templatesRoot)
{
	/// <summary>The conversion's path, or null when the case has none.</summary>
	public string? PathFor(string caseName)
	{
		var parts = caseName.Split('_');
		if (parts.Length < 2 || !File.Exists(formsCsvPath) || !Directory.Exists(templatesRoot)) return null;
		var formKey = parts[0] + "_" + parts[1];

		string? form = null;
		string[]? header = null;
		foreach (var line in File.ReadLines(formsCsvPath))
		{
			if (line.Length == 0) continue;
			var fields = SplitCsvLine(line);
			if (header is null)
			{
				header = fields;
				continue;
			}
			string Field(string name) => Array.IndexOf(header, name) is var i and >= 0 && i < fields.Length ? fields[i] : string.Empty;
			if (string.Equals(Field("FormCode") + "_" + Field("Edition"), formKey, StringComparison.OrdinalIgnoreCase))
			{
				form = Field("GhostDraftForm");
				break;
			}
		}
		if (string.IsNullOrEmpty(form)) return null;

		foreach (var file in Directory.EnumerateFiles(templatesRoot, "*.json"))
		{
			try
			{
				using var doc = JsonDocument.Parse(File.ReadAllText(file));
				if (doc.RootElement.ValueKind == JsonValueKind.Object &&
					doc.RootElement.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.String &&
					string.Equals(Path.GetFileNameWithoutExtension(source.GetString()), form, StringComparison.OrdinalIgnoreCase))
				{
					return file;
				}
			}
			catch (JsonException)
			{
				// not a conversion
			}
		}
		return null;
	}

	/// <summary>Splits one CSV line: commas, double-quoted fields, "" for a quote inside a field.</summary>
	public static string[] SplitCsvLine(string line)
	{
		var fields = new List<string>();
		var field = new StringBuilder();
		var quoted = false;
		for (var i = 0; i < line.Length; i++)
		{
			var c = line[i];
			if (quoted)
			{
				if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { field.Append('"'); i++; }
				else if (c == '"') quoted = false;
				else field.Append(c);
			}
			else if (c == '"') quoted = true;
			else if (c == ',') { fields.Add(field.ToString()); field.Clear(); }
			else field.Append(c);
		}
		fields.Add(field.ToString());
		return fields.ToArray();
	}
}
