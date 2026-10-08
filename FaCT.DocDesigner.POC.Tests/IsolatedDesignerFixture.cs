using System.Net.Http.Json;
using System.Text.Json;
using FaCT.DocDesigner.POC.Tests.Clauses;
using PuppeteerSharp;

namespace FaCT.DocDesigner.POC.Tests;

/// <summary>
/// A designer started for UI tests on a free port with its template, clause and scenario stores in a temp folder, so
/// tests can save and publish without touching App_Data (or a designer the developer has open).
/// </summary>
public class IsolatedDesignerFixture : IAsyncLifetime
{
	private static readonly string[] BrowserPaths =
	[
		@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
		@"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
		@"C:\Program Files\Google\Chrome\Application\chrome.exe"
	];

	private readonly TempStores _folder = new();
	private System.Diagnostics.Process? _server;
	private IBrowser? _browser;

	public Uri BaseUri { get; } = new($"http://localhost:{DesignerServer.FreePort()}/");
	public HttpClient Http { get; private set; } = null!;
	public IPage Page { get; private set; } = null!;

	/// <summary>Extra configuration for the designer (e.g. security on).</summary>
	protected virtual IEnumerable<string> Settings => [];

	/// <summary>When the page counts as loaded.</summary>
	protected virtual string Ready =>
		"() => window.grapesjs && grapesjs.editors.length > 0 && document.getElementById('status').textContent.length > 0";

	public async Task InitializeAsync()
	{
		try
		{
			_server = DesignerServer.Start(BaseUri, [$"--Templates:Root={_folder.TemplatesRoot}", $"--Clauses:Root={_folder.ClausesRoot}",
				$"--Scenarios:Root={_folder.ScenariosRoot}", $"--Blocks:Root={_folder.BlocksRoot}",
				$"--Comments:Root={_folder.CommentsRoot}", $"--Spelling:Root={_folder.SpellingRoot}", $"--Audit:Root={_folder.AuditRoot}", $"--Themes:Root={_folder.ThemesRoot}", .. Settings]);
			Http = new HttpClient { BaseAddress = BaseUri, Timeout = TimeSpan.FromMinutes(2) };
			await DesignerServer.WaitUntilUpAsync(Http, BaseUri, _server, TimeSpan.FromSeconds(90));
			_browser = await Puppeteer.LaunchAsync(new LaunchOptions
			{
				Headless = true,
				ExecutablePath = BrowserPaths.FirstOrDefault(File.Exists) ?? throw new InvalidOperationException("Edge/Chrome not found."),
				Args = ["--no-first-run", "--disable-extensions"]
			});
			Page = await _browser.NewPageAsync();
			// Publish / discard / delete ask for confirmation.
			Page.Dialog += async (_, e) => await e.Dialog.Accept();
			await Page.GoToAsync(BaseUri.ToString(), WaitUntilNavigation.Networkidle0);
			await Page.WaitForFunctionAsync(Ready, new WaitForFunctionOptions { Timeout = 60_000 });
		}
		catch
		{
			// xunit doesn't dispose a fixture whose setup failed: don't leave the server and browser running.
			await DisposeAsync();
			throw;
		}
	}

	public virtual async Task DisposeAsync()
	{
		// Also runs from a failed InitializeAsync, then again from xunit: everything is released once.
		Http?.Dispose();
		if (_browser is not null)
		{
			await _browser.CloseAsync();
			await _browser.DisposeAsync();
			_browser = null;
		}
		if (_server is not null)
		{
			if (!_server.HasExited) _server.Kill(entireProcessTree: true);
			_server.Dispose();
			_server = null;
		}
		_folder.Dispose();
	}

	/// <summary>Saves and publishes a clause version through the API; returns the version.</summary>
	public async Task<int> PublishClauseAsync(string name, string html, string css = "")
	{
		var version = await SaveClauseAsync(name, html, css);
		(await Http.PostAsync($"api/clauses/{name}/versions/{version}/publish", null)).EnsureSuccessStatusCode();
		return version;
	}

	public async Task<int> SaveClauseAsync(string name, string html, string css = "")
	{
		var response = await Http.PutAsJsonAsync($"api/clauses/{name}/draft", new { project = new { }, html, css });
		response.EnsureSuccessStatusCode();
		return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt32();
	}
}

[CollectionDefinition("Isolated designer")]
public sealed class IsolatedDesignerCollection : ICollectionFixture<IsolatedDesignerFixture>;
