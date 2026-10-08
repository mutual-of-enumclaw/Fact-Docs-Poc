using FaCT.DocDesigner.POC.Mapping;

namespace FaCT.DocDesigner.POC.Tests.Mapping;

public sealed class FieldMatcherTests
{
	private static readonly MappingPath[] Model =
	[
		new("policy.number", "text"),
		new("policy.effectiveDate", "date"),
		new("policy.expirationDate", "date"),
		new("policy.premium", "number"),
		new("policy.premiumNote", "text"),
		new("insured.name", "text"),
		new("insured.address.street", "text"),
		new("insured.address.city", "text"),
		new("insured.address.state", "text"),
		new("insured.address.postalCode", "text"),
		new("insured.phone", "text"),
		new("agent.name", "text"),
		new("agent.code", "text"),
		new("coverage.buildingLimit", "number"),
		new("coverage.deductible", "number")
	];

	private static MappingSuggestion Suggest(string field) => FieldMatcher.Suggest([field], Model).Single();

	// ---- Words ------------------------------------------------------------------------------------------------------

	[Theory]
	[InlineData("PolicyNumber", "policy number")]
	[InlineData("POLICY_NO", "policy number")]
	[InlineData("Policy #", "policy number")]
	[InlineData("policy-nbr", "policy number")]
	[InlineData("InsuredName_2", "insured name")]
	[InlineData("Insured Name 1", "insured name")]
	[InlineData("EffDt", "effective date")]
	[InlineData("Exp. Date", "expiration date")]
	[InlineData("USState", "us state")]
	[InlineData("ZipCode", "zip")]
	[InlineData("Postal Code", "zip")]
	[InlineData("Buildings", "building")]
	[InlineData("Address", "address")]
	[InlineData("Status", "status")]
	[InlineData("Name of the Insured", "name insured")]
	[InlineData("Text Field 12", "")]
	[InlineData("", "")]
	[InlineData(null, "")]
	[InlineData("Prem Amt", "premium amount")]
	[InlineData("Agt Code", "agent code")]
	[InlineData("Bldg Lim", "building limit")]
	public void Names_are_reduced_to_comparable_words(string? name, string expected) =>
		Assert.Equal(expected, string.Join(' ', FieldMatcher.Words(name)));

	[Theory]
	[InlineData("policy", "policy", 1.0)]
	[InlineData("insured", "insure", 0.8)]
	[InlineData("premium", "premum", 0.8)]
	[InlineData("insur", "insured", 0.7)]
	[InlineData("name", "named", 0.7)]
	[InlineData("name", "number", 0.0)]
	[InlineData("ab", "abc", 0.0)]
	[InlineData("date", "data", 0.0)]
	public void Words_match_exactly_nearly_or_by_prefix(string a, string b, double expected)
	{
		Assert.Equal(expected, FieldMatcher.WordMatch(a, b));
		Assert.Equal(expected, FieldMatcher.WordMatch(b, a));
	}

	// ---- Recommendations --------------------------------------------------------------------------------------------

	[Theory]
	[InlineData("PolicyNumber", "policy.number")]
	[InlineData("POLICY_NO", "policy.number")]
	[InlineData("Policy #", "policy.number")]
	[InlineData("PolNo", "policy.number")]
	[InlineData("Eff Date", "policy.effectiveDate")]
	[InlineData("EFFECTIVE_DT", "policy.effectiveDate")]
	[InlineData("Exp Date", "policy.expirationDate")]
	[InlineData("Expiration Date", "policy.expirationDate")]
	[InlineData("InsuredName", "insured.name")]
	[InlineData("Named Insured", "insured.name")]
	[InlineData("Insd Name", "insured.name")]
	[InlineData("Insured City", "insured.address.city")]
	[InlineData("City", "insured.address.city")]
	[InlineData("Zip Code", "insured.address.postalCode")]
	[InlineData("Insured Phone", "insured.phone")]
	[InlineData("Agent Name", "agent.name")]
	[InlineData("AgtCode", "agent.code")]
	[InlineData("Deductible", "coverage.deductible")]
	[InlineData("Bldg Limit", "coverage.buildingLimit")]
	[InlineData("InsuredName_2", "insured.name")]
	public void Clear_matches_are_recommended(string field, string expected) =>
		Assert.Equal(expected, Suggest(field).Recommended);

