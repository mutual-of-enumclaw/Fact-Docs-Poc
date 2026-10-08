using System.Text;
using System.Text.Json;
using FaCT.DocDesigner.POC.Templates;

namespace FaCT.DocDesigner.POC.Tests.Clauses;

/// <summary>A clause (or template) store in a temp folder, deleted afterwards.</summary>
internal sealed class TempStores : IDisposable
{
	private static readonly JsonElement EmptyProject = JsonDocument.Parse("{}").RootElement.Clone();

	public TempStores()
	{
		Root = Path.Combine(Path.GetTempPath(), "clause-tests-" + Guid.NewGuid().ToString("N"));
		Clauses = new ClauseStore(ClausesRoot);
		Templates = new TemplateStore(TemplatesRoot);
	}

	public string Root { get; }
	public string ClausesRoot => Path.Combine(Root, "clauses");
	public string TemplatesRoot => Path.Combine(Root, "templates");
	public string ScenariosRoot => Path.Combine(Root, "scenarios");
	public string BlocksRoot => Path.Combine(Root, "blocks");
	public string CommentsRoot => Path.Combine(Root, "comments");
	public string SpellingRoot => Path.Combine(Root, "spelling");
	public string AuditRoot => Path.Combine(Root, "audit");
	public string ThemesRoot => Path.Combine(Root, "themes");
	public ClauseStore Clauses { get; }
	public TemplateStore Templates { get; }

	/// <summary>Saves a draft of the clause and returns its version.</summary>
	public int Draft(string name, string html, string css = "") =>
		Clauses.Versions.SaveDraftAsync(name, EmptyProject, html, css, null).GetAwaiter().GetResult().Version;

	/// <summary>Saves and publishes a new version of the clause and returns it.</summary>
	public int Publish(string name, string html, string css = "")
	{
		var version = Draft(name, html, css);
		Clauses.Versions.PublishAsync(name, version).GetAwaiter().GetResult();
		return version;
	}

	public int PublishTemplate(string name, string html)
	{
		var version = Templates.SaveDraftAsync(name, EmptyProject, html, "", null).GetAwaiter().GetResult().Version;
		Templates.PublishAsync(name, version).GetAwaiter().GetResult();
		return version;
	}

	public void Dispose()
	{
		try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
	}
}

public sealed class ClauseStoreTests : IDisposable
{
	private readonly TempStores _stores = new();
	private ClauseStore Clauses => _stores.Clauses;

	public void Dispose() => _stores.Dispose();

	// ---- Parse ---------------------------------------------------------------------------------------------------

	[Theory]
	[InlineData("std-exclusion", "std-exclusion", null)]
	[InlineData("std-exclusion@3", "std-exclusion", 3)]
	[InlineData("std_exclusion.liquid", "std_exclusion", null)]
	[InlineData("std@12.liquid", "std", 12)]
	[InlineData("/std.liquid", "std", null)]
	[InlineData("\\std@2.LIQUID", "std", 2)]
	[InlineData("A1-b_2", "A1-b_2", null)]
	public void Parse_accepts_names_and_pinned_versions(string path, string name, int? version)
	{
		Assert.Equal(new ClauseReference(name, version), ClauseStore.Parse(path));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("../templates/x")]
	[InlineData("sub/std")]
	[InlineData("std clause")]
	[InlineData("std@0")]
	[InlineData("std@-1")]
	[InlineData("std@x")]
	[InlineData("std@1234567")]
	[InlineData("std@")]
	[InlineData("@3")]
	[InlineData("std.html")]
	[InlineData("C:\\temp\\std")]
	public void Parse_rejects_anything_that_is_not_a_clause_name(string? path)
	{
		Assert.Null(ClauseStore.Parse(path));
	}

	[Fact]
	public void Parse_rejects_names_longer_than_64_characters()
	{
		Assert.NotNull(ClauseStore.Parse(new string('a', 64)));
		Assert.Null(ClauseStore.Parse(new string('a', 65)));
	}

	[Fact]
	public void Reference_prints_as_the_include_path()
	{
		Assert.Equal("std", new ClauseReference("std", null).ToString());
		Assert.Equal("std@4", new ClauseReference("std", 4).ToString());
	}

	// ---- References ----------------------------------------------------------------------------------------------

	[Fact]
	public void References_finds_include_and_render_tags_in_order_once_each()
	{
		var html = "<p>{% include 'b' %}</p>{%- render \"a@2\" -%}{% include 'b' %}{%include 'c'%}<div>{% render 'a@2' %}</div>";
		Assert.Equal(
			[new ClauseReference("b", null), new ClauseReference("a", 2), new ClauseReference("c", null)],
			ClauseStore.References(html));
	}

