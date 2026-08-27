using System.Globalization;
using System.Reflection;
using MoE.CommonDataModel;

namespace FapPdfTools.Population;

/// <summary>
/// One placement of an image in the assembled packet: the values for its fields,
/// and which instance of its parent image it belongs under.
/// </summary>
public sealed class PacketInstance
{
	/// <summary>Zero-based index of the parent image's instance; -1 at the root.</summary>
	public int ParentIndex { get; set; } = -1;

	/// <summary>FAP field name -> formatted value, ready to splice into a field span.</summary>
	public Dictionary<string, string> Fields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Every instance of one image, in packet order.</summary>
public sealed class PacketImageData
{
	/// <summary>The image whose instances this one repeats under, or null at the root.</summary>
	public string? Parent { get; set; }

	public List<PacketInstance> Instances { get; set; } = new();
}

/// <summary>
/// The data half of a Documaker packet: how many times each image repeats, and what
/// goes in its fields.
/// </summary>
/// <remarks>
/// The other half — the ORDER images appear in and the rules that nest them — comes
/// from FORM.DAT and the DDTs and is read by <c>tools/ddtpacket.py</c>. The DDTs
/// express repetition as a filter over a DocProd extract table:
/// <c>PNTAddImgAfterCurImg;…,QCPPSUMDTL_CA,1,TBLOFF,QCOV,INSLINE,CA,COVCODE,~CT</c>
/// means "one QCPPSUMDTL_CA per QCOV row whose INSLINE is CA and whose COVCODE is not
/// CT". We do not have the extract; we have the CDM. So rather than emulate the
/// extract tables, this maps each driving table to the CDM collection that stands in
/// for it and emits the resulting instance counts directly:
/// <code>
///   extract table   filter               CDM collection
///   QCOV            INSLINE = CA         the auto Line's Coverages
///   ASB5CPL1        B5AGTX = CA          Policy.InsuredAssets (the vehicles)
///   AUTCOV          BYAENB = &lt;unit&gt;      that asset's Coverages
///   ASBECPL1        BEB8NB = &lt;form&gt;      Policy.Forms
///   PMSP0000/0200   —                    the policy header itself
/// </code>
/// Only the Commercial Auto branch is mapped here, which is the POC target agreed in
/// HANDOFF-QUOTE-POC.md §6 phase 0.3. An image with no entry in <see cref="Images"/>
/// is left to the assembler's default of one instance, so unmapped boilerplate still
/// renders; an image mapped to zero instances is suppressed, which is how the
/// conditional variants (per-state, per-endorsement-edition) get pruned.
/// </remarks>
public sealed class QuotePacketData
{
	public string PolicyNumber { get; set; } = "";
	public string LineOfBusinessCode { get; set; } = "";
	public string State { get; set; } = "";
	public Dictionary<string, PacketImageData> Images { get; set; }
		= new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Builds a <see cref="QuotePacketData"/> for a Commercial Auto quote from a CDM policy.
/// </summary>
/// <remarks>
/// Deliberately the same source as <see cref="Maps.BaDecPageFieldMap"/> — the dec page
/// and the quote proposal report the same premiums, so they read the same CDM
/// properties and a divergence is a bug in one of the two, not a data question.
/// </remarks>
public static class QuotePacketDataBuilder
{
	public static QuotePacketData Build(Policy policy)
	{
		ArgumentNullException.ThrowIfNull(policy);

		var data = new QuotePacketData
		{
			PolicyNumber = policy.Number ?? "",
			LineOfBusinessCode = policy.LineOfBusinessCode ?? "",
			State = policy.State ?? "",
		};

		var insured = policy.Parties?.OfType<InsuredParty>().FirstOrDefault();
		var lines = policy.Lines ?? new List<Line>();
		var autoLine = lines.OfType<CommonAutoLine>().FirstOrDefault();
		var assets = (policy.InsuredAssets ?? new List<InsuredAsset>())
			.OfType<AutoInsuredAsset>()
			.OrderBy(a => a.InsuredAssetNumber)
			.ToList();

		// ---- page header, repeated on every page (FORM.DAT flag OX) ---------------
		Root(data, "QTE_HDR", new()
		{
			["INSURED NAME1"] = insured?.FullName ?? "",
			["INSURED NAME2"] = SecondNameLine(insured),
			["POLICYNBR"] = policy.Number ?? "",
		});
		Root(data, "QTE_FTR", new());

		// ---- page 3: premium summary by insurance line ---------------------------
		// FORM.DAT has two variants of this page and the Products reference document
		// uses the second: `QUOTE CPPSUM.1` is a single Premium column, `QUOTE CPPSUM.2`
		// is the three-column Premium / *TRIA / Total Premium layout built from the
		// `_A` images. Both are populated here so either entry can be assembled.
		Root(data, "QCPPSUM_HDR", new());
		Root(data, "QCPPSUM_HDR_A", new());
		Root(data, "QTE_BILLINFO_A", new());

		// One summary row per insurance line the policy actually carries. The DDT
		// drives these off QCOV[INSLINE=…]; the CDM equivalent is simply which Line
		// subtypes are present.
		foreach (var (image, codes, premField, terrField, totalField) in LineSummaryRows)
		{
			var line = lines.FirstOrDefault(
				l => codes.Contains(l.Code ?? "", StringComparer.OrdinalIgnoreCase));
			if (line == null)
			{
				Suppress(data, image);
				Suppress(data, image + "_A");
				continue;
			}

			decimal terrorism = TerrorismPremium(line);
			decimal total = LinePremium(line);
			Root(data, image, new() { [totalField] = Money(total) });
			Root(data, image + "_A", new()
			{
				[premField] = Money(total - terrorism),
				[terrField] = Money(terrorism),
				[totalField] = Money(total),
			});

			// The nested rows under a line row: its endorsements/misc coverages.
			// Both variants splice in the SAME detail image, so it is emitted once.
			var detail = DetailImageFor(image);
			if (detail != null)
			{
				var img = Child(data, detail, image);
				foreach (var cov in (line.Coverages ?? new List<Coverage>())
						 .Where(c => !IsTerrorism(c)))
					img.Instances.Add(new PacketInstance
					{
						ParentIndex = 0,
						Fields = { ["DESCRIPTION"] = cov.Description ?? cov.Code ?? "" },
					});
			}
		}

		// "Scheduled Autos" sits under the Commercial Auto row when the quote has units.
		var sched = Child(data, "QCPPSUMDTLS_CA", "QCPPSUM_CA");
		if (assets.Count > 0)
			sched.Instances.Add(new PacketInstance { ParentIndex = 0 });

		// Terrorism gets its own summary row only when a terrorism coverage is present.
		bool hasTerrorism = lines
			.SelectMany(l => l.Coverages ?? new List<Coverage>()).Any(IsTerrorism);
		if (!hasTerrorism) Suppress(data, "QCPPSUM_TERR");

		decimal grand = lines.Sum(LinePremium);
		decimal grandTerr = lines.Sum(TerrorismPremium);
		Root(data, "QCPPSUM_TOTAL", new() { ["TOTALPREMIUM"] = Money(grand) });
		Root(data, "QCPPSUM_TOTAL_A", new()
		{
			["TOTPREM"] = Money(grand - grandTerr),
			["TOTTERR"] = Money(grandTerr),
			["TOTALPREMIUM"] = Money(grand),
		});

		// ---- the Commercial Auto detail pages -------------------------------------
		if (autoLine == null)
		{
			foreach (var image in AutoDetailImages) Suppress(data, image);
			return data;
		}

		Root(data, "QCPP_CA", new());

		// One vehicle block per unit, each with its own coverage rows and total.
		var veh = Child(data, "QCPP_CAV", "QCPP_CA");
		var vehTotal = Child(data, "QCPP_CAV2", "QCPP_CAV");
		var vehCov = Child(data, "QCPP_CAV1", "QCPP_CAV");
		for (int i = 0; i < assets.Count; i++)
		{
			var a = assets[i];
			veh.Instances.Add(new PacketInstance
			{
				ParentIndex = 0,
				Fields =
				{
					["VEHICLE A"] = a.InsuredAssetNumber.ToString(CultureInfo.InvariantCulture),
					["VEH1 YEAR"] = a.Year?.ToString(CultureInfo.InvariantCulture) ?? "",
					["VEH1 MAKE"] = Join(a.Make, a.Model, a.VehicleDescription),
					["VEH1 ID NO"] = a.Vin ?? "",
					["VEH1 RATE CLASS"] = a.ClassCode ?? "",
					["VEH1 TERRITORY"] = a.TerritoryCode ?? "",
					["VEH1 STATE"] = a.RateState ?? "",
					["VEH1 COSTNEW"] = Money(a.CostNew),
					["VEH1STCOST"] = Money(a.StatedAmount),
				},
			});
			vehTotal.Instances.Add(new PacketInstance
			{
				ParentIndex = i,
				Fields = { ["PREMIUM"] = Money(a.TotalPremium) },
			});
			foreach (var cov in (a.Coverages ?? new List<Coverage>())
					 .OrderBy(c => c.Code, StringComparer.Ordinal))
				vehCov.Instances.Add(new PacketInstance
				{
					ParentIndex = i,
					Fields =
					{
						["COVERAGE"] = cov.Description ?? cov.Code ?? "",
						["LIMIT"] = Amount(LimitOf(cov)),
						["DEDUCTIBLE"] = Amount(DeductibleOf(cov)),
						["PREMIUM"] = Money(cov.Premium),
					},
				});
		}

		// The line-level (non-vehicle) coverage table and the insurance-line total.
		Root(data, "QCPP_CAH", new());
		var lineTotal = Child(data, "QCPP_CAZ", "QCPP_CAH");
		lineTotal.Instances.Add(new PacketInstance
		{
			ParentIndex = 0,
			Fields = { ["CA PREM"] = Money(LinePremium(autoLine)) },
		});
		var lineCov = Child(data, "QCPP_CAA", "QCPP_CAH");
		foreach (var cov in autoLine.Coverages ?? new List<Coverage>())
			lineCov.Instances.Add(new PacketInstance
			{
				ParentIndex = 0,
				Fields =
				{
					["COVERAGE"] = cov.Description ?? cov.Code ?? "",
					["LIMIT"] = Amount(LimitOf(cov)),
					["DEDUCTIBLE"] = Amount(DeductibleOf(cov)),
					["PREMIUM"] = Money(cov.Premium),
				},
			});

		// Endorsement benefit schedules: one image per form AND edition, so the DDT
		// filter is `ASBECPL1[BEB8NB=EA9911 and BEAMDT=12412]`. Keep only the edition
		// the policy actually carries.
		var onPolicy = ((policy.Forms ?? new List<Form>()).Cast<Form>())
			.Select(f => (Code: f.FormCode ?? "", Edition: EditionKey(f.FormEditionDate)))
			.ToHashSet();
		foreach (var (image, code, edition) in EndorsementSchedules)
		{
			bool present = onPolicy.Contains((code, edition));
			if (present) Root(data, image, new());
			else Suppress(data, image);
		}

		// Montana has its own auto schedule; every other state gets the symbol page.
		bool mt = string.Equals(policy.State, "MT", StringComparison.OrdinalIgnoreCase);
		Count(data, "QTE_COVAUTOSYM", mt ? 0 : 1);
		Count(data, "QTE_AUTOSCHED_MT", mt ? 1 : 0);
		Count(data, "QTE_AUTOSCHED_MT_A", mt ? 1 : 0);

		return data;
	}

