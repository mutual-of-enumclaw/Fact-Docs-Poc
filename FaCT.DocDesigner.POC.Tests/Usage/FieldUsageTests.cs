using FaCT.DocDesigner.POC.Templates;

namespace FaCT.DocDesigner.POC.Tests.Usage;

public sealed class FieldUsageTests
{
	private static string[] Paths(string html) => FieldUsage.References(html).Select(r => r.Path).ToArray();

	private static (string Path, string Written)[] Refs(string html) =>
		FieldUsage.References(html).Select(r => (r.Path, r.Written)).ToArray();

	[Fact]
	public void Outputs_are_references()
	{
		Assert.Equal(["policy.number", "insured.name"], Paths("<p>{{ policy.number }} for {{insured.name}}</p>"));
	}

	[Fact]
	public void Whitespace_control_is_understood()
	{
		Assert.Equal(["a.b", "c"], Paths("{{- a.b -}}{%- if c -%}x{%- endif -%}"));
	}

	[Fact]
	public void Filters_are_not_fields_but_their_arguments_are()
	{
		Assert.Equal(["policy.effective", "policy.fallback", "rate"],
			Paths("{{ policy.effective | date: '%b %d, %Y' | default: policy.fallback | times: rate | upcase }}"));
	}

	[Fact]
	public void String_and_number_literals_are_ignored()
	{
		Assert.Equal(["a"], Paths("{{ 'policy.number' }}{{ \"x.y\" }}{{ 3.14 }}{% if a == 'z' %}{% endif %}"));
	}

	[Fact]
	public void Conditions_in_if_elsif_unless_case_and_when_are_references()
	{
		Assert.Equal(["a", "b.c", "d", "e", "f", "g"],
			Paths("{% if a and b.c > 2 %}{% elsif d contains 'x' %}{% endif %}{% unless e == empty %}{% endunless %}{% case f %}{% when g %}{% endcase %}"));
	}

	[Fact]
	public void Liquid_keywords_are_not_fields()
	{
		Assert.Empty(Paths("{% if x == nil or y == blank or true and false %}{% endif %}".Replace("x", "nil").Replace("y", "empty")));
	}

	[Fact]
	public void Loop_aliases_resolve_to_their_list()
	{
		Assert.Equal(
			[("locations", "locations"), ("locations[].name", "location.name"), ("locations[].tiv", "location.tiv")],
			Refs("{% for location in locations %}<td>{{ location.name }}</td><td>{{ location.tiv | currency }}</td>{% endfor %}"));
	}

	[Fact]
	public void Nested_loops_resolve_through_each_alias()
	{
		Assert.Equal(
			["locations", "locations[].buildings", "locations[].buildings[].number", "locations[].name"],
			Paths("{% for location in locations %}{% for building in location.buildings %}{{ building.number }}{% endfor %}{{ location.name }}{% endfor %}"));
	}

	[Fact]
	public void An_alias_is_only_an_alias_inside_its_loop()
	{
		Assert.Equal(["claims", "claims[].amount", "claim.amount"],
			Paths("{% for claim in claims %}{{ claim.amount }}{% endfor %}{{ claim.amount }}"));
	}

	[Fact]
	public void Loop_parameters_and_tablerow_are_understood()
	{
		Assert.Equal(["items", "max", "items[].sku"],
			Paths("{% tablerow item in items limit: max cols: 3 %}{{ item.sku }}{% endtablerow %}"));
	}

	[Fact]
	public void Loop_position_is_not_a_field()
	{
		Assert.Equal(["rows"], Paths("{% for r in rows %}{% if forloop.first %}{{ forloop.index }}{% endif %}{% endfor %}"));
	}

	[Fact]
	public void Range_loops_open_a_scope_without_a_field()
	{
		Assert.Empty(Paths("{% for i in (1..3) %}{{ i }}{% endfor %}"));
	}

	[Fact]
	public void Assigned_and_captured_variables_are_not_message_fields()
	{
		Assert.Equal(["policy.total", "policy.number"],
			Paths("{% assign total = policy.total | plus: 1 %}{{ total }}{% capture label %}{{ policy.number }}{% endcapture %}{{ label }}"));
	}

	[Fact]
	public void Comment_and_raw_blocks_are_ignored()
	{
		Assert.Equal(["live"], Paths("{% comment %}{{ old.field }}{% endcomment %}{%- raw -%}{{ shown.literally }}{%- endraw -%}{{ live }}"));
	}

	[Fact]
	public void Includes_are_not_fields()
	{
		Assert.Empty(Paths("{% include 'std-exclusion' %}{% render 'other@2' %}"));
	}

	[Fact]
	public void Index_access_is_kept_as_written()
	{
		Assert.Equal(["locations[0].name"], Paths("{{ locations[0].name }}"));
	}

	[Fact]
	public void Html_entities_inside_liquid_are_decoded()
	{
		Assert.Equal(["policy.agent"], Paths("{{ policy.agent | default: &quot;n/a&quot; }}"));
	}

	[Fact]
	public void Designer_export_is_scanned()
	{
		const string html = "<body><span class=\"df\" data-field=\"policy.number\">{{ policy.number }}</span>" +
			"{% if policy.isRenewal %}<p>Renewal</p>{% endif %}<img src=\"{{ insured.logo }}\"/></body>";
		Assert.Equal(["policy.number", "policy.isRenewal", "insured.logo"], Paths(html));
	}

	[Theory]
	[InlineData("policy.number", "policy.number", true)]
	[InlineData("policy", "policy.number", true)]
	[InlineData("pol", "policy.number", false)]
	[InlineData("policy.numberX", "policy.number", false)]
	[InlineData("locations[].name", "locations[].name", true)]
	[InlineData("locations.name", "locations[].name", true)]
	[InlineData("locations", "locations[].name", true)]
	[InlineData("locations[0].name", "locations[].name", true)]
	[InlineData("location.name", "locations[].name", true)] // alias as written
	[InlineData("buildings", "locations[].buildings", false)]
	public void Matches_the_path_or_anything_under_it(string query, string path, bool expected)
	{
		var reference = path == "locations[].name" ? new FieldReference(path, "location.name") : new FieldReference(path, path);
		Assert.Equal(expected, FieldUsage.Matches(reference, query));
	}

	[Theory]
	[InlineData("policy.number", true)]
	[InlineData("locations[].name", true)]
	[InlineData("locations[0].name", true)]
	[InlineData("_x", true)]
	[InlineData("", false)]
	[InlineData(" policy", false)]
	[InlineData("policy..number", false)]
	[InlineData("policy.", false)]
	[InlineData("1abc", false)]
	[InlineData("a b", false)]
	[InlineData("{{ a }}", false)]
	public void Queries_must_be_data_paths(string query, bool valid)
	{
		Assert.Equal(valid, FieldUsage.IsValidQuery(query));
	}

	[Fact]
	public void Queries_are_at_most_200_characters()
	{
		Assert.False(FieldUsage.IsValidQuery(new string('a', 201)));
		Assert.False(FieldUsage.IsValidQuery(null));
	}

	[Fact]
	public void Normalize_drops_list_markers_and_indexes()
	{
		Assert.Equal("locations.buildings.number", FieldUsage.Normalize("locations[].buildings[3].number"));
	}
}
