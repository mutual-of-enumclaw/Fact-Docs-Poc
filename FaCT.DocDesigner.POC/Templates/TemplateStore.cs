using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace FaCT.DocDesigner.POC.Templates;

/// <summary>
/// Lifecycle: Draft -> Published -> Retired.
/// At most one Draft (always the newest version) and at most one Published version per template.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<TemplateStatus>))]
public enum TemplateStatus { Draft, Published, Retired }

/// <summary>
/// A template version: GrapesJS project JSON (to re-open in the designer) + compiled HTML/Liquid and CSS (to render)
/// + the data model (example message payload) the template was designed against.
/// </summary>
public sealed record TemplateVersion(
	int Version,
	TemplateStatus Status,
	DateTimeOffset SavedUtc,
	DateTimeOffset? PublishedUtc,
	DateTimeOffset? RetiredUtc,
	JsonElement Project,
	string Html,
	string Css,
	JsonElement? Model = null,
	TemplateDetails? Details = null)
{
	public TemplateVersionInfo ToInfo() => new(Version, Status, SavedUtc, PublishedUtc, RetiredUtc, Details);
}

public sealed record TemplateVersionInfo(int Version, TemplateStatus Status, DateTimeOffset SavedUtc, DateTimeOffset? PublishedUtc, DateTimeOffset? RetiredUtc,
	TemplateDetails? Details = null);

/// <summary>A template in the list: its newest and published versions, the newest version's status and details.</summary>
public sealed record TemplateSummary(string Name, int LatestVersion, int? PublishedVersion, TemplateStatus? LatestStatus = null,
	TemplateDetails? Details = null);

/// <summary>
/// File-backed for the POC (one JSON file per template holding all versions).
/// The real version would live in Cosmos/Blob, with who-published auditing.
/// </summary>
public sealed partial class TemplateStore
{
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
	private readonly SemaphoreSlim _writeLock = new(1, 1);
	private readonly string _root;
	private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TemplateStore> _languages = new();

	public TemplateStore(IWebHostEnvironment environment)
		: this(Path.Combine(environment.ContentRootPath, "App_Data", "templates"))
	{
	}

	/// <summary>A store in <paramref name="root"/> (clauses use the same lifecycle in their own folder).</summary>
	public TemplateStore(string root)
	{
		_root = root;
		Directory.CreateDirectory(_root);
	}

	public static bool IsValidName(string name) => ValidName().IsMatch(name);

	/// <summary>
	/// The versions in another language ({root}/_lang/{code}: own Draft -> Published -> Retired versions, same names).
	/// English (null, "" or "en") is this store. Only known language codes reach here.
	/// </summary>
	public TemplateStore Language(string? code)
	{
		if (DocumentLanguages.IsBase(code)) return this;
		var language = DocumentLanguages.Find(code) ?? throw new ArgumentException(DocumentLanguages.UnknownLanguage(code), nameof(code));
		return _languages.GetOrAdd(language.Code, c => new TemplateStore(Path.Combine(_root, "_lang", c)));
	}

	/// <summary>Whether this store has any version of <paramref name="name"/>.</summary>
	public bool Exists(string name) => IsValidName(name) && File.Exists(PathFor(name));

	public async Task<IReadOnlyList<TemplateSummary>> ListAsync()
	{
		var summaries = new List<TemplateSummary>();
		foreach (var file in Directory.EnumerateFiles(_root, "*.json"))
		{
			var name = Path.GetFileNameWithoutExtension(file);
			if (!IsValidName(name)) continue;
			var versions = await ReadAsync(name);
			if (versions.Count == 0) continue;
			summaries.Add(new TemplateSummary(name, versions[^1].Version, versions.FirstOrDefault(v => v.Status == TemplateStatus.Published)?.Version,
				versions[^1].Status, versions[^1].Details));
		}
		return summaries;
	}

	public async Task<IReadOnlyList<TemplateVersionInfo>> ListVersionsAsync(string name) =>
		(await ReadAsync(name)).Select(v => v.ToInfo()).ToList();

	/// <summary>Specific version, or the newest when <paramref name="version"/> is null.</summary>
	public async Task<TemplateVersion?> GetAsync(string name, int? version = null)
	{
		var versions = await ReadAsync(name);
		return version is null ? versions.LastOrDefault() : versions.FirstOrDefault(v => v.Version == version);
	}

	public async Task<TemplateVersion?> GetPublishedAsync(string name) =>
		(await ReadAsync(name)).FirstOrDefault(v => v.Status == TemplateStatus.Published);

