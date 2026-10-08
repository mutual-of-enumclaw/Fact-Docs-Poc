using System.Buffers.Binary;

namespace FaCT.DocDesigner.POC.Import;

/// <summary>
/// Just enough of the TrueType / OpenType format to decide whether a font embedded in a PDF can be used as a web font:
/// it must have outlines (glyf or CFF) and a Unicode character map that covers the characters it is used for.
/// PDF subset fonts often have only a symbolic map (glyphs by PDF code, not by character); those would print the wrong
/// glyphs in a browser, so they are rejected and the text falls back to a standard family.
/// Every read is bounds-checked: the bytes come from an uploaded file.
/// </summary>
public sealed class TrueTypeFont
{
	private readonly byte[] _data;
	private readonly int _cmapFormat;
	private readonly int _cmapOffset;

	private TrueTypeFont(byte[] data, int cmapFormat, int cmapOffset)
	{
		_data = data;
		_cmapFormat = cmapFormat;
		_cmapOffset = cmapOffset;
	}

	/// <summary>Reads the font, or returns null when it isn't a usable TrueType/OpenType font with a Unicode map.</summary>
	public static TrueTypeFont? TryRead(byte[] data)
	{
		try
		{
			if (data.Length < 12) return null;
			var version = BinaryPrimitives.ReadUInt32BigEndian(data);
			if (version is not (0x00010000u or 0x4F54544Fu /* OTTO */ or 0x74727565u /* true */)) return null;
			var tables = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4));
			int cmap = -1, cmapLength = 0;
			var outlines = false;
			for (var i = 0; i < tables; i++)
			{
				var record = 12 + i * 16;
				if (record + 16 > data.Length) return null;
				var tag = System.Text.Encoding.ASCII.GetString(data, record, 4);
				var offset = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(record + 8)));
				var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(record + 12)));
				if (offset < 0 || length < 0 || (long)offset + length > data.Length) return null;
				if (tag == "cmap") { cmap = offset; cmapLength = length; }
				if (tag is "glyf" or "CFF " or "CFF2") outlines = true;
			}
			if (cmap < 0 || !outlines || cmapLength < 4) return null;

			// Prefer the full-repertoire map (3,10 format 12), then the BMP map (3,1 format 4), then Unicode platform 0.
			var subtables = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(cmap + 2));
			(int Format, int Offset)? best = null;
			var bestRank = int.MaxValue;
			for (var i = 0; i < subtables; i++)
			{
				var record = cmap + 4 + i * 8;
				if (record + 8 > cmap + cmapLength) return null;
				var platform = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(record));
				var encoding = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(record + 2));
				var offset = cmap + checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(record + 4)));
				if (offset + 2 > data.Length) return null;
				var format = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset));
				var rank = (platform, encoding, format) switch
				{
					(3, 10, 12) => 0,
					(0, _, 12) => 1,
					(3, 1, 4) => 2,
					(0, _, 4) => 3,
					_ => int.MaxValue
				};
				if (rank < bestRank) { bestRank = rank; best = (format, offset); }
			}
			return best is { } b ? new TrueTypeFont(data, b.Format, b.Offset) : null;
		}
		catch (Exception ex) when (ex is OverflowException or ArgumentOutOfRangeException or IndexOutOfRangeException)
		{
			return null;
		}
	}

	/// <summary>The glyph for a Unicode code point, or 0 (missing).</summary>
	public int Glyph(int codePoint)
	{
		try
		{
			return _cmapFormat == 12 ? Format12(codePoint) : Format4(codePoint);
		}
		catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
		{
			return 0;
		}
	}

	/// <summary>True when every character of <paramref name="text"/> (whitespace aside) has a glyph.</summary>
	public bool Covers(string text)
	{
		foreach (var rune in text.EnumerateRunes())
		{
			if (System.Text.Rune.IsWhiteSpace(rune) || System.Text.Rune.IsControl(rune)) continue;
			if (Glyph(rune.Value) == 0) return false;
		}
		return true;
	}

	private int Format4(int codePoint)
	{
		if (codePoint > 0xFFFF) return 0;
		var span = _data.AsSpan(_cmapOffset);
		var segments = BinaryPrimitives.ReadUInt16BigEndian(span[6..]) / 2;
		var ends = 14;
		var starts = ends + segments * 2 + 2;
		var deltas = starts + segments * 2;
		var ranges = deltas + segments * 2;
		for (var i = 0; i < segments; i++)
		{
			var end = BinaryPrimitives.ReadUInt16BigEndian(span[(ends + i * 2)..]);
			if (codePoint > end) continue;
			var start = BinaryPrimitives.ReadUInt16BigEndian(span[(starts + i * 2)..]);
			if (codePoint < start) return 0;
			var delta = BinaryPrimitives.ReadInt16BigEndian(span[(deltas + i * 2)..]);
			var rangeOffset = BinaryPrimitives.ReadUInt16BigEndian(span[(ranges + i * 2)..]);
			if (rangeOffset == 0) return (codePoint + delta) & 0xFFFF;
			var at = ranges + i * 2 + rangeOffset + (codePoint - start) * 2;
			var glyph = BinaryPrimitives.ReadUInt16BigEndian(span[at..]);
			return glyph == 0 ? 0 : (glyph + delta) & 0xFFFF;
		}
		return 0;
	}

	private int Format12(int codePoint)
	{
		var span = _data.AsSpan(_cmapOffset);
		var groups = BinaryPrimitives.ReadUInt32BigEndian(span[12..]);
		for (var i = 0; i < groups; i++)
		{
			var group = span[(16 + i * 12)..];
			var start = BinaryPrimitives.ReadUInt32BigEndian(group);
			var end = BinaryPrimitives.ReadUInt32BigEndian(group[4..]);
			if (codePoint < start) return 0;
			if (codePoint <= end) return (int)(BinaryPrimitives.ReadUInt32BigEndian(group[8..]) + (codePoint - start));
		}
		return 0;
	}
}
