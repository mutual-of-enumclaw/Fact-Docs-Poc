namespace FaCT.DocDesigner.POC.Features;

/// <summary>A designer feature that can be switched off (hidden in the designer) for demos or a staged roll-out.</summary>
public sealed record FeatureInfo(string Name, string Description);

/// <summary>
/// Feature flags: every feature is on unless configuration turns it off. <c>Features:{Name}</c> (true/false) sets one
/// feature; <c>Features:Profile</c> picks a named set from <c>FeatureProfiles:{profile}:{Name}</c> (e.g. "demo"), and a
/// feature set directly wins over the profile. The designer hides the controls of features that are off.
/// </summary>
public sealed class FeatureFlags(IConfiguration configuration)
{
	public static readonly IReadOnlyList<FeatureInfo> Known =
	[
		new("Gallery", "Gallery of templates and clauses with previews"),
		new("Clauses", "Shared clauses (Clause kind and Clause block)"),
		new("Scenarios", "Test-data scenarios and Preview all"),
		new("Comments", "Reviewer comments"),
		new("History", "History (audit log) dialog"),
		new("Duplicate", "Duplicate a template"),
		new("CompareWithPublished", "Compare with Published (the publish review still shows changes)"),
		new("PageSetup", "Page setup, header and footer"),
		new("Watermarks", "Watermarks"),
		new("Themes", "Themes: brand colors and fonts"),
		new("ConditionalStyling", "Conditional styling"),
		new("Visuals", "Barcodes, charts and signature blocks"),
		new("CalculatedFields", "Calculated fields"),
		new("FindReplace", "Find and replace"),
		new("Spelling", "Spell check"),
		new("CustomBlocks", "Saving and reusing your own blocks"),
		new("FieldUsage", "Field usage search"),
		new("Export", "Export to Word and HTML"),
		new("LiquidView", "Liquid view of data fields and Show Liquid"),
		new("ImportDocument", "Import PDF / Word"),
		new("ImportGhostDraft", "Import a converted GhostDraft form"),
		new("ImportLegacy", "Import a converted Documaker form"),
		new("ReviewToLibrary", "Add to Designer Library on the GhostDraft review page"),
		new("Languages", "Spanish and French versions"),
		new("StylePanels", "Styles and Layers panels"),
		new("MaterialBlocks", "Material design blocks (icons, banners, cards)")
	];

	public string? Profile => configuration["Features:Profile"] is { Length: > 0 } profile ? profile : null;

	public bool IsEnabled(string name)
	{
		if (bool.TryParse(configuration[$"Features:{name}"], out var direct)) return direct;
		if (Profile is { } profile && bool.TryParse(configuration[$"FeatureProfiles:{profile}:{name}"], out var fromProfile)) return fromProfile;
		return true;
	}

	/// <summary>Every known feature and whether it is on.</summary>
	public IReadOnlyDictionary<string, bool> All() => Known.ToDictionary(f => f.Name, f => IsEnabled(f.Name));
}