	/// <summary>
	/// Synchronous read for the Liquid file provider (Fluid asks for partials synchronously): the given version, or the
	/// Published one when <paramref name="version"/> is null. Null when the name is invalid or nothing matches.
	/// </summary>
	public TemplateVersion? Read(string name, int? version)
	{
		if (!IsValidName(name)) return null;
		var path = PathFor(name);
		if (!File.Exists(path)) return null;
		var versions = JsonSerializer.Deserialize<List<TemplateVersion>>(File.ReadAllText(path), JsonOptions) ?? [];
		return version is null
			? versions.FirstOrDefault(v => v.Status == TemplateStatus.Published)
			: versions.FirstOrDefault(v => v.Version == version);
	}

	/// <summary>
	/// Overwrites the current draft, or starts a new draft (latest + 1) when the newest version is Published/Retired.
	/// Published and Retired versions are never modified. <paramref name="details"/> is stored as given (validate first).
	/// </summary>
	public async Task<TemplateVersionInfo> SaveDraftAsync(string name, JsonElement project, string html, string css, JsonElement? model,
		TemplateDetails? details = null)
	{
		await _writeLock.WaitAsync();
		try
		{
			var versions = (await ReadAsync(name)).ToList();
			var latest = versions.LastOrDefault();
			var now = DateTimeOffset.UtcNow;

			TemplateVersion draft;
			if (latest is { Status: TemplateStatus.Draft })
			{
				draft = latest with { SavedUtc = now, Project = project, Html = html, Css = css, Model = model, Details = details };
				versions[^1] = draft;
			}
			else
			{
				draft = new TemplateVersion((latest?.Version ?? 0) + 1, TemplateStatus.Draft, now, null, null, project, html, css, model, details);
				versions.Add(draft);
			}

			await WriteAsync(name, versions);
			return draft.ToInfo();
		}
		finally
		{
			_writeLock.Release();
		}
	}

	/// <summary>
	/// Publishes a Draft (go-live) or a Retired version (rollback). The previously Published version becomes Retired.
	/// Returns null when the version doesn't exist.
	/// </summary>
	public async Task<TemplateVersionInfo?> PublishAsync(string name, int version)
	{
		await _writeLock.WaitAsync();
		try
		{
			var versions = (await ReadAsync(name)).ToList();
			var index = versions.FindIndex(v => v.Version == version);
			if (index < 0) return null;
			if (versions[index].Status == TemplateStatus.Published) return versions[index].ToInfo();

			var now = DateTimeOffset.UtcNow;
			for (var i = 0; i < versions.Count; i++)
			{
				if (versions[i].Status == TemplateStatus.Published)
				{
					versions[i] = versions[i] with { Status = TemplateStatus.Retired, RetiredUtc = now };
				}
			}
			versions[index] = versions[index] with { Status = TemplateStatus.Published, PublishedUtc = now, RetiredUtc = null };

			await WriteAsync(name, versions);
			return versions[index].ToInfo();
		}
		finally
		{
			_writeLock.Release();
		}
	}

	/// <summary>Deletes the draft (only drafts can be discarded). Returns false when there is no draft.</summary>
	public async Task<bool> DiscardDraftAsync(string name)
	{
		await _writeLock.WaitAsync();
		try
		{
			var versions = (await ReadAsync(name)).ToList();
			if (versions.LastOrDefault() is not { Status: TemplateStatus.Draft }) return false;
			versions.RemoveAt(versions.Count - 1);

			if (versions.Count == 0) File.Delete(PathFor(name));
			else await WriteAsync(name, versions);
			return true;
		}
		finally
		{
			_writeLock.Release();
		}
	}

	private async Task<IReadOnlyList<TemplateVersion>> ReadAsync(string name)
	{
		var path = PathFor(name);
		if (!File.Exists(path)) return [];
		await using var stream = File.OpenRead(path);
		return await JsonSerializer.DeserializeAsync<List<TemplateVersion>>(stream, JsonOptions) ?? [];
	}

	private async Task WriteAsync(string name, IReadOnlyList<TemplateVersion> versions) =>
		// temp file + move, retried: a virus scanner or indexer holding the file briefly fails a plain move (500 on save)
		await AtomicFile.WriteAllTextAsync(PathFor(name), JsonSerializer.Serialize(versions, JsonOptions));

	// Name is validated against a strict allow-list so it can never escape the templates folder.
	private string PathFor(string name) =>
		IsValidName(name)
			? Path.Combine(_root, name + ".json")
			: throw new ArgumentException("Invalid template name.", nameof(name));

	[GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
	private static partial Regex ValidName();
}
