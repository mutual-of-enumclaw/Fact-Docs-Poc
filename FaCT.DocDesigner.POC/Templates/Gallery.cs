namespace FaCT.DocDesigner.POC.Templates;

/// <summary>One template or clause in the gallery. <see cref="Languages"/>: codes of its translations ("es").</summary>
public sealed record GalleryEntry(
	string Kind,
	string Name,
	int LatestVersion,
	TemplateStatus LatestStatus,
	DateTimeOffset SavedUtc,
	int? PublishedVersion,
	DateTimeOffset? PublishedUtc,
	int Versions,
	int Scenarios,
	int OpenComments,
	IReadOnlyList<string>? Languages = null);

/// <summary>Why a duplicate couldn't be made (maps to an HTTP status).</summary>
public enum DuplicateProblem { None, SourceNotFound, NameTaken }

/// <summary>
/// The template gallery (every template and clause with its versions, test data and open comments) and duplicating:
/// a version of one document becomes draft v1 of a new one, with its data model and, optionally, its test scenarios.
/// Its translations come along too (each language's published version, else its newest, as that language's draft v1).
/// Comments are not copied (they are about the original).
/// </summary>
public sealed class Gallery(TemplateStore templates, ClauseStore clauses, ScenarioStore scenarios, CommentStore comments)
{
	private TemplateStore Store(string kind) => kind == "clauses" ? clauses.Versions : templates;

	public async Task<IReadOnlyList<GalleryEntry>> ListAsync(string? kind)
	{
		var entries = new List<GalleryEntry>();
		foreach (var docKind in ScenarioStore.Kinds)
		{
			if (kind is not null && kind != docKind) continue;
			var store = Store(docKind);
			foreach (var summary in await store.ListAsync())
			{
				var versions = await store.ListVersionsAsync(summary.Name);
				var latest = versions[^1];
				var published = versions.FirstOrDefault(v => v.Status == TemplateStatus.Published);
				entries.Add(new GalleryEntry(
					docKind, summary.Name, latest.Version, latest.Status, latest.SavedUtc,
					published?.Version, published?.PublishedUtc, versions.Count,
					(await scenarios.ListAsync(docKind, summary.Name)).Count,
					(await comments.ListAsync(docKind, summary.Name)).Count(c => !c.Resolved),
					DocumentLanguages.All.Where(l => l != DocumentLanguages.English && store.Language(l.Code).Exists(summary.Name)).Select(l => l.Code).ToList()));
			}
		}
		return entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Kind, StringComparer.Ordinal).ToList();
	}

	/// <summary>The version a document opens / previews as: the given one, else the published one, else the newest.</summary>
	public async Task<TemplateVersion?> ResolveAsync(string kind, string name, int? version)
	{
		var store = Store(kind);
		return version is not null ? await store.GetAsync(name, version) : await store.GetPublishedAsync(name) ?? await store.GetAsync(name);
	}

	/// <summary>Copies a version (default: published, else newest) to <paramref name="newName"/> as draft v1.</summary>
	public async Task<(DuplicateProblem Problem, TemplateVersion? Source, TemplateVersionInfo? Copy, int ScenariosCopied, IReadOnlyList<string> LanguagesCopied)> DuplicateAsync(
		string kind, string name, string newName, int? version, bool includeScenarios)
	{
		var store = Store(kind);
		var source = await ResolveAsync(kind, name, version);
		if (source is null) return (DuplicateProblem.SourceNotFound, null, null, 0, []);
		if ((await store.ListVersionsAsync(newName)).Count > 0) return (DuplicateProblem.NameTaken, source, null, 0, []);
		// a translation left behind under the new name (its English version was discarded) also takes the name
		foreach (var language in DocumentLanguages.All)
		{
			if (store.Language(language.Code).Exists(newName)) return (DuplicateProblem.NameTaken, source, null, 0, []);
		}

		var copy = await store.SaveDraftAsync(newName, source.Project, source.Html, source.Css, source.Model, source.Details);
		var languages = new List<string>();
		foreach (var language in DocumentLanguages.All.Where(l => l != DocumentLanguages.English))
		{
			var translations = store.Language(language.Code);
			if ((await translations.GetPublishedAsync(name) ?? await translations.GetAsync(name)) is not { } translation) continue;
			await translations.SaveDraftAsync(newName, translation.Project, translation.Html, translation.Css, translation.Model, translation.Details);
			languages.Add(language.Code);
		}
		var copied = 0;
		if (includeScenarios)
		{
			foreach (var scenario in await scenarios.ListAsync(kind, name))
			{
				await scenarios.SaveAsync(kind, newName, scenario.Name, scenario.Data);
				copied++;
			}
		}
		return (DuplicateProblem.None, source, copy, copied, languages);
	}
}
