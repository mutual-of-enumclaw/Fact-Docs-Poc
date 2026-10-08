using MoE.CommonDataModel;

namespace FapPdfTools.Population.Maps;

/// <summary>
/// Field map for the Business Auto declarations page — FAP <c>BADECpg1</c>, printed
/// form number <c>DA 00 93 01 08</c>.
///
/// <para>
/// This is deliberately a MIRROR of fact-docgen's
/// <c>ProprietaryCommercialAuto/ItemTwoSection</c>, which builds the same dec page's
/// Server XML for GhostDraft from the same CDM policy. Same source, same numbers, two
/// renderers — so a value that differs between the HTML and the GhostDraft render is a
/// bug in one of the two maps rather than a data question. The correspondence is:
/// </para>
/// <code>
///   FAP field       GhostDraft Server XML path                                  CDM
///   INSURED NAME    Insured/FullName                                            InsuredParty.FullName
///   POLICYNBR       Policy/PolicyNumber                                         Policy.Number
///   LIABPREM        ItemTwo.../Liability/Premium                                PremiumTotals.LiabilityPremium
///   PIPPREM         ItemTwo.../PersonalInjuryProtection/Premium                 PremiumTotals.PersonaInjuryProtectionPremium
///   MEDPAYPREM      ItemTwo.../AutoMedicalPayments/Premium                      PremiumTotals.MedPayPremium
///   UMPREM          ItemTwo.../UninsuredMotoristCoverage/Premium                PremiumTotals.UninsuredMotoristPremium
///   UNDERPREM       ItemTwo.../UnderinsuredMotoristsCoverage/Premium            PremiumTotals.UnderinsuredMotoristPremium
///   COMPPREM        ItemTwo.../PhysicalDamageComprehensiveCoverage/Premium      PremiumTotals.ComprehensivePremium
///   SCLPREM         ItemTwo.../PhysicalDamageSpecifiedCausesOfLossCoverage/...  PremiumTotals.SpecifiedPerilsPremium
///   COLPREM         ItemTwo.../PhysicalDamageCollisionCoverage/Premium          PremiumTotals.CollisionPremium
///   TLPREM          ItemTwo.../PhysicalDamageTowingAndLaborCoverage/Premium     PremiumTotals.TowingPremium
///   ENDORSPREM      ItemTwo.../EstimatedAdditionalPremiumForEndorsements        PremiumTotals.Estimated...
///   ESTTOTALPREM    (total)                                                     CommonAutoLine.Premium
///   *CAS1..4        ItemTwo.../&lt;coverage&gt;/ApplicableCoveredAutoSymbols   LiabilityAutoSymbols / OtherAutoSymbols
/// </code>
///
/// <para>
/// Not owned here, and left blank exactly as an unfilled dec page is: the limit columns
/// (the DDT derives several through <c>if</c> rules over intermediate <c>VAR</c> fields —
/// 25 of the form's 111 fields are those intermediates, not printable), the
/// <c>FORM OF BUS</c> field (legacy resolves it through the DAL function
/// <c>CPP_FARM_BUSTYPE</c>; there is no single CDM property for it), and the
/// <c>COV1..7 / PREMIUM1..7</c> additional-endorsement rows, which need list projection.
/// </para>
/// </summary>
public sealed class BaDecPageFieldMap : IFormFieldMap
{
	public string FormNumber => "DA0093";

	public string EditionDate => "0108";

	// The DDT selects coverage rows by the DB2 BYAOTX coverage code; the CDM carries the
	// same codes on the auto symbols, so the row-to-symbol join uses them verbatim.
	private const string Pip = "PIP";
	private const string MedPay = "MEDPAY";
	private const string Um = "UM";
	private const string Un = "UN";
	private const string Comp = "COMP";
	private const string Coll = "COLL";
	private const string Spec = "SPEC";
	private const string Towing = "TOWING";

