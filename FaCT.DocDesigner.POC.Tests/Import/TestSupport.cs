using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using FaCT.DocDesigner.POC.Legacy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;

namespace FaCT.DocDesigner.POC.Tests.Import;

/// <summary>Minimal host environment rooted in a folder (LegacyAssetStore writes to {root}/App_Data/legacy-assets).</summary>
internal sealed class TestEnvironment(string root) : IWebHostEnvironment
{
	public string WebRootPath { get; set; } = Path.Combine(root, "wwwroot");
	public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
	public string ApplicationName { get; set; } = "FaCT.DocDesigner.POC.Tests";
	public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
	public string ContentRootPath { get; set; } = root;
	public string EnvironmentName { get; set; } = "Test";
}

/// <summary>A LegacyAssetStore in a throw-away folder, so tests never touch the designer's App_Data.</summary>
internal sealed class TempAssets : IDisposable
{
	public TempAssets()
	{
		Root = Path.Combine(Path.GetTempPath(), "docdesigner-import-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(Root);
		Store = new LegacyAssetStore(new TestEnvironment(Root));
	}

	public string Root { get; }
	public LegacyAssetStore Store { get; }
	public string AssetFolder => Path.Combine(Root, "App_Data", "legacy-assets");

	public void Dispose()
	{
		try { Directory.Delete(Root, recursive: true); }
		catch (IOException) { /* best effort */ }
		catch (UnauthorizedAccessException) { /* best effort */ }
	}
}

internal static class Html
{
	public static IDocument Parse(string html) => new HtmlParser().ParseDocument("<!DOCTYPE html><html><body>" + html + "</body></html>");

	/// <summary>A 1x1 PNG.</summary>
	public static readonly byte[] Png = Convert.FromBase64String(
		"iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
}
