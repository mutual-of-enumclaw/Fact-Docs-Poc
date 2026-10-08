using System.Buffers.Binary;
using System.IO.Compression;
using Docnet.Core;
using Docnet.Core.Models;

namespace FaCT.DocDesigner.POC.Tests.Import;

/// <summary>Which pixels of a page carry ink (anything clearly darker than paper), at a fixed resolution.</summary>
public sealed record InkMap(int Width, int Height, bool[] Ink)
{
	public bool this[int x, int y] => x >= 0 && y >= 0 && x < Width && y < Height && Ink[y * Width + x];

	public int Count => Ink.Count(i => i);
}

/// <summary>
/// Page images for the import proof's visual comparison: PDF pages rasterized (PDFium via Docnet), reduced to ink maps,
/// compared with a small position tolerance, and drawn as overlays (original red, render blue, both black).
/// </summary>
public static class PageImages
{
	/// <summary>Pixels per PDF point: 0.5 = 36 dpi, a 306 x 396 Letter page.</summary>
	public const double Scale = 0.5;

	/// <summary>Ink may sit this many pixels (about 4pt at 36 dpi) from where it is on the other page.</summary>
	public const int Tolerance = 2;

	private static readonly object PdfiumLock = new();

	/// <summary>Ink maps of pages [first, first + count) (1-based), each scaled by <paramref name="pageScale"/>(width, height)
	/// on top of <see cref="Scale"/> (an imported page larger than Letter was scaled down to fit).</summary>
	public static IReadOnlyList<InkMap> Render(byte[] pdf, int first = 1, int count = int.MaxValue, Func<double, double, double>? pageScale = null)
	{
		var maps = new List<InkMap>();
		lock (PdfiumLock)
		{
			using var reader = DocLib.Instance.GetDocReader(pdf, new PageDimensions(Scale));
			var last = Math.Min(reader.GetPageCount(), first - 1 + count);
			for (var index = first - 1; index < last; index++)
			{
				using var page = reader.GetPageReader(index);
				int width = page.GetPageWidth(), height = page.GetPageHeight();
				var map = FromBgra(page.GetImage(), width, height);
				var extra = pageScale?.Invoke(width / Scale, height / Scale) ?? 1;
				maps.Add(extra < 0.999 ? Resize(map, extra) : map);
			}
		}
		return maps;
	}

	public static int PageCount(byte[] pdf)
	{
		lock (PdfiumLock)
		{
			using var reader = DocLib.Instance.GetDocReader(pdf, new PageDimensions(Scale));
			return reader.GetPageCount();
		}
	}

	/// <summary>A page's pixels (1-based page) as BGRA at <see cref="Scale"/> (or the given scale); alpha 0 where nothing was painted.</summary>
	public static (byte[] Bgra, int Width, int Height) Pixels(byte[] pdf, int page = 1, double scale = Scale)
	{
		lock (PdfiumLock)
		{
			using var reader = DocLib.Instance.GetDocReader(pdf, new PageDimensions(scale));
			using var pageReader = reader.GetPageReader(page - 1);
			return (pageReader.GetImage(), pageReader.GetPageWidth(), pageReader.GetPageHeight());
		}
	}

	/// <summary>Ink = composited over white, luminance below 170 of 255.</summary>
	public static InkMap FromBgra(byte[] bgra, int width, int height)
	{
		var ink = new bool[width * height];
		for (var i = 0; i < ink.Length; i++)
		{
			int b = bgra[i * 4], g = bgra[i * 4 + 1], r = bgra[i * 4 + 2], a = bgra[i * 4 + 3];
			var luminance = (r * 299 + g * 587 + b * 114) / 1000;
			var onWhite = (luminance * a + 255 * (255 - a)) / 255;
			ink[i] = onWhite < 170;
		}
		return new InkMap(width, height, ink);
	}

	/// <summary>Scales a map by <paramref name="factor"/> (&lt; 1): a target pixel has ink if any source pixel under it does.</summary>
	public static InkMap Resize(InkMap map, double factor)
	{
		var width = Math.Max(1, (int)Math.Round(map.Width * factor));
		var height = Math.Max(1, (int)Math.Round(map.Height * factor));
		var ink = new bool[width * height];
		for (var y = 0; y < map.Height; y++)
		{
			for (var x = 0; x < map.Width; x++)
			{
				if (!map.Ink[y * map.Width + x]) continue;
				var tx = Math.Min(width - 1, (int)(x * factor));
				var ty = Math.Min(height - 1, (int)(y * factor));
				ink[ty * width + tx] = true;
			}
		}
		return new InkMap(width, height, ink);
	}

