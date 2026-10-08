using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace FaCT.DocDesigner.POC.Templates;

/// <summary>A clause as a template refers to it: <c>{% include 'name' %}</c> (published) or <c>'name@3'</c> (pinned).</summary>
public sealed record ClauseReference(string Name, int? Version)
{
	public override string ToString() => Version is null ? Name : Name + "@" + Version;
}

/// <summary>A clause version as a document uses it, and the language it is written in.</summary>
public sealed record ResolvedClause(TemplateVersion? Version, DocumentLanguage Language);

/// <summary>
/// Shared clauses: reusable pieces of wording/layout (standard exclusions, disclaimers, headers) designed once and
/// included by many templates with <c>{% include 'name' %}</c>. Clauses have the same Draft -> Published -> Retired
/// versions as templates (in App_Data/clauses). A template includes the clause's Published version at render time, so
/// publishing a clause updates every template that uses it; <c>'name@3'</c> pins a version (Published or Retired; drafts
/// are never rendered). Clause CSS is added to the document's CSS when the clause is used. A clause may have versions in
/// other languages (<see cref="TemplateStore.Language"/>): a Spanish document includes the Spanish clause when one can be
/// used, and the English one otherwise.
/// </summary>
public sealed partial class ClauseStore
{
	/// <summary>Clauses including clauses deeper than this are not followed (and Fluid refuses deeper recursion).</summary>
	public const int MaxDepth = 10;

	public ClauseStore(IWebHostEnvironment environment)
		: this(Path.Combine(environment.ContentRootPath, "App_Data", "clauses"))
	{
	}

	public ClauseStore(string root)
	{
		Versions = new TemplateStore(root);
		FileProvider = new ClauseFileProvider(this, DocumentLanguages.English);
	}

	/// <summary>Versions of each clause (same lifecycle and API as templates).</summary>
	public TemplateStore Versions { get; }

	/// <summary>Liquid partials for Fluid's include/render: "name.liquid" or "name@3.liquid".</summary>
	public IFileProvider FileProvider { get; }

	/// <summary>Liquid partials for a document in <paramref name="language"/>: its translation of a clause when it has one.</summary>
	public IFileProvider FileProviderFor(DocumentLanguage language) =>
		language == DocumentLanguages.English ? FileProvider : new ClauseFileProvider(this, language);

	/// <summary>Parses a partial path ("name", "name@3", optionally with .liquid and a leading slash).</summary>
	public static ClauseReference? Parse(string? path)
	{
		if (string.IsNullOrEmpty(path)) return null;
		var value = path.TrimStart('/', '\\');
		if (value.EndsWith(".liquid", StringComparison.OrdinalIgnoreCase)) value = value[..^".liquid".Length];
		var match = ReferencePattern().Match(value);
		if (!match.Success) return null;
		return new ClauseReference(match.Groups["name"].Value,
			match.Groups["version"].Success ? int.Parse(match.Groups["version"].Value, System.Globalization.CultureInfo.InvariantCulture) : null);
	}

	/// <summary>The version a reference renders: Published for a bare name; the pinned version unless it is a Draft.</summary>
	public TemplateVersion? Resolve(ClauseReference reference, DocumentLanguage? language = null) => ResolveIn(reference, language).Version;

	/// <summary>
	/// What a reference renders in a document written in <paramref name="language"/>: the clause's version in that language
	/// when it has one that can be used (same rules: Published, or the pinned version of that language), otherwise the
	/// English one. <see cref="ResolvedClause.Language"/> says which.
	/// </summary>
	public ResolvedClause ResolveIn(ClauseReference reference, DocumentLanguage? language)
	{
		if (language is not null && language != DocumentLanguages.English &&
			ResolveFrom(Versions.Language(language.Code), reference) is { } translated)
		{
			return new ResolvedClause(translated, language);
		}
		return new ResolvedClause(ResolveFrom(Versions, reference), DocumentLanguages.English);
	}

