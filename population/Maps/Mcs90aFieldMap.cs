using MoE.CommonDataModel;
using MoE.GhostDraftDataModel.SDK;

namespace FapPdfTools.Population.Maps;

/// <summary>
/// Field map for <c>MCS-90</c> (file <c>MCS90A</c>) — the FMCSA Endorsement for Motor Carrier
/// Policies of Insurance for Public Liability. This is a Legacy WIP (hybrid) form: only the
/// policy-derived header fields are owned here and are pre-filled AND locked (read-only) in the
/// interactive UI — the named insured ("issued to"), policy number, and effective date. Everything
/// else on the form (limit amount, primary/excess selection, telephone, the "this Xth day of" date
/// parts, etc.) is left for the user to fill in.
/// </summary>
public sealed class Mcs90aFieldMap : IFormFieldMap
{
	public string FormNumber => "MCS90";

	public string EditionDate => "0117";

	public IReadOnlyDictionary<string, string> BuildValues(CDMPolicyView policy)
	{
		var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		var insured = policy.Parties.OfType<InsuredParty>().FirstOrDefault();

		// Policy-owned (locked / pre-filled) header fields only.
		Add(values, "ISSUEDTO", insured?.FullName);     // the motor carrier / named insured
		Add(values, "POLICYNUM", policy.Number);
		Add(values, "EFFDATE", policy.EffectiveDate.ToString("MM/dd/yyyy"));

		return values;
	}

	private static void Add(IDictionary<string, string> map, string field, string value)
	{
		if (!string.IsNullOrWhiteSpace(value))
			map[field] = value;
	}
}
