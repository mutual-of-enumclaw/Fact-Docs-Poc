using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using FapPdfTools.Server.Configuration;
using FapPdfTools.Server.Models;

namespace FapPdfTools.Server.Infrastructure;

/// <summary>
/// Persists <see cref="FormDefinition"/> objects as individual JSON files under
/// the directory configured by <see cref="FormFileOptions.AuthoredFormsDirectory"/>.
/// </summary>
public class FormDefinitionStore
{
	private readonly string _directory;
	private readonly ILogger<FormDefinitionStore> _logger;

	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

	public FormDefinitionStore(IOptions<FormFileOptions> opts, ILogger<FormDefinitionStore> logger)
	{
		_directory = string.IsNullOrWhiteSpace(opts.Value.AuthoredFormsDirectory)
			? Path.Combine(AppContext.BaseDirectory, "authored-forms")
			: opts.Value.AuthoredFormsDirectory;
		_logger = logger;
		Directory.CreateDirectory(_directory);
	}

	public IReadOnlyList<FormDefinition> ListAll()
	{
		var result = new List<FormDefinition>();
		foreach (var file in Directory.GetFiles(_directory, "*.json"))
		{
			try
			{
				var def = JsonSerializer.Deserialize<FormDefinition>(File.ReadAllText(file), JsonOptions);
				if (def != null) result.Add(def);
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Could not load form definition from {File}", file);
			}
		}
		return result.OrderByDescending(d => d.UpdatedAt).ToList();
	}

	public FormDefinition? Get(string id)
	{
		var path = GetPath(id);
		if (!File.Exists(path)) return null;
		try
		{
			return JsonSerializer.Deserialize<FormDefinition>(File.ReadAllText(path), JsonOptions);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Could not load form definition {Id}", id);
			return null;
		}
	}

	public void Save(FormDefinition def)
	{
		def.UpdatedAt = DateTime.UtcNow;
		File.WriteAllText(GetPath(def.Id), JsonSerializer.Serialize(def, JsonOptions));
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