	// ---------------------------------------------------------------- packet tables

	/// <summary>
	/// Page-3 summary rows. Image, the CDM Line codes it stands for, and its field
	/// names: the single-column variant has just <c>&lt;xx&gt;PREM</c>, while the `_A`
	/// (TRIA) variant splits it into <c>&lt;xx&gt;PRM</c> / <c>&lt;xx&gt;TERR</c> /
	/// <c>&lt;xx&gt;PREM</c> — premium, terrorism, total. Note that in the `_A` layout
	/// <c>&lt;xx&gt;PREM</c> is the TOTAL column, not the premium column.
	/// </summary>
	private static readonly (string Image, string[] Codes, string PremiumField,
		string TerrorismField, string TotalField)[] LineSummaryRows =
	[
		("QCPPSUM_PL", ["PL"], "PLPRM", "PLTERR", "PLPREM"),
		("QCPPSUM_IM", ["IMC", "IM"], "IMPRM", "IMTERR", "IMPREM"),
		("QCPPSUM_CR", ["CR"], "CRPRM", "CRTERR", "CRPREM"),
		("QCPPSUM_GL", ["GL"], "GLPRM", "GLTERR", "GLPREM"),
		("QCPPSUM_CP", ["CF", "CP"], "CPPRM", "CPTERR", "CPPREM"),
		("QCPPSUM_CA", ["CA", "AS"], "CAPRM", "CATERR", "CAPREM"),
	];

