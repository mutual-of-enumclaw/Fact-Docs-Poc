using MoE.CommonDataModel;
using MoE.GhostDraftDataModel.SDK;

namespace FapPdfTools.Population.Maps;

/// <summary>
/// Field map for <c>BOPDEC PAGE</c> (file <c>BOPDEC1</c>) — the Businessowners
/// declarations page. Binds the policy-level declaration fields to the
/// <see cref="CDMPolicyView"/>. The repeating premises / building / coverage
/// sections (PREM #n, PREMISES #n, LIMIT #n, …) are intentionally deferred to a
/// later phase since they require array projection over InsuredAssets/Coverages.
/// </summary>
public sealed class BopDecPageFieldMap : IFormFieldMap
{
	public string FormNumber => "BOPDEC";

	public string EditionDate => "PAGE";

	public IReadOnlyDictionary<string, string> BuildValues(CDMPolicyView policy)
	{
		var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		var insured = policy.Parties.OfType<InsuredParty>().FirstOrDefault();
		var agency = policy.Parties.OfType<AgencyParty>().FirstOrDefault();
		var insuredAddr = insured?.Address?.FirstOrDefault();
		var agencyAddr = agency?.Address?.FirstOrDefault();

		// --- Policy-level scalars ---
		Add(values, "POLICYNBR", policy.Number);
		Add(values, "EFFDATE", policy.EffectiveDate.ToString("MM/dd/yyyy"));
		Add(values, "EXPDATE", policy.ExpirationDate.ToString("MM/dd/yyyy"));
		Add(values, "TOTPREM", policy.Premium?.ToString("N2"));

		// --- Named insured ---
		if (insured != null)
		{
			Add(values, "INSURED NAME", insured.FullName);
			if (insuredAddr != null)
			{
				Add(values, "INSURED ADDR1", insuredAddr.AddressLine);
				Add(values, "INSURED ADDR2", insuredAddr.ExtendedAddressLine);
				Add(values, "INSURED CTYSTZ", FormatCityStateZip(insuredAddr));
			}
		}

		// --- Producer / agency ---
		if (agency != null)
		{
			Add(values, "AGENT NAME", agency.FullName);
			Add(values, "AGENT NUM", agency.AgencyCode);
			Add(values, "AGT TELE1", agency.Phone);
			if (agencyAddr != null)
			{
				Add(values, "AGENT ADDR1", agencyAddr.AddressLine);
				Add(values, "AGENT ADDR2", agencyAddr.ExtendedAddressLine);
				Add(values, "AGENT CTYSTZ", FormatCityStateZip(agencyAddr));
			}
		}

		return values;
	}

	private static void Add(IDictionary<string, string> map, string field, string value)
	{
		if (!string.IsNullOrWhiteSpace(value))
			map[field] = value;
	}

	private static string FormatCityStateZip(Address a)
		=> $"{a.City}, {a.State} {a.ZipCode}".Trim().Trim(',').Trim();
}
