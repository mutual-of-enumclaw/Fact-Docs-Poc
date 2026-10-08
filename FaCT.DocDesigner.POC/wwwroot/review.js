// Conversion review: the reference PDF beside the HTML render for each case; a person approves or rejects. Decisions
// are pinned to the render (fingerprint); a later, different render shows as "stale".
// ?source=ghostdraft (default): GhostDraft golden cases. ?source=import: PDF / Word imports that scored low.
(() => {
	const PASS = 0.9;
	const $ = id => document.getElementById(id);
	const SOURCES = {
		ghostdraft: { title: 'GhostDraft Conversion Review', golden: 'GhostDraft (golden)', pages: 'GhostDraft / HTML pages' },
		import: { title: 'PDF / Word Import Review', golden: 'Original document', pages: 'Original / HTML pages' }
	};
	const source = SOURCES[new URLSearchParams(location.search).get('source')] ? new URLSearchParams(location.search).get('source') : 'ghostdraft';
	const query = source === 'ghostdraft' ? '' : '?source=' + source;
	document.title = SOURCES[source].title;
	$('reviewTitle').textContent = SOURCES[source].title;
	$('goldenTitle').textContent = SOURCES[source].golden;
	$('pagesHead').title = SOURCES[source].pages;
	let cases = [];
	let visible = [];
	let current = null;

	const reviewer = $('reviewer');
	reviewer.value = localStorage.getItem('gdReviewer') || '';
	reviewer.addEventListener('change', () => localStorage.setItem('gdReviewer', reviewer.value.trim()));
	$('filter').value = localStorage.getItem('gdReviewFilter') || 'todo';
	$('filter').addEventListener('change', () => { localStorage.setItem('gdReviewFilter', $('filter').value); render(); });

	const status = c => !c.decision ? 'todo' : c.stale ? 'stale' : c.decision.status.toLowerCase();
	const statusText = c => ({ todo: 'Needs review', stale: 'Stale', approved: 'Approved', rejected: 'Rejected' })[status(c)];
	const url = (c, artifact) => `/api/review/cases/${encodeURIComponent(c.case)}/${artifact}${query}`;

	function pill(text, cls) {
		const s = document.createElement('span');
		s.className = 'pill ' + cls;
		s.textContent = text;
		return s;
	}

	function scorePill(c) {
		return pill((c.score * 100).toFixed(1) + '%', c.score >= PASS ? 'pass' : 'below');
	}

	function matches(c, f) {
		const s = status(c);
		if (f === 'todo') return s === 'todo' || s === 'stale';
		if (f === 'below') return c.score < PASS;
		if (f === 'all') return true;
		return s === f;
	}

	function render() {
		const f = $('filter').value;
		visible = cases.filter(c => matches(c, f));
		const done = cases.filter(c => ['approved', 'rejected'].includes(status(c))).length;
		const passing = cases.filter(c => status(c) === 'approved' || (status(c) !== 'rejected' && c.score >= PASS)).length;
		$('counts').textContent = `${cases.length} cases · ${passing} passing · ${done} reviewed · ${visible.length} shown`;
		const body = $('caseRows');
		body.replaceChildren();
		for (const c of visible) {
			const tr = document.createElement('tr');
			tr.className = 'row' + (current && current.case === c.case ? ' selected' : '');
			const cells = [c.case, scorePill(c), `${c.goldenPages} / ${c.htmlPages}`, pill(statusText(c), status(c))];
			for (const v of cells) {
				const td = document.createElement('td');
				if (typeof v === 'string') td.textContent = v; else td.appendChild(v);
				tr.appendChild(td);
			}
			tr.addEventListener('click', () => select(c));
			body.appendChild(tr);
		}
	}

	async function select(c) {
		current = c;
		$('empty').hidden = true;
		$('detail').hidden = false;
		$('caseName').textContent = c.case;
		$('caseScore').replaceWith(Object.assign(scorePill(c), { id: 'caseScore' }));
		$('caseStatus').replaceWith(Object.assign(pill(statusText(c), status(c)), { id: 'caseStatus' }));
		$('note').value = c.decision ? c.decision.note : '';
		$('decisionInfo').textContent = c.decision
			? `${c.decision.status} by ${c.decision.reviewer} on ${new Date(c.decision.reviewedUtc).toLocaleString()}` +
				` at ${(c.decision.score * 100).toFixed(1)}%` + (c.stale ? ' — the render has changed since; review again.' : '')
			: '';
		$('goldenPdf').src = c.hasGoldenPdf ? url(c, 'golden.pdf') : 'about:blank';
		$('htmlPdf').src = c.hasHtmlPdf ? url(c, 'html.pdf') : 'about:blank';
		const overlays = $('overlays');
		overlays.replaceChildren();
		$('overlaySection').hidden = !c.overlays;
		for (let n = 1; n <= (c.overlays || 0); n++) {
			const img = document.createElement('img');
			img.src = url(c, `overlay-${n}.png`);
			img.alt = `Page ${n}: original in red, render in blue, both in black`;
			overlays.appendChild(img);
		}
		$('diff').textContent = '';
		render();
		const r = await fetch(url(c, 'diff'));
		if (current === c) $('diff').textContent = r.ok ? await r.text() : '(no diff)';
	}

	async function decide(decision) {
		if (!current) return;
		const c = current;
		const r = decision === null
			? await fetch(`/api/review/cases/${encodeURIComponent(c.case)}${query}`, { method: 'DELETE' })
			: await fetch(`/api/review/cases/${encodeURIComponent(c.case)}${query}`, {
				method: 'PUT',
				headers: { 'Content-Type': 'application/json' },
				body: JSON.stringify({ status: decision, reviewer: reviewer.value.trim(), note: $('note').value })
			});
		if (!r.ok && r.status !== 404) { alert('Saving the decision failed: ' + r.status); return; }
		c.decision = decision === null ? null : await r.json();
		c.stale = false;
		const at = visible.indexOf(c);
		render();
		// move on to the next case still in the list
		const next = visible.find((v, i) => i >= at && v !== c) || visible[visible.length - 1];
		if (next && next !== c) select(next); else select(c);
	}

	$('btnApprove').addEventListener('click', () => decide('Approved'));
	$('btnReject').addEventListener('click', () => {
		if (!$('note').value.trim()) { $('note').focus(); $('note').placeholder = 'Say what is wrong, then Reject'; return; }
		decide('Rejected');
	});
	$('btnClear').addEventListener('click', () => decide(null));

	// Add to Designer Library (GhostDraft cases): the designer opens in a new tab, imports the form's conversion and
	// saves it as a draft template. Hidden for imports and when the ReviewToLibrary feature is off.
	const library = $('btnAddToLibrary');
	if (source === 'ghostdraft') {
		fetch('/api/features').then(r => r.ok ? r.json() : null).then(f => {
			library.hidden = !!(f && f.features && f.features.ReviewToLibrary === false);
		}).catch(() => { library.hidden = false; }).finally(() => { library.dataset.flags = 'applied'; });
	}
	library.addEventListener('click', () => {
		if (!current) return;
		window.open('/?addToLibrary=' + encodeURIComponent(current.case), '_blank', 'noopener');
	});

	document.addEventListener('keydown', e => {
		if (e.target.tagName === 'INPUT' || e.target.tagName === 'SELECT' || e.ctrlKey || e.altKey || e.metaKey) return;
		const at = current ? visible.indexOf(current) : -1;
		if (e.key === 'j' || e.key === 'ArrowDown') { if (visible[at + 1]) select(visible[at + 1]); e.preventDefault(); }
		else if (e.key === 'k' || e.key === 'ArrowUp') { if (at > 0) select(visible[at - 1]); e.preventDefault(); }
		else if (e.key === 'a') $('btnApprove').click();
		else if (e.key === 'r') $('btnReject').click();
	});

	fetch('/api/review/cases' + query).then(r => r.json()).then(list => {
		cases = list;
		render();
		if (visible.length) select(visible[0]);
	});
})();