	private static string? DetailImageFor(string summaryImage) =>
		summaryImage.Replace("QCPPSUM_", "QCPPSUMDTL_", StringComparison.Ordinal) is { } d
		&& d != summaryImage ? d : null;

	private static readonly string[] AutoDetailImages =
	[
		"QCPP_CA", "QCPP_CAV", "QCPP_CAV1", "QCPP_CAV2",
		"QCPP_CAH", "QCPP_CAZ", "QCPP_CAA",
		"QTE_COVAUTOSYM", "QTE_AUTOSCHED_MT", "QTE_AUTOSCHED_MT_A",
	];

	/// <summary>
	/// The endorsement benefit schedules the CA pages can splice in, and the form and
	/// edition each one belongs to. Read straight off the DDT filters in QCPP_CA.DDT:
	/// <c>ASBECPL1[BEB8NB = EA9911 and BEAMDT = 12412]</c>. BEAMDT is <c>1</c> + MMYY.
	/// </summary>
	private static readonly (string Image, string Code, string Edition)[] EndorsementSchedules =
	[
		("QTE_EA9911F", "EA9911", "1224"),
		("QTE_EA9911E", "EA9911", "0318"),
		("QTE_EA9911D", "EA9911", "1113"),
		("QTE_EA9910E", "EA9910", "1224"),
		("QTE_EA9910D", "EA9910", "1113"),
	];

