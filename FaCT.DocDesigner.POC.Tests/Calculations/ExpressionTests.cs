using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using FaCT.DocDesigner.POC.Rendering;
using FaCT.DocDesigner.POC.Templates;
using FaCT.DocDesigner.POC.Tests.Clauses;
using Microsoft.Extensions.DependencyInjection;

namespace FaCT.DocDesigner.POC.Tests.Calculations;

/// <summary>The expression language: what compiles, what is refused (with where), and the Liquid it produces.</summary>
public sealed class ExpressionCompilerTests
{
	private static ExpressionException Fails(string expression, string[]? lists = null) =>
		Assert.Throws<ExpressionException>(() => ExpressionCompiler.Compile(expression, lists));

	[Fact]
	public void A_single_path_is_assigned_to_the_result()
	{
		var compiled = ExpressionCompiler.Compile("policy.premium");
		Assert.Equal("{%- assign calc_result = policy.premium | calc_value -%}", compiled.Liquid);
		Assert.Equal(ExpressionCompiler.ResultVariable, compiled.Result);
		Assert.Equal(["policy.premium"], compiled.Paths);
		Assert.Empty(compiled.Lists);
	}

	[Fact]
	public void Operations_become_assigns_of_temporaries()
	{
		var compiled = ExpressionCompiler.Compile("a + b - c");
		Assert.Equal(
			"{%- assign calc_t1 = a | plus: b -%}{%- assign calc_t2 = calc_t1 | minus: c -%}{%- assign calc_result = calc_t2 | calc_value -%}",
			compiled.Liquid);
		Assert.Equal(["a", "b", "c"], compiled.Paths);
	}

	[Fact]
	public void Division_never_divides_by_zero_or_nothing()
	{
		var liquid = ExpressionCompiler.Compile("a / b").Liquid;
		Assert.Contains("{%- assign calc_t1 = 0 -%}{%- unless b == 0 or b == blank -%}{%- assign calc_t2 = b | times: 1.0 -%}{%- assign calc_t1 = a | divided_by: calc_t2 -%}{%- endunless -%}", liquid);
	}

	[Theory]
	[InlineData("a \u00d7 b", "times")]
	[InlineData("a \u00f7 b", "divided_by")]
	[InlineData("a \u2212 b", "minus")]
	public void Typographic_operators_are_accepted(string expression, string filter)
	{
		Assert.Contains(filter, ExpressionCompiler.Compile(expression).Liquid);
	}

	[Fact]
	public void Paths_may_index_lists_and_text_may_use_either_quote()
	{
		var compiled = ExpressionCompiler.Compile("concat(locations[0].name, ' - ', \"it's\")");
		Assert.Contains("locations[0].name", compiled.Liquid);
		Assert.Contains("\"it's\"", compiled.Liquid);
		Assert.Contains("' - '", compiled.Liquid);
	}

	[Fact]
	public void Sum_count_and_average_use_the_known_lists()
	{
		var compiled = ExpressionCompiler.Compile("sum(lossRatio.claims.totalLoss) + count(locations) + average(location.buildings.tiv)",
			["lossRatio.claims", "locations", "location.buildings"]);
		Assert.Contains("lossRatio.claims | sum: 'totalLoss'", compiled.Liquid);
		Assert.Contains("locations | size", compiled.Liquid);
		Assert.Contains("location.buildings | sum: 'tiv'", compiled.Liquid);
		Assert.Contains("location.buildings | size", compiled.Liquid);
		Assert.Equal(["lossRatio.claims", "locations", "location.buildings"], compiled.Lists);
		Assert.Empty(compiled.Paths);
	}

	[Fact]
	public void Sum_of_a_nested_property_and_of_a_plain_list()
	{
		Assert.Contains("claims | sum: 'payment.amount'", ExpressionCompiler.Compile("sum(claims.payment.amount)", ["claims"]).Liquid);
		Assert.Contains("amounts | sum: ''", ExpressionCompiler.Compile("sum(amounts)", ["amounts"]).Liquid);
	}

	[Fact]
	public void Without_known_lists_the_last_segment_is_the_property()
	{
		Assert.Contains("lossRatio.claims | sum: 'totalLoss'", ExpressionCompiler.Compile("sum(lossRatio.claims.totalLoss)").Liquid);
		Assert.Contains("lossRatio.claims | size", ExpressionCompiler.Compile("count(lossRatio.claims)").Liquid);
	}

