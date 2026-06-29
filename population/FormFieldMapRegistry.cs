namespace FapPdfTools.Population;

/// <summary>
/// Resolves the correct <see cref="IFormFieldMap"/> for a given form number and edition date.
/// Register all <see cref="IFormFieldMap"/> implementations with DI and inject this registry
/// into controllers that need to populate forms from a <see cref="MoE.GhostDraftDataModel.SDK.CDMPolicyView"/>.
/// </summary>
public class FormFieldMapRegistry
{
	private readonly Dictionary<string, IFormFieldMap> _maps;
	private readonly IFormFieldMap? _fallback;

	public FormFieldMapRegistry(IEnumerable<IFormFieldMap> maps)
	{
		var all = maps.ToList();
		// A map with the sentinel FormNumber "*" is the generic fallback (common header fields).
		_fallback = all.FirstOrDefault(m => m.FormNumber == "*");
		_maps = all
			.Where(m => m.FormNumber != "*")
			.ToDictionary(
				m => MakeKey(m.FormNumber, m.EditionDate),
				m => m,
				StringComparer.OrdinalIgnoreCase);
	}

	/// <summary>Returns the form-specific map, else the generic fallback, else <see langword="null"/>.</summary>
	public IFormFieldMap? Resolve(string formNumber, string editionDate)
	{
		string key = MakeKey(formNumber, editionDate);
		return _maps.TryGetValue(key, out IFormFieldMap? map) ? map : _fallback;
	}

	/// <summary>All registered form keys in the format <c>FormNumber|EditionDate</c>.</summary>
	public IReadOnlyList<string> KnownForms => _maps.Keys.ToList();

	private static string MakeKey(string formNumber, string edition)
		=> $"{formNumber.Trim().ToUpperInvariant()}|{edition.Trim().ToUpperInvariant()}";
}