	/// <summary>`BEAMDT` 12412 is century 1 + MMYY 2412; the CDM carries a real date.</summary>
	private static string EditionKey(DateOnly? d) =>
		d is null ? "" : d.Value.ToString("MMyy", CultureInfo.InvariantCulture);

	// ---------------------------------------------------------------------- helpers

	private static PacketImageData Root(QuotePacketData data, string image,
		Dictionary<string, string> fields)
	{
		var img = Get(data, image, parent: null);
		img.Instances.Add(new PacketInstance
		{
			ParentIndex = -1,
			Fields = new Dictionary<string, string>(fields, StringComparer.OrdinalIgnoreCase),
		});
		return img;
	}

	private static PacketImageData Child(QuotePacketData data, string image, string parent)
		=> Get(data, image, parent);

	private static void Suppress(QuotePacketData data, string image)
		=> Get(data, image, parent: null).Instances.Clear();

	private static void Count(QuotePacketData data, string image, int n)
	{
		var img = Get(data, image, parent: null);
		img.Instances.Clear();
		for (int i = 0; i < n; i++)
			img.Instances.Add(new PacketInstance { ParentIndex = -1 });
	}

	private static PacketImageData Get(QuotePacketData data, string image, string? parent)
	{
		if (!data.Images.TryGetValue(image, out var img))
			data.Images[image] = img = new PacketImageData();
		if (parent != null) img.Parent = parent;
		return img;
	}

