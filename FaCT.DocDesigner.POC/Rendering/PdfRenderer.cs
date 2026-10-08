using System.Text.Json;
using System.Text.RegularExpressions;
using PuppeteerSharp;
using PuppeteerSharp.Media;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Writer;

namespace FaCT.DocDesigner.POC.Rendering;

/// <summary>
/// HTML => PDF using headless Chromium (Edge/Chrome already on the machine, or a downloaded Chrome for Testing).
/// One browser process is shared; each render gets its own page.
/// </summary>
public sealed class PdfRenderer(IConfiguration configuration, ILogger<PdfRenderer> logger) : IAsyncDisposable
{
	private static readonly string[] KnownBrowserPaths =
	[
		@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
		@"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
		@"C:\Program Files\Google\Chrome\Application\chrome.exe"
	];

	private readonly SemaphoreSlim _launchLock = new(1, 1);
	private IBrowser? _browser;

	public async Task<byte[]> RenderAsync(string html)
	{
		var browser = await GetBrowserAsync();
		await using var page = await browser.NewPageAsync();

		// Templates must not reach the network (prevents SSRF from template content). Only inline data is allowed;
		// images such as maps should be fetched server-side and embedded as data: URIs.
		await page.SetRequestInterceptionAsync(true);
		page.Request += async (_, e) =>
		{
			var url = e.Request.Url;
			if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
				url.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
			{
				await e.Request.ContinueAsync();
			}
			else
			{
				logger.LogWarning("Blocked outbound request during PDF render.");
				await e.Request.AbortAsync();
			}
		};

		await page.SetContentAsync(html);

		// Legacy form pages (imported Documaker forms) are full sheets that carry their own footer: no margins, no MOE footer.
		// Imported GhostDraft documents (gd-doc) carry their own running header/footer and @page margins too.
		if (html.Contains("class=\"form-page\"", StringComparison.Ordinal) || html.Contains("gd-doc", StringComparison.Ordinal))
		{
			// GhostDraft footers carrying page numbers move into Chrome's own footer, the only place "Page X of Y"
			// can be filled per flowing page.
			await page.SetViewportAsync(new ViewPortOptions { Width = 816, Height = 1056 });
			var plan = await page.EvaluateFunctionAsync<string?>(PrepareFootersScript);
			var footers = plan is null ? null : JsonSerializer.Deserialize<FooterPlan>(plan, JsonOptions)!;
			Task<byte[]> Print() => footers is null ? PrintAsync(page, null) : PrintWithFootersAsync(page, footers);
			return await PrintWithFlowNumbersAsync(page, Print);
		}

		// A template's own page setup (designer: Page Setup): paper, orientation, margins and header/footer variants.
		var setup = await page.EvaluateFunctionAsync<string?>(PrepareSetupScript);
		if (setup is not null)
		{
			return await PrintWithSetupAsync(page, JsonSerializer.Deserialize<PageSetupPlan>(setup, JsonOptions)!);
		}

		// The standard footer, in the document's language (<html lang>, written by DocumentComposer).
		var language = Templates.DocumentLanguages.Find(HtmlLang().Match(html) is { Success: true } lang ? lang.Groups[1].Value : null)
			?? Templates.DocumentLanguages.English;
		return await page.PdfDataAsync(new PdfOptions
		{
			Format = PaperFormat.Letter,
			PrintBackground = true,
			DisplayHeaderFooter = true,
			HeaderTemplate = "<span></span>",
			FooterTemplate =
				"<div style=\"font-family:'Figtree','Segoe UI',Arial,sans-serif;font-size:10pt;width:100%;box-sizing:border-box;padding:0 0.5in;display:flex;justify-content:space-between;color:#144835;\">" +
				$"<span>Mutual Of Enumclaw</span><span>{language.Page} <span class=\"pageNumber\"></span> {language.Of} <span class=\"totalPages\"></span></span></div>",
			MarginOptions = new MarginOptions { Top = "0.5in", Bottom = "0.6in", Left = "0.5in", Right = "0.5in" }
		});
	}

	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

	private static Regex HtmlLang() => HtmlLangPattern;

	private static readonly Regex HtmlLangPattern = new("^<!DOCTYPE html><html lang=\"([a-z]{2})\"", RegexOptions.Compiled);