	[Fact]
	public void References_keeps_pinned_and_latest_uses_of_one_clause_apart()
	{
		Assert.Equal(
			[new ClauseReference("a", null), new ClauseReference("a", 3)],
			ClauseStore.References("{% include 'a' %}{% include 'a@3' %}"));
	}

	[Fact]
	public void References_ignores_text_and_tags_that_are_not_clause_includes()
	{
		var html = "<p>include 'a'</p>{{ 'b' }}{% assign x = 'c' %}{% include '../etc/passwd' %}{% include 'a b' %}{% include x %}";
		Assert.Empty(ClauseStore.References(html));
	}

	// ---- Resolve -------------------------------------------------------------------------------------------------

	[Fact]
	public void Resolve_unknown_clause_is_null()
	{
		Assert.Null(Clauses.Resolve(new ClauseReference("nope", null)));
		Assert.Null(Clauses.Resolve(new ClauseReference("nope", 1)));
	}

	[Fact]
	public void Resolve_never_returns_a_draft()
	{
		_stores.Draft("std", "<p>draft</p>");
		Assert.Null(Clauses.Resolve(new ClauseReference("std", null)));
		Assert.Null(Clauses.Resolve(new ClauseReference("std", 1)));
	}

	[Fact]
	public void Resolve_bare_name_is_the_published_version_even_with_a_newer_draft()
	{
		_stores.Publish("std", "<p>one</p>");
		_stores.Draft("std", "<p>two</p>");
		var resolved = Clauses.Resolve(new ClauseReference("std", null));
		Assert.Equal(1, resolved!.Version);
		Assert.Equal("<p>one</p>", resolved.Html);
	}

	[Fact]
	public void Resolve_pinned_version_works_for_published_and_retired_versions()
	{
		_stores.Publish("std", "<p>one</p>");
		_stores.Publish("std", "<p>two</p>");
		Assert.Equal("<p>one</p>", Clauses.Resolve(new ClauseReference("std", 1))!.Html);
		Assert.Equal(TemplateStatus.Retired, Clauses.Resolve(new ClauseReference("std", 1))!.Status);
		Assert.Equal("<p>two</p>", Clauses.Resolve(new ClauseReference("std", 2))!.Html);
		Assert.Equal("<p>two</p>", Clauses.Resolve(new ClauseReference("std", null))!.Html);
		Assert.Null(Clauses.Resolve(new ClauseReference("std", 3)));
	}

	[Fact]
	public void Resolve_follows_a_rollback()
	{
		_stores.Publish("std", "<p>one</p>");
		_stores.Publish("std", "<p>two</p>");
		Clauses.Versions.PublishAsync("std", 1).GetAwaiter().GetResult();
		Assert.Equal("<p>one</p>", Clauses.Resolve(new ClauseReference("std", null))!.Html);
	}

	// ---- Used / CssFor -------------------------------------------------------------------------------------------

	[Fact]
	public void Used_lists_nested_clauses_once_with_what_renders()
	{
		_stores.Publish("outer", "<div>{% include 'inner' %}{% include 'missing' %}</div>");
		_stores.Publish("inner", "<p>inner</p>");
		var used = Clauses.Used("{% include 'outer' %}{% include 'inner' %}");
		Assert.Equal(["outer", "inner", "missing"], used.Select(u => u.Reference.ToString()));
		Assert.NotNull(used[0].Version);
		Assert.NotNull(used[1].Version);
		Assert.Null(used[2].Version);
	}

	[Fact]
	public void Used_stops_at_clauses_that_include_each_other()
	{
		_stores.Publish("a", "{% include 'b' %}");
		_stores.Publish("b", "{% include 'a' %}");
		var used = Clauses.Used("{% include 'a' %}");
		Assert.Equal(["a", "b"], used.Select(u => u.Reference.ToString()));
	}

	[Fact]
	public void Used_does_not_follow_deeper_than_the_maximum_depth()
	{
		for (var i = 0; i <= ClauseStore.MaxDepth + 3; i++) _stores.Publish("c" + i, "{% include 'c" + (i + 1) + "' %}");
		var used = Clauses.Used("{% include 'c0' %}");
		Assert.Equal(ClauseStore.MaxDepth + 1, used.Count);
	}

	[Fact]
	public void CssFor_collects_the_css_of_every_clause_used_including_nested()
	{
		_stores.Publish("outer", "{% include 'inner' %}", ".outer{color:red}");
		_stores.Publish("inner", "<p>x</p>", ".inner{color:blue}");
		_stores.Publish("plain", "<p>no css</p>");
		var css = Clauses.CssFor("{% include 'outer' %}{% include 'plain' %}");
		Assert.Contains(".outer{color:red}", css);
		Assert.Contains(".inner{color:blue}", css);
		Assert.Contains("/* clause outer v1 */", css);
		Assert.Contains("/* clause inner v1 */", css);
		Assert.DoesNotContain("plain", css);
	}

