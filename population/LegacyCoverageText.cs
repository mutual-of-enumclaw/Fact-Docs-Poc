using System.Collections.Concurrent;
using System.Globalization;
using MoE.CommonDataModel;

namespace FapPdfTools.Population;

/// <summary>
/// The coverage wording, ordering and column values the legacy quote proposal prints —
/// taken from Documaker's own table and DAL rather than from the CDM's descriptions.
/// </summary>
/// <remarks>
/// <para>
/// Products asked where our coverage descriptions came from, having noticed they differ
/// from the real render. Ours were <c>Coverage.Description</c> off the CDM. Legacy's come
/// from a lookup the DDT names explicitly:
/// </para>
/// <code>
///   QCPP_CAA.DDT   ;0;1;;0;0;COVERAGE;0;60;;DAL;CALL("CPPQ_CAA_COVERAGE");…
///   QUOTE.DAL      COVDESC = FieldRule(";…;18;60;MOE_ASAH;tbllook;
///                                       TBLOFF,PMSP0200 KEY:CPPCA CA    " & CoverageType & …")
/// </code>
/// <para>
/// So the wording lives in <c>mstrres/MOEC0/TABLE/MOE_ASAH.TBL</c>, keyed
/// <c>"CPPCA CA    " + &lt;6-char coverage code&gt;</c> with the description from column 18.
/// That file is the vocabulary, and reading it is what makes our text match — "Pollution
/// Liability - All Other" rather than the CDM's "Pollution Liability-All Other",
/// "Audio, Visual and Data Electronic Equipment" rather than "…Equip".
/// </para>
/// <para>
/// Three coverages are not a straight lookup; <c>CPPQ_CAV1_COVERAGE</c> overrides them,
/// and <see cref="Describe"/> reproduces those branches.
/// </para>
/// </remarks>
public static class LegacyCoverageText
{
	private const string DefaultTablePath =
		@"C:\src\FaCT-DocProd-Development\mstrres\MOEC0\TABLE\MOE_ASAH.TBL";

	/// <summary>Overridable for tests or a different mstrres root.</summary>
	public static string TablePath { get; set; } = DefaultTablePath;

	private static readonly ConcurrentDictionary<string, Dictionary<string, string>> Tables
		= new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Load the descriptions for one <c>&lt;lob&gt;&lt;line&gt;</c> key prefix, e.g.
	/// <c>"CPPCA CA"</c>. Rows are fixed-width: 12-character key, 6-character coverage
	/// code, description from column 18.
	/// </summary>
	private static Dictionary<string, string> Load(string prefix)
	{
		return Tables.GetOrAdd(prefix, p =>
		{
			var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			if (!File.Exists(TablePath)) return map;
			foreach (var line in File.ReadLines(TablePath))
			{
				if (line.Length < 18 || !line.StartsWith(p, StringComparison.OrdinalIgnoreCase))
					continue;
				var code = line.Substring(12, 6).Trim();
				var text = line[18..].TrimEnd();
				if (code.Length > 0 && !map.ContainsKey(code)) map[code] = text;
			}
			return map;
		});
	}

	/// <summary>True when the table was found and has rows — so a caller can say so.</summary>
	public static int KnownCommercialAutoCoverages => Load("CPPCA CA").Count;

	/// <summary>
	/// The description legacy prints for a coverage, including the three DAL overrides.
	/// Falls back to the CDM description when the code is not in the table.
	/// </summary>
	public static string Describe(Coverage coverage)
	{
		var code = (coverage.Code ?? "").Trim();

		// CPPQ_CAV1_COVERAGE: RENTAL names the physical-damage coverage it reimburses
		// against, from the rental coverage code.
		if (code.Equals("RENTAL", StringComparison.OrdinalIgnoreCase))
		{
			var against = Get(coverage, "RentalCoverageCode")?.Trim().ToUpperInvariant();
			var suffix = against switch
			{
				"COLL" => "Collision",
				"COMP" => "Comprehensive",
				"SP" => "Specified Causes of Loss",
				_ => null,
			};
			if (suffix != null) return $"Rental Reimbursement {suffix}";
		}

		// CPPQ_CAV1_COVERAGE: UM/UN name their limit basis. The reference document
		// applies the same suffix to the drive-other-car twins in the line table
		// ("DOC Uninsured Motorists Single Limit (BI)").
		if (code is "UM" or "UN" or "DOC-UM" or "DOC-UN")
		{
			var noun = code switch
			{
				"UM" => "Uninsured Motorists",
				"UN" => "Underinsured Motorists",
				"DOC-UM" => "DOC Uninsured Motorists",
				_ => "DOC Underinsured Motorists",
			};
			var basis = Get(coverage, "LimitType")?.Trim().ToUpperInvariant() switch
			{
				"SL" => "Single Limit (BI)",
				"SPLIT" => "Split Limit (BI)",
				"CSL" => "Combined Single Limit (BI/PD)",
				_ => null,
			};
			if (basis != null) return $"{noun} {basis}";
		}

		var table = Load("CPPCA CA");
		if (table.TryGetValue(code, out var described) && described.Length > 0)
			return described;
		return coverage.Description ?? code;
	}

