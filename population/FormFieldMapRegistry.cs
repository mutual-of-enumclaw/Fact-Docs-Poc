namespace FapPdfTools.Population;

/// <summary>
/// Resolves the correct <see cref="IFormFieldMap"/> for a given form number and edition date.
/// Register all <see cref="IFormFieldMap"/> implementations with DI and inject this registry
/// into controllers that need to populate forms from a <see cref="MoE.GhostDraftDataModel.SDK.CDMPolicyView"/>.
/// </summary>
public class FormFieldMapRegistry
{
	private readonly Dictionary<string, IFormFieldMap> _maps;

	public FormFieldMapRegistry(IEnumerable<IFormFieldMap> maps)
	{
		_maps = maps.ToDictionary(
			m => MakeKey(m.FormNumber, m.EditionDate),
			m => m,
			StringComparer.OrdinalIgnoreCase);
	}

	/// <summary>Returns the map for the form, or <see langword="null"/> if none is registered.</summary>
	public IFormFieldMap? Resolve(string formNumber, string editionDate)
	{
		string key = MakeKey(formNumber, editionDate);
		return _maps.TryGetValue(key, out IFormFieldMap? map) ? map : null;
	}

	/// <summary>All registered form keys in the format <c>FormNumber|EditionDate</c>.</summary>
	public IReadOnlyList<string> KnownForms => _maps.Keys.ToList();

	private static string MakeKey(string formNumber, string edition)
		=> $"{formNumber.Trim().ToUpperInvariant()}|{edition.Trim().ToUpperInvariant()}";
}
