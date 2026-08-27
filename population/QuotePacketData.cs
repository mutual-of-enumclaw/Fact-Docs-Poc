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
		// QTE_FTR's only field is QUOTE.PAGE and the page number is not a property of the
		// policy -- the assembler fills it once the layout is known.
		Root(data, "QTE_FTR", new());

		// ---- page 1: the branded cover -------------------------------------------
		// `QUOTE COVER.4` -> QTE_COVER_A, the page that opens "This Mutual of Enumclaw
		// Quote is personally prepared for". COVER.1's QTE_COVER is a different, older
		// cover; all four carry the same field names, so all are populated.
		//
		// EFFDATE and EXPDATE are deliberately NOT filled. On QTE_COVER_A they sit at
		// columns 635 and 1035 -- outside the form's own 1600 left margin, in 7.2pt
		// boxes -- because they are WORKING fields that feed PROPOSAL PERIOD, which is
		// the one the reader sees. Filling them printed two dates on top of each other
		// in the margin.
		var agency = policy.Parties?.OfType<AgencyParty>().FirstOrDefault();
		var cover = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			["INSURED NAME1"] = insured?.FullName ?? "",
			["INSURED NAME2"] = SecondNameLine(insured),
			["AGENT NAME"] = agency?.FullName ?? "",
			["AGENT PHONE"] = agency?.Phone ?? "",
			["PROPOSAL PERIOD"] =
				$"{Date(policy.EffectiveDate)} to {Date(policy.ExpirationDate)}".Trim(),
		};
		foreach (var image in new[] { "QTE_COVER_A", "QTE_COVER_B", "QTE_COVER_C" })
			Root(data, image, new(cover));
		// The older QTE_COVER (QUOTE COVER.1) puts the insured in TITLE as well.
		Root(data, "QTE_COVER", new(cover) { ["TITLE"] = insured?.FullName ?? "" });

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

		// The CA detail page has three FORM.DAT variants -- CPPCA.1/.2/.3, built from
		// QCPP_CA / _A / _B. The Products reference document prints the .3 layout:
		// Territory on its own line, no second State row inside the vehicle box, and
		// the state as a NAME ("WYOMING") in the block header. All three are populated
		// so any of the entries can be assembled.
		Root(data, "QCPP_CA", new());
		Root(data, "QCPP_CA_A", new());
		Root(data, "QCPP_CA_B", new());

		// One vehicle block per unit, each with its own coverage rows and total.
		var vehImages = new[] { "QCPP_CAV", "QCPP_CAV_A", "QCPP_CAV_B" }
			.Select(v => Child(data, v, "QCPP_CA")).ToArray();
		var vehTotals = new[] { "QCPP_CAV2", "QCPP_CAV2_A" }
			.Select(v => Child(data, v, "QCPP_CAV")).ToArray();
		var vehCov = Child(data, "QCPP_CAV1", "QCPP_CAV");
		for (int i = 0; i < assets.Count; i++)
		{
			var a = assets[i];
			var vehicle = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
			{
				["VEHICLE A"] = a.InsuredAssetNumber.ToString(CultureInfo.InvariantCulture),
				["VEH1 YEAR"] = a.Year?.ToString(CultureInfo.InvariantCulture) ?? "",
				["VEH1 MAKE"] = Join(a.Make, a.Model, a.VehicleDescription),
				["VEH1 ID NO"] = a.Vin ?? "",
				["VEH1 RATE CLASS"] = a.ClassCode ?? "",
				["VEH1 TERRITORY"] = a.TerritoryCode ?? "",
				// The field is 15 characters and the render says "WYOMING", not "WY".
				["VEH1 STATE"] = StateName(a.RateState),
				["VEH1 COSTNEW"] = Money(a.CostNew),
				["VEH1STCOST"] = Money(a.StatedAmount),
			};
			foreach (var img in vehImages)
				img.Instances.Add(new PacketInstance { ParentIndex = 0, Fields = new(vehicle) });
			foreach (var img in vehTotals)
				img.Instances.Add(new PacketInstance
				{
					ParentIndex = i,
					Fields =
					{
						// QCPP_CAV2_A carries the row LABEL as a field, not as static text.
						// `ThisVehState` is a CONTROL field -- the DAL reads it to decide
						// state-specific wording and legacy never prints it, so it stays
						// blank here rather than putting a stray "WY" in the total row.
						["TOTAL VERBIAGE"] = "Total Vehicle Premium",
						["PREMIUM"] = Money(a.TotalPremium),
					},
				});

			var own = a.Coverages ?? new List<Coverage>();
			foreach (var cov in LegacyCoverageText.InVehicleOrder(own))
				vehCov.Instances.Add(new PacketInstance
				{
					ParentIndex = i,
					Fields =
					{
						["COVERAGE"] = LegacyCoverageText.Describe(cov),
						["LIMIT"] = LegacyCoverageText.Limit(cov, own),
						["DEDUCTIBLE"] = LegacyCoverageText.Deductible(cov, own),
						["PREMIUM"] = Money(cov.Premium),
					},
				});
		}

		// The line-level (non-vehicle) coverage table and ITS total.
		//
		// "Total Commercial Auto Insurance Line Premium" is NOT the insurance line's
		// premium. Measured against the reference document, which prints 541 for this
		// quote: it is the sum of the rows in THIS table -- the line-level coverages --
		// where the line premium including every vehicle is 9,810. We printed 9,810 and
		// Products caught it.
		var lineCoverages = (autoLine.Coverages ?? new List<Coverage>()).ToList();
		Root(data, "QCPP_CAH", new());
		Root(data, "QCPP_CAH_A", new());
		foreach (var img in new[] { "QCPP_CAZ", "QCPP_CAZ_A" }
					 .Select(v => Child(data, v, "QCPP_CAH")))
			img.Instances.Add(new PacketInstance
			{
				ParentIndex = 0,
				Fields =
				{
					["TOTAL VERBIAGE"] = "Total Commercial Auto Insurance Line Premium",
					["CA PREM"] = Money(lineCoverages.Sum(c => c.Premium)),
				},
			});
		var lineCov = Child(data, "QCPP_CAA", "QCPP_CAH");
		foreach (var cov in LegacyCoverageText.InLineOrder(lineCoverages))
			lineCov.Instances.Add(new PacketInstance
			{
				ParentIndex = 0,
				Fields =
				{
					["COVERAGE"] = LegacyCoverageText.Describe(cov),
					// lineTable: CPPQ_CAA_* and CPPQ_CAV1_* disagree about these two
					// columns, so which table this is has to be said out loud.
					["LIMIT"] = LegacyCoverageText.Limit(cov, lineCoverages, lineTable: true),
					["DEDUCTIBLE"] = LegacyCoverageText.Deductible(cov, lineCoverages, lineTable: true),
					["PREMIUM"] = Money(cov.Premium),
				},
			});

		// Covered auto symbols. The page renders blank without these -- the same
		// symbol sets BaDecPageFieldMap spreads across the dec page's cells.
		var symbols = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		SpreadSymbols(symbols, "LIABCAS", autoLine.LiabilityAutoSymbols?
			.Select(s => s.AutoSymbol), 4);
		SpreadSymbols(symbols, "PIPCAS", SymbolsFor(autoLine, "PIP"), 2);
		SpreadSymbols(symbols, "MPCAS", SymbolsFor(autoLine, "MEDPAY"), 2);
		SpreadSymbols(symbols, "UMCAS", SymbolsFor(autoLine, "UM"), 3);
		SpreadSymbols(symbols, "UNCAS", SymbolsFor(autoLine, "UN"), 2);
		SpreadSymbols(symbols, "COMPCAS", SymbolsFor(autoLine, "COMP"), 2);
		SpreadSymbols(symbols, "SCLCAS", SymbolsFor(autoLine, "SPEC"), 2);
		SpreadSymbols(symbols, "COLCAS", SymbolsFor(autoLine, "COLL"), 2);
		SpreadSymbols(symbols, "TLCAS", SymbolsFor(autoLine, "TOWING"), 2);

		// Endorsement benefit schedules: one image per form AND edition, so the DDT
		// filter is `ASBECPL1[BEB8NB=EA9911 and BEAMDT=12412]`. Keep only the edition
		// the policy actually carries.
		//
		// The forms live on the LINE, not on the policy: `Policy.Forms` on this quote
		// holds a single ME0001 while `Line.Forms` holds 27 including EA9911/2018-03.
		// Looking only at Policy.Forms is why the EA 99 11 03 18 schedule was missing.
		//
		// A form code can appear TWICE with different editions -- this quote carries
		// EA9911 at both 2018-03 (sequence 32) and 2024-12 (sequence 38), and CA0001 the
		// same way, both with actionCode "A" so nothing marks one as superseded. Only one
		// schedule prints, and the reference document prints the 03 18 one. Legacy reads
		// the extract with `move_it`/`tbllook`, which take the FIRST matching row, so the
		// lowest form sequence wins here too. INFERRED from that one render -- if a
		// future quote prints the later edition, this is the rule that is wrong.
		var onPolicy = AllForms(policy)
			.Where(f => !string.IsNullOrEmpty(f.FormCode))
			.GroupBy(f => f.FormCode!, StringComparer.OrdinalIgnoreCase)
			.Select(g => g.OrderBy(f => f.FormSequenceNumber).First())
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
		foreach (var image in new[] { "QTE_COVAUTOSYM" })
		{
			if (mt) { Suppress(data, image); continue; }
			Get(data, image, parent: null).Instances.Clear();
			Get(data, image, parent: null).Instances.Add(new PacketInstance
			{
				ParentIndex = -1,
				Fields = new Dictionary<string, string>(symbols, StringComparer.OrdinalIgnoreCase),
			});
		}
		Count(data, "QTE_AUTOSCHED_MT", mt ? 1 : 0);
		Count(data, "QTE_AUTOSCHED_MT_A", mt ? 1 : 0);

		AutoSummary(data, assets, lineCoverages);
		FormsSchedule(data, policy, insured);

		return data;
	}

	// ------------------------------------------------------------------ Auto Summary

	/// <summary>
	/// `QUOTE CPPCAVS.3` — one row per vehicle, the three premium totals, and the
	/// VEHICLE TYPES SUMMARY. (`.1` is the same page without the types block.)
	/// </summary>
	private static void AutoSummary(QuotePacketData data, List<AutoInsuredAsset> assets,
		List<Coverage> lineCoverages)
	{
		Root(data, "QCPP_CAVS_HDR", new());
		foreach (var image in new[] { "QCPP_CAVS_VEHHDR", "QCPP_CAVS_VEHHDR_B" })
			Root(data, image, new());

		var rows = new[] { "QCPP_CAVS_VEHDET", "QCPP_CAVS_VEHDET_B" }
			.Select(v => Child(data, v, "QCPP_CAVS_VEHHDR")).ToArray();
		foreach (var a in assets)
		{
			var premium = Money(a.TotalPremium);
			var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
			{
				["VEH NUM"] = a.InsuredAssetNumber.ToString("000", CultureInfo.InvariantCulture),
				["VEH STATE"] = a.RateState ?? "",
				["VEH YEAR"] = a.Year?.ToString(CultureInfo.InvariantCulture) ?? "",
				["VEH MM"] = Join(a.Make, a.Model, a.VehicleDescription),
				["VEH CLASS"] = a.ClassCode ?? "",
				["VEH VIN"] = a.Vin ?? "",
				["VEH TOT PREM"] = premium,
				// The DDT prints these as literals conditioned on the value next to them:
				//   VSDS  ->  If LEN(TRIM(@("VEH TOT PREM"))) > 0 :: Return("$")
				//   LV    ->  printif  B5I4TX  Y=*      (the leased-vehicle asterisk)
				["VSDS"] = premium.Length > 0 ? "$" : "",
				["LV"] = string.Equals(a.LeasedVehicleCode, "Y",
					StringComparison.OrdinalIgnoreCase) ? "*" : "",
			};
			foreach (var img in rows)
				img.Instances.Add(new PacketInstance { ParentIndex = 0, Fields = new(row) });
		}

		decimal vehicles = assets.Sum(a => a.TotalPremium ?? 0);
		decimal line = lineCoverages.Sum(c => c.Premium);
		Root(data, "QCPP_CAVS_FTR", new()
		{
			["CA_VEHS_TOTAL_PREM"] = Money(vehicles),
			["VSVTDS"] = vehicles != 0 ? "$" : "",
			["CA_INSL_TOTAL_PREM"] = Money(line),
			["VSILDS"] = line != 0 ? "$" : "",
			["CA_TOTAL_PREM"] = Money(vehicles + line),
			["VSDS"] = vehicles + line != 0 ? "$" : "",
			["TOTVEHS"] = assets.Count.ToString(CultureInfo.InvariantCulture),
		});

		var counts = LegacyVehicleTypes.Count(assets.Select(a => a.ClassCode));
		Root(data, "QCPP_CAVS_VTS", counts.ToDictionary(
			kv => kv.Key,
			kv => kv.Value.ToString(CultureInfo.InvariantCulture),
			StringComparer.OrdinalIgnoreCase));
	}

	// -------------------------------------------------------------- Forms schedule

	/// <summary>
	/// `QUOTE CPP FORMS.1` — the FORMS AND ENDORSEMENT SCHEDULE. Each of the four
	/// images is one ROW TYPE, and the DDTs say which forms belong to each:
	/// <code>
	///   QTE_CPP95DP    FORMREC1  FORMID in (DP, ME0001)          COVLINE "All Lines"
	///   QTE_CPP95COM   FORMREC1  FORMID = COM                    COVLINE "All Lines"
	///   QTE_CPP95IL    FORMREC1  FORMID in (IL, ME), not ME0001  COVLINE "Interline"
	///   QTE_CPP95BD    FRMREC2   INSLINE != *AL                  COVLINE from INSLINE
	/// </code>
	/// The split is by FORM CODE PREFIX, not by the form's line — which is why every
	/// IL… form prints under Interline while the EL… forms print under Commercial Auto.
	/// </summary>
	private static void FormsSchedule(QuotePacketData data, Policy policy,
		InsuredParty? insured)
	{
		Root(data, "QTE_FORM", new()
		{
			["INSURED NAME1"] = insured?.FullName ?? "",
			["INSURED NAME2"] = SecondNameLine(insured),
			["POLICYNBR"] = policy.Number ?? "",
		});

		// One row per form code AND edition. A code legitimately appears twice at two
		// editions (CA0001 10/13 and 11/20), and the same form is attached at more than
		// one level, so dedupe on the pair rather than on the code.
		var forms = AllForms(policy)
			.Where(f => !string.IsNullOrWhiteSpace(f.FormCode))
			.GroupBy(f => (Code: f.FormCode!.Trim().ToUpperInvariant(),
						   Edition: EditionKey(f.FormEditionDate)))
			.Select(g => g.First())
			.ToList();

		// These four are TOP-LEVEL images in the FORM.DAT entry, not children of
		// QTE_FORM -- QTE_FORM is flagged OX, so it is the page header and the rows
		// flow beneath it. Declaring them as children filed their instances under a
		// parent index the assembler never asks for, and the page came out empty.
		var dp = Get(data, "QTE_CPP95DP", parent: null);
		var com = Get(data, "QTE_CPP95COM", parent: null);
		var il = Get(data, "QTE_CPP95IL", parent: null);
		var bd = Get(data, "QTE_CPP95BD", parent: null);
		foreach (var img in new[] { dp, com, il, bd }) img.Instances.Clear();

		foreach (var f in forms.OrderBy(f => f.FormCode, StringComparer.Ordinal)
					 .ThenBy(f => f.FormEditionDate))
		{
			var code = f.FormCode!.Trim().ToUpperInvariant();
			var edition = $"({EditionKey(f.FormEditionDate, "MM/yy")})";
			var name = f.FormDescription ?? "";

			if (code == "ME0001")
			{
				dp.Instances.Add(new PacketInstance
				{
					ParentIndex = -1,
					Fields =
					{
						["dpCOVLINE"] = "All Lines",
						["dpFORMNUM"] = code,
						["dpEDATE"] = edition,
						["dpFORMNAME"] = name,
					},
				});
			}
			else if (code.StartsWith("IL", StringComparison.Ordinal)
					 || code.StartsWith("ME", StringComparison.Ordinal))
			{
				il.Instances.Add(new PacketInstance
				{
					ParentIndex = -1,
					Fields =
					{
						["COVLINE"] = "Interline",
						["FORMNUM"] = code,
						["EDATE"] = edition,
						["FORMNAME"] = name,
					},
				});
			}
			else
			{
				bd.Instances.Add(new PacketInstance
				{
					ParentIndex = -1,
					Fields =
					{
						["COVLINE"] = CoverageLineName(f.InsuranceLineCode),
						["FORMNUM"] = code,
						["EDATE"] = edition,
						["FORMNAME"] = name,
					},
				});
			}
		}
	}

	/// <summary>
	/// The `Coverage line` column. Straight from the printif map in QTE_CPP95BD.DDT:
	/// <c>GL =General Liability:CF =Commercial Fire:CR =Crime:IMC=Inland Marine:
	/// CA =Commercial Auto:…</c>
	/// </summary>
	private static string CoverageLineName(string? insuranceLineCode) =>
		(insuranceLineCode ?? "").Trim().ToUpperInvariant() switch
		{
			"GL" => "General Liability",
			"CF" => "Commercial Fire",
			"CR" => "Crime",
			"IMC" => "Inland Marine",
			"CA" => "Commercial Auto",
			"PL" => "Professional Liability",
			"EC" => "Emerald Series Church",
			"FA" => "Farmowners Auto",
			"AAA" => "Farm Lines",
			"FD" => "Farm Dwellings",
			"FP" => "Farm Property",
			"FS" => "Farm Structures",
			"FL" => "Farmowners Liability",
			"FGL" => "Farmowners General Liability",
			"FIM" => "Farmowners Inland Marine",
			"*AL" => "All Lines",
			var other => other,
		};

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
	private static string EditionKey(DateOnly? d, string format = "MMyy") =>
		d is null ? "" : d.Value.ToString(format, CultureInfo.InvariantCulture);

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

	/// <summary>Every form the transaction carries — policy, line and unit level.</summary>
	private static IEnumerable<Form> AllForms(Policy policy)
	{
		foreach (var f in policy.Forms ?? new List<Form>()) yield return f;
		foreach (var line in policy.Lines ?? new List<Line>())
			foreach (var f in line.Forms ?? new List<Form>()) yield return f;
		foreach (var asset in policy.InsuredAssets ?? new List<InsuredAsset>())
			foreach (var f in asset.Forms ?? new List<Form>()) yield return f;
	}

	/// <summary>
	/// `VEH1 STATE` is 15 characters wide and the reference render says "WYOMING", so the
	/// field wants the state NAME. Only the states MoE writes are listed; anything else
	/// falls through as the code, which is still better than blank.
	/// </summary>
	private static string StateName(string? code) => (code ?? "").Trim().ToUpperInvariant() switch
	{
		"WA" => "WASHINGTON",
		"OR" => "OREGON",
		"ID" => "IDAHO",
		"AZ" => "ARIZONA",
		"UT" => "UTAH",
		"MT" => "MONTANA",
		"WY" => "WYOMING",
		var other => other,
	};

	private static string Date(DateTime? d) =>
		d?.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture) ?? "";

	/// <summary>
	/// The covered-auto symbols that apply to one coverage, matched on the coverage code
	/// the CDM carries on the symbol — the same join
	/// <see cref="Maps.BaDecPageFieldMap"/> uses for the dec page.
	/// </summary>
	private static IEnumerable<string>? SymbolsFor(CommonAutoLine? line, string coverageCode)
		=> line?.OtherAutoSymbols?
			.Where(s => string.Equals(s.CoverageCode, coverageCode,
				StringComparison.OrdinalIgnoreCase))
			.Select(s => s.AutoSymbol);

	/// <summary>One symbol per numbered cell: LIABCAS1, LIABCAS2, …</summary>
	private static void SpreadSymbols(IDictionary<string, string> into, string prefix,
		IEnumerable<string?>? symbols, int cells)
	{
		var list = (symbols ?? Enumerable.Empty<string?>())
			.Where(s => !string.IsNullOrWhiteSpace(s))
			.Select(s => s!.Trim())
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.OrderBy(s => s, StringComparer.Ordinal)
			.Take(cells)
			.ToList();
		for (int i = 0; i < list.Count; i++)
			into[$"{prefix}{i + 1}"] = list[i];
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
