using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using PuppeteerSharp;

namespace FaCT.DocDesigner.POC.Tests;

/// <summary>
/// The designer as a user drives it: a headless browser on the designer page imports each gd2designer JSON
/// through "Import GhostDraft Form", and the canvas' getHtml()/getCss() (what Save Draft stores) is rendered
/// by the server's /api/render. The designer is chosen by <see cref="DesignerServer"/>: DESIGNER_URL, else the one at
/// http://localhost:5199 if it is this build, else one started for the tests on a free port.
/// </summary>
public sealed class DesignerFixture : IAsyncLifetime
{
	private static readonly string[] BrowserPaths =
	[
		@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
		@"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
		@"C:\Program Files\Google\Chrome\Application\chrome.exe"
	];

	private static readonly string? ExplicitUrl = Environment.GetEnvironmentVariable("DESIGNER_URL") is { Length: > 0 } url ? url : null;
	private Uri _baseUri = ExplicitUrl is null ? DesignerServer.DefaultUri : new Uri(ExplicitUrl);
	private Process? _server;
	private IBrowser? _browser;
	private IPage? _page;

	public HttpClient Http { get; private set; } = null!;

	/// <summary>The designer the tests use, and whether the tests started it.</summary>
	public Uri BaseUri => _baseUri;
	public bool StartedOwnServer => _server is not null;

	public ConcurrentBag<(string Case, SnapshotComparison Result)> Results { get; } = new();

	public async Task InitializeAsync()
	{
		try
		{
			await StartAsync();
		}
		catch
		{
			// xunit doesn't dispose a fixture whose setup failed: don't leave the server and browser running.
			await DisposeAsync();
			throw;
		}
	}