	public IReadOnlyDictionary<string, string> BuildValues(Policy policy)
	{
		var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		var insured = policy.Parties?.OfType<InsuredParty>().FirstOrDefault();
		var autoLine = policy.Lines?.OfType<CommonAutoLine>().FirstOrDefault();
		var premiums = autoLine?.PremiumTotals;

		// ---- Item One
		Add(values, "INSURED NAME", insured?.FullName);
		Add(values, "POLICYNBR", policy.Number);

		// ---- Item Two: covered-auto symbols, one cell per symbol
		SpreadSymbols(values, "LIABCAS", autoLine?.LiabilityAutoSymbols?
			.Select(s => s.AutoSymbol).Where(s => !string.IsNullOrWhiteSpace(s)), 4);
		SpreadSymbols(values, "PIPCAS", SymbolsFor(autoLine, Pip), 2);
		SpreadSymbols(values, "MPCAS", SymbolsFor(autoLine, MedPay), 2);
		SpreadSymbols(values, "UMCAS", SymbolsFor(autoLine, Um), 3);
		SpreadSymbols(values, "UNCAS", SymbolsFor(autoLine, Un), 2);
		SpreadSymbols(values, "COMPCAS", SymbolsFor(autoLine, Comp), 2);
		SpreadSymbols(values, "SCLCAS", SymbolsFor(autoLine, Spec), 2);
		SpreadSymbols(values, "COLCAS", SymbolsFor(autoLine, Coll), 2);
		SpreadSymbols(values, "TLCAS", SymbolsFor(autoLine, Towing), 2);

		// ---- Item Two: the premium column
		Money(values, "LIABPREM", premiums?.LiabilityPremium);
		Money(values, "PIPPREM", premiums?.PersonaInjuryProtectionPremium);
		Money(values, "MEDPAYPREM", premiums?.MedPayPremium);
		Money(values, "UMPREM", premiums?.UninsuredMotoristPremium);
		Money(values, "UNDERPREM", premiums?.UnderinsuredMotoristPremium);
		Money(values, "COMPPREM", premiums?.ComprehensivePremium);
		Money(values, "SCLPREM", premiums?.SpecifiedPerilsPremium);
		Money(values, "COLPREM", premiums?.CollisionPremium);
		Money(values, "TLPREM", premiums?.TowingPremium);
		Money(values, "ENDORSPREM", premiums?.EstimatedAdditionalPremiumForEndorsements);
		Money(values, "ESTTOTALPREM", autoLine?.Premium ?? policy.Premium);

		return values;
	}

	private static IEnumerable<string?>? SymbolsFor(CommonAutoLine? line, string coverageCode)
		=> line?.OtherAutoSymbols?
			.Where(s => string.Equals(s.CoverageCode, coverageCode, StringComparison.OrdinalIgnoreCase))
			.Select(s => s.AutoSymbol);

	/// <summary>
	/// The dec page draws each covered-auto symbol in its own narrow cell (LIABCAS1..4),
	/// so a multi-symbol coverage spreads across them rather than concatenating into one.
	/// </summary>
	private static void SpreadSymbols(IDictionary<string, string> map, string prefix,
	                                  IEnumerable<string?>? symbols, int cells)
	{
		if (symbols == null) return;
		int i = 1;
		foreach (var s in symbols)
		{
			if (i > cells) break;
			if (!string.IsNullOrWhiteSpace(s)) map[$"{prefix}{i}"] = s.Trim();
			i++;
		}
	}

	/// <summary>
	/// Whole dollars. The GhostDraft template applies the "without cents" adornment to
	/// every premium and limit on this form, so matching it keeps the two renders
	/// comparable. A zero premium is left BLANK, not printed as 0 -- the form's own
	/// instruction is "only those coverages where a charge is shown".
	/// </summary>
	private static void Money(IDictionary<string, string> map, string field, decimal? amount)
	{
		if (amount is null or 0m) return;
		map[field] = amount.Value.ToString("N0");
	}

	private static void Add(IDictionary<string, string> map, string field, string? value)
	{
		if (!string.IsNullOrWhiteSpace(value)) map[field] = value;
	}
}