	[Fact]
	public void Min_max_round_abs_default_and_concat()
	{
		Assert.Contains("a | at_most: b | at_most: c", ExpressionCompiler.Compile("min(a, b, c)").Liquid);
		Assert.Contains("a | at_least: 100", ExpressionCompiler.Compile("max(a, 100)").Liquid);
		Assert.Contains("a | calc_round: 2", ExpressionCompiler.Compile("round(a, 2)").Liquid);
		Assert.Contains("a | calc_round: 0", ExpressionCompiler.Compile("round(a)").Liquid);
		Assert.Contains("a | abs", ExpressionCompiler.Compile("abs(a)").Liquid);
		Assert.Contains("a | default: 'n/a'", ExpressionCompiler.Compile("default(a, 'n/a')").Liquid);
		Assert.Contains("first | append: '' | append: ' ' | append: last | append: '3'", ExpressionCompiler.Compile("concat(first, ' ', last, 3)").Liquid);
	}

	[Fact]
	public void Function_names_ignore_case()
	{
		Assert.Contains("| size", ExpressionCompiler.Compile("COUNT(locations)").Liquid);
	}

	[Fact]
	public void Precedence_and_parentheses()
	{
		// a + b * c: the multiplication first.
		Assert.StartsWith("{%- assign calc_t1 = b | times: c -%}{%- assign calc_t2 = a | plus: calc_t1 -%}", ExpressionCompiler.Compile("a + b * c").Liquid);
		// (a + b) * c: the addition first.
		Assert.StartsWith("{%- assign calc_t1 = a | plus: b -%}{%- assign calc_t2 = calc_t1 | times: c -%}", ExpressionCompiler.Compile("(a + b) * c").Liquid);
		Assert.StartsWith("{%- assign calc_t1 = 0 | minus: a -%}", ExpressionCompiler.Compile("-a").Liquid);
		Assert.Equal("{%- assign calc_result = a | calc_value -%}", ExpressionCompiler.Compile("+a").Liquid);
	}

	[Theory]
	[InlineData("", "Type a calculation", 0)]
	[InlineData("   ", "Type a calculation", 0)]
	[InlineData("a +", "ends too early", 3)]
	[InlineData("(a + b", "Missing ')'", 6)]
	[InlineData("a + b)", "without a matching '('", 5)]
	[InlineData("a b", "Expected an operator", 2)]
	[InlineData("a + $", "Unexpected '$'", 4)]
	[InlineData("a + 'x", "closing quote", 4)]
	[InlineData("explode(a)", "Unknown function 'explode'", 0)]
	[InlineData("sum(a, b)", "sum() takes 1 value", 0)]
	[InlineData("min(a)", "min() takes at least 2 values", 0)]
	[InlineData("round(a, b)", "number of decimal places", 9)]
	[InlineData("round(a, 9)", "number of decimal places", 9)]
	[InlineData("a + 'text'", "Use concat", 4)]
	[InlineData("calc_t1 + 1", "reserved", 0)]
	[InlineData("1 + empty.x", "reserved word", 4)]
	[InlineData("nil", "reserved word", 0)]
	[InlineData("'{{ x }}'", "can't contain {", 0)]
	[InlineData("'%'", "can't contain {", 0)]
	[InlineData("sum(1)", "takes a list field", 4)]
	[InlineData("sum(claims[0].amount)", "without [ ]", 4)]
	[InlineData("count(claims.amount)", "count() takes a list", 0)]
	public void Bad_expressions_explain_what_is_wrong_and_where(string expression, string message, int position)
	{
		var error = Fails(expression, expression.StartsWith("count", StringComparison.Ordinal) ? ["claims"] : null);
		Assert.Contains(message, error.Message);
		Assert.Equal(position, error.Position);
	}

	[Fact]
	public void A_path_that_is_not_a_known_list_cannot_be_summed()
	{
		Assert.Contains("'policy.premium' is not a list", Fails("sum(policy.premium)", ["claims"]).Message);
	}

	[Fact]
	public void Long_expressions_are_refused()
	{
		Assert.Contains("too long", Fails(string.Join(" + ", Enumerable.Repeat("a", 300))).Message);
	}

	[Fact]
	public void Nothing_but_paths_numbers_and_quoted_text_reaches_the_liquid()
	{
		var liquid = ExpressionCompiler.Compile("concat(a, ' <b>bold</b> ')").Liquid;
		Assert.Contains("' <b>bold</b> '", liquid);
		Assert.DoesNotContain("{{", liquid);
	}
}