	private static decimal LinePremium(Line line)
	{
		if (line.Premium > 0) return line.Premium.Value;
		// An unrated line still reports its buckets; fall back to their sum rather
		// than printing zero, and add the coverages the buckets do not include.
		decimal total = 0;
		if (line is CommonAutoLine auto && auto.PremiumTotals is { } t)
			total += (t.LiabilityPremium ?? 0) + (t.MedPayPremium ?? 0)
				   + (t.UninsuredMotoristPremium ?? 0)
				   + (t.UnderinsuredMotoristPremium ?? 0)
				   + (t.ComprehensivePremium ?? 0) + (t.CollisionPremium ?? 0)
				   + (t.SpecifiedPerilsPremium ?? 0) + (t.TowingPremium ?? 0)
				   + (t.PersonaInjuryProtectionPremium ?? 0)
				   + (t.EstimatedAdditionalPremiumForEndorsements ?? 0);
		else
			total += (line.Coverages ?? new List<Coverage>()).Sum(c => c.Premium);
		return total;
	}

	/// <summary>
	/// The TRIA column. The DDT selects the terrorism row by <c>QCOV[COVCODE = CT]</c>,
	/// so the CDM equivalent is the coverage whose code is CT.
	/// </summary>
	private static decimal TerrorismPremium(Line line) =>
		(line.Coverages ?? new List<Coverage>()).Where(IsTerrorism).Sum(c => c.Premium);

	private static bool IsTerrorism(Coverage c) =>
		string.Equals(c.Code, "CT", StringComparison.OrdinalIgnoreCase);

	private static string SecondNameLine(InsuredParty? insured)
	{
		// The dec page splits a long insured name over two lines; the quote header has
		// the same pair of fields. Nothing in the CDM carries a second legal name, so
		// this stays blank rather than inventing one — the same choice
		// BaDecPageFieldMap makes for the fields it does not own.
		_ = insured;
		return "";
	}

	private static string Join(params string?[] parts) =>
		string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p))
			.Select(p => p!.Trim()));

	private static string Money(decimal? v) =>
		v is null or 0 ? "" : v.Value.ToString("#,##0", CultureInfo.InvariantCulture);

	private static string Amount(decimal? v) =>
		v is null or 0 ? "" : v.Value.ToString("#,##0", CultureInfo.InvariantCulture);

	// Limits and deductibles live on the coverage SUBTYPES, not on the Coverage base
	// (CommercialAutoLIABCoverage.Limit, CommercialAutoUMCoverage.PerAccidentLimit,
	// CommercialAutoSOUNDCoverage.PerLossLimit, …), so the property is looked up by
	// name in preference order rather than switched on 200 subtypes.
	private static readonly string[] LimitNames =
		["Limit", "PerAccidentLimit", "PerLossLimit", "PerPersonLimit", "LimitAmount"];

	private static readonly string[] DeductibleNames =
		["Deductible", "DeductibleAmount"];

	private static decimal? LimitOf(Coverage c) => FirstNumeric(c, LimitNames);

	private static decimal? DeductibleOf(Coverage c) => FirstNumeric(c, DeductibleNames);

	private static decimal? FirstNumeric(object o, string[] names)
	{
		var type = o.GetType();
		foreach (var name in names)
		{
			var p = type.GetProperty(name,
				BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
			if (p == null) continue;
			var v = p.GetValue(o);
			if (v == null) continue;
			try
			{
				var d = Convert.ToDecimal(v, CultureInfo.InvariantCulture);
				if (d != 0) return d;
			}
			catch (InvalidCastException) { }
			catch (FormatException) { }
			catch (OverflowException) { }
		}
		return null;
	}
}
