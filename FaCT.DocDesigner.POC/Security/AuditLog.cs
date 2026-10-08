using System.Text.Json;

namespace FaCT.DocDesigner.POC.Security;

/// <summary>One audited action: who, when, what, on which template / clause version.</summary>
public sealed record AuditEntry(
	DateTimeOffset TimeUtc,
	string User,
	string Action,
	string? Kind,
	string? Name,
	int? Version,
	string? Detail);

/// <summary>
/// Append-only audit log of changes (saves, publishes, duplicates, scenarios, blocks, comments, dictionary words,
/// sign-ins and refused requests). One JSON object per line in {root}/audit.jsonl; entries are never edited or removed
/// through the designer.
/// </summary>
public sealed class AuditLog
{
	public const int MaxTake = 1000;
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
	private readonly SemaphoreSlim _lock = new(1, 1);
	private readonly string _path;

	public AuditLog(string root)
	{
		Directory.CreateDirectory(root);
		_path = Path.Combine(root, "audit.jsonl");
	}

	public async Task AppendAsync(string user, string action, string? kind = null, string? name = null, int? version = null, string? detail = null)
	{
		var entry = new AuditEntry(DateTimeOffset.UtcNow, user, action, kind, name, version, detail is { Length: > 500 } ? detail[..500] : detail);
		await _lock.WaitAsync();
		try
		{
			await File.AppendAllTextAsync(_path, JsonSerializer.Serialize(entry, JsonOptions) + "\n");
		}
		finally
		{
			_lock.Release();
		}
	}

	/// <summary>Newest first, optionally only for one template / clause, user or action prefix.</summary>
	public async Task<IReadOnlyList<AuditEntry>> ReadAsync(string? kind = null, string? name = null, string? user = null, string? action = null, int take = 200)
	{
		if (!File.Exists(_path)) return [];
		string[] lines;
		await _lock.WaitAsync();
		try
		{
			lines = await File.ReadAllLinesAsync(_path);
		}
		finally
		{
			_lock.Release();
		}
		var entries = new List<AuditEntry>();
		for (var i = lines.Length - 1; i >= 0 && entries.Count < Math.Clamp(take, 1, MaxTake); i--)
		{
			if (lines[i].Length == 0) continue;
			var entry = JsonSerializer.Deserialize<AuditEntry>(lines[i], JsonOptions);
			if (entry is null) continue;
			if (kind is not null && entry.Kind != kind) continue;
			if (name is not null && entry.Name != name) continue;
			if (user is not null && !string.Equals(entry.User, user, StringComparison.OrdinalIgnoreCase)) continue;
			if (action is not null && !entry.Action.StartsWith(action, StringComparison.Ordinal)) continue;
			entries.Add(entry);
		}
		return entries;
	}

	/// <summary>
	/// Who saved a version last (for "a second person must publish"). Versions in another language are told apart by the
	/// language name their saves record as detail (English saves have none).
	/// </summary>
	public async Task<string?> LastSavedByAsync(string kind, string name, int version, string? language = null) =>
		(await ReadAsync(kind, name, action: "draft.saved", take: MaxTake)).FirstOrDefault(e => e.Version == version && e.Detail == language)?.User;
}
