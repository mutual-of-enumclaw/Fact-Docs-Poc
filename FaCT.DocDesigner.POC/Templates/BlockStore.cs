using System.Text.Json;

namespace FaCT.DocDesigner.POC.Templates;

/// <summary>
/// A reusable block saved from the designer: a copy of a selection (GrapesJS component JSON) and the CSS rules of the
/// classes it uses. Unlike a clause, a block is copied into the document when dropped and is not linked afterwards.
/// </summary>
public sealed record CustomBlock(string Name, string Label, string Category, JsonElement Components, string Css, DateTimeOffset SavedUtc);

/// <summary>The shared library of custom blocks (one JSON file per block in {root}).</summary>
public sealed class BlockStore
{
	public const int MaxBlocks = 200;
	public const int MaxLabelLength = 60;
	public const string DefaultCategory = "My blocks";

	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
	private readonly SemaphoreSlim _writeLock = new(1, 1);
	private readonly string _root;

	public BlockStore(string root)
	{
		_root = root;
		Directory.CreateDirectory(_root);
	}

	public async Task<IReadOnlyList<CustomBlock>> ListAsync()
	{
		var blocks = new List<CustomBlock>();
		foreach (var file in Directory.EnumerateFiles(_root, "*.json"))
		{
			if (!TemplateStore.IsValidName(Path.GetFileNameWithoutExtension(file))) continue;
			await using var stream = File.OpenRead(file);
			if (await JsonSerializer.DeserializeAsync<CustomBlock>(stream, JsonOptions) is { } block) blocks.Add(block);
		}
		return blocks.OrderBy(b => b.Category, StringComparer.OrdinalIgnoreCase).ThenBy(b => b.Label, StringComparer.OrdinalIgnoreCase).ToList();
	}

	/// <summary>Adds or replaces the block.</summary>
	/// <exception cref="InvalidOperationException">The library already has <see cref="MaxBlocks"/> blocks.</exception>
	public async Task<CustomBlock> SaveAsync(string name, string label, string? category, JsonElement components, string? css)
	{
		await _writeLock.WaitAsync();
		try
		{
			var path = PathFor(name);
			if (!File.Exists(path) && Directory.EnumerateFiles(_root, "*.json").Count() >= MaxBlocks)
			{
				throw new InvalidOperationException($"The block library can hold at most {MaxBlocks} blocks.");
			}
			var block = new CustomBlock(name, label.Trim(), string.IsNullOrWhiteSpace(category) ? DefaultCategory : category.Trim(),
				components.Clone(), css ?? string.Empty, DateTimeOffset.UtcNow);
			await AtomicFile.WriteAllTextAsync(path, JsonSerializer.Serialize(block, JsonOptions));
			return block;
		}
		finally
		{
			_writeLock.Release();
		}
	}

	public async Task<bool> DeleteAsync(string name)
	{
		await _writeLock.WaitAsync();
		try
		{
			var path = PathFor(name);
			if (!File.Exists(path)) return false;
			File.Delete(path);
			return true;
		}
		finally
		{
			_writeLock.Release();
		}
	}

	// Names are allow-listed so a block file can never be written outside the folder.
	private string PathFor(string name) =>
		TemplateStore.IsValidName(name) ? Path.Combine(_root, name + ".json") : throw new ArgumentException("Invalid block name.", nameof(name));
}