	// Page numbers outside the footers Chrome prints (a running header's "(Page X of Y)", DA 01 93) repeat in every
	// printed copy of the header: the document is printed once per page with the numbers spelled out and each page is
	// taken from its own print. One page needs one print.
	private async Task<byte[]> PrintWithFlowNumbersAsync(IPage page, Func<Task<byte[]>> print)
	{
		if (!await page.EvaluateFunctionAsync<bool>(FillFlowNumbersScript, 1, 1))
		{
			return await print();
		}
		var first = await print();
		var total = PageCount(first);
		if (total == 1)
		{
			return first;
		}
		var pages = Enumerable.Range(1, total).ToArray();
		var prints = new List<byte[]>();
		foreach (var n in pages)
		{
			await page.EvaluateFunctionAsync<bool>(FillFlowNumbersScript, n, total);
			prints.Add(await print());
		}
		return ComposePages(prints, pages.ToList(), pages);
	}

	private const string FillFlowNumbersScript = """
		(n, total) => {
			const own = e => !e.closest('.gd-pdffoot, .gd-pdffoot-even, .gd-sheetfoot');
			const numbers = [...document.querySelectorAll('.gd-pageno')].filter(own);
			const counts = [...document.querySelectorAll('.gd-pagecount')].filter(own);
			numbers.forEach(e => e.textContent = String(n));
			counts.forEach(e => e.textContent = String(total));
			return numbers.length + counts.length > 0;
		}
		""";

	// Size: letter|legal|a4. Margins: top, right, bottom, left in inches. Templates: "header:default|first|even" and
	// "footer:..." => Chrome header/footer template.
	private sealed record PageSetupPlan(string Size, string Orientation, double[] Margins, Dictionary<string, string> Templates);

	private const string EmptyTemplate = "<span></span>";

	// Page 1 uses the first-page variant when there is one; even pages the even variant when there is one.
	public static string VariantFor(int pageNumber, bool hasFirst, bool hasEven) =>
		pageNumber == 1 && hasFirst ? "first" : pageNumber % 2 == 0 && hasEven ? "even" : "default";

	private async Task<byte[]> PrintWithSetupAsync(IPage page, PageSetupPlan plan)
	{
		static string Inches(double[] margins, int i) =>
			(i < margins.Length && double.IsFinite(margins[i]) ? Math.Clamp(margins[i], 0, 3) : 0.5).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "in";

		var options = (string variant) => new PdfOptions
		{
			Format = plan.Size switch { "legal" => PaperFormat.Legal, "a4" => PaperFormat.A4, _ => PaperFormat.Letter },
			Landscape = plan.Orientation == "landscape",
			PrintBackground = true,
			DisplayHeaderFooter = true,
			HeaderTemplate = plan.Templates.GetValueOrDefault("header:" + variant) ?? EmptyTemplate,
			FooterTemplate = plan.Templates.GetValueOrDefault("footer:" + variant) ?? EmptyTemplate,
			MarginOptions = new MarginOptions
			{
				Top = Inches(plan.Margins, 0), Right = Inches(plan.Margins, 1), Bottom = Inches(plan.Margins, 2), Left = Inches(plan.Margins, 3)
			}
		};

		var hasFirst = plan.Templates.Keys.Any(k => k.EndsWith(":first", StringComparison.Ordinal));
		var hasEven = plan.Templates.Keys.Any(k => k.EndsWith(":even", StringComparison.Ordinal));
		var first = await page.PdfDataAsync(options("default"));
		if (!hasFirst && !hasEven)
		{
			return first;
		}

		// The variants only change what prints in the margins, so every print has the same pages.
		var keys = Enumerable.Range(1, PageCount(first)).Select(n => VariantFor(n, hasFirst, hasEven)).ToArray();
		var distinct = keys.Distinct().ToList();
		var prints = new List<byte[]>();
		foreach (var variant in distinct)
		{
			prints.Add(variant == "default" ? first : await page.PdfDataAsync(options(variant)));
		}
		return ComposePages(prints, distinct, keys);
	}

