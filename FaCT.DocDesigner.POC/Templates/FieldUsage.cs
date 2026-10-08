using System.Text.RegularExpressions;

namespace FaCT.DocDesigner.POC.Templates;

/// <summary>
/// A data path a template reads. <see cref="Path"/> is canonical: loop aliases are replaced by the list they iterate
/// ("location.name" inside <c>{% for location in locations %}</c> is "locations[].name"); <see cref="Written"/> is the
/// path as it appears in the template.
/// </summary>
public sealed record FieldReference(string Path, string Written);

/// <summary>
/// Finds the message-data paths a template (HTML + Liquid) reads, for "which templates use <c>policy.number</c>?".
/// A light Liquid scanner: outputs, filter arguments, if/elsif/unless/case/when conditions, for/tablerow lists (with
/// their aliases resolved), assign/capture locals skipped, comment/raw blocks and string literals ignored.
/// </summary>
public static partial class FieldUsage
{
	private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
	{
		"and", "or", "not", "contains", "true", "false", "nil", "null", "empty", "blank", "in", "reversed", "limit", "offset",
		"with", "as", "forloop", "tablerowloop", "cols"
	};

	public static IReadOnlyList<FieldReference> References(string html)
	{
		var references = new List<FieldReference>();
		var aliases = new List<(string Alias, string Path, string Tag)>();
		var locals = new HashSet<string>(StringComparer.Ordinal);
		var text = IgnoredBlocks().Replace(html ?? string.Empty, string.Empty);

		void Add(string expression)
		{
			foreach (var written in Paths(expression))
			{
				var head = written.Split('.', '[')[0];
				if (Keywords.Contains(head) || locals.Contains(head)) continue;
				// The alias of a loop over a range or a local list isn't message data.
				if (aliases.FindLast(a => a.Alias == head) is { Alias: not null, Path.Length: 0 }) continue;
				references.Add(new FieldReference(Canonical(written, aliases), written));
			}
		}

		foreach (Match token in Token().Matches(text))
		{
			if (token.Groups["out"].Success)
			{
				Add(token.Groups["out"].Value);
				continue;
			}
			var args = token.Groups["args"].Value;
			switch (token.Groups["tag"].Value)
			{
				case "if" or "elsif" or "unless" or "case" or "when" or "echo":
					Add(args);
					break;
				case "for" or "tablerow":
					var loop = Loop().Match(args);
					if (!loop.Success) break;
					var list = loop.Groups["list"].Value;
					var listPath = Paths(list).FirstOrDefault();
					string? canonical = null;
					if (listPath is not null && !Keywords.Contains(listPath.Split('.', '[')[0]) && !locals.Contains(listPath.Split('.', '[')[0]))
					{
						canonical = Canonical(listPath, aliases);
						references.Add(new FieldReference(canonical, listPath));
					}
					Add(loop.Groups["rest"].Value);
					// A range or a local list still opens a scope, so its alias isn't mistaken for message data.
					aliases.Add((loop.Groups["alias"].Value, canonical is null ? "" : canonical + "[]", token.Groups["tag"].Value));
					break;
				case "endfor" or "endtablerow":
					var open = token.Groups["tag"].Value == "endfor" ? "for" : "tablerow";
					var index = aliases.FindLastIndex(a => a.Tag == open);
					if (index >= 0) aliases.RemoveAt(index);
					break;
				case "assign":
					var assign = Assign().Match(args);
					if (!assign.Success) break;
					Add(assign.Groups["expr"].Value);
					locals.Add(assign.Groups["name"].Value);
					break;
				case "capture":
					var name = args.Trim();
					if (name.Length > 0) locals.Add(name.Trim('\'', '"'));
					break;
			}
		}
		return references;
	}

	/// <summary>"locations[].name" / "locations[0].name" -> "locations.name": how paths are compared.</summary>
	public static string Normalize(string path) => Indexer().Replace(path ?? string.Empty, string.Empty);

	/// <summary>Whether a reference is the queried path or below it (canonical or as written).</summary>
	public static bool Matches(FieldReference reference, string query)
	{
		var q = Normalize(query);
		return Within(Normalize(reference.Path), q) || Within(Normalize(reference.Written), q);
	}

	public static bool IsValidQuery(string? query) => query is { Length: > 0 and <= 200 } && QueryPattern().IsMatch(query);

	private static bool Within(string path, string query) =>
		path == query || path.StartsWith(query + ".", StringComparison.Ordinal) || path.StartsWith(query + "[", StringComparison.Ordinal);

	private static string Canonical(string written, List<(string Alias, string Path, string Tag)> aliases)
	{
		var head = written.Split('.', '[')[0];
		for (var i = aliases.Count - 1; i >= 0; i--)
		{
			if (aliases[i].Alias != head) continue;
			return aliases[i].Path.Length == 0 ? written : aliases[i].Path + written[head.Length..];
		}
		return written;
	}

	// The variable paths in an expression: string literals dropped, filter names skipped (their arguments kept). Exported
	// HTML may carry entities inside Liquid ("&quot;n/a&quot;"), so it's decoded first.
	private static IEnumerable<string> Paths(string expression)
	{
		var segments = StringLiteral().Replace(System.Net.WebUtility.HtmlDecode(expression), "''").Split('|');
		for (var i = 0; i < segments.Length; i++)
		{
			var segment = i == 0 ? segments[i] : FilterName().Replace(segments[i], string.Empty, 1);
			foreach (Match m in PathToken().Matches(segment)) yield return m.Value;
		}
	}

	[GeneratedRegex(@"\{%-?\s*(comment|raw)\s*-?%\}.*?\{%-?\s*end\1\s*-?%\}", RegexOptions.Singleline)]
	private static partial Regex IgnoredBlocks();

	[GeneratedRegex(@"\{\{-?(?<out>.*?)-?\}\}|\{%-?\s*(?<tag>[a-z]+)(?<args>.*?)-?%\}", RegexOptions.Singleline)]
	private static partial Regex Token();

	[GeneratedRegex(@"^\s*(?<alias>[A-Za-z_][A-Za-z0-9_]*)\s+in\s+(?<list>\([^)]*\)|[^\s]+)(?<rest>.*)$", RegexOptions.Singleline)]
	private static partial Regex Loop();

	[GeneratedRegex(@"^\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=(?<expr>.*)$", RegexOptions.Singleline)]
	private static partial Regex Assign();

	[GeneratedRegex(@"'[^']*'|""[^""]*""")]
	private static partial Regex StringLiteral();

	[GeneratedRegex(@"^\s*[A-Za-z_][A-Za-z0-9_]*\s*:?")]
	private static partial Regex FilterName();

	[GeneratedRegex(@"(?<![\w.\]'])[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*|\[\d+\])*")]
	private static partial Regex PathToken();

	[GeneratedRegex(@"\[\d*\]")]
	private static partial Regex Indexer();

	[GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*(?:\[\d*\])?(?:\.[A-Za-z_][A-Za-z0-9_]*(?:\[\d*\])?)*$")]
	private static partial Regex QueryPattern();
}
