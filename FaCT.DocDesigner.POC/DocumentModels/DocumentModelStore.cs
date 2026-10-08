using System.Text.Json;
using FaCT.DocDesigner.POC.Templates;

namespace FaCT.DocDesigner.POC.DocumentModels;

/// <summary>A document model: the data a kind of document is rendered from (e.g. the Commercial Auto Dec page).</summary>
public sealed record DocumentModelInfo(string Name, string? Title, string? Description, bool HasSample);

/// <summary>
/// Document models from the repository's models folder (<c>DocumentModels:Root</c>, default <c>../models</c>): one folder per
/// model with <c>{name}.schema.json</c> (JSON Schema) and <c>{name}.sample.json</c> (an example record). Read-only; the host
/// app offers them to template authors, and the hydrator (next) builds real records in the same shape.
/// </summary>
public sealed class DocumentModelStore
{
	private const long MaxFileBytes = 2 * 1024 * 1024;
	private readonly string _root;

	public DocumentModelStore(IConfiguration configuration, IWebHostEnvironment environment)
	{
		_root = configuration["DocumentModels:Root"] is { Length: > 0 } root
			? root
			: Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", "models"));
	}

	public async Task<IReadOnlyList<DocumentModelInfo>> ListAsync()
	{
		if (!Directory.Exists(_root)) return [];
		var models = new List<DocumentModelInfo>();
		foreach (var folder in Directory.EnumerateDirectories(_root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
		{
			var name = Path.GetFileName(folder);
			if (!TemplateStore.IsValidName(name)) continue;
			var schema = await ReadAsync(name, "schema");
			var hasSample = File.Exists(PathFor(name, "sample"));
			if (schema is null && !hasSample) continue;
			string? Text(string property) =>
				schema is { ValueKind: JsonValueKind.Object } s && s.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
			models.Add(new DocumentModelInfo(name, Text("title"), Text("description"), hasSample));
		}
		return models;
	}

	/// <summary>The model's sample record or schema; null when the name is invalid or there is no such file.</summary>
	public async Task<JsonElement?> ReadAsync(string name, string kind)
	{
		if (!TemplateStore.IsValidName(name) || kind is not ("sample" or "schema")) return null;
		var path = PathFor(name, kind);
		if (!File.Exists(path) || new FileInfo(path).Length > MaxFileBytes) return null;
		await using var stream = File.OpenRead(path);
		try
		{
			return await JsonSerializer.DeserializeAsync<JsonElement>(stream);
		}
		catch (JsonException)
		{
			return null;
		}
	}

	// Names are checked against the template-name allow-list, so the path stays inside the models folder.
	private string PathFor(string name, string kind) => Path.Combine(_root, name, $"{name}.{kind}.json");
}