	/// <summary>Ink grown by <paramref name="radius"/> pixels in every direction.</summary>
	public static InkMap Dilate(InkMap map, int radius)
	{
		if (radius <= 0) return map;
		var rows = new bool[map.Ink.Length];
		for (var y = 0; y < map.Height; y++)
		{
			for (var x = 0; x < map.Width; x++)
			{
				if (!map.Ink[y * map.Width + x]) continue;
				for (var dx = -radius; dx <= radius; dx++)
				{
					var nx = x + dx;
					if (nx >= 0 && nx < map.Width) rows[y * map.Width + nx] = true;
				}
			}
		}
		var result = new bool[map.Ink.Length];
		for (var y = 0; y < map.Height; y++)
		{
			for (var x = 0; x < map.Width; x++)
			{
				if (!rows[y * map.Width + x]) continue;
				for (var dy = -radius; dy <= radius; dy++)
				{
					var ny = y + dy;
					if (ny >= 0 && ny < map.Height) result[ny * map.Width + x] = true;
				}
			}
		}
		return new InkMap(map.Width, map.Height, result);
	}

	/// <summary>
	/// How alike two pages look, 0..1: F1 of "original ink found near render ink" and "render ink found near original
	/// ink" (pages compared from their top-left corners). Two blank pages are alike; ink against a blank page is not.
	/// </summary>
	public static double Similarity(InkMap original, InkMap rendered, int tolerance = Tolerance)
	{
		var originalInk = original.Count;
		var renderedInk = rendered.Count;
		if (originalInk == 0 && renderedInk == 0) return 1;
		if (originalInk == 0 || renderedInk == 0) return 0;
		var nearOriginal = Dilate(original, tolerance);
		var nearRendered = Dilate(rendered, tolerance);

		var found = 0;
		for (var y = 0; y < original.Height; y++)
			for (var x = 0; x < original.Width; x++)
				if (original[x, y] && nearRendered[x, y]) found++;
		var placed = 0;
		for (var y = 0; y < rendered.Height; y++)
			for (var x = 0; x < rendered.Width; x++)
				if (rendered[x, y] && nearOriginal[x, y]) placed++;

		var recall = (double)found / originalInk;
		var precision = (double)placed / renderedInk;
		return recall + precision == 0 ? 0 : 2 * recall * precision / (recall + precision);
	}

	/// <summary>Both pages on one image, the size of the render: original-only ink red, render-only blue, shared black.</summary>
	public static byte[] OverlayPng(InkMap original, InkMap rendered)
	{
		int width = rendered.Width, height = rendered.Height;
		var rgb = new byte[width * height * 3];
		for (var y = 0; y < height; y++)
		{
			for (var x = 0; x < width; x++)
			{
				var (r, g, b) = (original[x, y], rendered[x, y]) switch
				{
					(true, true) => ((byte)30, (byte)30, (byte)30),
					(true, false) => ((byte)220, (byte)40, (byte)40),
					(false, true) => ((byte)40, (byte)90, (byte)220),
					_ => ((byte)255, (byte)255, (byte)255)
				};
				var i = (y * width + x) * 3;
				rgb[i] = r; rgb[i + 1] = g; rgb[i + 2] = b;
			}
		}
		return Png.Encode(rgb, width, height);
	}
}

/// <summary>A minimal PNG encoder (8-bit RGB, no filtering) for the overlay images.</summary>
public static class Png
{
	private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
	{
		var c = (uint)n;
		for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
		return c;
	}).ToArray();

	public static byte[] Encode(byte[] rgb, int width, int height)
	{
		if (rgb.Length != width * height * 3) throw new ArgumentException("Expected width * height * 3 bytes.", nameof(rgb));
		using var png = new MemoryStream();
		png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

		var header = new byte[13];
		BinaryPrimitives.WriteInt32BigEndian(header, width);
		BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
		header[8] = 8;  // bit depth
		header[9] = 2;  // colour type: RGB
		Chunk(png, "IHDR", header);

		using var raw = new MemoryStream();
		using (var zlib = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
		{
			for (var y = 0; y < height; y++)
			{
				zlib.WriteByte(0);  // filter: none
				zlib.Write(rgb, y * width * 3, width * 3);
			}
		}
		Chunk(png, "IDAT", raw.ToArray());
		Chunk(png, "IEND", []);
		return png.ToArray();
	}

	public static uint Crc(ReadOnlySpan<byte> data)
	{
		var c = 0xFFFFFFFFu;
		foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
		return c ^ 0xFFFFFFFFu;
	}

	private static void Chunk(Stream png, string type, byte[] data)
	{
		Span<byte> four = stackalloc byte[4];
		BinaryPrimitives.WriteInt32BigEndian(four, data.Length);
		png.Write(four);
		var typed = new byte[4 + data.Length];
		System.Text.Encoding.ASCII.GetBytes(type, typed);
		data.CopyTo(typed, 4);
		png.Write(typed);
		BinaryPrimitives.WriteUInt32BigEndian(four, Crc(typed));
		png.Write(four);
	}
}