	/// <summary>
	/// The Limit Amount column. Straight from the coverage's own limit property, except
	/// RENTAL, which <c>CPPQ_CAV1_LIMIT</c> prints as <c>days/perDay</c> — "30/75".
	/// </summary>
	public static string Limit(Coverage coverage)
	{
		if ((coverage.Code ?? "").Trim().Equals("RENTAL", StringComparison.OrdinalIgnoreCase))
		{
			var days = Number(coverage, "NumberOfDays");
			var perDay = Number(coverage, "MaxAmountPerDay");
			if (days is > 0 && perDay is > 0)
				return $"{days.Value:0}/{perDay.Value:0}";
		}
		return Money(FirstNumeric(coverage, LimitNames));
	}

	/// <summary>
	/// The Deductible column. <c>CPPQ_CAV1_DEDUCTIBLE</c> gives the loan/lease gap
	/// coverages the deductible of the physical-damage coverage they sit on — LOANCM and
	/// LEASCM take the unit's comprehensive deductible (<c>B5USCD5</c>), LOANCO and LEASCO
	/// its collision deductible (<c>B5PLTX</c>) — which is why the real render shows 50
	/// against Auto Loan/Lease Gap Comprehensive and 2,000 against its Collision twin
	/// where the CDM carries neither.
	/// </summary>
	public static string Deductible(Coverage coverage, IEnumerable<Coverage> siblings)
	{
		var code = (coverage.Code ?? "").Trim().ToUpperInvariant();
		string? inheritFrom = code switch
		{
			"LOANCM" or "LEASCM" => "COMP",
			"LOANCO" or "LEASCO" => "COLL",
			_ => null,
		};
		if (inheritFrom != null)
		{
			var donor = siblings.FirstOrDefault(
				c => string.Equals((c.Code ?? "").Trim(), inheritFrom,
					StringComparison.OrdinalIgnoreCase));
			if (donor != null) return Money(FirstNumeric(donor, DeductibleNames));
		}
		return Money(FirstNumeric(coverage, DeductibleNames));
	}

	// ------------------------------------------------------------------------ ordering

	/// <summary>
	/// Display order for the per-vehicle table. The legacy sequence is the extract's
	/// <c>BYC0NB</c>, which the CDM does not carry, so this is EMPIRICAL: it is the order
	/// the Products render puts these codes in, and it reproduces both vehicle tables in
	/// that document exactly. Codes not listed keep their CDM order, after the listed ones.
	/// </summary>
	private static readonly string[] VehicleOrder =
	[
		"LIAB", "MEDPAY", "PIP", "UM", "UMPD", "UN", "UNPD",
		"COMP", "COLL", "SP", "F", "FT", "FTW", "LMT", "TOWING",
		"LOANCM", "LOANCO", "LEASCM", "LEASCO",
		"RENTAL", "SOUND", "TAPES", "AMUSE", "POLL",
	];

	/// <summary>
	/// Display order for the insurance-line table. Same caveat as
	/// <see cref="VehicleOrder"/>; this reproduces the reference document's Commercial
	/// Auto line table exactly.
	/// </summary>
	private static readonly string[] LineOrder =
	[
		"EA9910", "EA9911",
		"DOC-L", "DOC-MP", "DOC-UM", "DOC-UN", "DOC-CM", "DOC-CO",
		"HIRE", "HIRECM", "HIRECO", "HIRESP",
		"NONOWN", "NONDON", "NONEMP", "NONPAR", "NONVOL",
		"POLLHA", "POLLPP", "POLL1", "POLLUT",
		"MANCA", "MINPRM",
	];

	public static IEnumerable<Coverage> InVehicleOrder(IEnumerable<Coverage> coverages)
		=> Ordered(coverages, VehicleOrder);

	public static IEnumerable<Coverage> InLineOrder(IEnumerable<Coverage> coverages)
		=> Ordered(coverages, LineOrder);

	private static IEnumerable<Coverage> Ordered(IEnumerable<Coverage> coverages,
		string[] order)
	{
		// OrderBy is a stable sort, so coverages sharing a code (the two RENTAL rows)
		// keep the order the CDM returned them in, which is what the render shows.
		var rank = order
			.Select((code, i) => (code, i))
			.ToDictionary(x => x.code, x => x.i, StringComparer.OrdinalIgnoreCase);
		return coverages.OrderBy(c =>
			rank.TryGetValue((c.Code ?? "").Trim(), out var i) ? i : order.Length);
	}

	// ------------------------------------------------------------------------- helpers

	private static readonly string[] LimitNames =
		["Limit", "PerAccidentLimit", "PerLossLimit", "PerPersonLimit", "LimitAmount"];

	private static readonly string[] DeductibleNames = ["Deductible", "DeductibleAmount"];

	private static string Money(decimal? v) =>
		v is null or 0 ? "" : v.Value.ToString("#,##0", CultureInfo.InvariantCulture);

	private static string? Get(object o, string property) =>
		o.GetType().GetProperty(property)?.GetValue(o)?.ToString();

	private static decimal? Number(object o, string property)
	{
		var v = o.GetType().GetProperty(property)?.GetValue(o);
		if (v == null) return null;
		try { return Convert.ToDecimal(v, CultureInfo.InvariantCulture); }
		catch (InvalidCastException) { return null; }
		catch (FormatException) { return null; }
		catch (OverflowException) { return null; }
	}

	// Limits and deductibles live on the coverage SUBTYPES, not on the Coverage base,
	// so the property is looked up by name in preference order rather than switched on
	// 200 subtypes.
	private static decimal? FirstNumeric(object o, string[] names)
	{
		foreach (var name in names)
		{
			var d = Number(o, name);
			if (d is not null and not 0) return d;
		}
		return null;
	}
}
