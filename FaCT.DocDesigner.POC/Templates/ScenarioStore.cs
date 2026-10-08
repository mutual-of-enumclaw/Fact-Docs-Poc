using System.Text.Json;
using System.Text.RegularExpressions;

namespace FaCT.DocDesigner.POC.Templates;

/// <summary>A named set of test data for a template (e.g. "Minimal", "Many locations", "No claims").</summary>
public sealed record TestScenario(string Name, JsonElement Data, DateTimeOffset SavedUtc);

/// <summary>
/// Test-data scenarios per template (or clause): sample message payloads the author previews the document with, beside
/// the template's own example data. They belong to the template name, not a version, and are saved straight away (they
/// are test inputs, never published). File-backed for the POC: {root}/{kind}/{name}.json.
/// </summary>
public sealed partial class ScenarioStore
{
	public const int MaxScenarios = 50;
	public static readonly IReadOnlyList<string> Kinds = ["templates", "clauses"];

	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
	private readonly SemaphoreSlim _writeLock = new(1, 1);
	private readonly string _root;

	public ScenarioStore(string root)
	{
		_root = root;
		foreach (var kind in Kinds) Directory.CreateDirectory(Path.Combine(_root, kind));
	}

	public static bool IsValidKind(string kind) => Kinds.Contains(kind);

	/// <summary>Letters, numbers, spaces, "-" and "_"; starts with a letter or number, doesn't end with a space; at most 64 characters.</summary>
	public static bool IsValidScenarioName(string name) => ScenarioName().IsMatch(name);

	public async Task<IReadOnlyList<TestScenario>> ListAsync(string kind, string name)
	{
		var path = PathFor(kind, name);
		if (!File.Exists(path)) return [];
		await using var stream = File.OpenRead(path);
		return await JsonSerializer.DeserializeAsync<List<TestScenario>>(stream, JsonOptions) ?? [];
	}

	/// <summary>Adds the scenario, or replaces the one with the same name (case-insensitive) keeping its place.</summary>
	/// <exception cref="InvalidOperationException">The template already has <see cref="MaxScenarios"/> scenarios.</exception>
	public async Task<TestScenario> SaveAsync(string kind, string name, string scenario, JsonElement data)
	{
		await _writeLock.WaitAsync();
		try
		{
			var scenarios = (await ListAsync(kind, name)).ToList();
			var saved = new TestScenario(scenario, data.Clone(), DateTimeOffset.UtcNow);
			var index = scenarios.FindIndex(s => string.Equals(s.Name, scenario, StringComparison.OrdinalIgnoreCase));
			if (index >= 0)
			{
				scenarios[index] = saved;
			}
			else
			{
				if (scenarios.Count >= MaxScenarios) throw new InvalidOperationException($"A template can have at most {MaxScenarios} test scenarios.");
				scenarios.Add(saved);
			}
			await WriteAsync(kind, name, scenarios);
			return saved;
		}
		finally
		{
			_writeLock.Release();
		}
	}

	/// <summary>Deletes the scenario; false when there is no scenario with that name.</summary>
	public async Task<bool> DeleteAsync(string kind, string name, string scenario)
	{
		await _writeLock.WaitAsync();
		try
		{
			var scenarios = (await ListAsync(kind, name)).ToList();
			if (scenarios.RemoveAll(s => string.Equals(s.Name, scenario, StringComparison.OrdinalIgnoreCase)) == 0) return false;
			if (scenarios.Count == 0) File.Delete(PathFor(kind, name));
			else await WriteAsync(kind, name, scenarios);
			return true;
		}
		finally
		{
			_writeLock.Release();
		}
	}

	private Task WriteAsync(string kind, string name, IReadOnlyList<TestScenario> scenarios) =>
		AtomicFile.WriteAllTextAsync(PathFor(kind, name), JsonSerializer.Serialize(scenarios, JsonOptions));

	// Kind and name are allow-listed so the path can never leave the scenarios folder.
	private string PathFor(string kind, string name) =>
		IsValidKind(kind) && TemplateStore.IsValidName(name)
			? Path.Combine(_root, kind, name + ".json")
			: throw new ArgumentException("Invalid template name.", nameof(name));

	[GeneratedRegex("^[A-Za-z0-9](?:[A-Za-z0-9 _-]{0,62}[A-Za-z0-9_-])?$")]
	private static partial Regex ScenarioName();
}