	// Reads the page setup written by the designer (div.doc-setup, settings as classes since the sanitizer keeps only
	// those), builds Chrome header/footer templates from its Liquid-rendered slots with inline styles (templates don't
	// see the page's CSS), and removes it from the document. Returns null when the template has no page setup.
	private const string PrepareSetupScript = """
		() => {
			const setup = document.querySelector('.doc-setup');
			if (!setup) return null;
			const classes = [...setup.classList];
			const value = prefix => { const c = classes.find(c => c.startsWith(prefix)); return c ? c.slice(prefix.length) : null; };
			const size = ['letter', 'legal', 'a4'].includes(value('ds-size-')) ? value('ds-size-') : 'letter';
			const orientation = value('ds-orient-') === 'landscape' ? 'landscape' : 'portrait';
			const defaults = [0.5, 0.5, 0.6, 0.5];
			const given = (value('ds-margins-') || '').split('_').map(Number);
			const margins = defaults.map((d, i) => Number.isFinite(given[i]) && given[i] >= 0 && given[i] <= 3 ? given[i] : d);
			const font = Number(value('ds-font-'));
			const pt = Number.isFinite(font) && font >= 6 && font <= 16 ? font : 10;
			const templates = {};
			for (const hf of setup.querySelectorAll('.doc-hf')) {
				const part = hf.classList.contains('doc-hf-header') ? 'header' : hf.classList.contains('doc-hf-footer') ? 'footer' : null;
				const variant = ['first', 'even', 'default'].find(v => hf.classList.contains('doc-hf-' + v));
				if (!part || !variant || templates[part + ':' + variant]) continue;
				const slots = ['left', 'center', 'right'].map(slot => {
					const span = hf.querySelector('.doc-hf-' + slot);
					if (!span) return '';
					const copy = span.cloneNode(true);
					copy.querySelectorAll('.doc-pageno').forEach(e => { e.textContent = ''; e.className = 'pageNumber'; });
					copy.querySelectorAll('.doc-pagecount').forEach(e => { e.textContent = ''; e.className = 'totalPages'; });
					copy.querySelectorAll('img').forEach(e => e.setAttribute('style', 'height:' + (pt * 2) + 'pt;width:auto;vertical-align:middle;'));
					return copy.innerHTML;
				});
				const align = ['left', 'center', 'right'];
				templates[part + ':' + variant] =
					'<div style="font-family:\'Figtree\',\'Segoe UI\',Arial,sans-serif;font-size:' + pt + 'pt;color:#144835;width:100%;' +
					'box-sizing:border-box;padding:0 ' + margins[1] + 'in 0 ' + margins[3] + 'in;display:flex;align-items:center;gap:12pt;">' +
					slots.map((html, i) => '<div style="flex:1 1 0;min-width:0;text-align:' + align[i] + ';">' + html + '</div>').join('') +
					'</div>';
			}
			setup.remove();
			// The brand stylesheet's @page size (Letter) would otherwise keep the pages portrait.
			const page = document.createElement('style');
			page.textContent = '@page { size: ' + size + ' ' + orientation + '; }';
			document.head.appendChild(page);
			return JSON.stringify({ size, orientation, margins, templates });
		}
		""";

	// Templates: "{section}:odd" / "{section}:even" => Chrome footer template. Marks: one per .gd-pgmark in document
	// order, the section of the flow it repeats in, or null for a form sheet's footer.
	private sealed record FooterPlan(Dictionary<string, string> Templates, List<string?> Marks, bool Sheets);

	private static Task<byte[]> PrintAsync(IPage page, string? footerTemplate) =>
		page.PdfDataAsync(new PdfOptions
		{
			Format = PaperFormat.Letter,
			PrintBackground = true,
			DisplayHeaderFooter = footerTemplate is not null,
			HeaderTemplate = "<span></span>",
			FooterTemplate = footerTemplate ?? "<span></span>",
			MarginOptions = new MarginOptions { Top = "0", Bottom = "0", Left = "0", Right = "0" }
		});

