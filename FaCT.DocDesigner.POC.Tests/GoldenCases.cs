namespace FaCT.DocDesigner.POC.Tests;

/// <summary>
/// One fact-docgen golden snapshot case that can be replayed through the HTML designer:
///   golden     fact-docgen TestData/Snapshots/{case}/GoldenSnapshot.txt (GhostDraft TST render, Spire text)
///   serverXml  the Server XML fact-docgen sent GhostDraft for that case (dumped with SERVERXML_DUMP_DIR)
///   template   the gd2designer conversion of the same GhostDraft template
/// </summary>
public sealed record GoldenCase(string Name, string GoldenPath, string ServerXmlPath, string TemplatePath);

public static class GoldenCases
{
	public static readonly string RepoRoot = FindRepoRoot();

	public static string GoldenRoot => Env("DOCGEN_SNAPSHOTS",
		@"C:\src\fact-docgen\src\Moe.Commercial.Documents.Generation.Integration.Tests\TestData\Snapshots");

	public static string ServerXmlRoot => Env("GD_SERVERXML_DIR", Path.Combine(RepoRoot, "output", "ghostdraft", "serverxml"));

	public static string TemplateRoot => Env("GD_TEMPLATES_DIR", Path.Combine(RepoRoot, "output", "ghostdraft", "batch"));

	public static string ResultsRoot => Env("GD_HTML_RESULTS_DIR", Path.Combine(RepoRoot, "output", "ghostdraft", "html-snapshots"));

	private static readonly Lazy<Dictionary<string, GoldenCase>> All = new(Discover);

	public static TheoryData<string> Names()
	{
		var data = new TheoryData<string>();
		foreach (var name in All.Value.Keys.Order(StringComparer.Ordinal))
		{
			data.Add(name);
		}
		return data;
	}

	public static GoldenCase Get(string name) => All.Value[name];

	private static Dictionary<string, GoldenCase> Discover()
	{
		// Snapshot folders are "{FormCode}_{Edition}[_{suffix}]" (fact-docgen FormNames with the space replaced).
		// ghostdraft-forms.csv maps FormCode + Edition to the GhostDraft template; gd2designer records the .gd it read.
		var templateByForm = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (var row in ReadCsv(Path.Combine(RepoRoot, "ghostdraft-forms.csv")))
		{
			templateByForm.TryAdd(row["FormCode"] + "_" + row["Edition"], row["GhostDraftForm"]);
		}

		var convertedByTemplate = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		if (Directory.Exists(TemplateRoot))
		{
			foreach (var file in Directory.EnumerateFiles(TemplateRoot, "*.json"))
			{
				using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
				if (doc.RootElement.TryGetProperty("source", out var source) && source.GetString() is { } gd)
				{
					convertedByTemplate.TryAdd(Path.GetFileNameWithoutExtension(gd), file);
				}
			}
		}

		var cases = new Dictionary<string, GoldenCase>(StringComparer.Ordinal);
		if (!Directory.Exists(ServerXmlRoot))
		{
			return cases;
		}
		foreach (var dir in Directory.EnumerateDirectories(ServerXmlRoot))
		{
			var name = Path.GetFileName(dir);
			var serverXml = Path.Combine(dir, "ServerXml.xml");
			var golden = Path.Combine(GoldenRoot, name, "GoldenSnapshot.txt");
			var formKey = string.Join('_', name.Split('_').Take(2));
			if (File.Exists(serverXml) && File.Exists(golden) &&
				templateByForm.TryGetValue(formKey, out var template) &&
				convertedByTemplate.TryGetValue(template, out var converted))
			{
				cases[name] = new GoldenCase(name, golden, serverXml, converted);
			}
		}
		return cases;
	}

	private static string Env(string name, string fallback) =>
		Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;

	private static string FindRepoRoot()
	{
		for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
		{
			if (File.Exists(Path.Combine(dir.FullName, "ghostdraft-forms.csv")))
			{
				return dir.FullName;
			}
		}
		throw new InvalidOperationException("fact-poc root (ghostdraft-forms.csv) not found above " + AppContext.BaseDirectory);
	}

	private static IEnumerable<Dictionary<string, string>> ReadCsv(string path)
	{
		string[]? header = null;
		foreach (var line in File.ReadLines(path))
		{
			if (line.Length == 0) continue;
			var fields = SplitCsvLine(line);
			if (header is null)
			{
				header = fields;
				continue;
			}
			var row = new Dictionary<string, string>(StringComparer.Ordinal);
			for (var i = 0; i < header.Length && i < fields.Length; i++)
			{
				row[header[i]] = fields[i];
			}
			yield return row;
		}
	}

	private static string[] SplitCsvLine(string line)
	{
		var fields = new List<string>();
		var field = new System.Text.StringBuilder();
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