	[Fact]
	public void An_exact_match_scores_one()
	{
		Assert.Equal(1.0, FieldMatcher.Score("PolicyNumber", new MappingPath("policy.number", "text")));
		Assert.Equal(1.0, FieldMatcher.Score("policy.number", new MappingPath("policy.number", "text")));
	}

	[Fact]
	public void Ambiguous_names_are_suggested_but_not_recommended()
	{
		var suggestion = Suggest("Name");
		Assert.Null(suggestion.Recommended);
		Assert.Equal(["agent.name", "insured.name"], suggestion.Candidates.Take(2).Select(c => c.Path).Order());
		Assert.Equal(suggestion.Candidates[0].Score, suggestion.Candidates[1].Score);
	}

	[Theory]
	[InlineData("Signature")]
	[InlineData("Text Field 12")]
	[InlineData("Check Box 3")]
	[InlineData("")]
	[InlineData("X")]
	public void Unrelated_names_get_no_candidates(string field)
	{
		var suggestion = Suggest(field);
		Assert.Empty(suggestion.Candidates);
		Assert.Null(suggestion.Recommended);
	}

	[Fact]
	public void Candidates_are_best_first_and_at_most_three()
	{
		var suggestion = Suggest("Insured Address");
		Assert.InRange(suggestion.Candidates.Count, 1, 3);
		Assert.Equal(suggestion.Candidates.OrderByDescending(c => c.Score).Select(c => c.Path), suggestion.Candidates.Select(c => c.Path));
		Assert.All(suggestion.Candidates, c => Assert.InRange(c.Score, FieldMatcher.MinimumScore, 1));
	}

	[Fact]
	public void The_property_type_breaks_ties_for_dates_and_amounts()
	{
		var premium = Suggest("Premium");
		Assert.Equal("policy.premium", premium.Candidates[0].Path);
		Assert.True(premium.Candidates[0].Score > premium.Candidates.Single(c => c.Path == "policy.premiumNote").Score);
	}

	[Fact]
	public void Near_misses_still_match()
	{
		Assert.Equal("insured.name", Suggest("Insurd Name").Recommended);
		Assert.Equal("policy.premium", Suggest("Premum").Candidates[0].Path);
		// One letter apart in a word of five or more letters counts as the same word.
		Assert.Equal("agreed", FieldMatcher.Suggest(["Agree"], [new MappingPath("agreed", null)]).Single().Recommended);
	}

	[Fact]
	public void Each_field_name_is_suggested_once_in_order()
	{
		var suggestions = FieldMatcher.Suggest(["PolicyNumber", "Agent Name", "PolicyNumber"], Model);
		Assert.Equal(["PolicyNumber", "Agent Name"], suggestions.Select(s => s.Field));
	}

	[Fact]
	public void No_model_means_no_candidates()
	{
		var suggestion = FieldMatcher.Suggest(["PolicyNumber"], []).Single();
		Assert.Empty(suggestion.Candidates);
	}

	[Fact]
	public void A_single_good_candidate_is_recommended_without_a_margin_check()
	{
		var suggestion = FieldMatcher.Suggest(["PolicyNumber"], [new MappingPath("policy.number", null)]).Single();
		Assert.Equal("policy.number", suggestion.Recommended);
	}

	[Fact]
	public void Two_close_candidates_are_not_recommended()
	{
		var suggestion = FieldMatcher.Suggest(["Insured Name"],
			[new MappingPath("insured.name", null), new MappingPath("insured.names", null)]).Single();
		Assert.Null(suggestion.Recommended);
	}
}
