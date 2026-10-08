using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using FapPdfTools.Server.Configuration;
using FapPdfTools.Server.Models;

namespace FapPdfTools.Server.Infrastructure;

/// <summary>
/// Persists <see cref="FormScenario"/> objects as individual JSON files under
/// the directory configured by <see cref="FormFileOptions.ScenariosDirectory"/>.
/// </summary>
public class ScenarioStore
{
	private readonly string _directory;
	private readonly ILogger<ScenarioStore> _logger;

	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

	public ScenarioStore(IOptions<FormFileOptions> opts, ILogger<ScenarioStore> logger)
	{
		_directory = string.IsNullOrWhiteSpace(opts.Value.ScenariosDirectory)
			? Path.Combine(AppContext.BaseDirectory, "scenarios")
			: opts.Value.ScenariosDirectory;
		_logger = logger;
		Directory.CreateDirectory(_directory);
	}

	public IReadOnlyList<FormScenario> ListAll()
	{
		var result = new List<FormScenario>();
		foreach (var file in Directory.GetFiles(_directory, "*.json"))
		{
			try
			{
				var scenario = JsonSerializer.Deserialize<FormScenario>(File.ReadAllText(file), JsonOptions);
				if (scenario != null) result.Add(scenario);
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Could not load scenario from {File}", file);
			}
		}
		return result.OrderByDescending(s => s.UpdatedAt).ToList();
	}

	public FormScenario? Get(string id)
	{
		var path = GetPath(id);
		if (!File.Exists(path)) return null;
		try
		{
			return JsonSerializer.Deserialize<FormScenario>(File.ReadAllText(path), JsonOptions);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Could not load scenario {Id}", id);
			return null;
		}
	}

	public void Save(FormScenario scenario)
	{
		scenario.UpdatedAt = DateTime.UtcNow;
		File.WriteAllText(GetPath(scenario.Id), JsonSerializer.Serialize(scenario, JsonOptions));
	}

	public bool Delete(string id)
	{
		var path = GetPath(id);
		if (!File.Exists(path)) return false;
		File.Delete(path);
		return true;
	}

	private string GetPath(string id) => Path.Combine(_directory, $"{id}.json");
}