	[Fact]
	public void CssFor_uses_the_css_of_the_version_that_renders()
	{
		_stores.Publish("std", "<p>1</p>", ".v1{}");
		_stores.Publish("std", "<p>2</p>", ".v2{}");
		_stores.Draft("std", "<p>3</p>", ".v3{}");
		Assert.Contains(".v2{}", Clauses.CssFor("{% include 'std' %}"));
		Assert.DoesNotContain(".v3{}", Clauses.CssFor("{% include 'std' %}"));
		Assert.Contains(".v1{}", Clauses.CssFor("{% include 'std@1' %}"));
		Assert.Equal("", Clauses.CssFor("<p>no clauses</p>"));
	}

	// ---- IncludesItself ------------------------------------------------------------------------------------------

	[Fact]
	public void IncludesItself_catches_direct_pinned_and_indirect_self_includes()
	{
		_stores.Publish("b", "{% include 'a' %}");
		_stores.Publish("c", "{% include 'b' %}");
		Assert.True(Clauses.IncludesItself("a", "{% include 'a' %}"));
		Assert.True(Clauses.IncludesItself("a", "{% render 'a@2' %}"));
		Assert.True(Clauses.IncludesItself("a", "{% include 'b' %}"));
		Assert.True(Clauses.IncludesItself("a", "{% include 'c' %}"));
		Assert.False(Clauses.IncludesItself("a", "{% include 'd' %}<p>a</p>"));
		Assert.False(Clauses.IncludesItself("b", "{% include 'c2' %}"));
	}

	// ---- FileProvider (what Fluid reads) -------------------------------------------------------------------------

	[Fact]
	public void FileProvider_serves_the_published_clause_as_a_liquid_file()
	{
		_stores.Publish("std", "<p>Standard wording</p>");
		var file = Clauses.FileProvider.GetFileInfo("std.liquid");
		Assert.True(file.Exists);
		Assert.Equal("std.liquid", file.Name);
		Assert.Null(file.PhysicalPath);
		Assert.False(file.IsDirectory);
		using var reader = new StreamReader(file.CreateReadStream(), Encoding.UTF8);
		Assert.Equal("<p>Standard wording</p>", reader.ReadToEnd());
		Assert.Equal(Encoding.UTF8.GetByteCount("<p>Standard wording</p>"), file.Length);
	}

	[Fact]
	public void FileProvider_last_modified_changes_when_another_version_is_published()
	{
		_stores.Publish("std", "<p>1</p>");
		var first = Clauses.FileProvider.GetFileInfo("std.liquid").LastModified;
		Thread.Sleep(20);
		_stores.Publish("std", "<p>2</p>");
		Assert.NotEqual(first, Clauses.FileProvider.GetFileInfo("std.liquid").LastModified);
	}

	[Theory]
	[InlineData("missing.liquid")]
	[InlineData("draft-only.liquid")]
	[InlineData("draft-only@1.liquid")]
	[InlineData("std@9.liquid")]
	[InlineData("../templates/std.liquid")]
	[InlineData("")]
	public void FileProvider_reports_missing_drafts_and_bad_paths_as_not_found(string path)
	{
		_stores.Publish("std", "<p>x</p>");
		_stores.Draft("draft-only", "<p>d</p>");
		Assert.False(Clauses.FileProvider.GetFileInfo(path).Exists);
	}

	[Fact]
	public void FileProvider_has_no_directory_listing_and_no_watching()
	{
		Assert.False(Clauses.FileProvider.GetDirectoryContents("").Exists);
		Assert.False(Clauses.FileProvider.Watch("*").HasChanged);
	}

	[Fact]
	public void Clauses_and_templates_with_the_same_name_are_kept_apart()
	{
		_stores.Publish("shared-name", "<p>clause</p>");
		_stores.PublishTemplate("shared-name", "<p>template</p>");
		Assert.Equal("<p>clause</p>", Clauses.Resolve(new ClauseReference("shared-name", null))!.Html);
		Assert.Equal("<p>template</p>", _stores.Templates.Read("shared-name", null)!.Html);
	}

	[Fact]
	public void TemplateStore_Read_returns_published_or_the_requested_version()
	{
		_stores.PublishTemplate("t", "<p>1</p>");
		_stores.Templates.SaveDraftAsync("t", JsonDocument.Parse("{}").RootElement, "<p>2</p>", "", null).GetAwaiter().GetResult();
		Assert.Equal("<p>1</p>", _stores.Templates.Read("t", null)!.Html);
		Assert.Equal("<p>2</p>", _stores.Templates.Read("t", 2)!.Html);
		Assert.Null(_stores.Templates.Read("t", 3));
		Assert.Null(_stores.Templates.Read("nope", null));
		Assert.Null(_stores.Templates.Read("../x", null));
	}
}
