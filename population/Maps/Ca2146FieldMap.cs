using MoE.CommonDataModel;
using MoE.CommonDataModel;

namespace FapPdfTools.Population.Maps;

/// <summary>
/// Field map for MoE form <c>CA 21 46</c> (file <c>A2146A</c>) — Split Underinsured
/// Motorists Coverage Limits. A short Commercial Auto endorsement: policy header,
/// named insured, and the underinsured-motorists split limit schedule
/// (Bodily Injury per person / per accident, Property Damage).
/// </summary>
public sealed class Ca2146FieldMap : IFormFieldMap
{
	public string FormNumber => "CA2146";

	public string EditionDate => "1293";

	public IReadOnlyDictionary<string, string> BuildValues(Policy policy)
	{
		var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		var insured = policy.Parties.OfType<InsuredParty>().FirstOrDefault();

		// Underinsured Motorists coverages live on the Commercial Auto line.
		var coverages = policy.Lines.SelectMany(l => l.Coverages)
			.Concat(policy.InsuredAssets.SelectMany(a => a.Coverages));
		var un = coverages.OfType<CommonAutoUNCoverage>().FirstOrDefault();
		var unpd = coverages.OfType<CommonAutoUNPDCoverage>().FirstOrDefault();

		var effDate = policy.EffectiveDate.ToString("MM/dd/yyyy");

		Add(values, "POLNUM", policy.Number);
		Add(values, "EFFDATE", effDate);
		Add(values, "EFFDATE #002", effDate);
		Add(values, "INSNAME1", insured?.FullName);

		// Schedule — Underinsured Motorists split limits.
		Add(values, "LIMIT1", FormatLimit(un?.PerPersonLimit));   // Bodily Injury, Each Person
		Add(values, "LIMIT2", FormatLimit(un?.PerAccidentLimit)); // Bodily Injury, Each Accident
		Add(values, "LIMIT3", FormatLimit(unpd?.Limit));          // Property Damage

		return values;
	}

	private static void Add(IDictionary<string, string> map, string field, string value)
	{
		if (!string.IsNullOrWhiteSpace(value))
			map[field] = value;
	}

	private static string FormatLimit(int? amount)
		=> amount.HasValue ? amount.Value.ToString("N0") : null;
}
