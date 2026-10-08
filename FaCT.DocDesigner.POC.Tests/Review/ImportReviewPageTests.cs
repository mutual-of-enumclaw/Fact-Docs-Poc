using PuppeteerSharp;

namespace FaCT.DocDesigner.POC.Tests.Review;

/// <summary>review.html?source=import in a browser, against a designer started with the import review in a temp folder.</summary>
[Collection("Designer UI")]
public sealed class ImportReviewPageTests : IAsyncLifetime
{
	private static readonly string[] BrowserPaths =
	[
		@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
		@"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
		@"C:\Program Files\Google\Chrome\Application\chrome.exe"
	];

	private readonly ReviewFolder _folder = new();
	private readonly Uri _uri = new($"http://localhost:{DesignerServer.FreePort()}/");
	private System.Diagnostics.Process? _server;
	private IBrowser? _browser;
	private IPage? _page;

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
		_folder.AddCase("low-case", 0.35, overlays: 2);
		_folder.AddCase("mid-case", 0.80, overlays: 1);
		// Real PNG overlays so the page can show them.
		var png = Import.Png.Encode(Enumerable.Repeat((byte)200, 4 * 4 * 3).ToArray(), 4, 4);
		foreach (var file in Directory.GetFiles(_folder.Cases, "Overlay-p*.png", SearchOption.AllDirectories)) File.WriteAllBytes(file, png);

		_server = DesignerServer.Start(_uri, $"--ImportReview:OutputRoot={_folder.Cases}", $"--ImportReview:DecisionsPath={_folder.Decisions}");
		using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
		{
			await DesignerServer.WaitUntilUpAsync(http, _uri, _server, TimeSpan.FromSeconds(90));
		}
		_browser = await Puppeteer.LaunchAsync(new LaunchOptions
		{
			Headless = true,
			ExecutablePath = BrowserPaths.FirstOrDefault(File.Exists) ?? throw new InvalidOperationException("Edge/Chrome not found."),
			Args = ["--no-first-run", "--disable-extensions"]
		});
		_page = await _browser.NewPageAsync();
		await _page.EvaluateExpressionOnNewDocumentAsync("localStorage.setItem('gdReviewFilter', 'all')");
		// The page's PDF frames keep the network busy, so wait for the document and then for the case list.
		await _page.GoToAsync(new Uri(_uri, "review.html?source=import").ToString(), WaitUntilNavigation.DOMContentLoaded);
		await _page.WaitForFunctionAsync("() => document.querySelectorAll('#caseRows tr').length > 0", new WaitForFunctionOptions { Timeout = 30_000 });
	}

	public async Task DisposeAsync()
	{
		if (_browser is not null)
		{
			await _browser.CloseAsync();
			await _browser.DisposeAsync();
		}
		if (_server is { HasExited: false }) _server.Kill(entireProcessTree: true);
		_server?.Dispose();
		_folder.Dispose();
	}

	[Fact]
	public async Task The_import_review_lists_cases_with_its_own_titles_and_shows_overlays()
	{
		var page = _page!;
		Assert.Equal("PDF / Word Import Review", await page.EvaluateExpressionAsync<string>("document.getElementById('reviewTitle').textContent"));
		Assert.Equal("Original document", await page.EvaluateExpressionAsync<string>("document.getElementById('goldenTitle').textContent"));
		Assert.Equal(["low-case", "mid-case"], await page.EvaluateExpressionAsync<string[]>(
			"[...document.querySelectorAll('#caseRows tr')].map(r => r.cells[0].textContent)"));

		// The first case is selected: both PDFs and its two overlays load.
		await page.WaitForFunctionAsync("() => document.querySelectorAll('#overlays img').length === 2 && " +
			"[...document.querySelectorAll('#overlays img')].every(i => i.complete && i.naturalWidth === 4)",
			new WaitForFunctionOptions { Timeout = 30_000 });
		Assert.False(await page.EvaluateExpressionAsync<bool>("document.getElementById('overlaySection').hidden"));
		Assert.Contains("source=import", await page.EvaluateExpressionAsync<string>("document.getElementById('goldenPdf').src"));
		await page.WaitForFunctionAsync("() => document.getElementById('diff').textContent.includes('DIFFERENT')", new WaitForFunctionOptions { Timeout = 30_000 });
	}

	[Fact]
	public async Task Approving_an_import_case_saves_to_the_import_decisions_file()
	{
		var page = _page!;
		await page.EvaluateExpressionAsync("document.getElementById('reviewer').value = 'UI Test'; document.getElementById('btnApprove').click()");
		// The page moves on to the next case; the approved one's row shows the decision.
		await page.WaitForFunctionAsync("() => [...document.querySelectorAll('#caseRows tr')].some(r => r.cells[0].textContent === 'low-case' && r.textContent.includes('Approved'))",
			new WaitForFunctionOptions { Timeout = 30_000 });

		var decisions = await File.ReadAllTextAsync(_folder.Decisions);
		Assert.Contains("low-case", decisions);
		Assert.Contains("UI Test", decisions);
	}
}
