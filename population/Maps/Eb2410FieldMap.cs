using MoE.CommonDataModel;

namespace FapPdfTools.Population.Maps;

/// <summary>
/// Field map for <c>EB-24-10</c> (file <c>EB2410A</c>) — Employment-Related Practices Liability
/// schedule. A simple Legacy WIP (hybrid) form: only the policy number is policy-owned (pre-filled
/// and locked); the user fills the schedule (limits, deductible, retro / prior-acts dates).
/// </summary>
public sealed class Eb2410FieldMap : IFormFieldMap
{
	public string FormNumber => "EB2410";

	public string EditionDate => "0815";

	public IReadOnlyDictionary<string, string> BuildValues(Policy policy)
	{
		var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		// Policy-owned (locked / pre-filled) field only.
		if (!string.IsNullOrWhiteSpace(policy.Number))
			values["POLNUM"] = policy.Number;

		return values;
	}
}