	// Chrome prints one footer template on every page, so a document whose sections (or odd/even sides) print
	// different footers is printed once per footer and each page is taken from the print with its own footer. A first
	// print with the page marks spelled out tells which section's flow (or which form sheet) each page holds; the form
	// sheets' in-page footers get their page numbers from it before the final prints.
	private async Task<byte[]> PrintWithFootersAsync(IPage page, FooterPlan plan)
	{
		var sections = plan.Templates.Keys.Select(k => k[..k.LastIndexOf(':')]).Distinct().ToList();
		string? KeyFor(string section, int pageNumber) =>
			pageNumber % 2 == 0 && plan.Templates.ContainsKey(section + ":even") ? section + ":even"
			: plan.Templates.ContainsKey(section + ":odd") ? section + ":odd" : null;

		string?[] keys;
		if (!plan.Sheets && sections.Count == 1)
		{
			if (!plan.Templates.ContainsKey(sections[0] + ":even"))
			{
				return await PrintAsync(page, plan.Templates.GetValueOrDefault(sections[0] + ":odd"));
			}
			var count = PageCount(await PrintAsync(page, null));
			keys = Enumerable.Range(1, count).Select(n => KeyFor(sections[0], n)).ToArray();
		}
		else
		{
			await page.EvaluateExpressionAsync("document.querySelectorAll('.gd-pgmark').forEach((m, i) => m.textContent = 'gdmk' + i + 'x')");
			var marks = ReadMarks(await PrintAsync(page, null));
			var sheetPages = marks
				.Select((mark, index) => (mark, number: index + 1))
				.Where(p => p.mark is int m && m < plan.Marks.Count && plan.Marks[m] is null)
				.Select(p => new[] { p.mark!.Value, p.number })
				.ToArray();
			await page.EvaluateFunctionAsync(FillSheetsScript, sheetPages, marks.Length);
			keys = marks
				.Select((mark, index) => mark is int m && m < plan.Marks.Count && plan.Marks[m] is string section
					? KeyFor(section, index + 1)
					: null)
				.ToArray();
		}

		var distinct = keys.Distinct().ToList();
		var prints = new List<byte[]>();
		foreach (var key in distinct)
		{
			prints.Add(await PrintAsync(page, key is null ? null : plan.Templates[key]));
		}
		return ComposePages(prints, distinct, keys);
	}

	// Builds the final PDF taking page i from the print made for keys[i] (prints[distinct.IndexOf(keys[i])]).
	private byte[] ComposePages<TKey>(List<byte[]> prints, List<TKey> distinct, TKey[] keys)
	{
		if (prints.Count == 1)
		{
			return prints[0];
		}

		var documents = prints.Select(p => PdfDocument.Open(p)).ToList();
		try
		{
			var builder = new PdfDocumentBuilder();
			for (var i = 0; i < keys.Length; i++)
			{
				var source = documents[distinct.IndexOf(keys[i])];
				if (i < source.NumberOfPages)
				{
					builder.AddPage(source, i + 1);
				}
				else
				{
					logger.LogWarning("Footer print has {Count} pages, expected {Expected}.", source.NumberOfPages, keys.Length);
				}
			}
			return builder.Build();
		}
		finally
		{
			documents.ForEach(d => d.Dispose());
		}
	}

	private static int PageCount(byte[] pdf)
	{
		using var document = PdfDocument.Open(pdf);
		return document.NumberOfPages;
	}

	// The page mark ("gdmk{n}x", 2pt white text) printed on each page, in content-stream order.
	private static int?[] ReadMarks(byte[] pdf)
	{
		using var document = PdfDocument.Open(pdf);
		return document.GetPages()
			.Select(p => Regex.Match(string.Concat(p.Letters.Select(l => l.Value)), @"gdmk(\d+)x"))
			.Select(m => m.Success ? int.Parse(m.Groups[1].Value) : (int?)null)
			.ToArray();
	}

	// Clears the page marks, then numbers each printed form sheet's footer and shows its even-side footer on even pages.
	private const string FillSheetsScript = """
		(pages, total) => {
			const marks = [...document.querySelectorAll('.gd-pgmark')];
			marks.forEach(m => m.textContent = '');
			for (const [k, n] of pages) {
				const sheet = marks[k] && marks[k].closest('.form-page');
				if (!sheet) continue;
				const showEven = n % 2 === 0 && !!sheet.querySelector('.gd-sheetfoot.gd-side-even');
				sheet.querySelectorAll('.gd-sheetfoot').forEach(f => {
					f.style.display = f.classList.contains('gd-side-even') === showEven ? 'block' : 'none';
					f.querySelectorAll('.gd-pageno').forEach(e => e.textContent = String(n));
					f.querySelectorAll('.gd-pagecount').forEach(e => e.textContent = String(total));
				});
			}
		}
		""";

