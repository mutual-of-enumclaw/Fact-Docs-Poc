using MoE.CommonDataModel;
using MoE.GhostDraftDataModel.SDK;

namespace FapPdfTools.Population.Maps;

/// <summary>
/// Field map for <c>CA 20 09 10 13</c> (file <c>A2009C</c>) — Leasing Or Rental Concerns –
/// Contingent Coverage. The schedule has four fields:
///   POLNUM, INSNAME, EFFDATE  → policy header, mapped from the CDM (locked / pre-filled);
///   LIMIT ("Limit Of Insurance") → NOT mapped. Per the form's DDT this value comes from a
///   specialized rate table (ASBBCPL1 / BBAGTX), and the CDM's leasing/rental coverage types carry
///   no limit property — so it is a user-entered (hybrid) field, left editable for the user.
/// </summary>
public sealed class Ca2009FieldMap : IFormFieldMap
{
	public string FormNumber => "CA2009";

	public string EditionDate => "1013";

	public IReadOnlyDictionary<string, string> BuildValues(CDMPolicyView policy)
	{
		var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		var insured = policy.Parties.OfType<InsuredParty>().FirstOrDefault();

		Add(values, "POLNUM", policy.Number);
		Add(values, "INSNAME", insured?.FullName);
		Add(values, "EFFDATE", policy.EffectiveDate.ToString("MM/dd/yyyy"));
		// LIMIT intentionally not set — no CDM source (see class summary); user-entered.

		return values;
	}

	private static void Add(IDictionary<string, string> map, string field, string value)
	{
		if (!string.IsNullOrWhiteSpace(value))
			map[field] = value;
	}
}
