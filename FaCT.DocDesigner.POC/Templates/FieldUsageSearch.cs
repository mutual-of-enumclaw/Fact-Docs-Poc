namespace FaCT.DocDesigner.POC.Templates;

/// <summary>How often a document version reads one path.</summary>
public sealed record PathCount(string Path, int Count);

/// <summary>
/// A template or clause version that uses the searched field, directly and/or through clauses it includes.
/// <see cref="Language"/> is the code of a translation ("es"), null for English.
/// </summary>
public sealed record FieldUsageHit(string Kind, string Name, int Version, TemplateStatus Status, IReadOnlyList<PathCount> Paths, IReadOnlyList<string> Via, string? Language = null);

/// <summary>A field path and how many documents use it.</summary>
public sealed record FieldIndexEntry(string Path, int Documents);

/// <summary>
/// Field usage across the template and clause stores, translations included. By default each document's newest version
/// and its published version are searched (what is being worked on and what is live); <c>allVersions</c> searches every
/// version.
/// </summary>
public sealed class FieldUsageSearch(TemplateStore templates, ClauseStore clauses)
{
	public async Task<IReadOnlyList<FieldUsageHit>> SearchAsync(string query, string? kind, bool allVersions)
	{
		var hits = new List<FieldUsageHit>();
		await foreach (var (docKind, version, name, language) in VersionsAsync(kind, allVersions))
		{
			var paths = Count(FieldUsage.References(version.Html).Where(r => FieldUsage.Matches(r, query)));
			var via = docKind == "templates"
				? clauses.Used(version.Html, language)
					.Where(u => u.Version is not null && FieldUsage.References(u.Version.Html).Any(r => FieldUsage.Matches(r, query)))
					.Select(u => u.Reference.Name + " v" + u.Version!.Version + (u.Language == DocumentLanguages.English ? "" : " (" + u.Language.Code + ")"))
					.ToList()
				: [];
			if (paths.Count == 0 && via.Count == 0) continue;
			hits.Add(new FieldUsageHit(docKind, name, version.Version, version.Status, paths, via,
				language == DocumentLanguages.English ? null : language.Code));
		}
		return hits;
	}

	/// <summary>Every canonical path used, with the number of documents (not versions or languages) using it.</summary>
	public async Task<IReadOnlyList<FieldIndexEntry>> IndexAsync(bool allVersions)
	{
		var documents = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
		await foreach (var (docKind, version, name, _) in VersionsAsync(null, allVersions))
		{
			foreach (var reference in FieldUsage.References(version.Html))
			{
				if (!documents.TryGetValue(reference.Path, out var set)) documents[reference.Path] = set = [];
				set.Add(docKind + "/" + name);
			}
		}
		return documents.Select(d => new FieldIndexEntry(d.Key, d.Value.Count)).OrderBy(e => e.Path, StringComparer.Ordinal).ToList();
	}

	public static IReadOnlyList<PathCount> Count(IEnumerable<FieldReference> references) =>
		references.GroupBy(r => r.Path, StringComparer.Ordinal)
			.Select(g => new PathCount(g.Key, g.Count()))
			.OrderBy(p => p.Path, StringComparer.Ordinal)
			.ToList();

	private async IAsyncEnumerable<(string Kind, TemplateVersion Version, string Name, DocumentLanguage Language)> VersionsAsync(string? kind, bool allVersions)
	{
		foreach (var (docKind, english) in new[] { ("templates", templates), ("clauses", clauses.Versions) })
		{
			if (kind is not null && kind != docKind) continue;
			foreach (var summary in (await english.ListAsync()).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
			{
				foreach (var language in DocumentLanguages.All)
				{
					var store = english.Language(language.Code);
					var infos = await store.ListVersionsAsync(summary.Name);
					if (infos.Count == 0) continue;
					var latest = infos[^1].Version;
					var wanted = allVersions
						? infos.Select(i => i.Version)
						: infos.Where(i => i.Version == latest || i.Status == TemplateStatus.Published).Select(i => i.Version);
					foreach (var number in wanted.OrderByDescending(v => v))
					{
						if (await store.GetAsync(summary.Name, number) is { } version) yield return (docKind, version, summary.Name, language);
					}
				}
			}
		}
	}
}