	private static TemplateVersion? ResolveFrom(TemplateStore store, ClauseReference reference)
	{
		var version = store.Read(reference.Name, reference.Version);
		return version is { Status: TemplateStatus.Draft } && reference.Version is not null ? null : version;
	}

	/// <summary>The clauses a piece of template HTML includes directly, in order of first use.</summary>
	public static IReadOnlyList<ClauseReference> References(string html) =>
		IncludeTag().Matches(html)
			.Select(m => Parse(m.Groups["path"].Value))
			.OfType<ClauseReference>()
			.Distinct()
			.ToList();

	/// <summary>
	/// Every clause the HTML uses, nested ones too (each once), with the version that renders (null = missing) and the
	/// language it is in (a document in another language falls back to English clauses that aren't translated).
	/// </summary>
	public IReadOnlyList<(ClauseReference Reference, TemplateVersion? Version, DocumentLanguage Language)> Used(string html, DocumentLanguage? language = null)
	{
		var used = new List<(ClauseReference, TemplateVersion?, DocumentLanguage)>();
		var seen = new HashSet<ClauseReference>();
		void Walk(string content, int depth)
		{
			if (depth > MaxDepth) return;
			foreach (var reference in References(content))
			{
				if (!seen.Add(reference)) continue;
				var resolved = ResolveIn(reference, language);
				used.Add((reference, resolved.Version, resolved.Language));
				if (resolved.Version is not null) Walk(resolved.Version.Html, depth + 1);
			}
		}
		Walk(html, 0);
		return used;
	}

	/// <summary>The CSS of every clause the HTML uses, to go with the template's own CSS.</summary>
	public string CssFor(string html, DocumentLanguage? language = null)
	{
		var css = new StringBuilder();
		foreach (var (reference, version, _) in Used(html, language))
		{
			if (version is null || string.IsNullOrWhiteSpace(version.Css)) continue;
			css.Append("/* clause ").Append(reference).Append(" v").Append(version.Version).Append(" */\n").Append(version.Css).Append('\n');
		}
		return css.ToString();
	}

	/// <summary>Whether a clause's HTML includes the clause itself (directly or through other clauses).</summary>
	public bool IncludesItself(string name, string html) =>
		References(html).Any(r => r.Name == name) || Used(html).Any(u => u.Version is not null && References(u.Version.Html).Any(r => r.Name == name));

	[GeneratedRegex(@"^(?<name>[A-Za-z0-9_-]{1,64})(?:@(?<version>[1-9][0-9]{0,5}))?$")]
	private static partial Regex ReferencePattern();

	[GeneratedRegex(@"\{%-?\s*(?:include|render)\s+(?<q>['""])(?<path>[^'""]{1,80})\k<q>")]
	private static partial Regex IncludeTag();

	private sealed class ClauseFileProvider(ClauseStore clauses, DocumentLanguage language) : IFileProvider
	{
		public IFileInfo GetFileInfo(string subpath) =>
			Parse(subpath) is { } reference && clauses.Resolve(reference, language) is { } version
				? new ClauseFile(reference.ToString() + ".liquid", version)
				: new NotFoundFileInfo(subpath);

		public IDirectoryContents GetDirectoryContents(string subpath) => NotFoundDirectoryContents.Singleton;

		public IChangeToken Watch(string filter) => NullChangeToken.Singleton;
	}

	private sealed class ClauseFile(string name, TemplateVersion version) : IFileInfo
	{
		private readonly byte[] _content = Encoding.UTF8.GetBytes(version.Html);

		public bool Exists => true;
		public long Length => _content.Length;
		public string? PhysicalPath => null;
		public string Name => name;
		public DateTimeOffset LastModified => version.PublishedUtc ?? version.SavedUtc;
		public bool IsDirectory => false;
		public Stream CreateReadStream() => new MemoryStream(_content, writable: false);
	}
}
