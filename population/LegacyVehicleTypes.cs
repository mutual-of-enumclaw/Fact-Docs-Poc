using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace FapPdfTools.Population;

/// <summary>
/// The Auto Summary's VEHICLE TYPES SUMMARY buckets — Light Trucks, Medium Trucks,
/// Heavy, Extra Heavy, Trailers, Buses, Private Passenger — read from the DDT that
/// defines them rather than copied into C#.
/// </summary>
/// <remarks>
/// <para>
/// <c>QCPP_CAVS_VTS.DDT</c> counts extract records per bucket by matching the class
/// code against an explicit list:
/// </para>
/// <code>
///   ;0;1;;0;0;LIGHT;0;6;…;JustFld;TBLOFF,ASB5CPL1,B5AGTX,CA,
///       B5PTTX,(011,012,…,036),MODE=R,RULE=ExtractRecordCount,CLIP;…
/// </code>
/// <para>
/// The entries are class-code PREFIXES of varying length — "011" catches class 01199,
/// "324" catches 32499, and the private-passenger list is four digits ("7391"). ALLOTHERS
/// is the remainder, which the DDT states outright:
/// <c>TOTSUMVEHS - LIGHT - MEDIUM - HEAVY - XHEAVY - TRAILERS - BUSES - PPTS</c>.
/// </para>
/// <para>
/// Reading the lists from the file keeps them in step with the form: the BUSES bucket
/// alone is seventy codes, and a class-code addition lands in the DDT, not here.
/// </para>
/// </remarks>
public static class LegacyVehicleTypes
{
	private const string DefaultDdtPath =
		@"C:\src\FaCT-DocProd-Development\mstrres\MOEC0\DDTLIB\QCPP_CAVS_VTS.DDT";

	/// <summary>Overridable for tests or a different mstrres root.</summary>
	public static string DdtPath { get; set; } = DefaultDdtPath;

	/// <summary>The counted buckets, in the DDT's own order. ALLOTHERS is derived.</summary>
	public static readonly string[] Buckets =
		["LIGHT", "MEDIUM", "HEAVY", "XHEAVY", "TRAILERS", "BUSES", "PPTS"];

	private static readonly Lazy<Dictionary<string, string[]>> Lists = new(Load);

	// ;0;1;;0;0;LIGHT;0;6;…;B5PTTX,(011,012,…),MODE=R,…
	private static readonly Regex RuleLine = new(
		@";(?<field>[A-Z]+);0;\d+;[^;]*;JustFld;[^;]*B5PTTX,\((?<codes>[^)]*)\)",
		RegexOptions.Compiled);

	private static Dictionary<string, string[]> Load()
	{
		var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
		if (!File.Exists(DdtPath)) return map;
		foreach (var line in File.ReadLines(DdtPath))
		{
			var m = RuleLine.Match(line);
			if (!m.Success) continue;
			var field = m.Groups["field"].Value;
			if (!Buckets.Contains(field, StringComparer.OrdinalIgnoreCase)) continue;
			map[field] = m.Groups["codes"].Value
				.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.ToArray();
		}
		return map;
	}

	/// <summary>True when the DDT was found and its lists parsed.</summary>
	public static bool Available => Lists.Value.Count > 0;

	/// <summary>
	/// The bucket a class code falls in, or null for "all other". Longest prefix wins,
	/// so a four-digit private-passenger code is not stolen by a three-digit truck one.
	/// </summary>
	public static string? BucketFor(string? classCode)
	{
		var code = (classCode ?? "").Trim();
		if (code.Length == 0) return null;
		string? best = null;
		int bestLength = 0;
		foreach (var (bucket, prefixes) in Lists.Value)
			foreach (var prefix in prefixes)
				if (prefix.Length > bestLength
					&& code.StartsWith(prefix, StringComparison.Ordinal))
				{
					best = bucket;
					bestLength = prefix.Length;
				}
		return best;
	}

	/// <summary>
	/// Count each bucket over a set of class codes and derive ALLOTHERS and TOTSUMVEHS
	/// exactly as the DDT does.
	/// </summary>
	public static Dictionary<string, int> Count(IEnumerable<string?> classCodes)
	{
		var counts = Buckets.ToDictionary(b => b, _ => 0, StringComparer.OrdinalIgnoreCase);
		int total = 0;
		foreach (var code in classCodes)
		{
			total++;
			var bucket = BucketFor(code);
			if (bucket != null) counts[bucket]++;
		}
		counts["TOTSUMVEHS"] = total;
		counts["ALLOTHERS"] = total - Buckets.Sum(b => counts[b]);
		return counts;
	}
}