	// Copies each section's marked footers (.gd-pdffoot odd side, .gd-pdffoot-even even side) with their computed
	// styles inlined into footer templates, turns the page-number spans into Chrome's pageNumber/totalPages
	// placeholders, then drops the in-page flow footers and widens the bottom page margin so the templates have room.
	// Returns null when there are neither such footers nor form-sheet footers.
	private const string PrepareFootersScript = """
		() => {
			const feet = [...document.querySelectorAll('.gd-pdffoot, .gd-pdffoot-even')];
			const sheets = document.querySelector('.gd-sheetfoot') !== null;
			if (!feet.length && !sheets) return null;
			const secOf = el => [...el.classList].find(c => c.startsWith('gd-sec-')) || 'gd-sec';
			const templates = {};
			let mb = 0;
			for (const src of feet) {
				const key = secOf(src) + (src.classList.contains('gd-pdffoot-even') ? ':even' : ':odd');
				if (templates[key]) continue;
				const odd = src.classList.contains('gd-pdffoot') ? src : (src.parentElement.querySelector('.gd-pdffoot') || src);
				const geo = k => { const c = [...odd.classList].find(c => c.startsWith('gd-' + k + '-')); return c ? parseFloat(c.slice(k.length + 4)) : NaN; };
				const clone = src.cloneNode(true);
				const from = [src, ...src.querySelectorAll('*')];
				const to = [clone, ...clone.querySelectorAll('*')];
				const even = src.classList.contains('gd-pdffoot-even');
				from.forEach((el, i) => {
					const cs = getComputedStyle(el);
					let s = '';
					for (let j = 0; j < cs.length; j++) s += cs[j] + ':' + cs.getPropertyValue(cs[j]) + ';';
					to[i].setAttribute('style', s);
					// the even side is laid out hidden: every descendant inherited that
					if (even) to[i].style.visibility = 'visible';
					// an empty paragraph keeps its line through a ::before space, which inline styles can't carry
					const before = getComputedStyle(el, '::before').content;
					if (!el.childNodes.length && before && before !== 'none' && before !== 'normal') to[i].textContent = '\u00a0';
				});
				clone.querySelectorAll('.gd-pageno').forEach(e => { e.textContent = ''; e.classList.add('pageNumber'); });
				clone.querySelectorAll('.gd-pagecount').forEach(e => { e.textContent = ''; e.classList.add('totalPages'); });
				clone.style.visibility = 'visible';
				clone.style.position = 'static';
				const h = src.getBoundingClientRect().height * 0.75;
				const fy = geo('fy') || 36;
				mb = Math.max(mb, geo('mb') || 72, fy + h);
				templates[key] = '<div style="position:absolute;left:0;right:0;bottom:' + fy + 'pt;padding:0 ' +
					(geo('mr') || 0) + 'pt 0 ' + (geo('ml') || 0) + 'pt;box-sizing:border-box;">' + clone.outerHTML + '</div>';
			}
			if (feet.length) {
				const st = document.createElement('style');
				st.textContent = '@page gdrun{margin-bottom:' + mb + 'pt;}' +
					'table.gd-runt>tfoot{display:none!important;}.gd-foot-fixed{display:none!important;}';
				document.head.appendChild(st);
			}
			const marks = [...document.querySelectorAll('.gd-pgmark')].map(m => m.closest('.form-page') ? null : secOf(m));
			return JSON.stringify({ templates, marks, sheets });
		}
		""";

	private async Task<IBrowser> GetBrowserAsync()
	{
		await _launchLock.WaitAsync();
		try
		{
			if (_browser is { IsConnected: true })
			{
				return _browser;
			}

			var executablePath = ResolveExecutablePath();
			if (executablePath is null)
			{
				logger.LogInformation("No local Edge/Chrome found; downloading Chrome for Testing.");
				var installed = await new BrowserFetcher().DownloadAsync();
				executablePath = installed.GetExecutablePath();
			}

			logger.LogInformation("Launching headless browser: {Path}", executablePath);
			_browser = await Puppeteer.LaunchAsync(new LaunchOptions
			{
				Headless = true,
				ExecutablePath = executablePath,
				Args = ["--no-first-run", "--disable-extensions"]
			});
			return _browser;
		}
		finally
		{
			_launchLock.Release();
		}
	}

	private string? ResolveExecutablePath()
	{
		var configured = configuration["Chromium:ExecutablePath"];
		if (!string.IsNullOrWhiteSpace(configured))
		{
			return configured;
		}

		return KnownBrowserPaths.FirstOrDefault(File.Exists);
	}

	public async ValueTask DisposeAsync()
	{
		if (_browser is not null)
		{
			await _browser.CloseAsync();
			await _browser.DisposeAsync();
		}
		_launchLock.Dispose();
	}
}
