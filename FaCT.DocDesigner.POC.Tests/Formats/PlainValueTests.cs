using System.Text.Json;
using FaCT.DocDesigner.POC.Rendering;
using FaCT.DocDesigner.POC.Tests.Clauses;
using Microsoft.Extensions.DependencyInjection;

namespace FaCT.DocDesigner.POC.Tests.Formats;

/// <summary>Plain values print the way DocGen's Word filler prints them: true/false as Yes/No, numbers without trailing zeros.</summary>
public sealed class PlainValueTests(ClauseAppFactory factory) : IClassFixture<ClauseAppFactory>
{
	private static readonly JsonElement Data = JsonDocument.Parse("""
		{ "flags": { "sprinklered": true, "vacant": false },
		  "amounts": { "whole": 1200, "point": 1200.0, "trailing": 1234.50, "fraction": 1234.5, "small": 0.25, "zero": 0.0, "negative": -15.10 },
		  "list": [ { "on": true }, { "on": false } ] }
		""").RootElement.Clone();

	private DocumentComposer Composer => factory.Services.GetRequiredService<DocumentComposer>();

	private async Task<string> RenderAsync(string liquid)
	{
		var result = await Composer.ComposeAsync($"<p>{liquid}</p>", "", Data);
		Assert.True(result.Error is null, result.Error);
		var body = result.Html![(result.Html.IndexOf("<p>", StringComparison.Ordinal) + 3)..];
		return body[..body.IndexOf("</p>", StringComparison.Ordinal)];
	}

	[Theory]
	[InlineData("{{ flags.sprinklered }}", "Yes")]
	[InlineData("{{ flags.vacant }}", "No")]
	[InlineData("{{ flags.sprinklered | upcase }}", "YES")]
	[InlineData("{% for i in list %}{{ i.on }} {% endfor %}", "Yes No ")]
	public async Task Booleans_print_yes_and_no(string liquid, string expected)
	{
		Assert.Equal(expected, await RenderAsync(liquid));
	}

	[Theory]
	[InlineData("{% if flags.sprinklered %}A{% else %}B{% endif %}", "A")]
	[InlineData("{% if flags.vacant %}A{% else %}B{% endif %}", "B")]
	[InlineData("{% if flags.sprinklered == true %}A{% else %}B{% endif %}", "A")]
	[InlineData("{% if flags.vacant == false %}A{% else %}B{% endif %}", "A")]
	[InlineData("{% if flags.vacant != true %}A{% else %}B{% endif %}", "A")]
	[InlineData("{% if flags.sprinklered != blank %}A{% else %}B{% endif %}", "A")]
	[InlineData("{% if flags.vacant == blank %}A{% else %}B{% endif %}", "A")]
	[InlineData("{% unless flags.vacant %}A{% endunless %}", "A")]
	[InlineData("{% if flags.sprinklered and flags.vacant %}A{% else %}B{% endif %}", "B")]
	[InlineData("{% if flags.sprinklered or flags.vacant %}A{% else %}B{% endif %}", "A")]
	[InlineData("{% assign n = list | where: 'on', true %}{{ n.size }}", "1")]
	public async Task Booleans_still_work_in_conditions(string liquid, string expected)
	{
		Assert.Equal(expected, await RenderAsync(liquid));
	}

	[Theory]
	[InlineData("{{ amounts.whole }}", "1200")]
	[InlineData("{{ amounts.point }}", "1200")]
	[InlineData("{{ amounts.trailing }}", "1234.5")]
	[InlineData("{{ amounts.fraction }}", "1234.5")]
	[InlineData("{{ amounts.small }}", "0.25")]
	[InlineData("{{ amounts.zero }}", "0")]
	[InlineData("{{ amounts.negative }}", "-15.1")]
	[InlineData("{{ amounts.trailing | currency }}", "$1,234.50")]
	[InlineData("{{ amounts.point | decimal }}", "1,200.00")]
	[InlineData("{{ amounts.point | plus: 1 }}", "1201")]
	[InlineData("{% if amounts.point == 1200 %}A{% endif %}", "A")]
	public async Task Numbers_print_without_trailing_zeros(string liquid, string expected)
	{
		Assert.Equal(expected, await RenderAsync(liquid));
	}
}
