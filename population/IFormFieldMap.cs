using MoE.GhostDraftDataModel.SDK;

namespace FapPdfTools.Population;

/// <summary>
/// Maps a single form's fillable fields to values drawn from a
/// <see cref="CDMPolicyView"/> (the Commercial API's Common Data Model shape).
/// One implementation exists per form.
/// </summary>
public interface IFormFieldMap
{
	/// <summary>Form number as it appears in FORM.DAT (spaces optional).</summary>
	string FormNumber { get; }

	/// <summary>Edition / key suffix used to resolve the FORM.DAT entry.</summary>
	string EditionDate { get; }

	/// <summary>
	/// Build the field-name → value dictionary for this policy. Keys must match
	/// the AcroForm field names produced by the renderer (the FAP field names).
	/// Only non-empty values should be returned.
	/// </summary>
	IReadOnlyDictionary<string, string> BuildValues(CDMPolicyView policy);
}