/// <summary>Compiled calculations run through the document Liquid engine with real data.</summary>
public sealed class ExpressionEvaluationTests(ClauseAppFactory factory) : IClassFixture<ClauseAppFactory>
{
	private static readonly JsonElement Data = JsonDocument.Parse("""
		{ "policy": { "premium": 1200.50, "fees": 99.5, "discount": 100, "zero": 0, "first": "Ada", "last": "Lovelace",
		              "effective": "2026-01-01", "expiration": "2027-01-01", "count": 3 },
		  "claims": [ { "amount": 100 }, { "amount": 250.25 }, { "amount": null } ],
		  "locations": [ { "name": "North", "buildings": [ { "tiv": 1000 }, { "tiv": 3000 } ] }, { "name": "South", "buildings": [] } ],
		  "none": [] }
		""").RootElement.Clone();

	private static readonly string[] Lists = ["claims", "locations", "location.buildings", "none"];

	private async Task<string> EvalAsync(string expression, string extra = "")
	{
		var compiled = ExpressionCompiler.Compile(expression, Lists);
		var (text, error) = await factory.Services.GetRequiredService<DocumentComposer>()
			.RenderTextAsync(compiled.Liquid + "{{ " + compiled.Result + extra + " }}", Data);
		Assert.True(error is null, error);
		return text!;
	}

	[Theory]
	[InlineData("policy.premium + policy.fees - policy.discount", "1200")]
	[InlineData("policy.premium * 2", "2401")]
	[InlineData("(policy.fees + 0.5) * 2", "200")]
	[InlineData("policy.fees + 0.5 * 2", "100.5")]
	[InlineData("-policy.discount", "-100")]
	[InlineData("10 - 2 - 3", "5")]
	[InlineData("policy.discount / 8", "12.5")]
	[InlineData("policy.count / 2", "1.5")]
	[InlineData("policy.discount / policy.zero", "0")]
	[InlineData("policy.discount / policy.missing", "0")]
	[InlineData("policy.missing + 5", "5")]
	public async Task Arithmetic(string expression, string expected)
	{
		Assert.Equal(expected, await EvalAsync(expression));
	}

	[Theory]
	[InlineData("sum(claims.amount)", "350.25")]
	[InlineData("count(claims)", "3")]
	[InlineData("count(none)", "0")]
	[InlineData("sum(none.amount)", "0")]
	[InlineData("average(none.amount)", "0")]
	[InlineData("count(locations) * 10", "20")]
	public async Task List_totals(string expression, string expected)
	{
		Assert.Equal(expected, await EvalAsync(expression));
	}

	[Fact]
	public async Task Average_of_a_list()
	{
		Assert.Equal("116.75", await EvalAsync("average(claims.amount)"));
	}

	[Fact]
	public async Task Totals_inside_a_loop_use_the_loop_item()
	{
		var compiled = ExpressionCompiler.Compile("sum(location.buildings.tiv)", Lists);
		var (text, error) = await factory.Services.GetRequiredService<DocumentComposer>().RenderTextAsync(
			"{% for location in locations %}[{{ location.name }}:" + compiled.Liquid + "{{ calc_result }}]{% endfor %}", Data);
		Assert.Null(error);
		Assert.Equal("[North:4000][South:0]", text);
	}

	[Theory]
	[InlineData("min(policy.premium, policy.fees, 500)", "99.5")]
	[InlineData("max(policy.fees, 500)", "500")]
	[InlineData("round(policy.premium)", "1201")]
	[InlineData("round(policy.premium / 3, 2)", "400.17")]
	[InlineData("abs(policy.discount - policy.premium)", "1100.5")]
	[InlineData("concat(policy.first, ' ', policy.last)", "Ada Lovelace")]
	[InlineData("concat('Policy ', policy.count)", "Policy 3")]
	[InlineData("default(policy.missing, 'Not applicable')", "Not applicable")]
	[InlineData("default(policy.first, 'Not applicable')", "Ada")]
	[InlineData("days_between(policy.effective, policy.expiration)", "365")]
	public async Task Functions(string expression, string expected)
	{
		Assert.Equal(expected, await EvalAsync(expression));
	}

