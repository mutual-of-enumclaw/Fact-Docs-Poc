using MoE.CommonDataModel;
using MoE.GhostDraftDataModel.SDK;

namespace FapPdfTools.Population.Maps;

/// <summary>
/// Fallback field map used for any form that has no form-specific <see cref="IFormFieldMap"/>.
/// It pre-fills the common policy header fields (policy number, named insured, effective /
/// expiration date) by emitting every well-known field-name variant; the controller only applies
/// the keys that actually exist on the target template. The sentinel FormNumber "*" marks it as the
/// registry fallback (see <c>FormFieldMapRegistry</c>).
/// </summary>
public sealed class GenericHeaderFieldMap : IFormFieldMap
{
	public string FormNumber => "*";

	public string EditionDate => "*";

	public IReadOnlyDictionary<string, string> BuildValues(CDMPolicyView policy)
	{
		var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		var insured = policy.Parties.OfType<InsuredParty>().FirstOrDefault();
		string number = policy.Number;
		string name = insured?.FullName;
		string eff = policy.EffectiveDate.ToString("MM/dd/yyyy");
		string exp = policy.ExpirationDate.ToString("MM/dd/yyyy");

		Set(values, number, "POLNUM", "POLICYNUM", "POLICY NUMBER", "POLICYNBR", "POLICY NBR", "POLICYNO", "POL NUM", "POLICY NO", "POLICYNUMBER");
		Set(values, name, "INSURED NAME", "INSNAME", "INSNAME1", "NAMED INSURED", "ISSUEDTO", "INSURED", "NAME OF INSURED", "INSUREDNAME");
		Set(values, eff, "EFFDATE", "EFFDATE #002", "EFFECTIVE DATE", "EFF DATE", "ENDORSEMENT EFFECTIVE DATE", "POLEFFDATE", "EFFECTIVEDATE");
		Set(values, exp, "EXPDATE", "EXPIRATION DATE", "EXP DATE", "EXPIRATIONDATE");

		return values;
	}

	private static void Set(IDictionary<string, string> map, string value, params string[] keys)
	{
		if (string.IsNullOrWhiteSpace(value)) return;
		foreach (var k in keys) map[k] = value;
	}
}