	private async Task StartAsync()
	{
		using (var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
		{
			var isUp = await DesignerServer.IsUpAsync(probe, _baseUri);
			var running = isUp ? await DesignerServer.RunningBuildAsync(probe, _baseUri) : null;
			switch (DesignerServer.Decide(isUp, running, DesignerServer.ExpectedBuild, ExplicitUrl is not null))
			{
				case DesignerServer.Choice.Stale:
					throw new InvalidOperationException(
						$"The designer at {_baseUri} is a different build ({running?.ToString() ?? "unknown"}) than the one under test " +
						$"({DesignerServer.ExpectedBuild}). Restart it after building, or unset DESIGNER_URL.");
				case DesignerServer.Choice.StartOwn:
					// Nothing running, or an older build (e.g. a dotnet run left open): never test against old code.
					if (isUp) _baseUri = new Uri($"http://localhost:{DesignerServer.FreePort()}/");
					_server = DesignerServer.Start(_baseUri);
					await DesignerServer.WaitUntilUpAsync(probe, _baseUri, _server, TimeSpan.FromSeconds(90));
					break;
			}
		}
		Http = new HttpClient { BaseAddress = _baseUri, Timeout = TimeSpan.FromMinutes(2) };

		_browser = await Puppeteer.LaunchAsync(new LaunchOptions
		{
			Headless = true,
			ExecutablePath = BrowserPaths.FirstOrDefault(File.Exists)
				?? throw new InvalidOperationException("Edge/Chrome not found."),
			Args = ["--no-first-run", "--disable-extensions"]
		});
		_page = await _browser.NewPageAsync();
		// Import may ask to discard unsaved changes from the previous import.
		_page.Dialog += async (_, e) => await e.Dialog.Accept();
		await _page.GoToAsync(_baseUri.ToString(), WaitUntilNavigation.Networkidle0);
		await _page.WaitForFunctionAsync(
			"() => window.grapesjs && grapesjs.editors.length > 0 && document.getElementById('status').textContent.length > 0",
			new WaitForFunctionOptions { Timeout = 60_000 });
	}

	/// <summary>Imports a gd2designer JSON through the designer UI and returns the template it would save.</summary>
	public async Task<(string Html, string Css)> ImportGhostDraftAsync(string jsonPath)
	{
		var page = _page!;
		await page.EvaluateExpressionAsync(
			"document.getElementById('status').textContent = ''; document.getElementById('gdFile').value = '';");
		var input = await page.QuerySelectorAsync("#gdFile");
		await input.UploadFileAsync(jsonPath);
		await page.WaitForFunctionAsync(
			"() => { const s = document.getElementById('status'); return s.textContent.startsWith('Imported') || s.classList.contains('error'); }",
			new WaitForFunctionOptions { Timeout = 60_000 });
		var status = await page.EvaluateExpressionAsync<string>("document.getElementById('status').textContent");
		if (!status.StartsWith("Imported", StringComparison.Ordinal))
		{
			throw new InvalidOperationException("Designer import failed: " + status);
		}

		var html = await page.EvaluateExpressionAsync<string>("grapesjs.editors[0].getHtml()");
		var css = await page.EvaluateExpressionAsync<string>("grapesjs.editors[0].getCss()");
		return (html, css);
	}

	/// <summary>
	/// Imports a PDF or .docx through "Import PDF / Word" and returns the designer's status message. For .docx the
	/// options dialog is answered: <paramref name="placeholders"/> are the kinds to tick (null = leave the defaults).
	/// </summary>
	public async Task<string> ImportDocumentAsync(string path, string[]? placeholders = null, (int From, int To)? pdfPages = null)
	{
		var page = _page!;
		await page.EvaluateExpressionAsync(
			"document.getElementById('status').textContent = ''; document.getElementById('docFile').value = '';");
		var input = await page.QuerySelectorAsync("#docFile");
		await input.UploadFileAsync(path);
		if (pdfPages is { } range)
		{
			await page.WaitForSelectorAsync("#pdfPagesGo", new WaitForSelectorOptions { Timeout = 30_000 });
			await page.EvaluateFunctionAsync("(a, b) => { document.getElementById('pdfFrom').value = a; document.getElementById('pdfTo').value = b; }",
				range.From, range.To);
			// A DOM click: a mouse click can miss while the modal is still animating in.
			await page.EvaluateExpressionAsync("document.getElementById('pdfPagesGo').click()");
		}
		if (path.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
		{
			await page.WaitForSelectorAsync("#docImportGo", new WaitForSelectorOptions { Timeout = 30_000 });
			if (placeholders is not null)
			{
				await page.EvaluateFunctionAsync(
					"picked => document.querySelectorAll('.import-options input').forEach(i => i.checked = picked.includes(i.name))",
					(object)placeholders);
			}
			await page.EvaluateExpressionAsync("document.getElementById('docImportGo').click()");
		}
		await page.WaitForFunctionAsync(
			"() => { const s = document.getElementById('status'); return s.textContent.startsWith('Imported') || s.classList.contains('error'); }",
			new WaitForFunctionOptions { Timeout = 60_000 });
		// Bindings are validated on a short timer after the load.
		await Task.Delay(400);
		return await page.EvaluateExpressionAsync<string>("document.getElementById('status').textContent");
	}

	/// <summary>Chooses a .docx, then closes the import options dialog instead of importing; returns the status.</summary>
	public async Task<string> CancelDocumentImportAsync(string path)
	{
		var page = _page!;
		await page.EvaluateExpressionAsync("document.getElementById('status').textContent = '';");
		var input = await page.QuerySelectorAsync("#docFile");
		await input.UploadFileAsync(path);
		await page.WaitForSelectorAsync("#docImportGo", new WaitForSelectorOptions { Timeout = 30_000 });
		await page.EvaluateExpressionAsync("grapesjs.editors[0].Modal.close()");
		await page.WaitForFunctionAsync("() => document.getElementById('status').textContent === 'Import cancelled.'",
			new WaitForFunctionOptions { Timeout = 30_000 });
		return await page.EvaluateExpressionAsync<string>("document.getElementById('status').textContent");
	}

	public Task<T> EvaluateAsync<T>(string script) => _page!.EvaluateExpressionAsync<T>(script);

	public Task WaitForAsync(string predicate) =>
		_page!.WaitForFunctionAsync(predicate, new WaitForFunctionOptions { Timeout = 30_000 });

	public async Task UploadAsync(string inputSelector, string path)
	{
		var input = await _page!.QuerySelectorAsync(inputSelector);
		await input.UploadFileAsync(path);
	}

	public async Task<byte[]> RenderPdfAsync(string html, string css, JsonObject data)
	{
		using var response = await Http.PostAsJsonAsync("api/render", new JsonObject
		{
			["html"] = html,
			["css"] = css,
			["data"] = data
		});
		if (!response.IsSuccessStatusCode)
		{
			throw new InvalidOperationException($"/api/render returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
		}
		return await response.Content.ReadAsByteArrayAsync();
	}

	public async Task DisposeAsync()
	{
		WriteSummary();
		Http?.Dispose();
		if (_browser is not null)
		{
			await _browser.CloseAsync();
			await _browser.DisposeAsync();
		}
		if (_server is { HasExited: false })
		{
			_server.Kill(entireProcessTree: true);
		}
		_server?.Dispose();
	}

	private void WriteSummary()
	{
		if (Results.IsEmpty) return;
		var ordered = Results.OrderBy(r => r.Result.Score).ThenBy(r => r.Case, StringComparer.Ordinal).ToList();
		var sb = new StringBuilder();
		sb.AppendLine("case,match,score,golden_pages,html_pages,golden_words,html_words,missing,extra");
		foreach (var (name, r) in ordered)
		{
			sb.AppendLine(FormattableString.Invariant(
				$"{name},{r.Matches},{r.Score:F3},{r.GoldenPages},{r.RenderedPages},{r.GoldenWords},{r.RenderedWords},{r.MissingCount},{r.ExtraCount}"));
		}
		Directory.CreateDirectory(GoldenCases.ResultsRoot);
		File.WriteAllText(Path.Combine(GoldenCases.ResultsRoot, "summary.csv"), sb.ToString());
	}
}