	[Theory]
	[InlineData("policy.premium + policy.fees", " | currency", "$1,300.00")]
	[InlineData("sum(claims.amount)", " | dollars", "$350")]
	[InlineData("policy.discount / 400", " | percent", "25.00%")]
	public async Task Results_take_the_field_formats(string expression, string format, string expected)
	{
		Assert.Equal(expected, await EvalAsync(expression, format));
	}

	[Fact]
	public async Task A_calculated_field_in_a_template_renders_in_the_document()
	{
		var compiled = ExpressionCompiler.Compile("policy.premium + policy.fees", Lists);
		var result = await factory.Services.GetRequiredService<DocumentComposer>().ComposeAsync(
			"<p>Total: <span class=\"df-calc\">" + compiled.Liquid + "{{ calc_result | currency }}</span></p>", "", Data);
		Assert.Null(result.Error);
		Assert.Contains("Total: <span class=\"df-calc\">$1,300.00</span>", result.Html);
	}

	[Fact]
	public async Task Text_values_are_encoded_in_the_document()
	{
		var compiled = ExpressionCompiler.Compile("concat(policy.first, ' <b>x</b>')", Lists);
		var result = await factory.Services.GetRequiredService<DocumentComposer>().ComposeAsync(
			"<p>" + compiled.Liquid + "{{ calc_result }}</p>", "", Data);
		Assert.Contains("Ada &lt;b&gt;x&lt;/b&gt;", result.Html);
	}

	[Fact]
	public void Field_usage_sees_the_paths_a_calculation_reads()
	{
		var compiled = ExpressionCompiler.Compile("policy.premium + sum(claims.amount)", Lists);
		var paths = FieldUsage.References("<span>" + compiled.Liquid + "{{ calc_result }}</span>").Select(r => r.Path).ToList();
		Assert.Contains("policy.premium", paths);
		Assert.Contains("claims", paths);
		Assert.DoesNotContain(paths, p => p.StartsWith("calc_", StringComparison.Ordinal));
	}
}

/// <summary>/api/expressions.</summary>
public sealed class ExpressionEndpointTests(ClauseAppFactory factory) : IClassFixture<ClauseAppFactory>
{
	private readonly HttpClient _client = factory.CreateClient();

	[Fact]
	public async Task Compile_returns_liquid_paths_and_lists()
	{
		var response = await _client.PostAsJsonAsync("/api/expressions/compile", new { expression = "policy.premium + sum(claims.amount)", lists = new[] { "claims" } });
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal("calc_result", body.GetProperty("result").GetString());
		Assert.Contains("claims | sum: 'amount'", body.GetProperty("liquid").GetString());
		Assert.Equal(["policy.premium"], body.GetProperty("paths").EnumerateArray().Select(p => p.GetString()));
		Assert.Equal(["claims"], body.GetProperty("lists").EnumerateArray().Select(p => p.GetString()));
	}

	[Fact]
	public async Task Compile_errors_say_where()
	{
		var response = await _client.PostAsJsonAsync("/api/expressions/compile", new { expression = "a + (b" });
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal("Missing ')'.", body.GetProperty("error").GetString());
		Assert.Equal(6, body.GetProperty("position").GetInt32());
	}

	[Fact]
	public async Task Preview_renders_with_the_given_data_as_text()
	{
		var response = await _client.PostAsJsonAsync("/api/expressions/preview", new JsonObject
		{
			["liquid"] = "{%- assign calc_result = policy.a | plus: policy.b -%}{{ calc_result | currency }}",
			["data"] = JsonNode.Parse("""{"policy":{"a":1,"b":2.5}}""")
		});
		Assert.Equal("$3.50", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("text").GetString());
	}

	[Fact]
	public async Task Preview_does_not_encode_text_the_designer_shows_as_text()
	{
		var response = await _client.PostAsJsonAsync("/api/expressions/preview", new JsonObject
		{
			["liquid"] = "{{ policy.name }}",
			["data"] = JsonNode.Parse("""{"policy":{"name":"Smith & Sons"}}""")
		});
		Assert.Equal("Smith & Sons", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("text").GetString());
	}

	[Theory]
	[InlineData("")]
	[InlineData(null)]
	public async Task Preview_needs_liquid(string? liquid)
	{
		var response = await _client.PostAsJsonAsync("/api/expressions/preview", new { liquid });
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task Preview_reports_liquid_errors()
	{
		var response = await _client.PostAsJsonAsync("/api/expressions/preview", new { liquid = "{% if %}" });
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.False(string.IsNullOrEmpty((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()));
	}
}
