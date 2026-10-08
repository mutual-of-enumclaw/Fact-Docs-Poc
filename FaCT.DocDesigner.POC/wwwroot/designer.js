/* MOE Document Designer POC: GrapesJS (free, BSD-3) + custom document component types that export Liquid. */
(async function () {
	'use strict';

	// Embedded in another app (/?embed=1, e.g. Commercial Web): no title bar; the host app drives the designer with window
	// messages (see "Embedded mode" near the end). embedHost = the parent page's origin once it is known to be allowed.
	const EMBEDDED = new URLSearchParams(location.search).get('embed') === '1';
	if (EMBEDDED) document.body.classList.add('embedded');
	let embedHost = null;
	let hostStateTimer = 0;
	// A record the host app loaded (e.g. a real policy): shown on the canvas and used by Preview PDF instead of the test data.
	let externalRecord = null;

	const statusEl = document.getElementById('status');
	const nameInput = document.getElementById('templateName');
	const versionSelect = document.getElementById('versionSelect');
	const badge = document.getElementById('versionBadge');
	const docKind = document.getElementById('docKind');
	const languageSelect = document.getElementById('docLanguage');
	const NAME_PATTERN = /^[A-Za-z0-9_-]{1,64}$/;

	// ---- Document languages: English plus translations that use the same data fields (server: DocumentLanguages). -------
	const LANGUAGES = {
		'': { name: 'English', culture: 'en-US', yes: 'Yes', no: 'No' },
		es: { name: 'Spanish', culture: 'es-US', yes: 'S\u00ed', no: 'No' },
		fr: { name: 'French (Canada)', culture: 'fr-CA', yes: 'Oui', no: 'Non' }
	};
	// The language of the document on the canvas ('' = English).
	function docLang() {
		return languageSelect && LANGUAGES[languageSelect.value] ? languageSelect.value : '';
	}
	function langInfo() {
		return LANGUAGES[docLang()];
	}
	// "?lang=es" / "&lang=es" for the API calls about the open document's versions; nothing for English.
	function langQuery(more) {
		return docLang() ? (more ? '&' : '?') + 'lang=' + docLang() : '';
	}

	// ---- Who is using the designer. With security on, nothing else loads until someone signs in. --------------------
	const me = await signedInUser();

	// ---- Feature flags (server: Features / FeatureProfiles, e.g. the "demo" profile). Controls of features that are
	// off are hidden (class feature-off); their blocks are removed once the editor is up. -----------------------------
	const features = await loadFeatures();
	async function loadFeatures() {
		try {
			const response = await fetch('/api/features');
			if (response.ok) return (await response.json()).features || {};
		} catch (e) { /* no flags: everything on */ }
		return {};
	}
	function featureOn(name) {
		return features[name] !== false;
	}
	document.querySelectorAll('[data-feature]').forEach(function (el) {
		if (!featureOn(el.getAttribute('data-feature'))) el.classList.add('feature-off');
	});
	const FEATURE_BLOCKS = {
		Clauses: ['clause'],
		CalculatedFields: ['calc-field'],
		Visuals: ['barcode', 'chart', 'signature'],
		MaterialBlocks: ['md-icon', 'md-icon-text', 'md-banner', 'md-card-elevated', 'md-card-outlined', 'md-card-filled']
	};

	async function signedInUser() {
		const response = await fetch('/api/me');
		if (response.ok) return response.json();
		const users = await (await fetch('/api/users')).json();
		showSignIn(users);
		// Stops here: the page reloads after signing in.
		return new Promise(function () { });
	}

	function showSignIn(users) {
		const overlay = document.createElement('div');
		overlay.id = 'signin';
		overlay.className = 'signin-overlay';
		const card = document.createElement('div');
		card.className = 'signin-card';
		const title = document.createElement('h2');
		title.textContent = 'Sign in to the Document Designer';
		const help = document.createElement('p');
		help.textContent = 'Choose who you are. (Proof of concept: production signs in with your Microsoft account.)';
		const error = document.createElement('div');
		error.className = 'model-error';
		card.appendChild(title);
		card.appendChild(help);
		users.forEach(function (user) {
			const button = document.createElement('button');
			button.type = 'button';
			button.className = 'signin-user';
			button.setAttribute('data-user', user.id);
			const name = document.createElement('strong');
			name.textContent = user.name;
			const roles = document.createElement('small');
			roles.textContent = user.roles.length ? user.roles.join(', ') : 'View only';
			button.appendChild(name);
			button.appendChild(roles);
			button.addEventListener('click', async function () {
				const response = await fetch('/api/signin', {
					method: 'POST',
					headers: { 'Content-Type': 'application/json' },
					body: JSON.stringify({ id: user.id })
				});
				if (response.ok) location.reload();
				else error.textContent = ((await response.json().catch(function () { return {}; })).error) || 'Sign-in failed.';
			});
			card.appendChild(button);
		});
		if (!users.length) help.textContent = 'No users are set up. Add them under Security:Users in appsettings.';
		card.appendChild(error);
		overlay.appendChild(card);
		document.body.appendChild(overlay);
	}

	function hasRole(role) {
		return role === 'any' ? me.roles.length > 0 : me.roles.indexOf(role) >= 0;
	}

	// The signed-in person, and what their roles let them do (the server checks again on every change).
	(function applyRoles() {
		const chip = document.getElementById('userChip');
		if (me.securityEnabled) {
			chip.hidden = false;
			chip.textContent = me.name + (me.roles.length ? ' \u00b7 ' + me.roles.join(', ') : ' \u00b7 View only');
			document.getElementById('btnSignOut').hidden = false;
		}
		document.querySelectorAll('[data-requires]').forEach(function (el) {
			const role = el.getAttribute('data-requires');
			if (hasRole(role)) return;
			el.disabled = true;
			el.setAttribute('data-role-locked', 'true');
			el.title = (el.title ? el.title + ' \u2014 ' : '') + (role === 'any' ? 'needs the Author, Reviewer or Publisher role' : 'needs the ' + role + ' role');
		});
	})();
	document.getElementById('btnSignOut').addEventListener('click', async function () {
		await fetch('/api/signout', { method: 'POST' });
		location.reload();
	});

	// ---- Shared clauses (Kind = Clause): saved/published like templates, included by templates with the Clause block.
	let clauseList = [];        // [{ name, latestVersion, latestStatus, publishedVersion }]
	let clauseCache = {};       // 'name@version' -> Promise<{ html, css, version } | { error }>

	async function refreshClauseList() {
		try {
			const response = await fetch('/api/clauses');
			clauseList = response.ok ? await response.json() : [];
		} catch (e) {
			clauseList = [];
		}
	}

	function clauseContent(name, version) {
		// A translated template shows (and prints) the clause's translation when it has one.
		const key = name + '@' + (version || '') + '/' + docLang();
		if (!clauseCache[key]) {
			clauseCache[key] = fetch('/api/clauses/' + encodeURIComponent(name) + '/content' + (version ? '?version=' + version : '') + langQuery(!!version))
				.then(function (r) {
					return r.json().catch(function () { return {}; }).then(function (j) { return r.ok ? j : { error: j.error || 'Clause could not be loaded (' + r.status + ').' }; });
				})
				.catch(function () { return { error: 'Clause could not be loaded.' }; });
		}
		return clauseCache[key];
	}

	function fillClausePicker(select, value) {
		select.innerHTML = '';
		const names = clauseList.filter(function (c) { return c.publishedVersion; }).map(function (c) { return c.name; });
		if (value && names.indexOf(value) < 0) names.unshift(value);
		if (!names.length) {
			const none = document.createElement('option');
			none.value = '';
			none.textContent = '(no published clauses)';
			select.appendChild(none);
		}
		names.forEach(function (name) {
			const option = document.createElement('option');
			option.value = name;
			option.textContent = name;
			select.appendChild(option);
		});
		select.value = value || '';
	}

	// After a clause is published the canvas previews must show the new wording.
	async function repaintClauses() {
		clauseCache = {};
		await refreshClauseList();
		const wrapper = editor.getWrapper();
		if (wrapper) wrapper.findType('clause').forEach(function (c) { if (c.view) c.view.paintClause(); });
	}
	await refreshClauseList();

	// ---- Calculated fields: expressions are compiled to Liquid by the server (ExpressionCompiler) and previewed with
	// the selected test data through the document engine, so the canvas shows what the PDF prints.
	const compileCache = {};

	function compileExpression(expression, lists) {
		const key = JSON.stringify([expression, lists]);
		if (!compileCache[key]) {
			compileCache[key] = fetch('/api/expressions/compile', {
				method: 'POST',
				headers: { 'Content-Type': 'application/json' },
				body: JSON.stringify({ expression: expression, lists: lists })
			}).then(function (r) { return r.json(); }).catch(function () { return { error: 'The calculation could not be checked.' }; });
		}
		return compileCache[key];
	}

	function previewLiquid(liquid, data) {
		return fetch('/api/expressions/preview', {
			method: 'POST',
			headers: { 'Content-Type': 'application/json' },
			body: JSON.stringify({ liquid: liquid, data: data })
		}).then(function (r) { return r.json(); }).catch(function () { return { error: 'The preview could not be loaded.' }; });
	}

	// Material snackbar: hides itself after a few seconds (errors stay longer).
	let statusTimer = 0;
	function setStatus(message, isError) {
		statusEl.textContent = message;
		statusEl.className = (message ? 'show' : '') + (isError ? ' error' : '');
		clearTimeout(statusTimer);
		if (message) {
			statusTimer = setTimeout(function () { statusEl.classList.remove('show'); }, isError ? 10000 : 5000);
		}
		if (message) postToHost('status', { message: message, error: !!isError });
	}

	// ---------------------------------------------------------------------------------------------
	// Schema: derived from the sample message payload. Arrays become Repeat / Data Table collections;
	// the item alias is the singular of the array name (locations -> location, tivs -> tiv).
	// ---------------------------------------------------------------------------------------------
	const ISO_DATE = /^\d{4}-\d{2}-\d{2}/;

	function singular(name) {
		return name.endsWith('s') ? name.slice(0, -1) : name + 'Item';
	}

	function kindOf(value) {
		if (typeof value === 'number') return 'number';
		if (typeof value === 'string' && /^(data:image\/|docimage:)/.test(value)) return 'image';
		if (typeof value === 'string' && ISO_DATE.test(value)) return 'date';
		return 'text';
	}

	// Property names become Liquid paths, so only plain identifiers are usable.
	const KEY_PATTERN = /^[A-Za-z_][A-Za-z0-9_]*$/;

	function isObject(value) {
		return value !== null && typeof value === 'object' && !Array.isArray(value);
	}

	// Representative item for an array: object items are merged so every property seen in any item is offered.
	function sampleItem(array) {
		if (!array.some(isObject)) return array.length ? array[0] : '';
		const merged = {};
		array.filter(isObject).forEach(function (item) {
			Object.keys(item).forEach(function (key) {
				if (!(key in merged) || merged[key] === null) merged[key] = item[key];
			});
		});
		return merged;
	}

	// Model tree: { name, kind: object|array|value, type, path, alias?, children?, unusable? }.
	// Values inside an array are addressed through the loop alias (claims[] -> claim.totalLoss).
	function buildTree(obj, prefix) {
		return Object.keys(obj).map(function (key) {
			const value = obj[key];
			const path = prefix ? prefix + '.' + key : key;
			if (!KEY_PATTERN.test(key)) return { name: key, kind: 'value', type: 'text', path: path, unusable: true };
			if (Array.isArray(value)) {
				const alias = singular(key);
				const item = sampleItem(value);
				const children = isObject(item)
					? buildTree(item, alias)
					: [{ name: alias, kind: 'value', type: kindOf(item), path: alias }];
				return { name: key, kind: 'array', type: 'list', path: path, alias: alias, children: children };
			}
			if (isObject(value)) return { name: key, kind: 'object', type: 'group', path: path, children: buildTree(value, path) };
			return { name: key, kind: 'value', type: kindOf(value), path: path };
		});
	}

	function buildSchema(data) {
		const fields = [];
		const kinds = {};
		const collections = [];
		(function walk(nodes) {
			nodes.forEach(function (node) {
				if (node.unusable) return;
				if (node.kind === 'value') {
					fields.push(node.path);
					kinds[node.path] = node.type;
				} else {
					const before = fields.length;
					walk(node.children);
					if (node.kind === 'array') {
						collections.push({ path: node.path, alias: node.alias, fields: fields.slice(before).filter(function (f) {
							return f === node.alias || f.indexOf(node.alias + '.') === 0;
						}) });
					}
				}
			});
		})(buildTree(data, ''));
		return { fields: fields, kinds: kinds, collections: collections };
	}

	// Import accepts either an example payload or a JSON Schema (e.g. generated from the C# message type).
	function looksLikeJsonSchema(json) {
		return isObject(json) && (typeof json.$schema === 'string' || (json.type === 'object' && isObject(json.properties)));
	}

	function sampleFromSchema(node, root, depth) {
		if (!isObject(node) || depth > 12) return null;
		if (typeof node.$ref === 'string') {
			const target = node.$ref.replace(/^#\//, '').split('/').reduce(function (acc, part) { return acc && acc[part]; }, root);
			return sampleFromSchema(target, root, depth + 1);
		}
		const example = node.example !== undefined ? node.example
			: Array.isArray(node.examples) && node.examples.length ? node.examples[0] : undefined;
		const type = Array.isArray(node.type) ? node.type.filter(function (t) { return t !== 'null'; })[0] : node.type;
		if (isObject(node.properties)) {
			// Every property, with the example's values over them: a master schema's example shows one case (e.g. a
			// commercial auto quote) and would otherwise hide every other section.
			const result = {};
			Object.keys(node.properties).forEach(function (key) {
				result[key] = sampleFromSchema(node.properties[key], root, depth + 1);
			});
			return isObject(example) ? withExample(result, example) : result;
		}
		if (example !== undefined) return example;
		const variants = node.allOf || node.oneOf || node.anyOf;
		if (Array.isArray(variants) && variants.length) return sampleFromSchema(variants[0], root, depth + 1);
		if (type === 'object') return {};
		if (type === 'array') return [sampleFromSchema(node.items, root, depth + 1)];
		if (Array.isArray(node.enum) && node.enum.length) return node.enum[0];
		if (type === 'number' || type === 'integer') return 0;
		if (type === 'boolean') return false;
		if (node.format === 'date' || node.format === 'date-time') return '2026-01-01';
		return 'text';
	}

	function withExample(generated, example) {
		Object.keys(example).forEach(function (key) {
			generated[key] = isObject(generated[key]) && isObject(example[key]) ? withExample(generated[key], example[key]) : example[key];
		});
		return generated;
	}

	// Sensible defaults when a field is dropped into a table column.
	function defaultFormat(schema, path) {
		const kind = schema.kinds[path];
		if (kind === 'date') return 'shortdate';
		if (kind === 'number' && /tiv|total|amount|premium|limit|insuredValue/i.test(path)) return 'currency';
		if (kind === 'number' && /ratio|coverage|deterioration|illumination|vegetation/i.test(path)) return 'percent';
		if (kind === 'number' && /area|footprint|distance/i.test(path)) return 'number';
		return '';
	}

	// claim.totalLoss -> "Total Loss"
	function labelFor(path) {
		const last = path.split('.').pop();
		return last.replace(/([a-z])([A-Z])/g, '$1 $2').replace(/^./, function (c) { return c.toUpperCase(); });
	}

	const FORMATS = [
		{ id: '', label: '(none)' },
		{ id: 'currency', label: 'Currency' },
		{ id: 'dollars', label: 'Whole dollars ($1,234)' },
		{ id: 'percent', label: 'Percent' },
		{ id: 'number', label: 'Number (1,234)' },
		{ id: 'decimal', label: 'Decimal (1,234.00)' },
		{ id: 'shortdate', label: 'Date (MM/dd/yyyy)' },
		{ id: 'upcase', label: 'UPPERCASE' }
	];

	// Show If conditions. "blank" is Liquid's built-in test for nil, empty text and empty lists.
	const CONDITIONS = [
		{ id: 'present', label: 'has a value / is not empty' },
		{ id: 'blank', label: 'is empty' },
		{ id: 'eq', label: 'equals' },
		{ id: 'ne', label: 'does not equal' },
		{ id: 'gt', label: 'is greater than' },
		{ id: 'lt', label: 'is less than' },
		{ id: 'contains', label: 'contains' }
	];
	const CONDITION_OPERATORS = { eq: '==', ne: '!=', gt: '>', lt: '<', contains: 'contains' };

	// How bound fields look on the canvas (never exported): the sample value as the PDF prints it, the field's name,
	// or the raw Liquid. Long Liquid paths don't fit the cells and boxes of converted forms.
	const FIELD_MODES = ['sample', 'name', 'liquid'];
	const FIELD_NAME_MAX = 18;
	const fieldDisplay = { mode: 'sample' };
	try {
		const saved = localStorage.getItem('designer.fieldMode');
		if (FIELD_MODES.indexOf(saved) >= 0) fieldDisplay.mode = saved;
		if (fieldDisplay.mode === 'liquid' && !featureOn('LiquidView')) fieldDisplay.mode = 'sample';
	} catch (e) { /* storage unavailable: keep the default */ }

	function liquidExpression(path, format, ifEmpty) {
		const fallback = fallbackText(ifEmpty);
		return '{{ ' + path + formatFilter(format) + (fallback ? ' | default: "' + fallback + '"' : '') + ' }}';
	}

	// Check boxes: {{ path | checkmark }} (X when the value means yes) or {{ path | checkmark: "value" }} (X when it is that
	// value). Same rules as the server's CheckMarks.
	function cleanCheckValue(when) {
		return String(when || '').replace(/["{}%]/g, '').trim().slice(0, 100);
	}
	function checkExpression(path, when) {
		const value = cleanCheckValue(when);
		return '{{ ' + path + ' | checkmark' + (value ? ': "' + value + '"' : '') + ' }}';
	}
	const CHECK_YES = ['true', 't', 'yes', 'y', 'x', 'on', 'checked', '1', 's\u00ed', 'si', 'oui'];
	function isChecked(value, when) {
		const target = cleanCheckValue(when).toLowerCase();
		if (value === undefined || value === null) return false;
		if (Array.isArray(value)) return value.some(function (v) { return target ? matchesCheck(v, target) : isChecked(v, ''); });
		if (!target) {
			if (typeof value === 'boolean') return value;
			if (typeof value === 'number') return value !== 0;
			return typeof value === 'string' && CHECK_YES.indexOf(value.trim().toLowerCase()) >= 0;
		}
		return matchesCheck(value, target);
	}
	function matchesCheck(value, target) {
		if (value === undefined || value === null || typeof value === 'object') return false;
		if (typeof value === 'boolean') return (target === 'true' || target === 'false') ? String(value) === target : (value ? 'yes' : 'no') === target;
		if (typeof value === 'number' && target !== '' && !isNaN(Number(target))) return value === Number(target);
		return String(value).trim().toLowerCase() === target;
	}

	// ---- Custom formats: the format prop is a preset id (currency, shortdate...) or one of
	// 'num:<culture>|<pattern>', 'date:<culture>|<pattern>' (a .NET format, the same in the PDF and in Word) or
	// 'mask:<pattern>' (# a digit, * a hidden digit).
	const FORMAT_CULTURES = [
		{ id: 'en-US', label: 'English (US)' },
		{ id: 'es-US', label: 'Spanish (US)' },
		{ id: 'es-MX', label: 'Spanish (Mexico)' },
		{ id: 'en-CA', label: 'English (Canada)' },
		{ id: 'fr-CA', label: 'French (Canada)' }
	];

	function cleanPattern(pattern) {
		return String(pattern || '').replace(/["{}]/g, '').slice(0, 100);
	}

	function parseCustomFormat(format) {
		const f = String(format || '');
		let m = /^(num|date):([A-Za-z]{2}-[A-Za-z]{2})\|([\s\S]+)$/.exec(f);
		if (m && FORMAT_CULTURES.some(function (c) { return c.id === m[2]; }) && cleanPattern(m[3])) {
			return { kind: m[1], culture: m[2], pattern: cleanPattern(m[3]) };
		}
		m = /^mask:([\s\S]+)$/.exec(f);
		if (m && cleanPattern(m[1]).slice(0, 40)) return { kind: 'mask', culture: 'en-US', pattern: cleanPattern(m[1]).slice(0, 40) };
		return null;
	}

	function encodeCustomFormat(custom) {
		return custom.kind === 'mask' ? 'mask:' + custom.pattern : custom.kind + ':' + custom.culture + '|' + custom.pattern;
	}

	// The Liquid filter(s) for a format prop.
	function formatFilter(format) {
		if (!format) return '';
		const custom = parseCustomFormat(format);
		if (!custom) return FORMATS.some(function (f) { return f.id === format; }) ? ' | ' + format : '';
		if (custom.kind === 'mask') return ' | mask: "' + custom.pattern + '"';
		return ' | format: "' + custom.pattern + '"' + (custom.culture !== 'en-US' ? ', "' + custom.culture + '"' : '');
	}

	function describeCustomFormat(custom) {
		const culture = custom.culture !== 'en-US' ? ' (' + custom.culture + ')' : '';
		return (custom.kind === 'mask' ? 'Mask ' : custom.kind === 'date' ? 'Date ' : 'Number ') + custom.pattern + culture;
	}

	// Canvas samples for custom formats come from the server (the same code as the PDF), cached; a field shows the plain
	// value until its formatted sample arrives.
	const customSamples = new Map();
	let customSampleQueue = [];
	let customSampleTimer = null;
	function customSample(value, custom) {
		const key = encodeCustomFormat(custom) + '\u0000' + docLang() + '\u0000' + JSON.stringify(value);
		if (customSamples.has(key)) return customSamples.get(key);
		if (!customSampleQueue.some(function (q) { return q.key === key; })) customSampleQueue.push({ key: key, value: value, custom: custom });
		if (!customSampleTimer) customSampleTimer = setTimeout(fetchCustomSamples, 30);
		return String(value);
	}
	async function fetchCustomSamples() {
		const batch = customSampleQueue.slice(0, 200);
		customSampleQueue = customSampleQueue.slice(200);
		customSampleTimer = customSampleQueue.length ? setTimeout(fetchCustomSamples, 30) : null;
		try {
			const results = await previewFormats(batch.map(function (q) { return { value: q.value, custom: q.custom }; }));
			batch.forEach(function (q, i) { customSamples.set(q.key, results[i].text != null ? results[i].text : String(q.value)); });
		} catch (e) {
			batch.forEach(function (q) { customSamples.set(q.key, String(q.value)); });
		}
		paintFields();
	}

	// [{ value, custom }] => [{ text, error }] from the server.
	async function previewFormats(items) {
		const response = await fetch('/api/formats/preview', {
			method: 'POST',
			headers: { 'Content-Type': 'application/json' },
			body: JSON.stringify({
				items: items.map(function (i) {
					// A format without a language (en-US) prints in the document's language.
					return i.custom.kind === 'mask'
						? { value: i.value, mask: i.custom.pattern }
						: { value: i.value, format: i.custom.pattern, culture: i.custom.culture === 'en-US' ? langInfo().culture : i.custom.culture };
				})
			})
		});
		if (!response.ok) throw new Error('Format preview failed (' + response.status + ').');
		return response.json();
	}

	// "If empty, show" text as a Liquid string literal: quotes, braces and markup characters are dropped.
	function fallbackText(text) {
		return String(text || '').replace(/["'{}%<>&\\]/g, '').replace(/\s+/g, ' ').trim();
	}

	function shortDate(raw) {
		let m = /^(\d{4})-(\d{2})-(\d{2})/.exec(raw);
		if (m) return m[2] + '/' + m[3] + '/' + m[1];
		m = /^(\d{1,2})\/(\d{1,2})\/(\d{4})/.exec(raw);
		if (m) return ('0' + m[1]).slice(-2) + '/' + ('0' + m[2]).slice(-2) + '/' + m[3];
		return raw;
	}

	// A sample value formatted like the server's Liquid filters (DocumentComposer, en-US).
	function formatSample(value, format) {
		if (value === undefined || value === null || typeof value === 'object') return '';
		// true/false print as Yes/No (as in the PDF and in DocGen's Word templates), in the document's language.
		if (typeof value === 'boolean') value = value ? langInfo().yes : langInfo().no;
		const custom = parseCustomFormat(format);
		if (custom) return customSample(value, custom);
		const n = Number(value);
		function fixed(digits) {
			return Math.abs(n).toLocaleString('en-US', { minimumFractionDigits: digits, maximumFractionDigits: digits });
		}
		if (['currency', 'dollars', 'percent', 'number', 'decimal'].indexOf(format) >= 0 && (value === '' || isNaN(n))) return String(value);
		switch (format) {
			case 'currency': return (n < 0 ? '-$' : '$') + fixed(2);
			case 'dollars': return (n < 0 ? '-$' : '$') + fixed(0);
			case 'percent': return (n < 0 ? '-' : '') + (Math.abs(n) * 100).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 }) + '%';
			case 'number': return (n < 0 ? '-' : '') + fixed(0);
			case 'decimal': return (n < 0 ? '-' : '') + fixed(2);
			case 'shortdate': return shortDate(String(value));
			case 'upcase': return String(value).toUpperCase();
			default: return String(value);
		}
	}

	// Table tools presets: classes styled in moe-document.css. '' leaves the table/cell as designed (or converted).
	const TABLE_BORDERS = [
		{ id: '', label: 'As designed' },
		{ id: 'all', label: 'All borders' },
		{ id: 'outside', label: 'Outside only' },
		{ id: 'rows', label: 'Row lines' },
		{ id: 'bottom', label: 'Bottom only' },
		{ id: 'none', label: 'None' }
	];
	const CELL_BORDERS = [
		{ id: '', label: 'Same as table' },
		{ id: 'all', label: 'All sides' },
		{ id: 'bottom', label: 'Bottom only' },
		{ id: 'top', label: 'Top only' },
		{ id: 'none', label: 'None' }
	];
	const CELL_SHADES = [
		{ id: '', label: 'None' },
		{ id: 'light', label: 'Light grey' },
		{ id: 'grey', label: 'Grey' },
		{ id: 'dark', label: 'Dark grey' },
		{ id: 'green', label: 'MOE green tint' }
	];
	const CELL_VALIGN = [
		{ id: '', label: 'As designed' },
		{ id: 'top', label: 'Top' },
		{ id: 'middle', label: 'Middle' },
		{ id: 'bottom', label: 'Bottom' }
	];

	// Current option of a prefix-named class group (e.g. tbl-b-all -> 'all'), or ''.
	function variantOf(component, prefix, options) {
		const classes = component.getClasses();
		const hit = options.filter(function (o) { return o.id && classes.indexOf(prefix + o.id) >= 0; })[0];
		return hit ? hit.id : '';
	}

	// Author-entered text placed in exported HTML: encoded, and braces neutralized so it can't become Liquid.
	function escapeText(text) {
		return String(text).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;')
			.replace(/\{/g, '&#123;').replace(/\}/g, '&#125;');
	}

	// ---- Page setup: paper, margins and the running header/footer --------------------------------------------------
	const PAGE_SIZES = {
		letter: { width: 8.5, height: 11, label: 'Letter (8.5 \u00d7 11 in)' },
		legal: { width: 8.5, height: 14, label: 'Legal (8.5 \u00d7 14 in)' },
		a4: { width: 8.27, height: 11.69, label: 'A4 (210 \u00d7 297 mm)' }
	};
	const HF_SLOTS = ['left', 'center', 'right'];
	const HF_PARTS = ['header', 'footer', 'firstHeader', 'firstFooter', 'evenHeader', 'evenFooter'];
	const FIELD_TOKEN = /&#123;([A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)&#125;/g;
	const DATE_LIQUID = "{{ 'now' | date: '%m/%d/%Y' }}";

	function emptySlots() {
		return { left: '', center: '', right: '' };
	}

	// What a template prints with when it has no page setup (matches the renderer's built-in footer).
	function defaultSetup() {
		return {
			size: 'letter', orientation: 'portrait', fontSize: 10,
			margins: { top: 0.5, right: 0.5, bottom: 0.6, left: 0.5 },
			header: emptySlots(),
			footer: { left: 'Mutual Of Enumclaw', center: '', right: 'Page [Page] of [Pages]' },
			differentFirst: false, firstHeader: emptySlots(), firstFooter: emptySlots(),
			differentEven: false, evenHeader: emptySlots(), evenFooter: emptySlots()
		};
	}

	function clampNumber(value, min, max, fallback) {
		const n = Number(value);
		return isFinite(n) && n >= min && n <= max ? Math.round(n * 100) / 100 : fallback;
	}

	// Any stored or typed setup, made complete and safe (known sizes, margins 0-3 in, font 6-16 pt, text slots).
	function normalizeSetup(setup) {
		const d = defaultSetup();
		const s = setup || {};
		const margins = s.margins || {};
		const result = {
			size: PAGE_SIZES[s.size] ? s.size : d.size,
			orientation: s.orientation === 'landscape' ? 'landscape' : 'portrait',
			fontSize: clampNumber(s.fontSize, 6, 16, d.fontSize),
			margins: {
				top: clampNumber(margins.top, 0, 3, d.margins.top),
				right: clampNumber(margins.right, 0, 3, d.margins.right),
				bottom: clampNumber(margins.bottom, 0, 3, d.margins.bottom),
				left: clampNumber(margins.left, 0, 3, d.margins.left)
			},
			differentFirst: !!s.differentFirst,
			differentEven: !!s.differentEven
		};
		HF_PARTS.forEach(function (part) {
			const given = s[part] || (setup ? emptySlots() : d[part]);
			result[part] = {};
			HF_SLOTS.forEach(function (slot) { result[part][slot] = String(given[slot] || '').slice(0, 200); });
		});
		return result;
	}

	function pageSizeInches(setup) {
		const size = PAGE_SIZES[setup.size] || PAGE_SIZES.letter;
		return setup.orientation === 'landscape' ? { width: size.height, height: size.width } : { width: size.width, height: size.height };
	}

	// Classes, not data-* attributes: the server's sanitizer keeps class names.
	function setupClasses(setup) {
		const m = setup.margins;
		return ['doc-setup', 'ds-size-' + setup.size, 'ds-orient-' + setup.orientation,
			'ds-margins-' + [m.top, m.right, m.bottom, m.left].join('_'), 'ds-font-' + setup.fontSize];
	}

	// Header/footer slot text -> HTML. [Page], [Pages], [Date], [Logo] and {field.path} are the only live parts;
	// everything else is encoded and can't become Liquid or markup.
	function slotHtml(text) {
		return escapeText(text || '')
			.replace(/\[Page\]/g, '<span class="doc-pageno"></span>')
			.replace(/\[Pages\]/g, '<span class="doc-pagecount"></span>')
			.replace(/\[Date\]/g, DATE_LIQUID)
			.replace(/\[Logo\]/g, '<img class="doc-hf-logo" src="{{ brand.logos.horizontal_4color }}" alt="Mutual of Enumclaw">')
			.replace(FIELD_TOKEN, '{{ $1 }}');
	}

	function slotText(span) {
		if (!span) return '';
		const copy = span.cloneNode(true);
		copy.querySelectorAll('.doc-pageno').forEach(function (el) { el.replaceWith('[Page]'); });
		copy.querySelectorAll('.doc-pagecount').forEach(function (el) { el.replaceWith('[Pages]'); });
		copy.querySelectorAll('img.doc-hf-logo').forEach(function (el) { el.replaceWith('[Logo]'); });
		return copy.textContent
			.split(DATE_LIQUID).join('[Date]')
			.replace(/\{\{\s*([A-Za-z_][A-Za-z0-9_.]*)\s*\}\}/g, '{$1}');
	}

	function hfHtml(part, variant, slots) {
		return '<div class="doc-hf doc-hf-' + part + ' doc-hf-' + variant + '">' + HF_SLOTS.map(function (slot) {
			return '<span class="doc-hf-' + slot + '">' + slotHtml(slots[slot]) + '</span>';
		}).join('') + '</div>';
	}

	function setupInnerHtml(setup) {
		let html = hfHtml('header', 'default', setup.header) + hfHtml('footer', 'default', setup.footer);
		if (setup.differentFirst) html += hfHtml('header', 'first', setup.firstHeader) + hfHtml('footer', 'first', setup.firstFooter);
		if (setup.differentEven) html += hfHtml('header', 'even', setup.evenHeader) + hfHtml('footer', 'even', setup.evenFooter);
		return html;
	}

	// Reads a page setup back from exported HTML (pasted or imported markup).
	function parseSetup(el) {
		const classes = Array.prototype.slice.call(el.classList);
		const value = function (prefix) {
			const hit = classes.filter(function (c) { return c.indexOf(prefix) === 0; })[0];
			return hit ? hit.slice(prefix.length) : undefined;
		};
		const margins = (value('ds-margins-') || '').split('_');
		const setup = {
			size: value('ds-size-'), orientation: value('ds-orient-'), fontSize: value('ds-font-'),
			margins: { top: margins[0], right: margins[1], bottom: margins[2], left: margins[3] }
		};
		const read = function (part, variant) {
			const hf = el.querySelector('.doc-hf.doc-hf-' + part + '.doc-hf-' + variant);
			if (!hf) return null;
			const slots = {};
			HF_SLOTS.forEach(function (slot) { slots[slot] = slotText(hf.querySelector('.doc-hf-' + slot)); });
			return slots;
		};
		setup.header = read('header', 'default');
		setup.footer = read('footer', 'default');
		setup.firstHeader = read('header', 'first');
		setup.firstFooter = read('footer', 'first');
		setup.evenHeader = read('header', 'even');
		setup.evenFooter = read('footer', 'even');
		setup.differentFirst = !!(setup.firstHeader || setup.firstFooter);
		setup.differentEven = !!(setup.evenHeader || setup.evenFooter);
		return normalizeSetup(setup);
	}

	// ---- Watermark: DRAFT / SPECIMEN / VOID ... across every page, always or when a condition holds ---------------
	const WM_PRESETS = ['DRAFT', 'SPECIMEN', 'VOID', 'COPY', 'SAMPLE'];
	const WM_OPTIONS = {
		color: [{ id: 'grey', label: 'Grey' }, { id: 'red', label: 'Red' }, { id: 'green', label: 'Green' }, { id: 'blue', label: 'Blue' }],
		strength: [{ id: 'light', label: 'Light' }, { id: 'medium', label: 'Medium' }, { id: 'strong', label: 'Strong' }],
		size: [{ id: 'small', label: 'Small' }, { id: 'medium', label: 'Medium' }, { id: 'large', label: 'Large' }],
		angle: [{ id: 'diagonal', label: 'Diagonal' }, { id: 'horizontal', label: 'Horizontal' }]
	};
	const WM_DEFAULTS = { color: 'grey', strength: 'medium', size: 'medium', angle: 'diagonal' };
	const THEME_NAME = /^[A-Za-z0-9_-]{1,64}$/;

	// ---- Barcodes, charts and signatures (drawn by the server; previews on the canvas) ------------------------------
	const BARCODE_KINDS = [
		{ id: 'qr', label: 'QR code' },
		{ id: 'code128', label: 'Code 128 (letters and numbers)' },
		{ id: 'code39', label: 'Code 39 (capitals and numbers)' },
		{ id: 'datamatrix', label: 'Data Matrix' },
		{ id: 'pdf417', label: 'PDF417' }
	];
	const BARCODE_HEIGHTS = [{ id: 's', label: 'Short' }, { id: 'm', label: 'Medium' }, { id: 'l', label: 'Tall' }];
	const CHART_TYPES = [
		{ id: 'column', label: 'Columns' },
		{ id: 'bar', label: 'Bars (horizontal)' },
		{ id: 'line', label: 'Line' },
		{ id: 'pie', label: 'Pie' },
		{ id: 'donut', label: 'Donut' }
	];
	const SIGNATURE_DATES = [
		{ id: 'blank', label: 'A line to write the date on' },
		{ id: 'today', label: 'The date the document is printed' },
		{ id: 'field', label: 'A date from the data' },
		{ id: 'none', label: 'No date' }
	];
	// Props whose change redraws a barcode / chart / signature on the canvas.
	const VISUAL_PROPS = ['field', 'kind', 'size', 'height', 'showText', 'list', 'label', 'value', 'chartType', 'format', 'title',
		'nameField', 'titleField', 'dateMode', 'dateField', 'imageField', 'anchor'];

	function isLinearBarcode(kind) {
		return kind === 'code128' || kind === 'code39';
	}

	// Fields of a list's items, relative to the item (claims[] with claim.year => year); numeric: number fields only.
	function chartItemFields(path, numeric) {
		const list = schema.collections.find(function (c) { return c.path === path; });
		if (!list) return [];
		return list.fields
			.filter(function (f) { return f !== list.alias && (!numeric || schema.kinds[f] === 'number'); })
			.map(function (f) { return f.slice(list.alias.length + 1); });
	}

	const visualPreviews = new Map();
	// The server's rendering of a barcode / chart / signature with the test data (cached).
	async function visualPreview(liquid) {
		const data = sampleData();
		const key = liquid + '\u0000' + JSON.stringify(data);
		if (visualPreviews.has(key)) return visualPreviews.get(key);
		let html = '';
		try {
			const response = await fetch('/api/expressions/preview', {
				method: 'POST',
				headers: { 'Content-Type': 'application/json' },
				body: JSON.stringify({ liquid: liquid, data: data })
			});
			if (response.ok) html = (await response.json()).text || '';
		} catch (e) { /* offline: no preview */ }
		if (visualPreviews.size > 200) visualPreviews.clear();
		visualPreviews.set(key, html);
		return html;
	}

	// ---- Conditional styling: brand-CSS classes (cs-*) an element gets when a data condition holds -----------------
	const COND_STYLES = [
		{ id: 'red-text', label: 'Red text' },
		{ id: 'green-text', label: 'Green text' },
		{ id: 'muted', label: 'Grey text' },
		{ id: 'bold', label: 'Bold' },
		{ id: 'italic', label: 'Italic' },
		{ id: 'strike', label: 'Struck through' },
		{ id: 'highlight-yellow', label: 'Yellow highlight' },
		{ id: 'highlight-red', label: 'Red highlight' },
		{ id: 'highlight-green', label: 'Green highlight' }
	];
	const MAX_COND_STYLES = 10;

	// Complete, safe rules: known style, field path, condition; at most MAX_COND_STYLES.
	function normalizeCondStyles(rules) {
		if (!Array.isArray(rules)) return [];
		return rules.filter(function (r) {
			return r && FIELD_PATH.test(r.field || '') && COND_STYLES.some(function (s) { return s.id === r.style; });
		}).slice(0, MAX_COND_STYLES).map(function (r) {
			return {
				field: r.field,
				operator: CONDITIONS.some(function (c) { return c.id === r.operator; }) ? r.operator : 'present',
				value: String(r.value == null ? '' : r.value).slice(0, 100),
				style: r.style
			};
		});
	}

	// A condition literal for inside an HTML attribute: single quotes (the attribute uses double quotes).
	function attributeLiteral(value, kind) {
		const clean = String(value || '').replace(/["'{}%<>&]/g, '').trim();
		if ((kind === 'number' || kind === undefined) && /^-?\d+(\.\d+)?$/.test(clean)) return clean;
		if (clean === 'true' || clean === 'false') return clean;
		return "'" + clean + "'";
	}

	function condStyleCondition(rule) {
		if (rule.operator === 'blank') return rule.field + ' == blank';
		if (!CONDITION_OPERATORS[rule.operator]) return rule.field + ' != blank';
		return rule.field + ' ' + CONDITION_OPERATORS[rule.operator] + ' ' + attributeLiteral(rule.value, schema.kinds[rule.field]);
	}

	// What the rules add to the exported class attribute: {% if cond %}cs-style{% endif %} for each.
	function condStyleLiquid(rules) {
		return normalizeCondStyles(rules).map(function (r) {
			return '{% if ' + condStyleCondition(r) + ' %}cs-' + r.style + '{% endif %}';
		}).join(' ');
	}

	// The rules are captured into a variable just before the element ({% capture cs_x %}...{% endcapture %}), and the
	// class attribute prints the variable: attributes can't hold the conditions (GrapesJS escapes < and > in them).
	// The name comes from the rules, so the same rules always export the same text.
	function condStyleVar(liquid) {
		let hash = 0;
		for (let i = 0; i < liquid.length; i++) hash = (hash * 31 + liquid.charCodeAt(i)) | 0;
		return 'cs_' + (hash >>> 0).toString(36);
	}

	function describeCondStyle(rule) {
		const style = COND_STYLES.find(function (s) { return s.id === rule.style; });
		const op = CONDITIONS.find(function (c) { return c.id === rule.operator; });
		return style.label + ' if ' + rule.field + ' ' + op.label + (CONDITION_OPERATORS[rule.operator] ? ' "' + attributeLiteral(rule.value).replace(/^'|'$/g, '') + '"' : '');
	}

	// Does the rule hold for a sample value (the canvas preview)? Mirrors Liquid's comparisons for the usual cases.
	function condStyleHolds(rule, value) {
		const empty = value === undefined || value === null || value === '' || value === false || (Array.isArray(value) && !value.length);
		if (rule.operator === 'blank') return empty;
		if (rule.operator === 'present') return !empty;
		const literal = attributeLiteral(rule.value, schema.kinds[rule.field]);
		const text = literal.charAt(0) === "'" ? literal.slice(1, -1) : literal;
		if (rule.operator === 'contains') return value != null && String(value).indexOf(text) >= 0;
		const numeric = literal.charAt(0) !== "'" && text !== 'true' && text !== 'false';
		const a = numeric ? Number(value) : typeof value === 'boolean' ? String(value) : String(value == null ? '' : value);
		const b = numeric ? Number(text) : text;
		switch (rule.operator) {
			case 'eq': return a === b;
			case 'ne': return a !== b;
			case 'gt': return numeric && a > b;
			case 'lt': return numeric && a < b;
			default: return false;
		}
	}

	// Canvas: the styles the test data gives, and a marker that the element is conditionally styled.
	function paintCondStyle(view) {
		const el = view && view.el;
		if (!el || !el.classList || !view.model) return;
		const rules = normalizeCondStyles(view.model.get('condStyles'));
		COND_STYLES.forEach(function (s) { el.classList.remove('cs-' + s.id); });
		if (!rules.length) {
			el.removeAttribute('data-cond-style');
			return;
		}
		el.setAttribute('data-cond-style', rules.map(describeCondStyle).join('; '));
		rules.forEach(function (r) {
			if (condStyleHolds(r, sampleValue(r.field))) el.classList.add('cs-' + r.style);
		});
	}
	const FIELD_PATH = /^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$/;

	function normalizeWatermark(watermark) {
		const w = watermark || {};
		const text = String(w.text == null ? 'DRAFT' : w.text).replace(/\s+/g, ' ').trim().slice(0, 40);
		const result = { text: text || 'DRAFT' };
		Object.keys(WM_OPTIONS).forEach(function (key) {
			result[key] = WM_OPTIONS[key].some(function (o) { return o.id === w[key]; }) ? w[key] : WM_DEFAULTS[key];
		});
		result.field = FIELD_PATH.test(w.field || '') ? w.field : '';
		result.operator = CONDITIONS.some(function (c) { return c.id === w.operator; }) ? w.operator : 'present';
		result.value = String(w.value || '').slice(0, 100);
		return result;
	}

	function watermarkClasses(w) {
		return ['doc-watermark', 'wm-color-' + w.color, 'wm-strength-' + w.strength, 'wm-size-' + w.size, 'wm-angle-' + w.angle];
	}

	// The Liquid condition the watermark prints under ('' = always). Same rules as Show If.
	function watermarkCondition(w) {
		if (!w.field) return '';
		if (w.operator === 'blank') return w.field + ' == blank';
		if (!CONDITION_OPERATORS[w.operator]) return w.field + ' != blank';
		return w.field + ' ' + CONDITION_OPERATORS[w.operator] + ' ' + conditionLiteral(w.value, schema.kinds[w.field]);
	}

	function parseWatermark(el) {
		const classes = Array.prototype.slice.call(el.classList);
		const value = function (prefix) {
			const hit = classes.filter(function (c) { return c.indexOf(prefix) === 0; })[0];
			return hit ? hit.slice(prefix.length) : undefined;
		};
		const text = el.querySelector('.doc-watermark-text');
		return normalizeWatermark({
			text: (text || el).textContent,
			color: value('wm-color-'), strength: value('wm-strength-'), size: value('wm-size-'), angle: value('wm-angle-')
		});
	}

	// Value typed into a Show If condition, as a Liquid literal. Quotes and braces are dropped (Liquid strings can't escape).
	// Untyped paths (list .size, forloop.index) compare as numbers when the value is numeric.
	function conditionLiteral(value, kind) {
		const clean = String(value || '').replace(/["{}%]/g, '').trim();
		if ((kind === 'number' || kind === undefined) && /^-?\d+(\.\d+)?$/.test(clean)) return clean;
		if (clean === 'true' || clean === 'false') return clean;
		return '"' + clean + '"';
	}

	function emptyText(component) {
		return (component.get('emptyText') || '').trim();
	}

	// {% else %} branch of a for loop: printed instead of the rows when the list is empty.
	function emptyBranch(component, open, close) {
		const text = emptyText(component);
		return text ? '{% else %}' + open + escapeText(text) + close : '';
	}

	function syncEmptyAttr(component) {
		const text = emptyText(component);
		if (text) component.addAttributes({ 'data-empty': text });
		else component.removeAttributes('data-empty');
	}

	// A row-level Repeat / Show If (a tbody) nested inside another one: HTML can't nest tbodies, so the inner one
	// exports as its Liquid tags around its rows (e.g. a coverage loop inside an auto loop).
	function nestedRowGroup(component) {
		const parent = component.parent();
		return component.get('tagName') === 'tbody' && parent && parent.get('tagName') === 'tbody';
	}

	// Approved public lockups only (brand guide 3.1). The restricted shield / "MOE" icon marks are deliberately absent.
	const LOGO_VARIANTS = [
		{ id: 'horizontal_4color', label: 'Horizontal, 4-color (primary)' },
		{ id: 'stacked_4color', label: 'Stacked, 4-color' },
		{ id: 'horizontal_1color', label: 'Horizontal, 1-color black' },
		{ id: 'stacked_1color', label: 'Stacked, 1-color black' },
		{ id: 'horizontal_moegreen', label: 'Horizontal, MOE Green' },
		{ id: 'horizontal_white', label: 'Horizontal, white (dark backgrounds only)' }
	];

	// Material Symbols (Apache 2.0): a curated outlined set in /lib/material-symbols/icons.json, exported as inline SVG.
	const ICON_SIZES = [
		{ id: '', label: 'Text size' },
		{ id: 'lg', label: 'Large' },
		{ id: 'xl', label: 'Extra large' }
	];
	const ICON_TONES = [
		{ id: '', label: 'MOE Green' },
		{ id: 'alpine', label: 'Alpine Green' },
		{ id: 'apple', label: 'Green Apple' },
		{ id: 'aqua', label: 'Aqua (accessible)' },
		{ id: 'sunshine', label: 'Sunshine' },
		{ id: 'autumn', label: 'Autumn' },
		{ id: 'black', label: 'Black' },
		{ id: 'white', label: 'White (dark backgrounds only)' }
	];

	// Path data is restricted to SVG path characters so a tampered project can't inject markup.
	function iconSvg(path) {
		return '<svg viewBox="0 -960 960 960" aria-hidden="true"><path d="' + String(path || '').replace(/[^0-9A-Za-z .,\-]/g, '') + '"/></svg>';
	}

	// Swap a prefix-named class (e.g. moe-icon--lg) to match an option id; '' means no class.
	function setVariantClass(component, prefix, options, value) {
		options.forEach(function (o) { if (o.id) component.removeClass(prefix + o.id); });
		if (value) component.addClass(prefix + value);
	}

	// ---------------------------------------------------------------------------------------------
	// Plugin: document component types + blocks.
	// ---------------------------------------------------------------------------------------------
	function documentPlugin(schema, icons, uiIcons, sampleValue) {
		function collectionFor(path) {
			return schema.collections.find(function (c) { return c.path === path; });
		}

		return function (editor) {
			const dc = editor.DomComponents;

			// Conditional styling works on any element, so it is added to the base component (before the types below
			// capture its methods): the rules' {% if %} class names go into the exported class attribute, and the canvas
			// shows the styles the test data gives.
			(function () {
				const model = dc.getType('default').model.prototype;
				const view = dc.getType('default').view.prototype;
				const getAttrToHTML = model.getAttrToHTML;
				model.getAttrToHTML = function () {
					const attrs = getAttrToHTML.apply(this, arguments);
					const liquid = condStyleLiquid(this.get('condStyles'));
					if (liquid) attrs['class'] = ((attrs['class'] || '') + ' {{ ' + condStyleVar(liquid) + ' }}').trim();
					return attrs;
				};
				const toHTML = model.toHTML;
				model.toHTML = function () {
					const html = toHTML.apply(this, arguments);
					const liquid = condStyleLiquid(this.get('condStyles'));
					return liquid ? '{% capture ' + condStyleVar(liquid) + ' %}' + liquid + '{% endcapture %}' + html : html;
				};
				['updateAttributes', 'updateClasses'].forEach(function (name) {
					const base = view[name];
					if (!base) return;
					view[name] = function () {
						const result = base.apply(this, arguments);
						paintCondStyle(this);
						return result;
					};
				});
				editor.on('component:update:condStyles', function (component) {
					if (component.view) paintCondStyle(component.view);
					countChange();
				});
			})();

			const baseGetInnerHTML = dc.getType('default').model.prototype.getInnerHTML;
			const baseGetAttrToHTML = dc.getType('default').model.prototype.getAttrToHTML;
			const baseRowToHTML = dc.getType('row').model.prototype.toHTML;
			const baseUpdateAttributes = dc.getType('default').view.prototype.updateAttributes;

			// Canvas text of a bound field. describe(model) -> { liquid, sample, name }; liquid '' means unmapped (empty).
			// The export comes from the binding (getInnerHTML), so what the canvas shows here is never saved.
			function fieldView(describe) {
				return {
					init: function () {
						this.listenTo(this.model, 'change:field change:format change:legacyName change:ifEmpty change:checkedWhen', this.paintField);
					},
					renderChildren: function () {
						this.paintField();
					},
					updateAttributes: function () {
						baseUpdateAttributes.apply(this, arguments);
						this.paintField();
					},
					paintField: function () {
						const el = this.el;
						if (!el) return;
						const d = describe(this.model);
						let mode = fieldDisplay.mode;
						let text;
						if (!d.liquid) {
							mode = 'unmapped';
							text = d.name || '';
						} else if (mode === 'liquid') {
							text = d.liquid;
						} else if (mode === 'sample' && d.sample) {
							text = d.sample;
						} else if (mode === 'sample' && d.fallback) {
							mode = 'fallback';
							text = d.fallback;
						} else {
							mode = 'name';
							text = d.name;
						}
						// Names are cut to a fixed length so they fit the cells and boxes; hovering shows the whole name.
						const full = text;
						if (mode === 'name' && text.length > FIELD_NAME_MAX) text = text.slice(0, FIELD_NAME_MAX - 1).replace(/\s+$/, '') + '\u2026';
						el.textContent = text;
						if (full !== text) el.setAttribute('data-full', full);
						else el.removeAttribute('data-full');
						el.setAttribute('data-dfmode', mode);
						if (d.liquid) el.setAttribute('title', d.liquid);
						else if (d.hint) el.setAttribute('title', d.hint);
						else el.removeAttribute('title');
					}
				};
			}

			function describeField(model) {
				const path = model.get('field');
				if (!path) return { liquid: '', sample: '', name: '', hint: model.get('formLabel') || '' };
				return {
					liquid: liquidExpression(path, model.get('format'), model.get('ifEmpty')),
					sample: formatSample(sampleValue(path), model.get('format')),
					fallback: fallbackText(model.get('ifEmpty')),
					name: labelFor(path)
				};
			}

			// A check box shows X (or stays empty: a no-break space, so the sample isn't replaced by the name) as the PDF prints it.
			function describeCheck(model) {
				const path = model.get('field');
				if (!path) return { liquid: '', sample: '', name: '', hint: model.get('formLabel') || '' };
				return {
					liquid: checkExpression(path, model.get('checkedWhen')),
					sample: isChecked(sampleValue(path), model.get('checkedWhen')) ? 'X' : '\u00a0',
					name: labelFor(path)
				};
			}

			// Custom props (field/format/collection) aren't tracked by GrapesJS, so count their changes as unsaved edits.
			function countChange() {
				const em = editor.getModel();
				em.set('changesCount', (em.get('changesCount') || 0) + 1);
			}

			// Format: the preset formats plus Custom... (a number / date pattern, or a mask) built in the Format dialog.
			editor.Traits.addType('format', {
				createInput: function (opts) {
					const el = document.createElement('div');
					el.className = 'format-trait';
					const select = document.createElement('select');
					select.className = 'format-select';
					FORMATS.concat([{ id: '__custom', label: 'Custom\u2026' }]).forEach(function (f) {
						const option = document.createElement('option');
						option.value = f.id;
						option.textContent = f.label;
						select.appendChild(option);
					});
					const summary = document.createElement('small');
					summary.className = 'format-summary';
					const edit = document.createElement('button');
					edit.type = 'button';
					edit.className = 'format-edit';
					edit.textContent = 'Edit custom format\u2026';
					select.addEventListener('change', function (e) {
						e.stopPropagation();
						if (select.value === '__custom') {
							const current = opts.component.get('format') || '';
							select.value = parseCustomFormat(current) ? '__custom' : current;
							openFormatDialog(opts.component);
						} else {
							opts.component.set('format', select.value);
						}
					});
					edit.addEventListener('click', function () { openFormatDialog(opts.component); });
					el.appendChild(select);
					el.appendChild(summary);
					el.appendChild(edit);
					return el;
				},
				onEvent: function () { /* the select and the dialog set the format themselves */ },
				onUpdate: function (opts) {
					const format = opts.component.get('format') || '';
					const custom = parseCustomFormat(format);
					opts.elInput.querySelector('.format-select').value = custom ? '__custom' : format;
					opts.elInput.querySelector('.format-summary').textContent = custom ? describeCustomFormat(custom) : '';
					opts.elInput.querySelector('.format-edit').hidden = !custom;
				}
			});

			// Read-only display of what a component is bound to. Binding is changed from the Model panel, not a dropdown.
			editor.Traits.addType('binding', {
				createInput: function (opts) {
					const el = document.createElement('div');
					el.className = 'binding-trait';
					const path = document.createElement('code');
					path.className = 'binding-path';
					const hint = document.createElement('small');
					hint.textContent = opts.trait.get('hint') || '';
					el.appendChild(path);
					el.appendChild(hint);
					return el;
				},
				onUpdate: function (opts) {
					opts.elInput.querySelector('.binding-path').textContent = opts.component.get(opts.trait.get('name')) || '(not bound)';
				}
			});

			// Data Field: shows {{ path | format }} in the canvas and exports exactly that Liquid expression.
			dc.addType('data-field', {
				// Imported documents (Word merge fields) arrive as <span class="df" data-field="path">.
				isComponent: function (el) {
					if (el.tagName !== 'SPAN' || !el.classList.contains('df') || !el.hasAttribute('data-field')) return false;
					const format = el.getAttribute('data-format') || '';
					const known = FORMATS.some(function (f) { return f.id === format; }) || !!parseCustomFormat(format);
					return { type: 'data-field', field: el.getAttribute('data-field'), format: known ? format : '', components: [] };
				},
				model: {
					defaults: {
						tagName: 'span',
						name: 'Data Field',
						classes: ['df'],
						droppable: false,
						editable: false,
						field: schema.fields[0],
						format: '',
						ifEmpty: '',
						traits: [
							{ type: 'binding', name: 'field', label: 'Field', changeProp: true, hint: 'Select a property in the Model panel to rebind.' },
							{ type: 'format', name: 'format', label: 'Format', changeProp: true },
							{ type: 'text', name: 'ifEmpty', label: 'If empty, show', changeProp: true, placeholder: 'Blank, or e.g. Not Applicable' }
						]
					},
					init: function () {
						this.on('change:field change:format change:ifEmpty', this.syncExpression);
						this.on('change:field change:format change:ifEmpty', countChange);
						this.on('change:field change:format', function () {
							const table = this.closestType('data-table');
							if (table && table.get('totals') && this.closestType('repeat-row')) table.syncFooter();
						});
						this.syncExpression();
					},
					syncExpression: function () {
						this.components(liquidExpression(this.get('field'), this.get('format'), this.get('ifEmpty')));
					},
					// From the binding, not the children: text editing around the field re-reads the canvas text into them.
					getInnerHTML: function () {
						return liquidExpression(this.get('field'), this.get('format'), this.get('ifEmpty'));
					}
				},
				view: fieldView(describeField)
			});

			// ---- Calculated Field: an expression over the data (policy.premium + policy.fees, sum(claims.amount)), compiled
			// to Liquid by the server. The compiled Liquid is kept on the component so export is synchronous.
			editor.Traits.addType('calc-expression', {
				createInput: function (opts) {
					const el = document.createElement('div');
					el.className = 'calc-trait';
					const code = document.createElement('code');
					code.className = 'calc-trait-expression';
					const error = document.createElement('small');
					error.className = 'calc-trait-error';
					const edit = document.createElement('button');
					edit.type = 'button';
					edit.className = 'calc-edit';
					edit.textContent = 'Edit calculation\u2026';
					edit.addEventListener('click', function () { openCalcBuilder(opts.component); });
					el.appendChild(code);
					el.appendChild(error);
					el.appendChild(edit);
					return el;
				},
				onUpdate: function (opts) {
					opts.elInput.querySelector('.calc-trait-expression').textContent = opts.component.get('expression') || '(none yet)';
					opts.elInput.querySelector('.calc-trait-error').textContent = opts.component.get('calcError') || '';
				}
			});

			// The repeats around a component, outermost first, so a preview can run inside their loops (first item).
			function loopsAround(component) {
				const loops = [];
				for (let p = component.parent(); p; p = p.parent()) {
					if ((p.is('repeat') || p.is('data-table')) && p.get('collection') && p.get('alias')) loops.unshift({ alias: p.get('alias'), path: p.get('collection') });
				}
				return loops;
			}

			function inLoops(component, liquid) {
				const loops = loopsAround(component);
				return loops.map(function (l) { return '{% for ' + l.alias + ' in ' + l.path + ' limit: 1 %}'; }).join('') + liquid +
					loops.map(function () { return '{% endfor %}'; }).join('');
			}
			editor.__calcInLoops = inLoops;

			let calcSamples = { data: null, values: {} };

			function calcSample(model) {
				const inner = model.get('liquid') ? inLoops(model, model.getInnerHTML()) : '';
				if (!inner) return '';
				const data = sampleData();
				if (calcSamples.data !== data) calcSamples = { data: data, values: {} };
				if (Object.prototype.hasOwnProperty.call(calcSamples.values, inner)) return calcSamples.values[inner];
				calcSamples.values[inner] = '';
				previewLiquid(inner, data).then(function (result) {
					if (calcSamples.data !== data) return;
					calcSamples.values[inner] = result.text || '';
					if (model.view) model.view.paintField();
				});
				return '';
			}

			function describeCalc(model) {
				const expression = String(model.get('expression') || '').trim();
				if (!expression || !model.get('liquid')) return { liquid: '', sample: '', name: expression ? '= ' + expression : 'Calculated field' };
				return {
					liquid: model.getInnerHTML(),
					sample: calcSample(model),
					fallback: fallbackText(model.get('ifEmpty')),
					name: '= ' + expression
				};
			}

			const calcView = fieldView(describeCalc);
			const calcViewInit = calcView.init;
			calcView.init = function () {
				calcViewInit.apply(this, arguments);
				this.listenTo(this.model, 'change:expression change:liquid change:calcError', this.paintField);
			};

			dc.addType('calc-field', {
				isComponent: function (el) {
					if (el.tagName !== 'SPAN' || !el.classList.contains('df-calc') || !el.hasAttribute('data-expression')) return false;
					const format = el.getAttribute('data-format') || '';
					const known = FORMATS.some(function (f) { return f.id === format; }) || !!parseCustomFormat(format);
					return { type: 'calc-field', expression: el.getAttribute('data-expression'), format: known ? format : '', components: [] };
				},
				model: {
					defaults: {
						tagName: 'span',
						name: 'Calculated Field',
						classes: ['df', 'df-calc'],
						droppable: false,
						editable: false,
						expression: '',
						format: '',
						ifEmpty: '',
						liquid: '',
						calcError: '',
						calcPaths: [],
						calcLists: [],
						traits: [
							{ type: 'calc-expression', name: 'expression', label: 'Calculation', changeProp: true },
							{ type: 'format', name: 'format', label: 'Format', changeProp: true },
							{ type: 'text', name: 'ifEmpty', label: 'If empty, show', changeProp: true, placeholder: 'Blank, or e.g. Not Applicable' }
						]
					},
					init: function () {
						this.on('change:expression', this.compile);
						this.on('change:expression change:format change:ifEmpty', countChange);
						this.on('change:expression change:format', this.syncAttributes);
						this.syncAttributes();
						if (this.get('expression') && !this.get('liquid')) this.compile();
					},
					compile: function () {
						const self = this;
						const expression = String(this.get('expression') || '');
						if (!expression.trim()) {
							this.set({ liquid: '', calcError: '', calcPaths: [], calcLists: [] });
							return Promise.resolve();
						}
						return compileExpression(expression, schema.collections.map(function (c) { return c.path; })).then(function (r) {
							if (String(self.get('expression') || '') !== expression) return;
							self.set({ liquid: r.liquid || '', calcError: r.error || '', calcPaths: r.paths || [], calcLists: r.lists || [] });
							clearTimeout(calcValidateTimer);
							calcValidateTimer = setTimeout(validateBindings, 50);
						});
					},
					// Expressions never contain braces or % (the compiler refuses them in text), so the attribute stays plain.
					syncAttributes: function () {
						this.addAttributes({
							'data-expression': String(this.get('expression') || '').replace(/[{}%]/g, ''),
							'data-format': this.get('format') || ''
						});
					},
					getInnerHTML: function () {
						const liquid = this.get('liquid');
						return liquid ? liquid + liquidExpression('calc_result', this.get('format'), this.get('ifEmpty')) : '';
					}
				},
				view: calcView
			});
			let calcValidateTimer = 0;

			// Repeat: a container whose children are emitted once per item of the chosen collection.
			dc.addType('repeat', {
				model: {
					defaults: {
						tagName: 'div',
						name: 'Repeat',
						droppable: true,
						collection: schema.collections.length ? schema.collections[0].path : '',
						alias: 'item',
						emptyText: '',
						field: '',
						operator: 'present',
						value: '',
						traits: [
							{ type: 'binding', name: 'collection', label: 'For each', changeProp: true, hint: 'Select a list [ ] in the Model panel to change.' },
							{ type: 'text', name: 'emptyText', label: 'If empty, show', changeProp: true, placeholder: 'e.g. No highlights' },
							{ type: 'binding', name: 'field', label: 'Only if', changeProp: true, hint: 'Select a property in the Model panel to change.' },
							{ type: 'select', name: 'operator', label: 'Condition', changeProp: true, options: CONDITIONS },
							{ type: 'text', name: 'value', label: 'Value', changeProp: true, placeholder: 'for equals / greater / less / contains' }
						]
					},
					init: function () {
						takeListPath(this);
						this.on('change:collection', this.syncAlias);
						this.on('change:collection change:emptyText', countChange);
						this.on('change:emptyText', function () { syncEmptyAttr(this); });
						this.syncAlias();
						syncEmptyAttr(this);
					},
					syncAlias: function () {
						const match = collectionFor(this.get('collection'));
						const alias = match ? match.alias : 'item';
						this.set('alias', alias);
						this.addAttributes({ 'data-repeat': alias + ' in ' + this.get('collection') });
					},
					getInnerHTML: function (opts) {
						const self = this;
						const conditionOf = function (prefix) {
							const get = function (k) { return self.get(prefix ? prefix + k.charAt(0).toUpperCase() + k.slice(1) : k); };
							return get('field') ? dc.getType('conditional').model.prototype.condition.call({ get: get }) : '';
						};
						// optional per-item filter (a row repeat has no element between the loop and its rows to hold a Show If)
						const itemIf = conditionOf('item');
						const body = baseGetInnerHTML.call(this, opts);
						const loop = '{% for ' + this.get('alias') + ' in ' + this.get('collection') + ' %}' +
							(itemIf ? '{% if ' + itemIf + ' %}' + body + '{% endif %}' : body) +
							emptyBranch(this, '<div class="moe-empty">', '</div>') +
							'{% endfor %}';
						// optional Show If around the loop (a row repeat inside a row-level choice has no other place for it)
						const condition = conditionOf('');
						return condition ? '{% if ' + condition + ' %}' + loop + '{% endif %}' : loop;
					},
					toHTML: function (opts) {
						return nestedRowGroup(this) ? this.getInnerHTML(opts) : dc.getType('default').model.prototype.toHTML.call(this, opts);
					}
				}
			});

			// Show If: a container whose content is only printed when its condition holds.
			dc.addType('conditional', {
				model: {
					defaults: {
						tagName: 'div',
						name: 'Show If',
						droppable: true,
						field: '',
						operator: 'present',
						value: '',
						traits: [
							{ type: 'binding', name: 'field', label: 'Show if', changeProp: true, hint: 'Select a property or list in the Model panel to change.' },
							{ type: 'select', name: 'operator', label: 'Condition', changeProp: true, options: CONDITIONS },
							{ type: 'text', name: 'value', label: 'Value', changeProp: true, placeholder: 'for equals / greater / less / contains' }
						]
					},
					init: function () {
						this.on('change:field change:operator change:value', this.syncLabel);
						this.on('change:field change:operator change:value', countChange);
						this.syncLabel();
					},
					needsValue: function () {
						return !!CONDITION_OPERATORS[this.get('operator')];
					},
					syncLabel: function () {
						const op = CONDITIONS.find(function (c) { return c.id === this.get('operator'); }, this) || CONDITIONS[0];
						const value = String(this.get('value') || '').replace(/["{}%]/g, '').trim();
						this.addAttributes({ 'data-show-if': (this.get('field') || '(choose a property)') + ' ' + op.label + (this.needsValue() ? ' "' + value + '"' : '') });
					},
					condition: function () {
						const field = this.get('field');
						if (!field) return '';
						const operator = this.get('operator');
						if (operator === 'blank') return field + ' == blank';
						if (!CONDITION_OPERATORS[operator]) return field + ' != blank';
						return field + ' ' + CONDITION_OPERATORS[operator] + ' ' + conditionLiteral(this.get('value'), schema.kinds[field]);
					},
					getInnerHTML: function (opts) {
						const inner = baseGetInnerHTML.call(this, opts);
						const condition = this.condition();
						return condition ? '{% if ' + condition + ' %}' + inner + '{% endif %}' : inner;
					},
					toHTML: function (opts) {
						return nestedRowGroup(this) ? this.getInnerHTML(opts) : dc.getType('default').model.prototype.toHTML.call(this, opts);
					}
				}
			});

			// Choose: the first branch whose condition holds is printed; an "otherwise" branch catches the rest.
			dc.addType('choice', {
				model: {
					defaults: {
						tagName: 'div',
						name: 'Choose',
						droppable: '[data-gjs-type=choice-branch]',
						attributes: { 'data-choice': '' }
					},
					getInnerHTML: function (opts) {
						let out = '';
						let opened = false;
						let closed = false;
						this.components().forEach(function (branch) {
							if (closed) return;
							const condition = branch.is('choice-branch') ? branch.condition() : '';
							if (condition) {
								out += (opened ? '{% elsif ' : '{% if ') + condition + ' %}';
								opened = true;
							} else if (opened) {
								out += '{% else %}';
								closed = true;
							} else {
								return;   // an unbound first branch can never be reached
							}
							out += branch.toHTML(opts);
						});
						return opened ? out + '{% endif %}' : '';
					}
				}
			});

			// Choose branch: a Show If whose Liquid tag is written by its Choose parent.
			dc.addType('choice-branch', {
				extend: 'conditional',
				model: {
					defaults: {
						name: 'When',
						draggable: '[data-gjs-type=choice]',
						traits: [
							{ type: 'binding', name: 'field', label: 'When', changeProp: true, hint: 'Select a property or list in the Model panel to change.' },
							{ type: 'select', name: 'operator', label: 'Condition', changeProp: true,
								options: CONDITIONS.concat([{ id: 'else', label: 'otherwise (none of the above)' }]) },
							{ type: 'text', name: 'value', label: 'Value', changeProp: true, placeholder: 'for equals / greater / less / contains' }
						]
					},
					syncLabel: function () {
						if (this.get('operator') === 'else') {
							this.addAttributes({ 'data-show-if': 'otherwise' });
							return;
						}
						dc.getType('conditional').model.prototype.syncLabel.call(this);
					},
					condition: function () {
						return this.get('operator') === 'else' ? '' : dc.getType('conditional').model.prototype.condition.call(this);
					},
					getInnerHTML: function (opts) {
						return baseGetInnerHTML.call(this, opts);
					}
				}
			});

			// Data Table: branded table whose single body row repeats once per collection item.
			dc.addType('data-table', {
				extend: 'table',
				isComponent: function () { return false; },
				model: {
					defaults: {
						name: 'Data Table',
						classes: ['moe-table'],
						collection: schema.collections.length ? schema.collections[0].path : '',
						alias: 'item',
						emptyText: '',
						totals: false,
						traits: [
							{ type: 'binding', name: 'collection', label: 'Rows for each', changeProp: true, hint: 'Select a list [ ] in the Model panel to change.' },
							{ type: 'text', name: 'emptyText', label: 'If empty, show', changeProp: true, placeholder: 'e.g. No claims reported' },
							{ type: 'checkbox', name: 'totals', label: 'Totals row', changeProp: true },
							{ type: 'select', name: 'tableStyle', label: 'Style', changeProp: true, options: [
								{ id: '', label: 'MOE brand (green header)' },
								{ id: 'material', label: 'Material (outlined)' }
							] },
							{ type: 'button', name: 'chooseColumns', label: ' ', text: 'Choose columns\u2026', full: true,
								command: function (ed) { const t = ed.getSelected(); if (t && t.is('data-table')) ed.trigger('doc:choose-columns', t); } }
						]
					},
					init: function () {
						takeListPath(this);
						this.on('change:collection', this.onCollectionChange);
						this.on('change:collection', countChange);
						this.on('change:emptyText change:totals', countChange);
						this.on('change:emptyText', function () { syncEmptyAttr(this); });
						this.on('change:totals', this.syncFooter);
						this.on('change:tableStyle', function () { this[this.get('tableStyle') === 'material' ? 'addClass' : 'removeClass']('md-table'); });
						this.on('change:tableStyle', countChange);
						this.syncAlias();
						syncEmptyAttr(this);
						if (this.get('totals') && !this.footRow()) this.syncFooter();
					},
					headRow: function () {
						const thead = this.components().filter(function (c) { return c.get('tagName') === 'thead'; })[0];
						return thead ? thead.components().at(0) : null;
					},
					bodyRow: function () {
						return this.findType('repeat-row')[0] || null;
					},
					footRow: function () {
						return this.findType('totals-row')[0] || null;
					},
					// Rebuild the totals row from the body row: numeric columns are summed, the first column keeps its label.
					syncFooter: function () {
						const tfoot = this.components().filter(function (c) { return c.get('tagName') === 'tfoot'; })[0];
						if (!this.get('totals')) {
							if (tfoot) tfoot.remove();
							return;
						}
						const body = this.bodyRow();
						if (!body) return;
						const oldFirst = this.footRow() && this.footRow().components().at(0);
						const oldLabel = oldFirst && !oldFirst.findType('total-field').length && oldFirst.getEl() ? oldFirst.getEl().textContent.trim() : '';
						const cells = body.components().map(function (cell, i) {
							const df = cell.findType('data-field')[0];
							const path = df && df.get('field');
							if (path && schema.kinds[path] === 'number') {
								return { type: 'cell', tagName: 'td', classes: ['num'], components: [{ type: 'total-field', field: path, format: df.get('format') }] };
							}
							if (i === 0) return { type: 'cell', tagName: 'td', components: [{ type: 'text', tagName: 'span', content: oldLabel || 'Total' }] };
							return { type: 'cell', tagName: 'td' };
						});
						const row = { type: 'totals-row', components: cells };
						if (tfoot) tfoot.components([row]);
						else this.append({ type: 'tfoot', components: [row] });
					},
					syncAlias: function () {
						const match = collectionFor(this.get('collection'));
						this.set('alias', match ? match.alias : 'item');
						const row = this.bodyRow();
						if (row) row.addAttributes({ 'data-repeat': this.get('alias') + ' in ' + this.get('collection') });
					},
					fields: function () {
						const match = collectionFor(this.get('collection'));
						return match && match.fields.length ? match.fields : schema.fields;
					},
					// Current columns in display order: [{ path, format, label }].
					columns: function () {
						const head = this.headRow();
						const body = this.bodyRow();
						if (!body) return [];
						return body.components().map(function (cell, i) {
							const df = cell.findType('data-field')[0];
							const th = head && head.components().at(i);
							return {
								path: df ? df.get('field') : '',
								format: df ? df.get('format') : '',
								label: th && th.getEl() ? th.getEl().textContent.trim() : ''
							};
						}).filter(function (c) { return c.path; });
					},
					// Rebuild header and body cells for the given columns; labels/formats are kept when supplied.
					setColumns: function (cols) {
						const head = this.headRow();
						const body = this.bodyRow();
						if (!head || !body || !cols.length) return;
						head.components(cols.map(function (c) {
							const cell = headerCell(c.path);
							if (c.label) cell.components[0].content = c.label;
							return cell;
						}));
						body.components(cols.map(function (c) {
							const cell = bodyCell(c.path);
							if (c.format !== undefined) cell.components[0].format = c.format;
							return cell;
						}));
						if (this.get('totals')) this.syncFooter();
					},
					// Re-point every column at the new collection's fields (in order) so the table stays valid.
					onCollectionChange: function () {
						this.syncAlias();
						const fields = this.fields();
						const head = this.headRow();
						const body = this.bodyRow();
						if (!body) return;
						body.components().forEach(function (cell, i) {
							const path = fields[i % fields.length];
							const numeric = schema.kinds[path] === 'number';
							const df = cell.findType('data-field')[0];
							if (df) df.set({ field: path, format: defaultFormat(schema, path) });
							cell[numeric ? 'addClass' : 'removeClass']('num');
							const th = head && head.components().at(i);
							if (th) {
								th[numeric ? 'addClass' : 'removeClass']('num');
								const label = th.components().at(0);
								if (label) label.components(labelFor(path));
							}
						});
						if (this.get('totals')) this.syncFooter();
					}
				}
			});

			// The table body row that is emitted inside {% for %}. Its collection comes from the enclosing Data Table.
			dc.addType('repeat-row', {
				extend: 'row',
				isComponent: function () { return false; },
				model: {
					defaults: { name: 'Repeat Row', draggable: false, removable: false, copyable: false },
					toHTML: function (opts) {
						const table = this.closestType('data-table');
						const alias = table ? table.get('alias') : 'item';
						const collection = table ? table.get('collection') : '';
						const empty = table ? emptyBranch(table, '<tr><td class="moe-empty" colspan="' + this.components().length + '">', '</td></tr>') : '';
						return '{% for ' + alias + ' in ' + collection + ' %}' + baseRowToHTML.call(this, opts) + empty + '{% endfor %}';
					}
				}
			});

			// Footer row of a Data Table with "Totals row" on. Only printed when the list has items.
			dc.addType('totals-row', {
				extend: 'row',
				isComponent: function () { return false; },
				model: {
					defaults: { name: 'Totals Row', draggable: false, removable: false, copyable: false },
					toHTML: function (opts) {
						const table = this.closestType('data-table');
						const collection = table ? table.get('collection') : '';
						return '{% if ' + collection + '.size > 0 %}' + baseRowToHTML.call(this, opts) + '{% endif %}';
					}
				}
			});

			// Sum of one column over the table's list: {{ lossRatio.claims | sum: "totalLoss" | currency }}.
			dc.addType('total-field', {
				model: {
					defaults: {
						tagName: 'span',
						name: 'Column Total',
						classes: ['df'],
						droppable: false,
						editable: false,
						draggable: false,
						field: '',
						format: '',
						traits: [{ type: 'format', name: 'format', label: 'Format', changeProp: true }]
					},
					init: function () {
						this.on('change:format', countChange);
						this.components('\u2211 ' + labelFor(this.get('field') || 'total'));
					},
					getInnerHTML: function () {
						const table = this.closestType('data-table');
						const prefix = table ? table.get('alias') + '.' : '';
						const field = this.get('field') || '';
						if (!table || field.indexOf(prefix) !== 0) return '';
						const format = this.get('format');
						return '{{ ' + table.get('collection') + ' | sum: "' + field.slice(prefix.length) + '"' + formatFilter(format) + ' }}';
					}
				},
				view: fieldView(function (model) {
					const field = model.get('field') || '';
					const name = '\u2211 ' + labelFor(field || 'total');
					const table = model.closestType('data-table');
					const prefix = table ? table.get('alias') + '.' : '';
					const list = table ? sampleValue(table.get('collection')) : null;
					let sample = '';
					if (Array.isArray(list) && field.indexOf(prefix) === 0) {
						const keys = field.slice(prefix.length).split('.');
						const sum = list.reduce(function (acc, item) {
							const v = Number(keys.reduce(function (o, k) { return o && o[k]; }, item));
							return isNaN(v) ? acc : acc + v;
						}, 0);
						sample = formatSample(sum, model.get('format'));
					}
					return { liquid: model.getInnerHTML() || name, sample: sample, name: name };
				})
			});

			// MOE logo: canvas shows the real SVG; export references the brand asset the server inlines as a data: URI.
			dc.addType('brand-logo', {
				model: {
					defaults: {
						tagName: 'img',
						name: 'MOE Logo',
						void: true,
						droppable: false,
						resizable: false,
						classes: ['brand-logo'],
						variant: 'horizontal_4color',
						traits: [{ type: 'select', name: 'variant', label: 'Lockup', changeProp: true, options: LOGO_VARIANTS }]
					},
					init: function () {
						this.on('change:variant', this.syncLogo);
						this.syncLogo();
					},
					syncLogo: function () {
						const variant = this.get('variant');
						this.addAttributes({ src: '/brand/logos/moe-logo-' + variant.replace(/_/g, '-') + '.svg', alt: 'Mutual Of Enumclaw' });
						this[variant.indexOf('stacked') === 0 ? 'addClass' : 'removeClass']('brand-logo--stacked');
					},
					getAttrToHTML: function (opts) {
						const attrs = baseGetAttrToHTML.call(this, opts);
						attrs.src = '{{ brand.logos.' + this.get('variant') + ' }}';
						return attrs;
					}
				}
			});

			// Data Image: a photo from the message, either a data: URI or a "docimage:{blob}" reference the server embeds
			// (e.g. Moody's aerial evidence per building). Export is <img src="{{ path }}"> skipped when the value is empty.
			// The canvas shows the sample model's image, set on the element only so it is never saved into the template.
			const IMAGE_PLACEHOLDER = 'data:image/svg+xml,' + encodeURIComponent(
				'<svg xmlns="http://www.w3.org/2000/svg" width="320" height="220"><rect width="320" height="220" fill="#E8ECE6"/>' +
				'<text x="160" y="115" text-anchor="middle" font-family="sans-serif" font-size="16" fill="#6B7563">No sample image</text></svg>');
			const baseToHTML = dc.getType('default').model.prototype.toHTML;
			dc.addType('data-image', {
				model: {
					defaults: {
						tagName: 'img',
						name: 'Data Image',
						void: true,
						droppable: false,
						classes: ['data-image'],
						attributes: { alt: '' },
						field: '',
						traits: [{ type: 'binding', name: 'field', label: 'Image', changeProp: true, hint: 'Select an image property in the Model panel to rebind.' }]
					},
					init: function () {
						this.on('change:field', countChange);
					},
					getAttrToHTML: function (opts) {
						const attrs = baseGetAttrToHTML.call(this, opts);
						attrs.src = '{{ ' + this.get('field') + ' }}';
						return attrs;
					},
					toHTML: function (opts) {
						const path = this.get('field');
						return path ? '{% if ' + path + ' != blank %}' + baseToHTML.call(this, opts) + '{% endif %}' : '';
					}
				},
				view: {
					init: function () {
						this.listenTo(this.model, 'change:field', this.updateAttributes);
					},
					updateAttributes: function () {
						baseUpdateAttributes.apply(this, arguments);
						const sample = sampleValue(this.model.get('field'));
						let src = IMAGE_PLACEHOLDER;
						if (typeof sample === 'string' && /^data:image\//.test(sample)) src = sample;
						else if (typeof sample === 'string' && sample.indexOf('docimage:') === 0) {
							src = '/api/document-images/' + sample.slice('docimage:'.length).split('/').map(encodeURIComponent).join('/');
						}
						this.el.onerror = function () { this.onerror = null; this.src = IMAGE_PLACEHOLDER; };
						this.el.setAttribute('src', src);
					}
				}
			});

			dc.addType('page-break', {
				isComponent: function (el) { return el.tagName === 'DIV' && el.classList.contains('page-break'); },
				model: {
					defaults: { tagName: 'div', name: 'Page Break', droppable: false, classes: ['page-break'] }
				}
			});

			// Imported text keeps "{" and a following "{" or "%" apart with an empty span so wording never becomes Liquid
			// (DocumentImport.LiquidBreak). It prints nothing, so it is invisible to the author: no layer, no selection.
			// Page setup: size, orientation, margins and the running header / footer (default, first page, even pages).
			// Exported as a hidden <div class="doc-setup"> the PDF renderer reads (and removes) before printing; its
			// header/footer text goes through Liquid with the document, so fields can be used in it.
			dc.addType('page-setup', {
				isComponent: function (el) {
					if (el.tagName !== 'DIV' || !el.classList || !el.classList.contains('doc-setup')) return false;
					return { type: 'page-setup', setup: parseSetup(el), components: [] };
				},
				model: {
					defaults: {
						tagName: 'div', name: 'Page setup', classes: ['doc-setup'], setup: null, components: [],
						layerable: false, selectable: false, hoverable: false, highlightable: false,
						draggable: false, droppable: false, copyable: false, removable: false, editable: false
					},
					init: function () {
						this.syncClasses();
						this.on('change:setup', function () { this.syncClasses(); countChange(); });
					},
					syncClasses: function () {
						this.setClass(setupClasses(normalizeSetup(this.get('setup'))));
					},
					getInnerHTML: function () {
						return setupInnerHtml(normalizeSetup(this.get('setup')));
					}
				},
				view: {
					onRender: function () { this.el.innerHTML = ''; }
				}
			});

			// Theme: which brand theme the template uses (colors and fonts over the brand stylesheet). A hidden marker the
			// server reads (div.doc-theme.theme-{name}); chosen with Theme... in the three-dot menu.
			dc.addType('theme-ref', {
				isComponent: function (el) {
					if (el.tagName !== 'DIV' || !el.classList || !el.classList.contains('doc-theme')) return false;
					const hit = Array.prototype.filter.call(el.classList, function (c) { return c.indexOf('theme-') === 0; })[0];
					return { type: 'theme-ref', theme: hit ? hit.slice(6) : '', components: [] };
				},
				model: {
					defaults: {
						tagName: 'div', name: 'Theme', classes: ['doc-theme'], theme: '', components: [],
						layerable: false, selectable: false, hoverable: false, highlightable: false,
						draggable: false, droppable: false, copyable: false, removable: false, editable: false
					},
					init: function () {
						this.syncClasses();
						this.on('change:theme', function () { this.syncClasses(); countChange(); applyCanvasTheme(); });
					},
					syncClasses: function () {
						const name = THEME_NAME.test(this.get('theme') || '') ? this.get('theme') : 'moe';
						this.setClass(['doc-theme', 'theme-' + name]);
					}
				},
				view: {
					onRender: function () { this.el.innerHTML = ''; }
				}
			});

			// Language: which language a translated template is written in (div.doc-language.lang-es). The server prints the
			// document in that language and keeps the marker in step with the language a version is saved under; the Language
			// selector in the toolbar sets it. English templates have none.
			dc.addType('language-ref', {
				isComponent: function (el) {
					if (el.tagName !== 'DIV' || !el.classList || !el.classList.contains('doc-language')) return false;
					const hit = Array.prototype.filter.call(el.classList, function (c) { return c.indexOf('lang-') === 0; })[0];
					return { type: 'language-ref', language: hit ? hit.slice(5) : '', components: [] };
				},
				model: {
					defaults: {
						tagName: 'div', name: 'Language', classes: ['doc-language'], language: '', components: [],
						layerable: false, selectable: false, hoverable: false, highlightable: false,
						draggable: false, droppable: false, copyable: false, removable: false, editable: false
					},
					init: function () {
						this.syncClasses();
						this.on('change:language', function () { this.syncClasses(); });
					},
					syncClasses: function () {
						const code = LANGUAGES[this.get('language')] ? this.get('language') : '';
						this.setClass(['doc-language', 'lang-' + (code || 'en')]);
					}
				},
				view: {
					onRender: function () { this.el.innerHTML = ''; }
				}
			});

			// Watermark: a fixed, see-through word across every page (brand CSS .doc-watermark). Shown on the canvas too,
			// but clicks go through it; it is edited from Watermark… in the three-dot menu.
			dc.addType('watermark', {
				isComponent: function (el) {
					if (el.tagName !== 'DIV' || !el.classList || !el.classList.contains('doc-watermark') || el.classList.contains('doc-watermark-stamp')) return false;
					return { type: 'watermark', watermark: parseWatermark(el), components: [] };
				},
				model: {
					defaults: {
						tagName: 'div', name: 'Watermark', classes: ['doc-watermark'], watermark: null, components: [],
						layerable: false, selectable: false, hoverable: false, highlightable: false,
						draggable: false, droppable: false, copyable: false, removable: false, editable: false
					},
					init: function () {
						this.syncClasses();
						this.on('change:watermark', function () { this.syncClasses(); countChange(); });
					},
					syncClasses: function () {
						this.setClass(watermarkClasses(normalizeWatermark(this.get('watermark'))));
					},
					getInnerHTML: function () {
						return '<span class="doc-watermark-text">' + escapeText(normalizeWatermark(this.get('watermark')).text) + '</span>';
					},
					toHTML: function (opts) {
						const html = dc.getType('default').model.prototype.toHTML.call(this, opts);
						const condition = watermarkCondition(normalizeWatermark(this.get('watermark')));
						return condition ? '{% if ' + condition + ' %}' + html + '{% endif %}' : html;
					}
				},
				view: {
					init: function () {
						this.listenTo(this.model, 'change:watermark', this.render);
					},
					onRender: function () {
						const w = normalizeWatermark(this.model.get('watermark'));
						const text = document.createElement('span');
						text.className = 'doc-watermark-text';
						text.textContent = w.text;
						this.el.innerHTML = '';
						this.el.appendChild(text);
						this.el.title = 'Watermark' + (w.field ? ' (prints when ' + watermarkCondition(w) + ')' : '');
					}
				}
			});

			dc.addType('no-liquid', {
				isComponent: function (el) { return el.tagName === 'SPAN' && el.classList.contains('no-liquid') && !el.childNodes.length; },
				model: {
					defaults: {
						tagName: 'span', name: 'Liquid separator', classes: ['no-liquid'], components: [],
						layerable: false, selectable: false, hoverable: false, highlightable: false,
						draggable: false, droppable: false, copyable: false, removable: false, editable: false
					}
				}
			});

			// ---- Shared clause: exports {% include 'name' %} (latest published) or {% include 'name@3' %} (pinned).
			// The canvas shows the clause's content read-only; edit it by switching Kind to Clause and opening it.
			editor.Traits.addType('clause-picker', {
				createInput: function (opts) {
					const select = document.createElement('select');
					select.className = 'clause-picker';
					fillClausePicker(select, opts.component.get('clause'));
					select.addEventListener('change', function () { opts.component.set('clause', select.value); });
					return select;
				},
				onUpdate: function (opts) { fillClausePicker(opts.elInput, opts.component.get('clause')); }
			});

			function cleanClauseHtml(html) {
				const doc = new DOMParser().parseFromString('<body>' + html + '</body>', 'text/html');
				doc.body.querySelectorAll('script,iframe,object,embed,link,meta,style,base,form').forEach(function (n) { n.remove(); });
				doc.body.querySelectorAll('*').forEach(function (n) {
					Array.from(n.attributes).forEach(function (a) {
						if (/^on/i.test(a.name) || (/^(href|src|xlink:href|action)$/i.test(a.name) && /^\s*javascript:/i.test(a.value))) n.removeAttribute(a.name);
					});
				});
				return doc.body.innerHTML;
			}

			dc.addType('clause', {
				isComponent: function (el) {
					if (el.tagName !== 'DIV' || !el.classList.contains('clause') || !el.hasAttribute('data-clause')) return false;
					return { type: 'clause', clause: el.getAttribute('data-clause') || '', pinned: el.getAttribute('data-version') || '', components: [] };
				},
				model: {
					defaults: {
						tagName: 'div', name: 'Clause', classes: ['clause'], droppable: false, editable: false,
						clause: '', pinned: '', components: [],
						traits: [
							{ type: 'clause-picker', name: 'clause', label: 'Clause', changeProp: true },
							{ type: 'text', name: 'pinned', label: 'Pin version', placeholder: 'Latest published', changeProp: true }
						]
					},
					init: function () {
						if (!this.get('clause')) {
							const first = clauseList.find(function (c) { return c.publishedVersion; });
							if (first) this.set('clause', first.name, { silent: true });
						}
						this.syncAttributes();
						this.on('change:clause change:pinned', function () { this.syncAttributes(); countChange(); });
					},
					pinnedVersion: function () {
						const v = String(this.get('pinned') || '').trim();
						return /^[1-9]\d{0,8}$/.test(v) ? v : '';
					},
					reference: function () {
						const name = this.get('clause');
						if (!NAME_PATTERN.test(name || '')) return '';
						const v = this.pinnedVersion();
						return name + (v ? '@' + v : '');
					},
					syncAttributes: function () {
						const name = this.get('clause') || '';
						this.addAttributes({ 'data-clause': NAME_PATTERN.test(name) ? name : '', 'data-version': this.pinnedVersion() });
						if (!this.pinnedVersion()) this.removeAttributes('data-version');
					},
					getInnerHTML: function () {
						const ref = this.reference();
						return ref ? "{% include '" + ref + "' %}" : '';
					}
				},
				view: {
					init: function () {
						this.listenTo(this.model, 'change:clause change:pinned', this.paintClause);
					},
					onRender: function () { this.paintClause(); },
					paintClause: async function () {
						const el = this.el;
						const model = this.model;
						const name = model.get('clause');
						const version = model.pinnedVersion();
						const ref = model.reference();
						el.setAttribute('data-clause-ref', ref);
						if (!ref) {
							el.innerHTML = '<div class="clause-empty">Choose a published clause in the settings panel.</div>';
							return;
						}
						const content = await clauseContent(name, version);
						// Another change may have repainted it while this one loaded.
						if (model.reference() !== ref) return;
						if (content.error) {
							el.innerHTML = '';
							const missing = document.createElement('div');
							missing.className = 'clause-empty';
							missing.textContent = content.error;
							el.appendChild(missing);
							return;
						}
						el.innerHTML = cleanClauseHtml(content.html || '');
						el.setAttribute('title', 'Clause ' + name + ' v' + content.version + (version ? ' (pinned)' : ' (latest published)'));
						const doc = el.ownerDocument;
						const key = name + '@' + content.version;
						if (content.css && !doc.querySelector('style[data-clause-css="' + key + '"]')) {
							const style = doc.createElement('style');
							style.setAttribute('data-clause-css', key);
							style.textContent = content.css;
							doc.head.appendChild(style);
						}
					}
				}
			});

			// Searchable grid of the curated Material Symbols.
			editor.Traits.addType('icon-picker', {
				createInput: function (opts) {
					const component = opts.component;
					const el = document.createElement('div');
					el.className = 'icon-picker';
					const search = document.createElement('input');
					search.type = 'search';
					search.placeholder = 'Search icons';
					const grid = document.createElement('div');
					grid.className = 'icon-grid';
					function paint() {
						const q = search.value.trim().toLowerCase().replace(/\s+/g, '_');
						grid.innerHTML = '';
						Object.keys(icons).filter(function (n) { return !q || n.indexOf(q) >= 0; }).forEach(function (name) {
							const b = document.createElement('button');
							b.type = 'button';
							b.title = name.replace(/_/g, ' ');
							b.setAttribute('data-icon', name);
							b.className = name === component.get('icon') ? 'on' : '';
							b.innerHTML = iconSvg(icons[name]);
							b.addEventListener('click', function () { component.set('icon', name); });
							grid.appendChild(b);
						});
					}
					search.addEventListener('input', paint);
					el.appendChild(search);
					el.appendChild(grid);
					paint();
					return el;
				},
				onUpdate: function (opts) {
					const current = opts.component.get('icon');
					opts.elInput.querySelectorAll('.icon-grid button').forEach(function (b) {
						b.className = b.getAttribute('data-icon') === current ? 'on' : '';
					});
				}
			});

			// Icon: inline SVG (currentColor), so it prints crisp and needs no icon font in the PDF.
			dc.addType('icon', {
				isComponent: function (el) {
					if (!el.classList || !el.classList.contains('moe-icon')) return false;
					const p = el.querySelector('path');
					return { type: 'icon', icon: el.getAttribute('data-icon') || '', iconPath: p ? p.getAttribute('d') : '', components: [] };
				},
				model: {
					defaults: {
						name: 'Icon',
						tagName: 'span',
						classes: ['moe-icon'],
						droppable: false,
						editable: false,
						icon: 'info',
						iconPath: '',
						size: '',
						tone: '',
						traits: [
							{ type: 'icon-picker', name: 'icon', label: 'Icon', changeProp: true },
							{ type: 'select', name: 'size', label: 'Size', changeProp: true, options: ICON_SIZES },
							{ type: 'select', name: 'tone', label: 'Colour', changeProp: true, options: ICON_TONES }
						]
					},
					init: function () {
						if (!this.get('iconPath')) this.set('iconPath', icons[this.get('icon')] || '');
						this.on('change:icon', function () { this.set('iconPath', icons[this.get('icon')] || ''); });
						this.on('change:icon change:size change:tone', this.syncIcon);
						this.on('change:icon change:size change:tone', countChange);
						this.syncIcon();
					},
					syncIcon: function () {
						this.addAttributes({ 'data-icon': this.get('icon') });
						setVariantClass(this, 'moe-icon--', ICON_SIZES, this.get('size'));
						setVariantClass(this, 'moe-icon--', ICON_TONES, this.get('tone'));
					},
					getInnerHTML: function () { return iconSvg(this.get('iconPath')); }
				},
				view: {
					init: function () { this.listenTo(this.model, 'change:iconPath', this.paintIcon); },
					onRender: function () { this.paintIcon(); },
					paintIcon: function () { this.el.innerHTML = iconSvg(this.model.get('iconPath')); }
				}
			});

			// ---- Barcode / QR code, Chart and Signature: drawn by the server (inline SVG), previewed on the canvas with the
			// test data through /api/expressions/preview, the same code as the PDF.
			function visualView() {
				return {
					init: function () {
						this.listenTo(this.model, 'change', function (model) {
							if (Object.keys(model.changedAttributes() || {}).some(function (k) { return VISUAL_PROPS.indexOf(k) >= 0; })) this.paintField();
						});
					},
					renderChildren: function () { this.paintField(); },
					paintField: function () {
						const view = this;
						const el = view.el;
						if (!el) return;
						const liquid = view.model.previewLiquid();
						if (!liquid) {
							el.innerHTML = '<span class="visual-empty">' + escapeHtml(view.model.get('name')) + ': choose what to show in Settings</span>';
							return;
						}
						const ticket = (view.paintTicket || 0) + 1;
						view.paintTicket = ticket;
						visualPreview(liquid).then(function (html) {
							if (view.paintTicket !== ticket || !view.el) return;
							view.el.innerHTML = cleanClauseHtml(html) || '<span class="visual-empty">' + escapeHtml(view.model.get('name')) + ': no value in the test data</span>';
						});
					}
				};
			}

			// path-select: a field from the model (optionally only some kinds), or none.
			editor.Traits.addType('path-select', {
				createInput: function (opts) {
					const select = document.createElement('select');
					select.className = 'path-select';
					select.addEventListener('change', function (e) {
						e.stopPropagation();
						opts.component.set(opts.trait.get('name'), select.value);
					});
					return select;
				},
				onUpdate: function (opts) {
					const select = opts.elInput;
					const kinds = opts.trait.get('kinds');
					const value = opts.component.get(opts.trait.get('name')) || '';
					select.innerHTML = '';
					const inLists = {};
					schema.collections.forEach(function (c) { c.fields.forEach(function (f) { inLists[f] = true; }); });
					const paths = schema.fields
						.filter(function (f) { return !inLists[f] && (kinds ? kinds.indexOf(schema.kinds[f] || 'text') >= 0 : schema.kinds[f] !== 'image'); });
					if (value && paths.indexOf(value) < 0) paths.push(value);
					[{ value: '', label: opts.trait.get('none') || '(none)' }].concat(paths.map(function (p) { return { value: p, label: labelFor(p) + ' (' + p + ')' }; })).forEach(function (o) {
						const option = document.createElement('option');
						option.value = o.value;
						option.textContent = o.label;
						select.appendChild(option);
					});
					select.value = value;
				},
				onEvent: function () { }
			});

			// chart-field: a field of the chart's list items (relative to the item).
			editor.Traits.addType('list-select', {
				createInput: function (opts) {
					const select = document.createElement('select');
					select.className = 'list-select';
					select.addEventListener('change', function (e) {
						e.stopPropagation();
						opts.component.set(opts.trait.get('name'), select.value);
					});
					return select;
				},
				onUpdate: function (opts) {
					const select = opts.elInput;
					const value = opts.component.get(opts.trait.get('name')) || '';
					const lists = schema.collections.map(function (c) { return c.path; });
					if (value && lists.indexOf(value) < 0) lists.push(value);
					select.innerHTML = '';
					lists.forEach(function (p) {
						const option = document.createElement('option');
						option.value = p;
						option.textContent = labelFor(p) + ' (' + p + ')';
						select.appendChild(option);
					});
					select.value = value;
				},
				onEvent: function () { }
			});

			editor.Traits.addType('chart-field', {
				createInput: function (opts) {
					const select = document.createElement('select');
					select.className = 'chart-field';
					select.addEventListener('change', function (e) {
						e.stopPropagation();
						opts.component.set(opts.trait.get('name'), select.value);
					});
					return select;
				},
				onUpdate: function (opts) {
					const select = opts.elInput;
					const numeric = !!opts.trait.get('numeric');
					const value = opts.component.get(opts.trait.get('name')) || '';
					const fields = chartItemFields(opts.component.get('list'), numeric);
					if (value && fields.indexOf(value) < 0) fields.push(value);
					select.innerHTML = '';
					fields.forEach(function (f) {
						const option = document.createElement('option');
						option.value = f;
						option.textContent = labelFor(f) + ' (' + f + ')';
						select.appendChild(option);
					});
					select.value = value;
				},
				onEvent: function () { }
			});

			dc.addType('barcode', {
				model: {
					defaults: {
						tagName: 'span', name: 'Barcode', classes: ['doc-barcode', 'doc-barcode-2d'], droppable: false, editable: false, components: [],
						field: schema.fields[0] || '', kind: 'qr', size: 1, height: 'm', showText: true,
						traits: [
							{ type: 'binding', name: 'field', label: 'Value', changeProp: true, hint: 'Select a property in the Model panel to rebind.' },
							{ type: 'select', name: 'kind', label: 'Kind', changeProp: true, options: BARCODE_KINDS },
							{ type: 'number', name: 'size', label: 'Width (inches)', changeProp: true, min: 0.5, max: 7.5, step: 0.25 },
							{ type: 'select', name: 'height', label: 'Bar height', changeProp: true, options: BARCODE_HEIGHTS },
							{ type: 'checkbox', name: 'showText', label: 'Print the value under the bars', changeProp: true }
						]
					},
					init: function () {
						this.on('change:field change:kind change:size change:height change:showText', function () { this.syncBarcode(); countChange(); });
						this.syncBarcode();
					},
					syncBarcode: function () {
						const linear = isLinearBarcode(this.get('kind'));
						const height = BARCODE_HEIGHTS.some(function (h) { return h.id === this.get('height'); }, this) ? this.get('height') : 'm';
						this.setClass(['doc-barcode', linear ? 'doc-barcode-1d' : 'doc-barcode-2d'].concat(linear ? ['doc-barcode-h-' + height] : []));
						const size = Math.min(7.5, Math.max(0.5, Number(this.get('size')) || 1));
						this.setStyle({ width: size + 'in' });
					},
					barcodeKind: function () {
						return BARCODE_KINDS.some(function (k) { return k.id === this.get('kind'); }, this) ? this.get('kind') : 'qr';
					},
					previewLiquid: function () {
						const field = this.get('field');
						return FIELD_PATH.test(field || '') ? '{{ ' + field + ' | barcode: "' + this.barcodeKind() + '" }}' : '';
					},
					getInnerHTML: function () {
						const field = this.get('field');
						if (!FIELD_PATH.test(field || '')) return '';
						return '{{ ' + field + ' | barcode: "' + this.barcodeKind() + '" }}' +
							(this.get('showText') && isLinearBarcode(this.barcodeKind()) ? '<span class="doc-barcode-text">{{ ' + field + ' }}</span>' : '');
					},
					toHTML: function (opts) {
						const field = this.get('field');
						const html = dc.getType('default').model.prototype.toHTML.call(this, opts);
						return FIELD_PATH.test(field || '') ? '{% if ' + field + ' != blank %}' + html + '{% endif %}' : html;
					}
				},
				view: visualView()
			});

			dc.addType('chart', {
				model: {
					defaults: {
						tagName: 'div', name: 'Chart', classes: ['doc-chart'], droppable: false, editable: false, components: [],
						list: '', label: '', value: '',
						chartType: 'column', format: '', title: '',
						traits: [
							{ type: 'list-select', name: 'list', label: 'List', changeProp: true },
							{ type: 'chart-field', name: 'label', label: 'Labels', changeProp: true },
							{ type: 'chart-field', name: 'value', label: 'Values', changeProp: true, numeric: true },
							{ type: 'select', name: 'chartType', label: 'Chart', changeProp: true, options: CHART_TYPES },
							{ type: 'select', name: 'format', label: 'Value format', changeProp: true, options: FORMATS.filter(function (f) { return ['', 'currency', 'dollars', 'percent', 'number', 'decimal'].indexOf(f.id) >= 0; }) },
							{ type: 'text', name: 'title', label: 'Title', changeProp: true, placeholder: 'e.g. Losses by year' }
						]
					},
					init: function () {
						// a new chart (from the block) takes the first list with numbers in the current model
						// ('list', not 'collection': that name belongs to Backbone)
						if (!this.get('list')) {
							const first = schema.collections.find(function (c) { return chartItemFields(c.path, true).length; }) || schema.collections[0];
							if (first) {
								this.set({ list: first.path, label: chartItemFields(first.path, false)[0] || '', value: chartItemFields(first.path, true)[0] || '' }, { silent: true });
							}
						}
						this.on('change:list', function () {
							// a new list: its own fields
							const labels = chartItemFields(this.get('list'), false);
							const values = chartItemFields(this.get('list'), true);
							if (labels.indexOf(this.get('label')) < 0) this.set('label', labels[0] || '');
							if (values.indexOf(this.get('value')) < 0) this.set('value', values[0] || '');
							const traits = this.get('traits');
							['label', 'value'].forEach(function (name) { const t = traits.where({ name: name })[0]; if (t) t.trigger('change:value'); });
							editor.trigger('component:toggled');
						});
						this.on('change:list change:label change:value change:chartType change:format change:title', countChange);
					},
					chartLiquid: function () {
						const list = this.get('list');
						const label = this.get('label');
						const value = this.get('value');
						if (!FIELD_PATH.test(list || '') || !FIELD_PATH.test(label || '') || !FIELD_PATH.test(value || '')) return '';
						const type = CHART_TYPES.some(function (t) { return t.id === this.get('chartType'); }, this) ? this.get('chartType') : 'column';
						const format = ['currency', 'dollars', 'percent', 'number', 'decimal'].indexOf(this.get('format')) >= 0 ? this.get('format') : '';
						return '{{ ' + list + ' | chart: "' + type + '", "' + label + '", "' + value + '"' + (format ? ', "' + format + '"' : '') + ' }}';
					},
					titleHtml: function () {
						const title = String(this.get('title') || '').trim().slice(0, 120);
						return title ? '<div class="doc-chart-title">' + escapeText(title) + '</div>' : '';
					},
					previewLiquid: function () {
						const liquid = this.chartLiquid();
						return liquid ? this.titleHtml() + liquid : '';
					},
					getInnerHTML: function () { return this.titleHtml() + this.chartLiquid(); }
				},
				view: visualView()
			});

			dc.addType('signature-block', {
				model: {
					defaults: {
						tagName: 'div', name: 'Signature', classes: ['doc-signature'], droppable: false, editable: false, components: [],
						label: 'Authorized Representative', nameField: '', titleField: '', dateMode: 'blank', dateField: '', imageField: '', anchor: '\\s1\\',
						traits: [
							{ type: 'text', name: 'label', label: 'Signer role', changeProp: true, placeholder: 'e.g. Insured' },
							{ type: 'path-select', name: 'nameField', label: 'Name', changeProp: true },
							{ type: 'path-select', name: 'titleField', label: 'Title', changeProp: true },
							{ type: 'select', name: 'dateMode', label: 'Date', changeProp: true, options: SIGNATURE_DATES },
							{ type: 'path-select', name: 'dateField', label: 'Date field', changeProp: true, kinds: ['date'] },
							{ type: 'path-select', name: 'imageField', label: 'Signature image', changeProp: true, kinds: ['image'], none: '(none: sign on the line)' },
							{ type: 'text', name: 'anchor', label: 'E-signature anchor', changeProp: true, placeholder: 'e.g. \\s1\\ (blank for none)' }
						]
					},
					init: function () {
						this.on('change:label change:nameField change:titleField change:dateMode change:dateField change:imageField change:anchor', countChange);
					},
					parts: function () {
						const path = function (p) { return FIELD_PATH.test(p || '') ? p : ''; };
						return {
							label: String(this.get('label') || '').trim().slice(0, 60),
							name: path(this.get('nameField')),
							title: path(this.get('titleField')),
							dateMode: SIGNATURE_DATES.some(function (d) { return d.id === this.get('dateMode'); }, this) ? this.get('dateMode') : 'blank',
							date: path(this.get('dateField')),
							image: path(this.get('imageField')),
							anchor: String(this.get('anchor') || '').trim().slice(0, 30)
						};
					},
					previewLiquid: function () { return this.getInnerHTML(); },
					getInnerHTML: function () {
						const p = this.parts();
						let html = '';
						if (p.image) html += '{% if ' + p.image + ' != blank %}<img class="doc-signature-image" src="{{ ' + p.image + ' }}" alt="Signature">{% endif %}';
						html += '<div class="doc-signature-line">' + (p.anchor ? '<span class="doc-esign-anchor">' + escapeText(p.anchor) + '</span>' : '') + '</div>';
						if (p.name) html += '<div class="doc-signature-name">{{ ' + p.name + ' }}</div>';
						if (p.title) html += '<div class="doc-signature-title">{{ ' + p.title + ' }}</div>';
						if (p.label) html += '<div class="doc-signature-label">' + escapeText(p.label) + '</div>';
						if (p.dateMode === 'today') html += '<div class="doc-signature-date">Date: ' + DATE_LIQUID + '</div>';
						else if (p.dateMode === 'field' && p.date) html += '<div class="doc-signature-date">Date: {{ ' + p.date + ' | shortdate }}</div>';
						else if (p.dateMode !== 'none') html += '<div class="doc-signature-date">Date: ____________________</div>';
						return html;
					}
				},
				view: visualView()
			});

			const blocks = editor.Blocks;
			blocks.add('doc-header', { label: 'Document Header', category: 'Brand', content: docHeader('Document title') });
			blocks.add('brand-logo', { label: 'MOE Logo', category: 'Brand', content: { type: 'brand-logo' } });
			blocks.add('card', { label: 'Card', category: 'Brand', content: card('Card title', [{ type: 'text', tagName: 'p', content: 'Card content' }]) });
			blocks.add('callout', { label: 'Callout', category: 'Brand', content: { tagName: 'div', classes: ['moe-callout'], components: [{ type: 'text', tagName: 'div', content: 'Callout text' }] } });
			blocks.add('leadin', { label: 'Lead-in', category: 'Brand', content: { type: 'text', tagName: 'div', classes: ['moe-leadin'], content: 'LEAD-IN' } });

			blocks.add('heading', { label: 'Heading', category: 'Basic', content: { type: 'text', tagName: 'h2', content: 'Section heading' } });
			blocks.add('subheading', { label: 'Subheading', category: 'Basic', content: { type: 'text', tagName: 'h3', content: 'Subheading' } });
			blocks.add('text', { label: 'Text', category: 'Basic', content: { type: 'text', tagName: 'p', content: 'Type text here' } });

			// Fields come from the Model panel (drag a property onto the document).
			blocks.add('data-table', { label: 'Data Table', category: 'Data', content: dataTable(schema.collections.length ? schema.collections[0].path : '', null) });
			blocks.add('repeat', { label: 'Repeat', category: 'Data', content: { type: 'repeat', components: [{ type: 'text', tagName: 'div', content: 'Drop fields in here' }] } });
			blocks.add('show-if', { label: 'Show If', category: 'Data', content: { type: 'conditional', components: [{ type: 'text', tagName: 'div', content: 'Printed only when the condition is met' }] } });
			blocks.add('data-image', { label: 'Data Image', category: 'Data', content: { type: 'data-image' } });
			blocks.add('clause', { label: 'Clause', category: 'Data', content: { type: 'clause' } });
			blocks.add('calc-field', { label: 'Calculated Field', category: 'Data', content: { type: 'calc-field' } });
			blocks.add('barcode', { label: 'Barcode / QR Code', category: 'Data', content: { type: 'barcode' } });
			blocks.add('chart', { label: 'Chart', category: 'Data', content: { type: 'chart' } });
			blocks.add('signature', { label: 'Signature', category: 'Data', content: { type: 'signature-block' } });
			// A new calculated field opens the calculation builder straight away.
			editor.on('block:drag:stop', function (component, block) {
				if (!component || !block || block.get('id') !== 'calc-field') return;
				editor.select(component);
				openCalcBuilder(component);
			});
			// Clauses published since the page loaded (e.g. by someone else) show up in the block's default and the picker.
			editor.on('block:drag:start', function (block) { if (block && block.get('id') === 'clause') refreshClauseList(); });
			editor.on('component:selected', function (component) {
				if (!component || !component.is('clause')) return;
				refreshClauseList().then(function () {
					if (editor.getSelected() !== component) return;
					document.querySelectorAll('select.clause-picker').forEach(function (select) { fillClausePicker(select, component.get('clause')); });
				});
			});

			// ---- Flexible layout: columns you can add, resize and align. Still flexbox, so content flows and paginates in the PDF.
			const COL_COUNTS = [1, 2, 3, 4].map(function (n) { return { id: String(n), label: n + (n === 1 ? ' column' : ' columns') }; });
			const ROW_GAPS = [{ id: '', label: 'Normal' }, { id: 'none', label: 'None' }, { id: 'sm', label: 'Small' }, { id: 'lg', label: 'Large' }];
			const ROW_ALIGNS = [{ id: '', label: 'Stretch (equal height)' }, { id: 'top', label: 'Top' }, { id: 'middle', label: 'Middle' }, { id: 'bottom', label: 'Bottom' }];
			const COL_WIDTHS = [{ id: '', label: 'Auto (share space)' }, { id: '25', label: '1/4' }, { id: '33.33', label: '1/3' }, { id: '50', label: '1/2' },
				{ id: '66.67', label: '2/3' }, { id: '75', label: '3/4' }, { id: 'custom', label: 'Custom (dragged)' }];
			const WIDTH_PRESETS = COL_WIDTHS.filter(function (o) { return o.id && o.id !== 'custom'; });

			function variantOf(component, prefix, options) {
				const match = options.filter(function (o) { return o.id && component.getClasses().indexOf(prefix + o.id) >= 0; })[0];
				return match ? match.id : '';
			}

			function isCol(c) {
				return c.get('type') === 'layout-col';
			}

			function colWidthOf(c) {
				const basis = parseFloat(c.getStyle()['flex-basis']);
				if (isNaN(basis)) return '';
				const preset = WIDTH_PRESETS.filter(function (o) { return Math.abs(parseFloat(o.id) - basis) < 0.01; })[0];
				return preset ? preset.id : 'custom';
			}

			dc.addType('layout-row', {
				isComponent: function (el) { return el.tagName === 'DIV' && el.classList.contains('row'); },
				model: {
					defaults: {
						name: 'Columns',
						tagName: 'div',
						classes: ['row'],
						traits: [
							{ type: 'select', name: 'columns', label: 'Columns', changeProp: true, options: COL_COUNTS },
							{ type: 'select', name: 'gap', label: 'Gap', changeProp: true, options: ROW_GAPS },
							{ type: 'select', name: 'valign', label: 'Align', changeProp: true, options: ROW_ALIGNS }
						]
					},
					init: function () {
						this.set({
							columns: String(this.components().filter(isCol).length || 1),
							gap: variantOf(this, 'row--gap-', ROW_GAPS),
							valign: variantOf(this, 'row--', ROW_ALIGNS)
						}, { silent: true });
						this.on('change:columns', this.syncColumns);
						this.on('change:gap', function () { setVariantClass(this, 'row--gap-', ROW_GAPS, this.get('gap')); });
						this.on('change:valign', function () { setVariantClass(this, 'row--', ROW_ALIGNS, this.get('valign')); });
						this.listenTo(this.components(), 'add remove reset', function () {
							this.set('columns', String(this.components().filter(isCol).length || 1));
						});
					},
					syncColumns: function () {
						const want = Math.max(1, Math.min(4, parseInt(this.get('columns'), 10) || 1));
						const cols = this.components().filter(isCol);
						for (let i = cols.length; i < want; i++) this.append({ type: 'layout-col' });
						// Removing a column keeps its content: it moves into the last remaining column.
						for (let i = cols.length - 1; i >= want; i--) {
							const keep = cols[want - 1];
							cols[i].components().models.slice().forEach(function (child) { child.move(keep, {}); });
							cols[i].remove();
						}
					}
				}
			});

			dc.addType('layout-col', {
				isComponent: function (el) { return el.tagName === 'DIV' && el.classList.contains('col'); },
				model: {
					defaults: {
						name: 'Column',
						tagName: 'div',
						classes: ['col'],
						draggable: '.row',
						// Drag the right edge to set the width (snaps to 1/4, 1/3, 1/2, 2/3, 3/4 when close).
						resizable: { tl: 0, tc: 0, tr: 0, cl: 0, cr: 1, bl: 0, bc: 0, br: 0, keyWidth: 'flex-basis', unitWidth: '%', currentUnit: 0, minDim: 40 },
						traits: [{ type: 'select', name: 'colWidth', label: 'Width', changeProp: true, options: COL_WIDTHS }]
					},
					init: function () {
						this.on('change:colWidth', function (model, value, opts) {
							if ((opts && opts.fromResize) || value === 'custom') return;
							const style = Object.assign({}, this.getStyle());
							delete style['flex-basis'];
							delete style['flex-grow'];
							if (value) {
								style['flex-basis'] = value + '%';
								style['flex-grow'] = '0';
							}
							this.setStyle(style);
						});
					}
				}
			});

			// Width styles live in CSS rules that load after the components, so read them once the canvas is loaded.
			editor.__syncColWidths = function () {
				editor.getWrapper().find('.col').filter(isCol).forEach(function (c) {
					c.set('colWidth', colWidthOf(c), { silent: true });
				});
			};

			editor.on('component:resize', function (e) {
				const c = e.component;
				if (!c || !isCol(c)) return;
				if (e.type === 'start') {
					c.addStyle({ 'flex-grow': '0' });
				} else if (e.type === 'end') {
					const basis = parseFloat(c.getStyle()['flex-basis']);
					if (isNaN(basis)) return;
					const near = WIDTH_PRESETS.filter(function (o) { return Math.abs(parseFloat(o.id) - basis) <= 3; })[0];
					c.addStyle({ 'flex-basis': (near ? near.id : String(Math.round(basis))) + '%' });
					c.set('colWidth', colWidthOf(c), { fromResize: true });
				}
			});

			// Material toolbar on the selected element: select parent, drag, move up/down, duplicate, delete.
			function toolbarIcon(name) {
				return uiIcons[name] ? iconSvg(uiIcons[name]) : name;
			}
			dc.getType('default').model.prototype.initToolbar = function () {
				if (this.get('toolbar') || !this.em) return;
				const tb = [];
				if (this.collection) {
					tb.push({ label: toolbarIcon('north_west'), attributes: { title: 'Select parent' },
						command: function (ed) { ed.runCommand('core:component-exit', { force: 1 }); } });
				}
				if (this.get('draggable')) {
					tb.push({ label: toolbarIcon('open_with'), attributes: { class: 'gjs-no-touch-actions', draggable: true, title: 'Drag to move' }, command: 'tlb-move' });
					// Order is meaningless for free-positioned (legacy form) elements.
					if (this.collection && this.get('dmode') !== 'absolute') {
						tb.push({ label: toolbarIcon('arrow_upward'), attributes: { title: 'Move up (Alt+\u2191)' }, command: 'doc:move-up' });
						tb.push({ label: toolbarIcon('arrow_downward'), attributes: { title: 'Move down (Alt+\u2193)' }, command: 'doc:move-down' });
					}
				}
				if (this.get('copyable')) tb.push({ label: toolbarIcon('content_copy'), attributes: { title: 'Duplicate' }, command: 'tlb-clone' });
				if (this.get('removable')) tb.push({ label: toolbarIcon('delete'), attributes: { title: 'Delete' }, command: 'tlb-delete' });
				this.set('toolbar', tb);
			};

			function moveSelected(delta) {
				const c = editor.getSelected();
				const parent = c && c.parent();
				if (!parent || !c.get('draggable')) return;
				const target = c.index() + delta;
				if (target < 0 || target >= parent.components().length) return;
				// move() takes the index before removal.
				c.move(parent, { at: delta > 0 ? target + 1 : target });
				editor.select(c);
			}
			editor.Commands.add('doc:move-up', function () { moveSelected(-1); });
			editor.Commands.add('doc:move-down', function () { moveSelected(1); });
			editor.Keymaps.add('doc:move-up', 'alt+up', 'doc:move-up', { prevent: true });
			editor.Keymaps.add('doc:move-down', 'alt+down', 'doc:move-down', { prevent: true });

			// ---- Legacy forms: Documaker forms converted by fact-pdf-tools ("demo emit-html"). Each page is a fixed Letter
			// sheet with every element at its original position, so on these pages (only) elements move freely.
			const LEGACY_SHAPES = { rule: 'Line', box: 'Box', shade: 'Shading', bullet: 'Bullet' };
			const legacyItem = { dmode: 'absolute', draggable: '.form-page', droppable: false, copyable: true };

			dc.addType('legacy-page', {
				isComponent: function (el) { return el.tagName === 'SECTION' && el.classList.contains('form-page'); },
				model: { defaults: { name: 'Form page', tagName: 'section', classes: ['form-page'], droppable: '.abs, .rule, .box, .shade, .bullet, .img' } }
			});

			dc.addType('legacy-text', {
				extend: 'text',
				isComponent: function (el) {
					return el.tagName === 'SPAN' && el.classList.contains('abs') && !el.classList.contains('field');
				},
				model: { defaults: Object.assign({ name: 'Form text', tagName: 'span', classes: ['abs'] }, legacyItem) }
			});

			// A fill-in field of the legacy form. Unmapped until a model property is chosen; then it prints {{ path | format }}.
			dc.addType('legacy-field', {
				isComponent: function (el) {
					if (el.tagName !== 'SPAN' || !el.classList.contains('field')) return false;
					return { type: 'legacy-field', legacyName: el.getAttribute('data-legacy-field') || '', check: el.classList.contains('field-check'),
						formLabel: el.getAttribute('data-label') || '', components: [] };
				},
				model: {
					defaults: Object.assign({
						name: 'Form field', tagName: 'span', classes: ['abs', 'field'], editable: false, resizable: true,
						legacyName: '', field: '', format: '', check: false, checkedWhen: '', formLabel: '',
						traits: [
							{ type: 'binding', name: 'field', label: 'Field', changeProp: true, hint: 'Select a property in the Model panel to map this form field.' },
							{ type: 'format', name: 'format', label: 'Format', changeProp: true },
							{ type: 'binding', name: 'legacyName', label: 'Documaker field', changeProp: true, hint: 'Name in the legacy form.' }
						]
					}, legacyItem),
					init: function () {
						// The printed label (from an imported PDF) is kept as a property for Suggest mappings, not in the template.
						if (this.getAttributes()['data-label'] !== undefined) this.removeAttributes('data-label');
						// A check box (from a fillable PDF) prints an X instead of the value: when the value means yes, or equals
						// "Checked when" (e.g. Corporation, for one box of a group).
						if (this.get('check')) {
							this.set('name', 'Check box', { silent: true });
							this.set('traits', [
								{ type: 'binding', name: 'field', label: 'Field', changeProp: true, hint: 'Select a property in the Model panel to map this check box.' },
								{ type: 'text', name: 'checkedWhen', label: 'Checked when', changeProp: true, placeholder: 'Yes / true' },
								{ type: 'binding', name: 'legacyName', label: 'Form field', changeProp: true, hint: 'Name in the original form.' }
							]);
						}
						this.on('change:field change:format change:checkedWhen', this.syncExpression);
						this.on('change:field change:format change:checkedWhen', countChange);
						this.syncExpression();
					},
					expression: function () {
						const path = this.get('field');
						if (!path) return '';
						return this.get('check') ? checkExpression(path, this.get('checkedWhen')) : liquidExpression(path, this.get('format'));
					},
					syncExpression: function () {
						this.addAttributes({ 'data-legacy-field': this.get('legacyName') });
						this.components(this.expression());
					},
					getInnerHTML: function () {
						return this.expression();
					}
				},
				view: fieldView(function (model) { return model.get('check') ? describeCheck(model) : describeField(model); })
			});

			// A GhostDraft text box on a form page: positioned like the sheet's text, holds flowing paragraphs/tables.
			dc.addType('form-textbox', {
				isComponent: function (el) {
					return el.tagName === 'DIV' && el.classList.contains('abs') && el.classList.contains('gd-tb');
				},
				model: {
					defaults: Object.assign({}, legacyItem, {
						name: 'Text box', tagName: 'div', classes: ['abs', 'gd-doc', 'gd-tb'], droppable: true,
						resizable: { tl: 0, tc: 0, tr: 0, cl: 0, cr: 1, bl: 0, bc: 0, br: 0 }
					})
				}
			});

			dc.addType('legacy-shape', {
				isComponent: function (el) {
					return el.tagName === 'DIV' && !!el.parentElement && el.parentElement.classList.contains('form-page') &&
						Object.keys(LEGACY_SHAPES).some(function (c) { return el.classList.contains(c); });
				},
				model: {
					defaults: Object.assign({ name: 'Line', tagName: 'div', resizable: true }, legacyItem),
					init: function () {
						const classes = this.getClasses();
						const kind = Object.keys(LEGACY_SHAPES).filter(function (c) { return classes.indexOf(c) >= 0; })[0];
						if (kind) this.set('name', LEGACY_SHAPES[kind], { silent: true });
					}
				}
			});

			dc.addType('legacy-image', {
				extend: 'image',
				isComponent: function (el) { return el.tagName === 'IMG' && el.classList.contains('img'); },
				model: { defaults: Object.assign({ name: 'Form image', classes: ['img'], activate: false, editable: false, resizable: { ratioDefault: true } }, legacyItem) }
			});

			function formItem(type, style, extra) {
				return Object.assign({ type: type, style: Object.assign({ position: 'absolute', left: '64px' }, style) }, extra);
			}
			blocks.add('form-page', { label: 'Blank Form Page', category: 'Form Page', content: { type: 'legacy-page' } });
			blocks.add('form-text', { label: 'Form Text', category: 'Form Page', content: formItem('legacy-text', { top: '64px', 'font-size': '10pt' }, { content: 'Text' }) });
			blocks.add('form-field', { label: 'Form Field', category: 'Form Page', content: formItem('legacy-field', { top: '96px', width: '200px', height: '16px' }, { legacyName: 'NEW FIELD' }) });
			blocks.add('form-line', { label: 'Form Line', category: 'Form Page', content: formItem('legacy-shape', { top: '128px', width: '200px', height: '1px' }, { classes: ['rule'] }) });
			blocks.add('form-box', { label: 'Form Box', category: 'Form Page', content: formItem('legacy-shape', { top: '160px', width: '200px', height: '80px', 'border-width': '1px' }, { classes: ['box'] }) });

			// Arrow keys nudge free-positioned elements 1px (Shift: 10px). GrapesJS skips keymaps while text is being edited.
			// Converted GhostDraft sheets position in pt, so the unit is kept.
			function shifted(value, px) {
				const m = /^(-?[\d.]+)(px|pt)?$/.exec(String(value || '0').trim());
				if (!m) return px + 'px';
				const n = parseFloat(m[1]);
				return m[2] === 'pt' ? +(n + px * 0.75).toFixed(2) + 'pt' : Math.round(n + px) + 'px';
			}
			function nudge(dx, dy) {
				return function (ed, sender, opts) {
					const c = ed.getSelected();
					if (!c || c.get('dmode') !== 'absolute') return;
					if (opts && opts.event) opts.event.preventDefault();
					const style = c.getStyle();
					c.addStyle({ left: shifted(style.left, dx), top: shifted(style.top, dy) });
				};
			}
			[['left', -1, 0], ['right', 1, 0], ['up', 0, -1], ['down', 0, 1]].forEach(function (k) {
				editor.Keymaps.add('doc:nudge-' + k[0], k[0], nudge(k[1], k[2]));
				editor.Keymaps.add('doc:nudge-' + k[0] + '-10', 'shift+' + k[0], nudge(k[1] * 10, k[2] * 10));
			});

			// Dragging a free-positioned element freezes its size in px (and a height that stops text boxes growing):
			// keep only the new left/top.
			editor.on('component:drag:start', function (e) {
				const c = e && e.target;
				if (c && c.get('dmode') === 'absolute') c.__preDragStyle = Object.assign({}, c.getStyle());
			});
			editor.on('component:drag:end', function (e) {
				const c = e && e.target;
				const pre = c && c.__preDragStyle;
				if (!pre) return;
				delete c.__preDragStyle;
				const style = Object.assign({}, c.getStyle());
				['width', 'height', 'position'].forEach(function (k) {
					if (pre[k] === undefined) delete style[k];
					else style[k] = pre[k];
				});
				c.setStyle(style);
			});

			// ---- Plain tables (Table block and converted GhostDraft tables): border presets, cell shading and
			// alignment as classes (moe-document.css), and row/column/merge tools. Data Tables manage their own columns.
			function presetTrait(name, label, options) {
				return { type: 'select', name: name, label: label, changeProp: true, options: options };
			}
			function bindPreset(model, name, prefix, options) {
				model.set(name, variantOf(model, prefix, options), { silent: true });
				model.on('change:' + name, function () { setVariantClass(model, prefix, options, model.get(name)); });
			}
			dc.addType('table', {
				model: {
					// Drag the right edge to set the width (% of the space it sits in).
					defaults: { traits: [presetTrait('borders', 'Borders', TABLE_BORDERS)],
						resizable: { tl: 0, tc: 0, tr: 0, cl: 0, cr: 1, bl: 0, bc: 0, br: 0, keyWidth: 'width', unitWidth: '%', currentUnit: 0, keepAutoHeight: true, minDim: 40 } },
					init: function () { bindPreset(this, 'borders', 'tbl-b-', TABLE_BORDERS); }
				}
			});
			dc.addType('cell', {
				model: {
					defaults: { traits: [
						presetTrait('cellBorders', 'Borders', CELL_BORDERS),
						presetTrait('shade', 'Shading', CELL_SHADES),
						presetTrait('valign', 'Vertical align', CELL_VALIGN)
					],
					// Right edge: column width (% of the table). Bottom edge: row height.
					resizable: { tl: 0, tc: 0, tr: 0, cl: 0, cr: 1, bl: 0, bc: 1, br: 0, unitWidth: '%', unitHeight: 'px', currentUnit: 0, minDim: 12 } },
					init: function () {
						bindPreset(this, 'cellBorders', 'cell-b-', CELL_BORDERS);
						bindPreset(this, 'shade', 'cell-shade-', CELL_SHADES);
						bindPreset(this, 'valign', 'cell-va-', CELL_VALIGN);
					}
				}
			});

			function cellText(text, like) {
				const para = like && like.components().filter(function (c) { return c.is('text'); })[0];
				return { type: 'text', tagName: para ? para.get('tagName') : 'p', classes: para ? para.getClasses() : [], content: text || '' };
			}
			// A new cell shaped like a neighbour (tag, classes, styles) but empty.
			function newCell(like, text) {
				const cell = { type: 'cell', tagName: 'td', components: [cellText(text, like)] };
				if (like) {
					cell.tagName = like.get('tagName');
					cell.classes = like.getClasses();
					const style = like.getStyle();
					if (Object.keys(style).length) cell.style = Object.assign({}, style);
				}
				return cell;
			}
			function plainTable(rows, cols) {
				const body = [];
				for (let r = 0; r < rows; r++) {
					const cells = [];
					for (let c = 0; c < cols; c++) cells.push(newCell(null, 'Text'));
					body.push({ type: 'row', components: cells });
				}
				return { type: 'table', classes: ['doc-table', 'tbl-b-all'], components: [{ type: 'tbody', components: body }] };
			}
			blocks.add('plain-table', { label: 'Table', category: 'Basic', content: plainTable(3, 3) });
			blocks.add('box', { label: 'Box', category: 'Basic', content: { tagName: 'div', classes: ['doc-box'], components: [
				{ type: 'text', tagName: 'p', content: 'Text in a bordered box. It flows with the text around it.' }
			] } });

			function span(cell, name) {
				return parseInt(cell.getAttributes()[name], 10) || 1;
			}
			function setSpan(cell, name, value) {
				if (value > 1) {
					const attrs = {};
					attrs[name] = String(value);
					cell.addAttributes(attrs);
				} else {
					cell.removeAttributes(name);
				}
			}
			function tableRows(table) {
				const rows = [];
				table.components().forEach(function (part) {
					if (part.get('tagName') === 'tr') rows.push(part);
					else part.components().forEach(function (r) { if (r.get('tagName') === 'tr') rows.push(r); });
				});
				return rows;
			}
			// Logical grid: map[row][col] -> { cell, r, c, rs, cs } of the cell covering that slot.
			function tableGrid(table) {
				const rows = tableRows(table);
				const map = rows.map(function () { return []; });
				const cells = [];
				rows.forEach(function (tr, r) {
					let col = 0;
					tr.components().forEach(function (cell) {
						while (map[r][col]) col++;
						const info = { cell: cell, r: r, c: col, rs: span(cell, 'rowspan'), cs: span(cell, 'colspan') };
						cells.push(info);
						for (let dr = 0; dr < info.rs && r + dr < rows.length; dr++) {
							for (let dc2 = 0; dc2 < info.cs; dc2++) map[r + dr][col + dc2] = info;
						}
						col += info.cs;
					});
				});
				const width = map.reduce(function (w, row) { return Math.max(w, row.length); }, 0);
				return { rows: rows, map: map, cells: cells, width: width };
			}
			// Index in row r's children where a cell starting at logical column c goes.
			function slotIndex(g, r, c) {
				return g.cells.filter(function (i) { return i.r === r && i.c < c; }).length;
			}
			function unique(list) {
				return list.filter(function (x, i) { return x && list.indexOf(x) === i; });
			}
			function moveContent(from, to) {
				from.components().models.slice().forEach(function (child) { child.move(to, {}); });
			}

			const TABLE_OPS = {
				rowAbove: function (g, me) { insertRow(g, me.r, false); },
				rowBelow: function (g, me) { insertRow(g, me.r + me.rs - 1, true); },
				colLeft: function (g, me) { insertCol(g, me.c, false); },
				colRight: function (g, me) { insertCol(g, me.c + me.cs - 1, true); },
				deleteRow: function (g, me) {
					if (g.rows.length < 2) return 'The table has only one row.';
					const r = me.r;
					const touching = unique(g.map[r]);
					touching.filter(function (i) { return i.r === r && i.rs > 1; }).sort(function (a, b) { return a.c - b.c; }).forEach(function (i, n) {
						setSpan(i.cell, 'rowspan', i.rs - 1);
						i.cell.move(g.rows[r + 1], { at: slotIndex(g, r + 1, i.c) + n });
					});
					touching.filter(function (i) { return i.r < r; }).forEach(function (i) { setSpan(i.cell, 'rowspan', i.rs - 1); });
					g.rows[r].remove();
				},
				deleteCol: function (g, me) {
					if (g.width < 2) return 'The table has only one column.';
					unique(g.map.map(function (row) { return row[me.c]; })).forEach(function (i) {
						if (i.cs > 1) setSpan(i.cell, 'colspan', i.cs - 1);
						else i.cell.remove();
					});
				},
				mergeRight: function (g, me) {
					const next = g.map[me.r][me.c + me.cs];
					if (!next || next.r !== me.r || next.rs !== me.rs) return 'Nothing to merge on the right (the cells must be the same height).';
					setSpan(me.cell, 'colspan', me.cs + next.cs);
					moveContent(next.cell, me.cell);
					next.cell.remove();
				},
				mergeDown: function (g, me) {
					const below = g.map[me.r + me.rs] && g.map[me.r + me.rs][me.c];
					if (!below || below.c !== me.c || below.cs !== me.cs) return 'Nothing to merge below (the cells must be the same width).';
					setSpan(me.cell, 'rowspan', me.rs + below.rs);
					moveContent(below.cell, me.cell);
					below.cell.remove();
				},
				split: function (g, me) {
					if (me.rs === 1 && me.cs === 1) return 'The cell is not merged.';
					setSpan(me.cell, 'rowspan', 1);
					setSpan(me.cell, 'colspan', 1);
					for (let dr = 0; dr < me.rs; dr++) {
						const r = me.r + dr;
						const at = dr === 0 ? me.cell.index() + 1 : slotIndex(g, r, me.c);
						const add = [];
						for (let dc2 = dr === 0 ? 1 : 0; dc2 < me.cs; dc2++) add.push(newCell(me.cell));
						if (add.length) g.rows[r].components().add(add, { at: at });
					}
				}
			};

			// New row after (or before) logical row r; cells merged across that edge grow instead.
			function insertRow(g, r, after) {
				const cells = [];
				unique(g.map[r]).sort(function (a, b) { return a.c - b.c; }).forEach(function (i) {
					const spans = after ? i.r + i.rs - 1 > r : i.r < r;
					if (spans) setSpan(i.cell, 'rowspan', i.rs + 1);
					else {
						const cell = newCell(i.cell);
						if (i.cs > 1) cell.attributes = { colspan: String(i.cs) };
						cells.push(cell);
					}
				});
				const tr = g.rows[r];
				tr.parent().components().add({ type: 'row', components: cells }, { at: tr.index() + (after ? 1 : 0) });
			}

			// New column after (or before) logical column c; cells merged across that edge grow instead.
			function insertCol(g, c, after) {
				const seen = [];
				g.rows.forEach(function (tr, r) {
					const i = g.map[r][c];
					if (!i) {
						tr.components().add(newCell(tr.components().last()));
						return;
					}
					if (seen.indexOf(i) >= 0) return;
					seen.push(i);
					const spans = after ? i.c + i.cs - 1 > c : i.c < c;
					if (spans) {
						setSpan(i.cell, 'colspan', i.cs + 1);
						return;
					}
					const cell = newCell(i.cell);
					if (i.rs > 1) cell.attributes = { rowspan: String(i.rs) };
					g.rows[i.r].components().add(cell, { at: i.cell.index() + (after ? 1 : 0) });
				});
			}

			// Column widths live on the table's first row (converted tables are fixed-layout, so later rows' widths are
			// ignored), and a column border moves width between its two columns like Word, so the table keeps its width.
			editor.on('component:resize', function (e) {
				const c = e && e.component;
				if (!c || !c.is('cell')) return;
				const table = c.closestType('table');
				if (!table || !c.getEl() || !c.getEl().parentElement) return;
				const g = tableGrid(table);
				const me = g.cells.filter(function (i) { return i.cell === c; })[0];
				if (!me || !g.map[0]) return;
				const covered = g.map[0].slice(me.c, me.c + me.cs);
				if (covered.length !== me.cs || covered.some(function (i) { return !i || i.cs !== 1; })) return;
				const last = covered[covered.length - 1];
				const next = g.map[0][me.c + me.cs];
				const hasNext = !!next && next.cs === 1;
				if (e.type === 'start') {
					c.__colStart = { cell: c.getEl().offsetWidth, last: last.cell.getEl().offsetWidth, next: hasNext ? next.cell.getEl().offsetWidth : 0 };
					return;
				}
				const start = c.__colStart;
				const width = c.getStyle().width;
				if (!start || !width) return;
				if (e.type === 'end') delete c.__colStart;
				const rowPx = c.getEl().parentElement.offsetWidth;
				const newPx = /%$/.test(width) ? parseFloat(width) / 100 * rowPx : parseFloat(width);
				let delta = Math.max(12 - start.last, newPx - start.cell);
				if (hasNext) delta = Math.min(start.next - 12, delta);
				function pt(px) { return +(px * 0.75).toFixed(2) + 'pt'; }
				if (last.cell !== c) {
					const style = Object.assign({}, c.getStyle());
					delete style.width;
					c.setStyle(style);
				}
				last.cell.addStyle({ width: pt(start.last + delta) });
				if (hasNext) next.cell.addStyle({ width: pt(start.next - delta) });
			});

			// The cell the table tools act on: the selected cell, or the cell around the selection (not in Data Tables).
			function toolCell(component) {
				const cell = component && (component.is('cell') ? component : component.closestType('cell'));
				if (!cell || cell.closestType('data-table')) return null;
				const table = cell.closestType('table');
				return table ? cell : null;
			}
			editor.__tableCell = toolCell;
			editor.Commands.add('doc:table', function (ed, sender, opts) {
				const cell = toolCell(ed.getSelected());
				const op = TABLE_OPS[opts && opts.op];
				if (!cell || !op) return;
				const table = cell.closestType('table');
				const g = tableGrid(table);
				const me = g.cells.filter(function (i) { return i.cell === cell; })[0];
				const problem = me ? op(g, me) : 'Cell not found.';
				if (problem) {
					ed.trigger('doc:notice', problem);
					return;
				}
				countChange();
				const alive = tableRows(table).some(function (tr) { return tr.components().indexOf(cell) >= 0; });
				ed.select(alive ? cell : table);
			});

			function layoutRow(widths) {
				return { type: 'layout-row', components: widths.map(function (w) {
					return w ? { type: 'layout-col', style: { 'flex-basis': w + '%', 'flex-grow': '0' } } : { type: 'layout-col' };
				}) };
			}

			blocks.add('columns', { label: '2 Columns', category: 'Layout', content: layoutRow(['', '']) });
			blocks.add('columns-3', { label: '3 Columns', category: 'Layout', content: layoutRow(['', '', '']) });
			blocks.add('columns-1-2', { label: '1/3 + 2/3', category: 'Layout', content: layoutRow(['33.33', '']) });
			blocks.add('columns-2-1', { label: '2/3 + 1/3', category: 'Layout', content: layoutRow(['', '33.33']) });
			blocks.add('divider', { label: 'Divider', category: 'Layout', content: { tagName: 'hr' } });
			blocks.add('page-break', { label: 'Page Break', category: 'Layout', content: { type: 'page-break' } });

			// Material 3 shapes and elevation, in MOE colours and Figtree.
			function mdCard(variant) {
				return { tagName: 'div', classes: ['md-card', 'md-card--' + variant], components: [
					{ type: 'text', tagName: 'div', classes: ['md-card-title'], content: 'Card title' },
					{ type: 'text', tagName: 'p', content: 'Card content' }
				] };
			}
			blocks.add('md-icon', { label: 'Icon', category: 'Material', content: { type: 'icon', icon: 'info' } });
			blocks.add('md-icon-text', { label: 'Icon + Text', category: 'Material', content: { tagName: 'div', classes: ['md-list-item'], components: [
				{ type: 'icon', icon: 'check_circle' },
				{ type: 'text', tagName: 'div', classes: ['md-list-item-text'], content: 'Text next to an icon' }
			] } });
			blocks.add('md-banner', { label: 'Banner', category: 'Material', content: { tagName: 'div', classes: ['md-banner'], components: [
				{ type: 'icon', icon: 'info', tone: 'aqua' },
				{ type: 'text', tagName: 'div', classes: ['md-list-item-text'], content: 'Banner message' }
			] } });
			blocks.add('md-card-elevated', { label: 'Elevated Card', category: 'Material', content: mdCard('elevated') });
			blocks.add('md-card-outlined', { label: 'Outlined Card', category: 'Material', content: mdCard('outlined') });
			blocks.add('md-card-filled', { label: 'Filled Card', category: 'Material', content: mdCard('filled') });

			// Material Symbol on each block tile.
			const BLOCK_ICONS = {
				'doc-header': 'view_agenda', 'brand-logo': 'image', 'card': 'crop_landscape', 'callout': 'campaign', 'leadin': 'short_text',
				'heading': 'title', 'subheading': 'text_fields', 'text': 'notes', 'plain-table': 'table', 'box': 'check_box_outline_blank',
				'data-table': 'table', 'repeat': 'repeat', 'show-if': 'filter_alt', 'data-image': 'image',
				'columns': 'view_column', 'columns-3': 'view_week', 'columns-1-2': 'view_sidebar', 'columns-2-1': 'vertical_split',
				'divider': 'horizontal_rule', 'page-break': 'insert_page_break',
				'md-icon': 'star', 'md-icon-text': 'format_list_bulleted', 'md-banner': 'info',
				'md-card-elevated': 'rectangle', 'md-card-outlined': 'check_box_outline_blank', 'md-card-filled': 'crop_landscape',
				'form-page': 'description', 'form-text': 'text_fields', 'form-field': 'tag', 'form-line': 'horizontal_rule', 'form-box': 'check_box_outline_blank'
			};
			Object.keys(BLOCK_ICONS).forEach(function (id) {
				const block = blocks.get(id);
				if (block && uiIcons[BLOCK_ICONS[id]]) block.set('media', iconSvg(uiIcons[BLOCK_ICONS[id]]));
			});

			function headerCell(path) {
				return { type: 'cell', tagName: 'th', classes: schema.kinds[path] === 'number' ? ['num'] : [],
					components: [{ type: 'text', tagName: 'span', content: labelFor(path) }] };
			}

			function bodyCell(path) {
				return { type: 'cell', tagName: 'td', classes: schema.kinds[path] === 'number' ? ['num'] : [],
					components: [field(path, defaultFormat(schema, path))] };
			}

			// columns: [{ path, label?, format? }] or null for the first three fields of the collection.
			function dataTable(collectionPath, columns) {
				const match = collectionFor(collectionPath);
				const cols = columns || (match ? match.fields.slice(0, 3) : []).map(function (p) { return { path: p }; });
				return {
					type: 'data-table',
					listPath: collectionPath,
					components: [
						{ type: 'thead', components: [{ type: 'row', components: cols.map(function (c) {
							const cell = headerCell(c.path);
							if (c.label) cell.components[0].content = c.label;
							return cell;
						}) }] },
						{ type: 'tbody', components: [{ type: 'repeat-row', components: cols.map(function (c) {
							const cell = bodyCell(c.path);
							if (c.format !== undefined) cell.components[0].format = c.format;
							return cell;
						}) }] }
					]
				};
			}

			// Exposed for the starter layout.
			editor.__docTable = dataTable;
		};
	}

	function field(path, format) {
		if (schema.kinds[path] === 'image') return { type: 'data-image', field: path };
		return { type: 'data-field', field: path, format: format || '' };
	}

	// New Repeat/Data Table definitions carry their list as `listPath`: GrapesJS append() treats a
	// top-level `collection` key as the component's Backbone collection and throws when dragging.
	function takeListPath(component) {
		const path = component.get('listPath');
		if (path === undefined) return;
		component.set('collection', path);
		component.unset('listPath');
	}

	function labelField(label, path, format) {
		if (schema.kinds[path] === 'image') return field(path);
		return {
			tagName: 'div', classes: ['kv'],
			components: [
				{ type: 'text', tagName: 'span', classes: ['kv-label'], content: label },
				field(path, format)
			]
		};
	}

	function col(components) {
		return { type: 'layout-col', components: components };
	}

	// Templates saved before the layout-row/layout-col types stored plain divs; give them the column tools.
	function upgradeLayout(node) {
		if (!node || typeof node !== 'object') return node;
		if (Array.isArray(node)) {
			node.forEach(upgradeLayout);
			return node;
		}
		if (!node.type && (node.tagName || 'div') === 'div' && Array.isArray(node.classes)) {
			const names = node.classes.map(function (c) { return typeof c === 'string' ? c : c && c.name; });
			if (names.indexOf('row') >= 0) node.type = 'layout-row';
			else if (names.indexOf('col') >= 0) node.type = 'layout-col';
			// GhostDraft text boxes were plain divs: make them free-moving like the rest of the sheet.
			else if (names.indexOf('gd-tb') >= 0 && names.indexOf('abs') >= 0) node.type = 'form-textbox';
		}
		Object.keys(node).forEach(function (key) { upgradeLayout(node[key]); });
		return node;
	}

	function docHeader(title) {
		return { tagName: 'div', classes: ['doc-header'], components: [
			{ type: 'brand-logo' },
			{ type: 'text', tagName: 'h1', content: title }
		] };
	}

	function card(title, components) {
		return { tagName: 'div', classes: ['moe-card', 'moe-card--accent'], components: [
			{ tagName: 'div', classes: ['moe-card-title'], components: [{ type: 'text', tagName: 'span', content: title }] }
		].concat(components) };
	}

	// Canvas-only styling (not exported): make Liquid constructs visible while designing.
	const CANVAS_CSS =
		'[data-repeat]:not(tr){outline:1px dashed #7b61ff;outline-offset:2px;position:relative;padding-top:14px;min-height:36px;}' +
		'[data-repeat]:not(tr)::before{content:"Repeat: " attr(data-repeat);position:absolute;top:0;left:2px;font:10px sans-serif;color:#7b61ff;}' +
		'tr[data-repeat]{outline:2px dashed #7b61ff;outline-offset:-2px;}' +
		'tr[data-repeat] td:first-child{box-shadow:inset 18px 0 0 -14px #7b61ff;}' +
		// Bound fields keep the document's font so they take the space the printed value will: a tint and underline
		// mark them; names are italic; the full expression is the tooltip.
		'.df{background:rgba(79,70,229,.08);color:#3730a3;border-radius:2px;box-shadow:inset 0 -1px 0 rgba(79,70,229,.5);}' +
		'.df[data-dfmode=name]{font-style:italic;white-space:nowrap;}' +
		'.df[data-dfmode=fallback]{font-style:italic;color:#6b7280;}' +
		'.df[data-full]{position:relative;}' +
		'.df[data-full]:hover::after,.form-page .field[data-full]:hover::after{content:attr(data-full);position:absolute;left:-2px;top:-1px;padding:0 2px;white-space:nowrap;background:#fff;color:#3730a3;font:inherit;font-style:italic;box-shadow:0 1px 4px rgba(0,0,0,.35);border-radius:2px;z-index:20;pointer-events:none;}' +
		'.form-page .field[data-full]:hover{overflow:visible;z-index:20;}' +
		'.df[data-dfmode=liquid]{font-family:Consolas,monospace;font-size:8pt;overflow-wrap:anywhere;}' +
		// Plain tables without printed borders: faint guides so the cells can be found.
		'.tbl-b-none td,.tbl-b-none th,.tbl-b-outside td,.tbl-b-outside th,.tbl-b-rows td,.tbl-b-bottom td{outline:1px dotted #C1C9BF;outline-offset:-1px;}' +
		'td>p:empty,th>p:empty{min-height:1.2em;}' +
		'.page-break{border-top:2px dashed #e11d48;margin:10px 0;position:relative;height:0;}' +
		'.page-break::after{content:"page break";position:absolute;right:0;top:-14px;font:10px sans-serif;color:#e11d48;}' +
		// Shared clause: read-only preview of the included clause, labelled with its name.
		'[data-comments]{outline:2px solid #f59e0b !important;outline-offset:2px;}' +
		'.clause{outline:2px dashed #0e7490;outline-offset:2px;position:relative;min-height:20px;}' +
		'.clause *{pointer-events:none;}' +
		'.clause::before{content:"Clause: " attr(data-clause-ref);position:absolute;top:-14px;right:0;font:10px sans-serif;color:#0e7490;}' +
		'.clause-empty{font:italic 11px sans-serif;color:#6b7280;padding:4px;}' +
		'.binding-bad{outline:2px solid #d9342b !important;background:#fde8e7 !important;color:#9b1c1c !important;}' +
		'.moe-icon svg{pointer-events:none;}' +
		'.row>.col{outline:1px dashed #C1C9BF;outline-offset:-1px;}' +
		'.row>.col:empty{min-height:56px;}' +
		'.row>.col:empty::before{content:"Drop content here";display:block;padding:18px 0;text-align:center;font:10px sans-serif;color:#9EA6BE;}' +
		'[data-show-if]{outline:1px dashed #d97706;outline-offset:2px;position:relative;padding-top:14px;min-height:36px;}' +
		'[data-cond-style]{outline:1px dashed #7c3aed;outline-offset:1px;}' +
		'.visual-empty{display:inline-block;padding:6px 8px;border:1px dashed #9EA6BE;font:11px sans-serif;color:#6b7280;}' +
		'.doc-barcode:empty,.doc-chart:empty,.doc-signature:empty{min-height:24px;outline:1px dashed #9EA6BE;}' +
		'[data-show-if]::before{content:"Show if: " attr(data-show-if);position:absolute;top:0;left:2px;font:10px sans-serif;color:#b45309;}' +
		'[data-empty]:not(table)::after{content:"If empty: " attr(data-empty);display:block;font:italic 9pt sans-serif;color:#6b7280;margin-top:4px;}' +
		'table[data-empty]::after{content:"If empty: " attr(data-empty);display:table-caption;caption-side:bottom;text-align:left;font:italic 9pt sans-serif;color:#6b7280;padding-top:2px;}' +
		// Legacy form pages: sheets on a grey desk; fields tinted, unmapped ones show their Documaker name.
		'body:has(>.form-page){background:#f3f4f6;}' +
		'body>:not(.form-page){max-width:720px;margin-left:auto;margin-right:auto;}' +
		'.form-page{margin:16px auto 24px;box-shadow:0 1px 3px rgba(0,0,0,.25),0 6px 16px rgba(0,0,0,.08);}' +
		'.form-page .field{background:rgba(0,120,138,.10);outline:1px dashed rgba(0,120,138,.6);outline-offset:-1px;color:#00606E;text-overflow:ellipsis;}' +
		'.form-page .field[data-dfmode=name]{font-style:italic;}' +
		'.form-page .field[data-dfmode=liquid],.form-page .field:empty::before{font-family:Consolas,monospace !important;font-size:7pt !important;line-height:1.3 !important;}' +
		'.form-page .gd-tb:hover{outline:1px dashed #9EA6BE;}' +
		'.form-page .field:empty::before{content:attr(data-legacy-field);color:#b45309;}' +
		// Converted GhostDraft forms: inline conditions are tinted runs; conditions and repeats around positioned
		// boxes on a form page take no space and mark the boxes instead.
		'span[data-show-if],span[data-choice]{padding:0;min-height:0;outline:none;position:static;background:rgba(217,119,6,.10);border-bottom:1px dashed #d97706;}' +
		'span[data-show-if]::before{display:none;}' +
		'[data-choice]:not(span){outline:1px dotted #b45309;outline-offset:4px;}' +
		'.form-page [data-show-if],.form-page [data-choice],.form-page [data-repeat]:not(tr){position:static;padding:0;min-height:0;outline:none;background:none;border:0;}' +
		'.form-page [data-show-if]::before,.form-page [data-repeat]:not(tr)::before{display:none;}' +
		'.form-page [data-show-if]>.abs{outline:1px dashed #d97706;outline-offset:1px;}' +
		'.form-page .gd-tb [data-repeat]:not(tr){outline:1px dashed #7b61ff;}';

	// ---------------------------------------------------------------------------------------------
	// Editor
	// ---------------------------------------------------------------------------------------------
	// The data model is an example message payload. It is saved with each template version and used as preview data.
	const defaultModel = await (await fetch('/api/sample-data')).json();
	let modelData = defaultModel;
	let modelDirty = false;
	// Test-data scenarios of the open template (saved per template name, not per version); '' = the model's example data.
	let scenarios = [];
	let activeScenario = '';

	function sampleData() {
		if (externalRecord) return externalRecord.data;
		const scenario = activeScenario && scenarios.find(function (s) { return s.name === activeScenario; });
		return scenario ? scenario.data : modelData;
	}
	// Mutated in place when the model changes: the plugin's component types hold a reference to this object.
	const schema = buildSchema(modelData);
	const icons = await (await fetch('/lib/material-symbols/icons.json')).json();
	const uiIcons = await (await fetch('/lib/material-symbols/ui-icons.json')).json();

	function uiIcon(name) {
		return uiIcons[name] ? iconSvg(uiIcons[name]) : '';
	}

	document.querySelectorAll('.ui-icon[data-icon]').forEach(function (el) {
		el.innerHTML = uiIcon(el.getAttribute('data-icon'));
	});

	// Right panel tabs: Settings | Styles | Layers.
	const tabs = Array.prototype.slice.call(document.querySelectorAll('.md-tab'));
	function selectTab(id) {
		tabs.forEach(function (tab) {
			const on = tab.getAttribute('data-tab') === id;
			tab.setAttribute('aria-selected', on ? 'true' : 'false');
			document.getElementById(tab.getAttribute('data-tab')).hidden = !on;
		});
	}
	tabs.forEach(function (tab) {
		tab.addEventListener('click', function () { selectTab(tab.getAttribute('data-tab')); });
	});

	// Overflow menu (Show Liquid, Render Published, Discard Draft).
	const moreBtn = document.getElementById('btnMore');
	const moreMenu = document.getElementById('moreMenu');
	function closeMenu() {
		moreMenu.hidden = true;
		moreBtn.setAttribute('aria-expanded', 'false');
	}
	moreBtn.addEventListener('click', function (e) {
		e.stopPropagation();
		moreMenu.hidden = !moreMenu.hidden;
		moreBtn.setAttribute('aria-expanded', moreMenu.hidden ? 'false' : 'true');
	});
	moreMenu.addEventListener('click', function (e) {
		if (e.target.closest('button')) closeMenu();
	});
	document.addEventListener('click', function (e) {
		if (!moreMenu.hidden && !moreMenu.contains(e.target)) closeMenu();
	});
	document.addEventListener('keydown', function (e) {
		if (e.key === 'Escape' && !moreMenu.hidden) closeMenu();
	});

	const editor = grapesjs.init({
		container: '#gjs',
		height: '100%',
		fromElement: false,
		// Saving is explicit (Save Draft). Autosave must be off or GrapesJS clears the dirty count after every edit.
		storageManager: { type: '', autosave: false, autoload: false },
		panels: { defaults: [] },
		blockManager: { appendTo: '#blocks' },
		traitManager: { appendTo: '#traits' },
		selectorManager: { appendTo: '#selectors', componentFirst: true },
		styleManager: { appendTo: '#styles' },
		layerManager: { appendTo: '#layers' },
		// 7.5in printable width (Letter minus 0.5in margins) at 96dpi. Legacy form pages are the full 8.5in sheet.
		// widthMedia '': devices only size the canvas. Otherwise GrapesJS scopes new styles to
		// @media (max-width: <device width>) and they vanish on the other device.
		deviceManager: { devices: [
			{ id: 'letter', name: 'Letter', width: '720px', widthMedia: '' },
			{ id: 'form', name: 'Form page', width: '856px', widthMedia: '' }
		] },
		// Same brand stylesheet (and self-hosted Figtree) the server prepends to every PDF, so the canvas matches the output.
		// fonts.css: fonts extracted from imported legacy forms.
		canvas: { styles: ['/brand/fonts/figtree.css', '/brand/moe-document.css', '/api/legacy/fonts.css'] },
		canvasCss: CANVAS_CSS,
		plugins: [documentPlugin(schema, icons, uiIcons, sampleValue)]
	});

	// Blocks of features that are switched off.
	Object.keys(FEATURE_BLOCKS).forEach(function (feature) {
		if (featureOn(feature)) return;
		FEATURE_BLOCKS[feature].forEach(function (id) { if (editor.Blocks.get(id)) editor.Blocks.remove(id); });
	});

	// Settings tab shows the template's details until something is selected.
	const settingsEmpty = document.querySelector('#tabSettings .pane-empty');
	const detailsEl = document.getElementById('templateDetails');
	editor.on('component:toggled', function () {
		const selected = editor.getSelected();
		settingsEmpty.hidden = !!selected;
		detailsEl.hidden = !!selected && !selected.is('wrapper');
		renderTableTools();
	});
	editor.on('doc:notice', function (message) { setStatus(message, true); });

	// Row / column / merge tools while a cell of a plain or converted table (or something in one) is selected.
	const tableToolsEl = document.getElementById('tableTools');
	const TABLE_TOOL_GROUPS = [
		{ label: 'Row', tools: [['rowAbove', 'Insert above'], ['rowBelow', 'Insert below'], ['deleteRow', 'Delete']] },
		{ label: 'Column', tools: [['colLeft', 'Insert left'], ['colRight', 'Insert right'], ['deleteCol', 'Delete']] },
		{ label: 'Cells', tools: [['mergeRight', 'Merge right'], ['mergeDown', 'Merge down'], ['split', 'Split']] }
	];
	function renderTableTools() {
		const cell = editor.__tableCell(editor.getSelected());
		tableToolsEl.hidden = !cell;
		if (!cell || tableToolsEl.childNodes.length) return;
		const head = document.createElement('div');
		head.className = 'table-tools-head';
		head.textContent = 'Table';
		tableToolsEl.appendChild(head);
		TABLE_TOOL_GROUPS.forEach(function (group) {
			const row = document.createElement('div');
			row.className = 'table-tools-row';
			const label = document.createElement('span');
			label.textContent = group.label;
			row.appendChild(label);
			group.tools.forEach(function (tool) {
				const btn = document.createElement('button');
				btn.type = 'button';
				btn.className = 'md-btn text';
				btn.textContent = tool[1];
				btn.addEventListener('click', function () { editor.runCommand('doc:table', { op: tool[0] }); });
				row.appendChild(btn);
			});
			tableToolsEl.appendChild(row);
		});
		const hint = document.createElement('small');
		hint.textContent = 'Borders, shading and alignment: select the cell (or the table) and use the settings below.';
		tableToolsEl.appendChild(hint);
	}

	// Canvas display of data fields: sample values, field names or Liquid. The template is the same in every mode.
	function paintFields() {
		const wrapper = editor.getWrapper();
		if (!wrapper) return;
		['data-field', 'total-field', 'legacy-field', 'calc-field', 'barcode', 'chart', 'signature-block'].forEach(function (type) {
			wrapper.findType(type).forEach(function (c) { if (c.view && c.view.paintField) c.view.paintField(); });
		});
		// conditional styles follow the test data too
		wrapper.find('*').forEach(function (c) { if (c.get('condStyles') && c.view) paintCondStyle(c.view); });
	}
	const fieldModeSelect = document.getElementById('fieldMode');
	fieldModeSelect.value = fieldDisplay.mode;
	fieldModeSelect.addEventListener('change', function () {
		fieldDisplay.mode = fieldModeSelect.value;
		try { localStorage.setItem('designer.fieldMode', fieldDisplay.mode); } catch (e) { /* not remembered */ }
		paintFields();
	});

	// Brand typeface only (guide: Figtree). It is embedded in every PDF, so it prints the same on any server.
	editor.onReady(function () {
		const fontFamily = editor.StyleManager.getProperty('typography', 'font-family');
		if (fontFamily) {
			fontFamily.set('default', 'var(--moe-font-sans)');
			fontFamily.setOptions([{ id: 'var(--moe-font-sans)', label: 'Figtree (brand)' }]);
		}
	});

	function starterComponents() {
		const table = editor.__docTable;
		return [
			docHeader('Underwriting Workbench Results'),
			{ type: 'layout-row', components: [
				col([labelField('Policy:', 'policy.number'), labelField('Insured:', 'policy.insuredName')]),
				col([labelField('Effective:', 'policy.effectiveDate', 'shortdate'), labelField('Expiration:', 'policy.expirationDate', 'shortdate')])
			] },
			{ type: 'text', tagName: 'h2', content: 'Policy Highlights' },
			{ type: 'repeat', collection: 'highlights', emptyText: 'No highlights for this policy.', classes: ['chips'], components: [
				{ tagName: 'div', classes: ['chip'], components: [field('highlight')] }
			] },
			{ type: 'text', tagName: 'h2', content: 'Three Year Loss Activity' },
			{ tagName: 'div', classes: ['moe-callout'], components: [labelField('3 year loss ratio:', 'lossRatio.threeYearLossRatio', 'percent')] },
			Object.assign(table('lossRatio.claims', [
				{ path: 'claim.claimNumber', label: 'Claim #' },
				{ path: 'claim.dol', label: 'Date of Loss' },
				{ path: 'claim.lossDescription', label: 'Description' },
				{ path: 'claim.totalLoss', label: 'Total Loss' }
			]), { emptyText: 'No losses reported in the last three years.', totals: true }),
			{ type: 'text', tagName: 'h2', content: 'Locations' },
			{ type: 'repeat', collection: 'locations', components: [
				{ tagName: 'div', classes: ['moe-card', 'moe-card--accent'], components: [
					{ tagName: 'div', classes: ['moe-card-title'], components: [
						{ type: 'text', tagName: 'span', content: 'Location ' }, field('location.locNum'),
						{ type: 'text', tagName: 'span', content: ' \u2013 ' }, field('location.address'),
						{ type: 'text', tagName: 'span', content: ' \u2013 ' }, field('location.riskLevel', 'upcase')
					] },
					{ type: 'layout-row', components: [
						col([field('location.photo'), labelField('Imagery captured:', 'location.photoDate', 'shortdate')]),
						col([
							labelField('County:', 'location.county'),
							labelField('Fire score:', 'location.fireScore'),
							labelField('Hail score:', 'location.hailScore'),
							labelField('Total insured value:', 'location.totalInsuredValue', 'currency'),
							labelField('Worst roof:', 'location.propertySummary.roofConditionWorst'),
							labelField('Structures:', 'location.propertySummary.structureCount')
						])
					] },
					{ type: 'text', tagName: 'h3', content: 'Total Insured Value' },
					table('location.tivs', [
						{ path: 'tiv.insLine', label: 'Line' },
						{ path: 'tiv.tiv', label: 'TIV' }
					]),
					labelField('Location notes:', 'location.notes'),
					{ type: 'conditional', field: 'location.propertySummary.status', operator: 'ne', value: 'ok', components: [
						{ tagName: 'div', classes: ['moe-callout'], components: [field('location.propertySummary.statusMessage')] }
					] },
					{ type: 'conditional', field: 'location.propertySummary.status', operator: 'eq', value: 'ok', components: [
						{ type: 'text', tagName: 'h3', content: 'Location Summary' },
						{ type: 'layout-row', components: [
							col([
								labelField('Matched address:', 'location.propertySummary.matchedAddress'),
								labelField('Parcel ID:', 'location.propertySummary.parcelId'),
								labelField('Captured:', 'location.propertySummary.capturedDate', 'shortdate'),
								labelField('Structures found:', 'location.propertySummary.structureCount'),
								labelField('Roof condition \u2013 worst:', 'location.propertySummary.roofConditionWorst'),
								labelField('Roof condition \u2013 weighted:', 'location.propertySummary.roofConditionWeighted'),
								labelField('Property area (sq ft):', 'location.propertySummary.propertyArea', 'number'),
								labelField('Pools:', 'location.propertySummary.poolCount')
							]),
							col([
								labelField('Pool area (sq ft):', 'location.propertySummary.poolArea', 'number'),
								labelField('Parking \u2013 paved area (sq ft):', 'location.propertySummary.parkingPavedArea', 'number'),
								labelField('Parking \u2013 deterioration:', 'location.propertySummary.parkingDeterioration', 'percent'),
								labelField('Parking \u2013 illumination:', 'location.propertySummary.parkingIllumination', 'percent'),
								labelField('Lot debris (sq ft):', 'location.propertySummary.lotDebrisArea', 'number'),
								labelField('Lot debris coverage:', 'location.propertySummary.lotDebrisCoverage', 'percent')
							])
						] },
						{ type: 'conditional', field: 'location.propertySummary.wildfire.riskLevel', operator: 'present', components: [
							{ tagName: 'div', classes: ['moe-callout'], components: [
								{ type: 'text', tagName: 'span', classes: ['kv-label'], content: 'Wildfire:' },
								field('location.propertySummary.wildfire.riskLevel'),
								{ type: 'text', tagName: 'span', content: ' (score ' }, field('location.propertySummary.wildfire.riskScore'),
								{ type: 'text', tagName: 'span', content: ') \u2013 ' }, field('location.propertySummary.wildfire.distanceToWildland', 'number'),
								{ type: 'text', tagName: 'span', content: ' ft to wildland, ' }, field('location.propertySummary.wildfire.structuresExposed'),
								{ type: 'text', tagName: 'span', content: ' structure(s) exposed' }
							] }
						] },
						{ type: 'layout-row', components: [
							col([field('location.propertySummary.poolPhoto'), { type: 'text', tagName: 'div', content: 'Pools' }]),
							col([field('location.propertySummary.parkingPhoto'), { type: 'text', tagName: 'div', content: 'Parking' }]),
							col([field('location.propertySummary.lotDebrisPhoto'), { type: 'text', tagName: 'div', content: 'Lot debris' }])
						] }
					] },
					{ type: 'text', tagName: 'h3', content: 'Structures' },
					{ type: 'repeat', collection: 'location.buildings', emptyText: 'No structures returned for this location.', components: [
						{ tagName: 'div', classes: ['moe-card'], components: [
							{ tagName: 'div', classes: ['moe-card-title'], components: [
								field('building.buildingNum'), { type: 'text', tagName: 'span', content: ' \u00b7 ' },
								field('building.footprint', 'number'), { type: 'text', tagName: 'span', content: ' sq ft' }
							] },
							{ type: 'layout-row', components: [
								col([field('building.photo'), labelField('Imagery:', 'building.photoDate', 'shortdate')]),
								col([
									labelField('Year built:', 'building.yearBuilt'),
									labelField('Property use:', 'building.propertyUse'),
									labelField('Construction:', 'building.construction'),
									labelField('Exterior:', 'building.exterior'),
									labelField('Foundation:', 'building.foundation'),
									labelField('Stories:', 'building.stories'),
									labelField('Roof condition:', 'building.roofCondition'),
									labelField('Roof confidence:', 'building.roofConditionConfidence', 'percent'),
									labelField('Condition notes:', 'building.roofConditionReasons'),
									labelField('Roof shape:', 'building.roofShape'),
									labelField('Roof covering:', 'building.roofCovering'),
									labelField('Tree overhang:', 'building.treeOverhang'),
									labelField('Solar panels:', 'building.solarPanels'),
									labelField('Skylights:', 'building.skylights'),
									labelField('HVAC units:', 'building.hvacUnits')
								])
							] },
							{ type: 'conditional', field: 'building.wildfire.riskLevel', operator: 'present', components: [
								{ type: 'text', tagName: 'h4', content: 'Wildfire' },
								{ type: 'layout-row', components: [
									col([
										labelField('Risk level:', 'building.wildfire.riskLevel'),
										labelField('Risk score:', 'building.wildfire.riskScore'),
										labelField('Nearest structure (ft):', 'building.wildfire.nearestStructureDistance', 'number'),
										labelField('Ember-resistant roof:', 'building.wildfire.emberResistantRoof')
									]),
									col([
										labelField('Vegetation 0\u20135 ft:', 'building.wildfire.vegetation0to5ft', 'percent'),
										labelField('Vegetation 5\u201330 ft:', 'building.wildfire.vegetation5to30ft', 'percent'),
										labelField('Vegetation 30\u2013100 ft:', 'building.wildfire.vegetation30to100ft', 'percent')
									])
								] }
							] },
							{ type: 'conditional', field: 'building.wildfire.riskLevel', operator: 'blank', components: [
								{ type: 'text', tagName: 'div', classes: ['kv'], content: 'Wildfire: not assessed for this structure.' }
							] }
						] }
					] }
				] }
			] }
		];
	}

	async function loadStarter() {
		applyModel(defaultModel);
		editor.setComponents(starterComponents());
		editor.setStyle('');
		setDetails(null);
		await afterLoad();
	}

	// ---------------------------------------------------------------------------------------------
	// Template details (Settings tab with nothing selected): title, form code, edition, type, category. Saved with each
	// version; the server checks them (TemplateDetails).
	// ---------------------------------------------------------------------------------------------
	let templateDetails = {};
	let detailsDirty = false;
	const DETAIL_INPUTS = { title: 'tdTitle', formCode: 'tdFormCode', edition: 'tdEdition', type: 'tdType', category: 'tdCategory' };
	const detailOptions = await fetch('/api/template-details').then(function (r) { return r.ok ? r.json() : {}; }).catch(function () { return {}; });
	[['tdType', detailOptions.types], ['tdCategory', detailOptions.categories]].forEach(function (pair) {
		const select = document.getElementById(pair[0]);
		(pair[1] || []).forEach(function (value) {
			const option = document.createElement('option');
			option.value = value;
			option.textContent = value;
			select.appendChild(option);
		});
	});

	function setDetails(details) {
		templateDetails = {};
		Object.keys(DETAIL_INPUTS).forEach(function (key) {
			if (details && typeof details[key] === 'string' && details[key].trim()) templateDetails[key] = details[key].trim();
		});
		detailsDirty = false;
		showDetails();
	}

	function showDetails() {
		document.getElementById('tdName').value = nameInput.value;
		Object.keys(DETAIL_INPUTS).forEach(function (key) {
			document.getElementById(DETAIL_INPUTS[key]).value = templateDetails[key] || '';
		});
		document.getElementById('tdError').textContent = detailsProblem();
	}

	// Same rules as the server, so mistakes show while typing.
	function detailsProblem() {
		const edition = templateDetails.edition;
		if (edition && !/^(0[1-9]|1[0-2])[/ ][0-9]{2}$/.test(edition)) return 'Edition: month and year, MM/YY (e.g. 10/26).';
		const code = templateDetails.formCode;
		if (code && !/^[A-Za-z0-9][A-Za-z0-9 .-]*$/.test(code)) return 'Form code: letters, numbers, spaces, "-" and "." (e.g. DA 00 93).';
		return '';
	}

	Object.keys(DETAIL_INPUTS).forEach(function (key) {
		const input = document.getElementById(DETAIL_INPUTS[key]);
		input.addEventListener(input.tagName === 'SELECT' ? 'change' : 'input', function () {
			const value = input.value.trim();
			if (value) templateDetails[key] = value;
			else delete templateDetails[key];
			detailsDirty = true;
			document.getElementById('tdError').textContent = detailsProblem();
			updateBadge();
		});
	});

	// ---------------------------------------------------------------------------------------------
	// Versions: Draft -> Published -> Retired
	// ---------------------------------------------------------------------------------------------
	let current = null;   // { version, status } of the version open in the canvas; null = new, never saved
	let versions = [];    // version list for the current template name

	function isDirty() {
		return modelDirty || detailsDirty || editor.getDirtyCount() > 0;
	}

	function markClean() {
		modelDirty = false;
		detailsDirty = false;
		editor.clearDirtyCount();
		updateBadge();
	}

	// GrapesJS counts load-time changes on a timer, so wait for them before treating the canvas as clean.
	async function afterLoad() {
		await new Promise(function (resolve) { setTimeout(resolve, 50); });
		syncLanguageMarker();
		editor.__syncColWidths();
		fitDevice();
		applyCanvasTheme();
		markClean();
		validateBindings();
		markComments();
	}

	// Legacy form pages need the full sheet width; a page setup sets the canvas to its page's text width.
	function fitDevice() {
		// No wrapper while loadProjectData builds the page; afterLoad fits it again.
		const wrapper = editor.getWrapper();
		if (!wrapper) return;
		if (wrapper.findType('legacy-page').length) {
			editor.setDevice('form');
			return;
		}
		const setup = wrapper.findType('page-setup')[0];
		if (!setup) {
			editor.setDevice('letter');
			return;
		}
		const s = normalizeSetup(setup.get('setup'));
		const width = Math.round((pageSizeInches(s).width - s.margins.left - s.margins.right) * 96) + 'px';
		const device = editor.Devices.get('page');
		if (device) device.set('width', width);
		else editor.Devices.add({ id: 'page', name: 'Page setup', width: width, widthMedia: '' });
		editor.setDevice('page');
	}
	editor.on('component:add', function (component) {
		if (component.is('legacy-page') || component.is('page-setup')) fitDevice();
	});
	editor.on('component:remove', function (component) {
		if (component.is('page-setup')) setTimeout(fitDevice);
		if (component.is('theme-ref')) setTimeout(applyCanvasTheme);
	});
	editor.on('component:add', function (component) {
		if (component.is('theme-ref')) applyCanvasTheme();
	});

	// The template's theme on the canvas: its stylesheet (token overrides + fonts) after the brand stylesheet.
	let themeCssVersion = Date.now();
	function applyCanvasTheme() {
		const doc = editor.Canvas.getDocument();
		const wrapper = editor.getWrapper();
		if (!doc || !doc.head || !wrapper) return;
		const ref = wrapper.findType('theme-ref')[0];
		const name = ref ? ref.get('theme') : '';
		let link = doc.getElementById('docThemeCss');
		if (!name) {
			if (link) link.remove();
			return;
		}
		if (!link) {
			link = doc.createElement('link');
			link.id = 'docThemeCss';
			link.rel = 'stylesheet';
			doc.head.appendChild(link);
		}
		const href = '/api/themes/' + encodeURIComponent(name) + '/theme.css?v=' + themeCssVersion;
		if (link.getAttribute('href') !== href) link.setAttribute('href', href);
	}

	function updateBadge() {
		const dirty = isDirty();
		if (!current) {
			badge.textContent = 'New';
			badge.className = 'badge' + (dirty ? ' dirty' : '');
		} else {
			badge.textContent = 'v' + current.version + ' ' + current.status + (docLang() ? ' \u00b7 ' + docLang().toUpperCase() : '');
			badge.className = 'badge ' + current.status.toLowerCase() + (dirty ? ' dirty' : '');
		}
		const latest = versions[versions.length - 1];
		document.getElementById('btnDiscard').disabled = !(latest && latest.status === 'Draft') || !hasRole('Author');
		document.getElementById('btnPublish').disabled = (!!current && current.status === 'Published' && !dirty) || !hasRole('Publisher');
		notifyHost();
	}

	function templateName() {
		const name = nameInput.value.trim();
		if (!NAME_PATTERN.test(name)) {
			throw new Error((isClause() ? 'Clause' : 'Template') + ' name may only contain letters, numbers, "-" and "_".');
		}
		return name;
	}

	function isClause() {
		return docKind.value === 'clauses';
	}

	function apiBase() {
		return '/api/' + (isClause() ? 'clauses' : 'templates') + '/' + encodeURIComponent(templateName());
	}

	async function ensureOk(response, action) {
		if (!response.ok) {
			let detail = '';
			try { detail = (await response.json()).error || ''; } catch (e) { /* no body */ }
			throw new Error(action + ' failed (' + response.status + ')' + (detail ? ': ' + detail : ''));
		}
		return response;
	}

	function formatWhen(iso) {
		return iso ? new Date(iso).toLocaleString() : '';
	}

	async function refreshVersions() {
		versions = await (await ensureOk(await fetch(apiBase() + '/versions' + langQuery()), 'List versions')).json();
		versionSelect.innerHTML = '';
		if (!versions.length) {
			const option = document.createElement('option');
			option.textContent = '(no saved versions)';
			option.value = '';
			versionSelect.appendChild(option);
		}
		versions.slice().reverse().forEach(function (v) {
			const option = document.createElement('option');
			option.value = String(v.version);
			option.textContent = 'v' + v.version + ' \u00b7 ' + v.status + ' \u00b7 ' + formatWhen(v.status === 'Published' ? v.publishedUtc : v.savedUtc);
			versionSelect.appendChild(option);
		});
		if (current) versionSelect.value = String(current.version);
		updateBadge();
		await refreshScenarios();
		await refreshComments();
		markComments();
	}

	async function openVersion(version) {
		const t = await (await ensureOk(await fetch(apiBase() + '/versions/' + version + langQuery()), 'Open')).json();
		applyModel(isObject(t.model) ? t.model : defaultModel);
		editor.loadProjectData(upgradeLayout(t.project));
		current = { version: t.version, status: t.status };
		versionSelect.value = String(t.version);
		setDetails(t.details);
		await afterLoad();
	}

	function confirmDiscardChanges() {
		return !isDirty() || window.confirm('You have unsaved changes. Discard them?');
	}

	// What Save Draft stores. A clause is a fragment placed inside templates: no <body> wrapper and no page-level base CSS.
	function exportForSave() {
		return isClause()
			? { html: editor.getWrapper().getInnerHTML(), css: editor.getCss({ avoidProtected: true }) }
			: { html: editor.getHtml(), css: editor.getCss() };
	}

	async function saveDraft() {
		const latest = versions[versions.length - 1];
		if (latest && latest.status === 'Draft' && (!current || current.version !== latest.version) &&
			!window.confirm('This will replace the existing draft v' + latest.version + ' with what is on the canvas. Continue?')) {
			return null;
		}
		const wasLocked = current && current.status !== 'Draft';
		syncLanguageMarker();
		const exported = exportForSave();
		const response = await fetch(apiBase() + '/draft' + langQuery(), {
			method: 'PUT',
			headers: { 'Content-Type': 'application/json' },
			body: JSON.stringify({ project: editor.getProjectData(), html: exported.html, css: exported.css, model: modelData, details: templateDetails })
		});
		const info = await (await ensureOk(response, 'Save')).json();
		current = { version: info.version, status: info.status };
		markClean();
		await refreshVersions();
		const saved = (docLang() ? langInfo().name + ' ' : '') + 'v' + info.version;
		const check = docLang() ? ' ' + await languageCheck() : '';
		return (wasLocked ? 'Saved as new draft ' + saved + ' (published/retired versions are read-only).' : 'Saved draft ' + saved + '.') + check;
	}

	// ---- Languages: a translation has its own versions and uses the English version's data fields -------------------
	// The canvas carries the language as a hidden marker (none for English and for clauses, which print in the language
	// of the template they are placed in).
	function syncLanguageMarker() {
		const wrapper = editor.getWrapper();
		if (!wrapper) return;
		const refs = wrapper.findType('language-ref');
		const code = isClause() ? '' : docLang();
		if (!code) {
			refs.forEach(function (r) { r.remove(); });
			return;
		}
		refs.slice(1).forEach(function (r) { r.remove(); });
		if (refs.length) refs[0].set('language', code);
		else wrapper.append({ type: 'language-ref', language: code }, { at: 0 });
	}

	async function fetchLanguages() {
		return (await ensureOk(await fetch(apiBase() + '/languages'), 'Languages')).json();
	}

	// One sentence on how the open translation's data fields compare with the English version's.
	function describeFields(entry) {
		if (entry.isEnglish) return '';
		if (!entry.latestVersion) return 'Not started.';
		const parts = [];
		if (entry.missingFields && entry.missingFields.length) parts.push('Missing ' + entry.missingFields.length + ' field(s) the English version prints: ' + entry.missingFields.join(', ') + '.');
		if (entry.extraFields && entry.extraFields.length) parts.push('Prints ' + entry.extraFields.length + ' field(s) the English version doesn\u2019t: ' + entry.extraFields.join(', ') + '.');
		if (entry.englishClauses && entry.englishClauses.length) parts.push('Clause(s) still printed in English: ' + entry.englishClauses.join(', ') + '.');
		return parts.length ? parts.join(' ') : 'Same data fields as English.';
	}

	async function languageCheck() {
		try {
			const entry = (await fetchLanguages()).find(function (l) { return l.code === docLang(); });
			return entry ? describeFields(entry) : '';
		} catch (err) {
			return '';
		}
	}

	// Switching language opens that language's newest version; a language with no versions yet keeps the canvas (to be
	// translated) and Save Draft creates its v1.
	let openLang = '';
	async function switchLanguage(code) {
		const previous = openLang;
		languageSelect.value = code;
		const list = await (await ensureOk(await fetch(apiBase() + '/versions' + langQuery()), 'List versions')).json();
		if (list.length && !confirmDiscardChanges()) {
			languageSelect.value = previous;
			return 'Language not changed.';
		}
		openLang = code;
		current = null;
		clauseCache = {};
		await refreshVersions();
		if (versions.length) {
			await openVersion(versions[versions.length - 1].version);
			return 'Opened ' + langInfo().name + ' v' + current.version + ' (' + current.status + ').';
		}
		syncLanguageMarker();
		await repaintClauses();
		paintFields();
		updateBadge();
		return code
			? 'No ' + langInfo().name + ' version yet: the canvas keeps the current text. Translate it and Save Draft to create ' + langInfo().name +
				' v1; data fields stay bound to the same data.'
			: 'No English version yet. Save Draft to create v1.';
	}

	function resetLanguage() {
		languageSelect.value = '';
		openLang = '';
		clauseCache = {};
	}

	languageSelect.addEventListener('change', function () {
		const code = docLang();
		run('Switching language', function () { return switchLanguage(code); });
	});

	document.getElementById('btnLanguages').addEventListener('click', function () {
		run('Loading languages', openLanguages);
	});

	async function openLanguages() {
		const entries = await fetchLanguages();
		document.querySelectorAll('.languages').forEach(function (el) { el.remove(); });
		const wrap = document.createElement('div');
		wrap.className = 'languages';
		const help = document.createElement('p');
		help.className = 'model-help';
		help.textContent = 'Each language has its own versions and is published on its own. Translations use the English version\u2019s data fields; a document asked for in a language with nothing published prints in English.';
		wrap.appendChild(help);
		const table = document.createElement('table');
		table.className = 'usage-table languages-table';
		const head = table.createTHead().insertRow();
		['Language', 'Newest', 'Published', 'Data fields', ''].forEach(function (h) {
			const th = document.createElement('th');
			th.textContent = h;
			head.appendChild(th);
		});
		const body = table.createTBody();
		entries.forEach(function (e) {
			const code = e.isEnglish ? '' : e.code;
			const row = body.insertRow();
			row.className = 'language-row';
			row.setAttribute('data-language', e.code);
			row.insertCell().textContent = e.name + (code === docLang() ? ' (open)' : '');
			row.insertCell().textContent = e.latestVersion ? 'v' + e.latestVersion : '\u2014';
			row.insertCell().textContent = e.publishedVersion ? 'v' + e.publishedVersion : '\u2014';
			const fields = row.insertCell();
			fields.className = 'language-fields';
			fields.textContent = e.isEnglish ? 'The data fields translations use.' : describeFields(e);
			const action = row.insertCell();
			if (code !== docLang()) {
				const open = document.createElement('button');
				open.type = 'button';
				open.className = 'md-btn text language-open';
				open.textContent = e.latestVersion ? 'Open' : 'Start';
				open.title = e.latestVersion ? 'Open the newest ' + e.name + ' version' : 'Translate the canvas into ' + e.name;
				open.addEventListener('click', function () {
					editor.Modal.close();
					run('Switching language', function () { return switchLanguage(code); });
				});
				action.appendChild(open);
			}
		});
		wrap.appendChild(table);
		editor.Modal.open({ title: 'Languages of ' + templateName(), content: wrap });
		editor.Modal.onceClose(function () { wrap.remove(); });
		return entries.filter(function (e) { return e.latestVersion; }).length + ' language(s) with versions.';
	}

	async function run(label, action) {
		try {
			setStatus(label + '...');
			const message = await action();
			setStatus(message || label + ' done.');
		} catch (err) {
			setStatus(err.message, true);
		}
	}

	async function showPdf(response, title) {
		await ensureOk(response, 'Render');
		const url = URL.createObjectURL(await response.blob());
		const wrap = document.createElement('div');
		const links = document.createElement('div');
		links.className = 'pdf-links';
		// Fallback for browsers without an inline PDF viewer (e.g. the VS Code integrated browser).
		const open = document.createElement('a');
		open.href = url;
		open.target = '_blank';
		open.rel = 'noopener';
		open.textContent = 'Open in new tab';
		const download = document.createElement('a');
		download.href = url;
		download.download = (NAME_PATTERN.test(nameInput.value.trim()) ? nameInput.value.trim() : 'document') + '.pdf';
		download.textContent = 'Download';
		links.appendChild(open);
		links.appendChild(download);
		const frame = document.createElement('iframe');
		frame.className = 'pdf-frame';
		frame.src = url;
		wrap.appendChild(links);
		wrap.appendChild(frame);
		editor.Modal.open({ title: title, content: wrap });
		editor.Modal.onceClose(function () { wrap.remove(); URL.revokeObjectURL(url); });
	}

	editor.getModel().on('change:changesCount', updateBadge);

	// ---------------------------------------------------------------------------------------------
	// Model panel: tree of the data model. Drag a property onto the document, or select a bound
	// component and click a property to rebind it.
	// ---------------------------------------------------------------------------------------------
	const modelTreeEl = document.getElementById('modelTree');
	const modelProblemsEl = document.getElementById('modelProblems');
	const TYPE_ICONS = { group: 'data_object', list: 'data_array', number: 'tag', date: 'calendar_today', text: 'text_fields', image: 'image' };
	let modelDrag = null;

	function applyModel(data, markDirty) {
		modelData = data;
		const next = buildSchema(data);
		schema.fields = next.fields;
		schema.kinds = next.kinds;
		schema.collections = next.collections;
		renderModelTree();
		const wrapper = editor.getWrapper();
		if (wrapper) wrapper.findType('data-image').forEach(function (c) { if (c.view) c.view.updateAttributes(); });
		paintFields();
		if (markDirty) {
			modelDirty = true;
			updateBadge();
			validateBindings();
		}
	}

	function renderModelTree() {
		modelTreeEl.innerHTML = '';
		modelTreeEl.appendChild(renderNodes(buildTree(modelData, '')));
	}

	// Sample value for a template path; loop aliases resolve to the first item of their list (building -> location.buildings[0]).
	// Values come from the selected test-data scenario, or the model's example data.
	function sampleValue(path, depth) {
		return valueIn(sampleData(), path, depth);
	}

	function valueIn(data, path, depth) {
		if (!path || (depth || 0) > 8 || !isObject(data)) return undefined;
		const parts = path.split('.');
		let value;
		if (Object.prototype.hasOwnProperty.call(data, parts[0])) {
			value = data[parts[0]];
		} else {
			const owner = schema.collections.find(function (c) { return c.alias === parts[0]; });
			const list = owner && valueIn(data, owner.path, (depth || 0) + 1);
			if (!Array.isArray(list)) return undefined;
			const tail = parts.slice(1);
			const hit = list.find(function (item) {
				return tail.reduce(function (acc, key) { return acc && acc[key]; }, item);
			});
			value = hit !== undefined ? hit : list[0];
		}
		for (let i = 1; i < parts.length && value !== undefined && value !== null; i++) value = value[parts[i]];
		return value;
	}

	// What a scenario leaves out: bound fields with no value (they print empty or their "if empty" text) and the lists
	// it has no items for (their rows don't print, so their fields aren't counted).
	function dataGaps(data) {
		const wrapper = editor.getWrapper();
		const gaps = { fields: [], emptyLists: [] };
		if (!wrapper) return gaps;
		const paths = [];
		wrapper.findType('data-field').concat(wrapper.findType('data-image'), wrapper.findType('legacy-field')).forEach(function (c) {
			const path = c.get('field');
			if (path && paths.indexOf(path) < 0) paths.push(path);
		});
		paths.forEach(function (path) {
			const owner = schema.collections.find(function (c) { return c.alias === path.split('.')[0]; });
			if (owner) {
				const list = valueIn(data, owner.path);
				if (!Array.isArray(list) || !list.length) {
					if (gaps.emptyLists.indexOf(owner.path) < 0) gaps.emptyLists.push(owner.path);
					return;
				}
			}
			const value = valueIn(data, path);
			if (value === undefined || value === null || value === '') gaps.fields.push(path);
		});
		return gaps;
	}

	function renderNodes(nodes) {
		const ul = document.createElement('ul');
		nodes.forEach(function (node) {
			const li = document.createElement('li');
			const row = document.createElement('div');
			row.className = 'mt-node mt-' + node.kind + (node.unusable ? ' mt-unusable' : '');
			const caret = document.createElement('span');
			caret.className = 'mt-caret';
			if (node.children && node.children.length) {
				caret.innerHTML = uiIcon('chevron_right');
				caret.addEventListener('click', function (e) {
					e.stopPropagation();
					li.classList.toggle('collapsed');
				});
			}
			const icon = document.createElement('span');
			icon.className = 'mt-icon';
			icon.innerHTML = uiIcon(TYPE_ICONS[node.type] || 'text_fields');
			icon.title = node.type;
			const label = document.createElement('span');
			label.className = 'mt-name';
			label.textContent = node.name;
			row.appendChild(caret);
			row.appendChild(icon);
			row.appendChild(label);
			if (node.unusable) {
				row.title = 'Property names must be letters, numbers and _ to be used in a template.';
			} else {
				row.title = node.kind === 'array'
					? node.path + '  (each item is "' + node.alias + '")\nDrag onto the document to choose a table, cards or list and its fields, or click to rebind the selected table/repeat.'
					: node.path + '\nDrag onto the document, or click to rebind the selected ' + (node.kind === 'value' ? 'field.' : 'item.');
				row.draggable = node.kind !== 'object' || node.children.some(function (c) { return c.kind === 'value' && !c.unusable; });
				row.addEventListener('dragstart', function (e) { startModelDrag(node, e); });
				row.addEventListener('dragend', endModelDrag);
				row.addEventListener('click', function () { bindSelected(node); });
				if (node.kind !== 'value') {
					const add = document.createElement('button');
					add.type = 'button';
					add.className = 'mt-add';
					add.title = 'Add a property to ' + (node.kind === 'array' ? 'each item of ' : '') + node.path;
					add.setAttribute('data-path', node.path);
					add.innerHTML = uiIcon('add');
					add.addEventListener('click', function (e) {
						e.stopPropagation();
						openAddProperty(node);
					});
					row.appendChild(add);
				}
				const find = document.createElement('button');
				find.type = 'button';
				find.className = 'mt-usage';
				find.title = 'Where is ' + node.path + ' used?';
				find.setAttribute('data-path', node.path);
				find.innerHTML = uiIcon('filter_alt');
				find.addEventListener('click', function (e) {
					e.stopPropagation();
					openFieldUsage(canonicalPath(node.path));
				});
				row.appendChild(find);
			}
			li.appendChild(row);
			if (node.children && node.children.length) li.appendChild(renderNodes(node.children));
			ul.appendChild(li);
		});
		return ul;
	}

	function valueChildren(node) {
		return node.children.filter(function (c) { return c.kind === 'value' && !c.unusable; });
	}

	// What a dragged property becomes on the document. Lists drop a placeholder; the list chooser replaces it.
	function contentFor(node) {
		if (node.kind === 'value') return labelField(labelFor(node.path) + ':', node.path, defaultFormat(schema, node.path));
		if (node.kind === 'object') return fieldsBlock(valueChildren(node).map(function (c) { return c.path; }));
		return { type: 'text', tagName: 'div', content: 'Adding ' + node.path + '\u2026' };
	}

	// "Label: value" for each path; 4 or more are split into two columns.
	function fieldsBlock(paths) {
		const fields = paths.map(function (p) { return labelField(labelFor(p) + ':', p, defaultFormat(schema, p)); });
		if (fields.length < 4) return { tagName: 'div', components: fields };
		const half = Math.ceil(fields.length / 2);
		return { type: 'layout-row', components: [col(fields.slice(0, half)), col(fields.slice(half))] };
	}

	// ---- List layouts: how a dropped list is shown, and which of its fields in what order ----------------------
	const LIST_LAYOUTS = {
		table: 'Table \u2013 one row per item, a column per field',
		cards: 'Cards \u2013 a bordered card per item, first field as the title',
		details: 'Label: value \u2013 a section per item',
		bullets: 'Bulleted list \u2013 one line per item',
		chips: 'Chips \u2013 a tag per item',
		empty: 'Empty Repeat \u2013 I will drag fields in myself'
	};

	function isPlainList(node) {
		const values = valueChildren(node);
		return values.length === 1 && values[0].path === node.alias;
	}

	function layoutsFor(node) {
		return isPlainList(node) ? ['chips', 'bullets', 'table'] : ['table', 'cards', 'details', 'bullets', 'empty'];
	}

	function listContent(node, layout, paths) {
		function fmt(p) { return field(p, defaultFormat(schema, p)); }
		function inline(ps) {
			const parts = [];
			ps.forEach(function (p, i) {
				if (i) parts.push({ type: 'text', tagName: 'span', content: ' \u2013 ' });
				parts.push(fmt(p));
			});
			return parts;
		}
		const repeat = function (extra) { return Object.assign({ type: 'repeat', listPath: node.path }, extra); };
		if (layout === 'table') return editor.__docTable(node.path, paths.map(function (p) { return { path: p }; }));
		if (layout === 'chips') return repeat({ classes: ['chips'], components: [{ tagName: 'div', classes: ['chip'], components: inline(paths) }] });
		if (layout === 'bullets') return repeat({ tagName: 'ul', components: [{ tagName: 'li', components: inline(paths) }] });
		if (layout === 'cards') {
			const rest = paths.slice(1);
			return repeat({ components: [{ tagName: 'div', classes: ['moe-card', 'moe-card--accent'], components: [
				{ tagName: 'div', classes: ['moe-card-title'], components: [fmt(paths[0])] }
			].concat(rest.length ? [fieldsBlock(rest)] : []) }] });
		}
		if (layout === 'details') return repeat({ components: [{ tagName: 'div', classes: ['moe-list-item'], components: [fieldsBlock(paths)] }] });
		return repeat({ components: [{ type: 'text', tagName: 'div', content: 'Drag ' + node.alias + ' properties in here' }] });
	}

	function findNode(nodes, path) {
		for (let i = 0; i < nodes.length; i++) {
			if (nodes[i].path === path) return nodes[i];
			const inner = nodes[i].children && findNode(nodes[i].children, path);
			if (inner) return inner;
		}
		return null;
	}

	// Dialog: pick a layout (unless columnsOnly) and tick/reorder fields. Resolves { layout, paths } or null.
	function chooseList(node, options) {
		return new Promise(function (resolve) {
			const plain = isPlainList(node);
			const values = valueChildren(node).map(function (c) { return c.path; });
			const preset = options.selected || values;
			const items = preset.filter(function (p) { return values.indexOf(p) >= 0; }).map(function (p) { return { path: p, on: true }; })
				.concat(values.filter(function (p) { return preset.indexOf(p) < 0; }).map(function (p) { return { path: p, on: false }; }));
			let layout = options.columnsOnly ? 'table' : layoutsFor(node)[0];
			let settled = false;

			const wrap = document.createElement('div');
			const intro = document.createElement('p');
			intro.className = 'model-help';
			intro.textContent = options.columnsOnly
				? 'Columns for "' + node.path + '" (each row is one "' + node.alias + '"). Tick to show; use \u2191 \u2193 to set the order.'
				: 'Show "' + node.path + '" (each item is "' + node.alias + '") as:';
			wrap.appendChild(intro);

			if (!options.columnsOnly) {
				const layoutsEl = document.createElement('div');
				layoutsEl.className = 'lc-layouts';
				layoutsFor(node).forEach(function (id) {
					const label = document.createElement('label');
					const radio = document.createElement('input');
					radio.type = 'radio';
					radio.name = 'lc-layout';
					radio.checked = id === layout;
					radio.addEventListener('change', function () { layout = id; renderFields(); });
					label.appendChild(radio);
					label.appendChild(document.createTextNode(' ' + LIST_LAYOUTS[id]));
					layoutsEl.appendChild(label);
				});
				wrap.appendChild(layoutsEl);
			}

			const fieldsHelp = document.createElement('p');
			fieldsHelp.className = 'model-help';
			const list = document.createElement('ul');
			list.className = 'lc-fields';
			const nested = node.children.filter(function (c) { return c.kind !== 'value'; }).map(function (c) { return c.name; });
			const nestedNote = document.createElement('p');
			nestedNote.className = 'model-help';
			nestedNote.textContent = nested.length ? 'Groups and lists inside each item (' + nested.join(', ') + ') can be dragged into the result afterwards.' : '';
			if (!options.columnsOnly) {
				wrap.appendChild(fieldsHelp);
			}
			wrap.appendChild(list);
			wrap.appendChild(nestedNote);

			function move(i, delta) {
				const j = i + delta;
				if (j < 0 || j >= items.length) return;
				const tmp = items[i];
				items[i] = items[j];
				items[j] = tmp;
				renderFields();
			}

			function renderFields() {
				const hidden = plain || layout === 'empty';
				list.hidden = hidden;
				nestedNote.hidden = hidden;
				fieldsHelp.hidden = hidden;
				fieldsHelp.textContent = layout === 'table' ? 'Columns (tick to show; \u2191 \u2193 to set the order):'
					: layout === 'cards' ? 'Fields (the first ticked field is the card title):'
					: 'Fields (tick to show; \u2191 \u2193 to set the order):';
				list.innerHTML = '';
				items.forEach(function (item, i) {
					const li = document.createElement('li');
					li.className = item.on ? '' : 'off';
					const label = document.createElement('label');
					const box = document.createElement('input');
					box.type = 'checkbox';
					box.checked = item.on;
					box.addEventListener('change', function () { item.on = box.checked; li.className = item.on ? '' : 'off'; });
					const code = document.createElement('code');
					code.textContent = item.path;
					label.appendChild(box);
					label.appendChild(document.createTextNode(' ' + labelFor(item.path)));
					label.appendChild(code);
					const up = document.createElement('button');
					up.type = 'button';
					up.innerHTML = uiIcon('arrow_upward');
					up.title = 'Move up';
					up.disabled = i === 0;
					up.addEventListener('click', function () { move(i, -1); });
					const down = document.createElement('button');
					down.type = 'button';
					down.innerHTML = uiIcon('arrow_downward');
					down.title = 'Move down';
					down.disabled = i === items.length - 1;
					down.addEventListener('click', function () { move(i, 1); });
					li.appendChild(label);
					li.appendChild(up);
					li.appendChild(down);
					list.appendChild(li);
				});
			}

			const error = document.createElement('div');
			error.className = 'model-error';
			const actions = document.createElement('div');
			actions.className = 'model-actions';
			const ok = document.createElement('button');
			ok.type = 'button';
			ok.className = 'primary';
			ok.textContent = options.columnsOnly ? 'Apply' : 'Add';
			const cancel = document.createElement('button');
			cancel.type = 'button';
			cancel.textContent = 'Cancel';
			actions.appendChild(ok);
			actions.appendChild(cancel);
			wrap.appendChild(error);
			wrap.appendChild(actions);

			function finish(result) {
				if (settled) return;
				settled = true;
				resolve(result);
			}

			ok.addEventListener('click', function () {
				const paths = plain ? values : items.filter(function (i) { return i.on; }).map(function (i) { return i.path; });
				if (layout !== 'empty' && !paths.length) {
					error.textContent = 'Tick at least one field.';
					return;
				}
				finish({ layout: layout, paths: paths });
				editor.Modal.close();
			});
			cancel.addEventListener('click', function () { editor.Modal.close(); });

			renderFields();
			editor.Modal.open({ title: options.columnsOnly ? 'Choose columns' : 'Add ' + node.name, content: wrap });
			editor.Modal.onceClose(function () { wrap.remove(); finish(null); });
		});
	}

	async function addList(node, placeholder) {
		const choice = await chooseList(node, {});
		if (!choice) {
			placeholder.remove();
			setStatus('Cancelled adding ' + node.path + '.');
			return;
		}
		const added = placeholder.replaceWith(listContent(node, choice.layout, choice.paths))[0];
		editor.select(added);
		const problems = validateBindings().filter(function (p) { return p.component === added || added.find('*').indexOf(p.component) >= 0; });
		setStatus(problems.length ? problems[0].message : 'Added ' + node.path + ' as ' + choice.layout + '.', problems.length > 0);
	}

	editor.on('doc:choose-columns', async function (table) {
		const node = findNode(buildTree(modelData, ''), table.get('collection'));
		if (!node || node.kind !== 'array') {
			setStatus('"' + table.get('collection') + '" is not a list in the data model; select a list in the Model panel first.', true);
			return;
		}
		const current = table.columns();
		const choice = await chooseList(node, { columnsOnly: true, selected: current.map(function (c) { return c.path; }) });
		if (!choice) return;
		table.setColumns(choice.paths.map(function (p) {
			return current.find(function (c) { return c.path === p; }) || { path: p };
		}));
		editor.select(table);
		validateBindings();
		setStatus('Columns updated: ' + table.columns().map(function (c) { return c.label || labelFor(c.path); }).join(', ') + '.');
	});

	function startModelDrag(node, e) {
		modelDrag = node;
		e.dataTransfer.effectAllowed = 'copy';
		e.dataTransfer.setData('text/plain', node.path);
		editor.Canvas.startDrag({ content: contentFor(node) });
	}

	function endModelDrag() {
		editor.Canvas.endDrag();
		modelDrag = null;
	}

	// A single property dropped into a table cell, heading or label row becomes just the field (no "Label:").
	editor.on('canvas:drop', function (dataTransfer, result) {
		const node = modelDrag;
		let dropped = Array.isArray(result) ? result[0] : result;
		if (!node || !dropped) return;
		if (node.kind === 'array') {
			addList(node, dropped);
			return;
		}
		if (node.kind === 'value') {
			const parent = dropped.parent();
			const inline = parent && (/^(td|th|span|p|h1|h2|h3|h4|li)$/.test(parent.get('tagName')) ||
				parent.getClasses().some(function (c) { return c === 'kv' || c === 'chip' || c === 'moe-card-title'; }));
			if (inline) dropped = dropped.replaceWith(field(node.path, defaultFormat(schema, node.path)))[0];
		}
		editor.select(dropped);
		const problems = validateBindings().filter(function (p) { return p.component === dropped || dropped.find('*').indexOf(p.component) >= 0; });
		setStatus(problems.length ? problems[0].message : 'Added ' + node.path + '.', problems.length > 0);
	});

	function bindSelected(node) {
		const selected = editor.getSelected();
		if (selected && selected.is('conditional') && node.kind !== 'object') {
			selected.set('field', node.path);
			const conditionProblem = validateBindings().filter(function (p) { return p.component === selected; })[0];
			setStatus(conditionProblem ? conditionProblem.message : 'Show If now tests ' + node.path + '.', !!conditionProblem);
			return;
		}
		if (node.kind === 'value') {
			if (!selected || !(selected.is('data-field') || selected.is('legacy-field') || selected.is('data-image'))) {
				setStatus('Drag "' + node.path + '" onto the document, or select a data field first to rebind it.');
				return;
			}
			if (selected.is('data-image') !== (node.type === 'image')) {
				setStatus(selected.is('data-image') ? 'Pick an image property for a Data Image.' : '"' + node.path + '" is an image; use a Data Image for it.', true);
				return;
			}
			selected.set(selected.is('data-image') ? { field: node.path } : { field: node.path, format: defaultFormat(schema, node.path) });
		} else if (node.kind === 'array') {
			const target = selected && (selected.is('repeat') || selected.is('data-table') ? selected
				: selected.closestType('data-table') || selected.closestType('repeat'));
			if (!target) {
				setStatus('Drag "' + node.path + '" onto the document, or select a Data Table / Repeat first to change its list.');
				return;
			}
			target.set('collection', node.path);
		} else {
			setStatus('Drag "' + node.path + '" onto the document to add all of its fields.');
			return;
		}
		const problem = validateBindings().filter(function (p) { return p.component === selected || p.component === selected.closestType('data-table'); })[0];
		setStatus(problem ? problem.message : 'Bound to ' + node.path + '.', !!problem);
	}

	// ---- Binding validation: unknown properties and loop-item fields used outside their loop -------------------
	function aliasesInScope(component) {
		const aliases = [];
		for (let p = component.parent(); p; p = p.parent()) {
			if (p.is('repeat') || p.is('data-table')) aliases.push(p.get('alias'));
		}
		return aliases;
	}

	// Liquid list accessors: items.size, and items.first.field (a field of the first item).
	function listPathKnown(path) {
		const size = /^(.+)\.size$/.exec(path);
		if (size) return schema.collections.some(function (c) { return c.path === size[1]; });
		const first = /^(.+?)\.(first|last)\.(.+)$/.exec(path);
		if (!first) return false;
		const owner = schema.collections.find(function (c) { return c.path === first[1]; });
		return !!owner && (schema.fields.indexOf(owner.alias + '.' + first[3]) >= 0 || listPathKnown(owner.alias + '.' + first[3]));
	}

	function bindingProblem(component) {
		if (component.is('calc-field')) return calcProblem(component);
		let path;
		let known;
		if (component.is('data-field') || component.is('legacy-field') || component.is('data-image')) {
			path = component.get('field');
			known = schema.fields.indexOf(path) >= 0;
		} else if (component.is('conditional') || component.is('choice-branch')) {
			path = component.get('field');
			if (component.get('operator') === 'else') return null;
			// Loop position (forloop.first) is valid inside any repeat.
			if (/^forloop\.(first|last|index|index0|length)$/.test(path || '')) {
				return aliasesInScope(component).length ? null : '"' + path + '" only works inside a Data Table or Repeat.';
			}
			known = schema.fields.indexOf(path) >= 0 || schema.collections.some(function (c) { return c.path === path; });
		} else {
			path = component.get('collection');
			known = schema.collections.some(function (c) { return c.path === path; });
		}
		if (path && !known) known = listPathKnown(path);
		if (!path) return 'Not bound to a property.';
		if (!known) {
			return { message: '"' + path + '" is not in the data model.', missing: path,
				list: component.is('repeat') || component.is('data-table') };
		}
		const head = path.split('.')[0];
		if (Object.prototype.hasOwnProperty.call(modelData, head) || aliasesInScope(component).indexOf(head) >= 0) return null;
		const owner = schema.collections.find(function (c) { return c.alias === head; });
		return '"' + path + '" only works inside a Data Table or Repeat over ' + (owner ? owner.path : head) + '.';
	}

	// A calculated field's problems: the compiler's message, or a field / list the model doesn't have.
	function calcProblem(component) {
		if (!String(component.get('expression') || '').trim()) return 'Calculated field: no calculation yet.';
		if (component.get('calcError')) return 'Calculation: ' + component.get('calcError');
		const scope = aliasesInScope(component);
		const paths = component.get('calcPaths') || [];
		for (let i = 0; i < paths.length; i++) {
			const path = paths[i].replace(/\[\d+\]/g, '');
			const known = schema.fields.indexOf(path) >= 0 || listPathKnown(path) || schema.collections.some(function (c) { return c.path === path; });
			if (!known) return { message: 'Calculation: "' + paths[i] + '" is not in the data model.', missing: path };
			const head = path.split('.')[0];
			if (!Object.prototype.hasOwnProperty.call(modelData, head) && scope.indexOf(head) < 0) {
				return 'Calculation: "' + paths[i] + '" only works inside a Data Table or Repeat.';
			}
		}
		const lists = component.get('calcLists') || [];
		for (let i = 0; i < lists.length; i++) {
			if (!schema.collections.some(function (c) { return c.path === lists[i]; })) {
				return { message: 'Calculation: "' + lists[i] + '" is not a list in the data model.', missing: lists[i], list: true };
			}
		}
		return null;
	}

	function validateBindings() {
		const wrapper = editor.getWrapper();
		if (!wrapper) return [];
		const problems = [];
		// Unmapped legacy form fields are expected after an import: listed as to-do, not flagged as errors.
		const legacyFields = wrapper.findType('legacy-field');
		const unmapped = legacyFields.filter(function (c) { return !c.get('field'); });
		unmapped.forEach(function (component) {
			const el = component.getEl();
			if (!el) return;
			el.classList.remove('binding-bad');
			el.setAttribute('title', 'Form field ' + component.get('legacyName') +
				(component.get('formLabel') ? ' (\u201c' + component.get('formLabel') + '\u201d on the form)' : '') + ' is not mapped to the model yet.');
		});
		wrapper.findType('data-field').concat(
			wrapper.findType('data-image'),
			legacyFields.filter(function (c) { return !!c.get('field'); }),
			wrapper.findType('repeat'), wrapper.findType('data-table'), wrapper.findType('conditional'), wrapper.findType('choice-branch'),
			wrapper.findType('calc-field')
		).forEach(function (component) {
			// a message, or { message, missing: the path the model lacks, list: whether it should be a list }
			const result = bindingProblem(component);
			const message = result && (result.message || result);
			const el = component.getEl();
			if (el) {
				el.classList.toggle('binding-bad', !!message);
				if (message) el.setAttribute('title', message); else el.removeAttribute('title');
			}
			if (message) problems.push({ component: component, message: message, missing: result.missing || null, list: !!result.list });
		});
		renderProblems(problems, unmapped);
		return problems;
	}

	function renderProblems(problems, unmapped) {
		modelProblemsEl.innerHTML = '';
		if (unmapped.length) {
			const todo = document.createElement('div');
			todo.className = 'mp-item mp-todo';
			todo.textContent = unmapped.length + ' form field' + (unmapped.length === 1 ? '' : 's') + ' not mapped yet \u2014 click to select the next, then click a Model property';
			todo.addEventListener('click', function () {
				const component = unmapped[(unmapped.indexOf(editor.getSelected()) + 1) % unmapped.length];
				editor.select(component);
				const el = component.getEl();
				if (el) el.scrollIntoView({ block: 'center' });
			});
			modelProblemsEl.appendChild(todo);
			const suggest = document.createElement('button');
			suggest.type = 'button';
			suggest.id = 'mapSuggest';
			suggest.className = 'md-btn text mp-suggest';
			suggest.textContent = 'Suggest mappings\u2026';
			suggest.title = 'Match the form fields to model properties by name';
			suggest.addEventListener('click', function () { run('Suggesting mappings', suggestMappings); });
			modelProblemsEl.appendChild(suggest);
		}
		if (!problems.length) return;
		const head = document.createElement('div');
		head.className = 'mp-head';
		head.textContent = problems.length + ' binding problem' + (problems.length === 1 ? '' : 's');
		modelProblemsEl.appendChild(head);
		const addable = problems.filter(function (p) { return quickFixes(p).some(function (f) { return f.add; }); });
		if (addable.length > 1) {
			head.appendChild(fixButton({ id: 'mpAddAll', icon: 'add', label: 'Add all to model',
				title: 'Add every missing property to the data model', apply: function () { addMissing(addable); } }));
		}
		problems.forEach(function (p) {
			const item = document.createElement('div');
			item.className = 'mp-item';
			item.textContent = p.message;
			item.title = 'Select it in the document';
			item.addEventListener('click', function () { editor.select(p.component); });
			const fixes = quickFixes(p);
			if (fixes.length) {
				const bar = document.createElement('div');
				bar.className = 'mp-fixes';
				fixes.forEach(function (fix) { bar.appendChild(fixButton(fix)); });
				item.appendChild(bar);
			}
			modelProblemsEl.appendChild(item);
		});
	}

	function fixButton(fix) {
		const button = document.createElement('button');
		button.type = 'button';
		button.className = 'mp-fix';
		if (fix.id) button.id = fix.id;
		button.title = fix.title;
		button.innerHTML = uiIcon(fix.icon);
		button.appendChild(document.createTextNode(fix.label));
		button.addEventListener('click', function (e) {
			e.stopPropagation();
			fix.apply();
		});
		return button;
	}

	// ---- Quick fixes: add a missing property to the model, or bind a group's .size to the list inside it -------------
	function quickFixes(problem) {
		if (!problem.missing) return [];
		const size = /^(.+)\.size$/.exec(problem.missing);
		const counted = size ? valueIn(modelData, size[1]) : undefined;
		if (counted !== undefined && counted !== null) {
			// .size of a group (GhostDraft counts a group's items): the list inside the group was meant
			if (!isObject(counted) || problem.component.is('calc-field')) return [];
			return Object.keys(counted).filter(function (k) { return Array.isArray(counted[k]) && KEY_PATTERN.test(k); }).slice(0, 3).map(function (k) {
				const path = size[1] + '.' + k + '.size';
				return { icon: 'link', label: 'Use ' + k + '.size', title: 'Bind to ' + path + ' (' + k + ' is the list inside ' + size[1] + ')',
					apply: function () { rebindTo(problem.component, path); } };
			});
		}
		return [{ icon: 'add', label: 'Add to model', title: 'Add ' + problem.missing + ' to the data model', add: true,
			apply: function () { addMissing([problem]); } }];
	}

	function rebindTo(component, path) {
		component.set(component.is('repeat') || component.is('data-table') ? 'collection' : 'field', path);
		const problem = validateBindings().filter(function (p) { return p.component === component; })[0];
		setStatus(problem ? problem.message : 'Now bound to ' + path + '.', !!problem);
	}

	function addMissing(problems) {
		const model = JSON.parse(JSON.stringify(modelData));
		const added = [];
		const failed = [];
		problems.forEach(function (p) {
			if (added.indexOf(p.missing) >= 0) return;
			try {
				// the lists of the Data Tables / Repeats around it first (outermost first), so their item names resolve
				const loops = [];
				for (let loop = p.component.parent(); loop; loop = loop.parent()) {
					if ((loop.is('repeat') || loop.is('data-table')) && loop.get('collection')) loops.unshift(loop);
				}
				loops.forEach(function (loop) { listsAt(model, loop.get('collection'), loopScope(loop), 0); });
				addToModel(model, p.missing, p.list ? [] : exampleFor(p.component, p.missing), loopScope(p.component));
				added.push(p.missing);
			} catch (err) {
				failed.push('Could not add ' + p.missing + ': ' + err.message);
			}
		});
		if (added.length) {
			applyModel(model, true);
			// a Repeat / Data Table over a list that was missing names its items after the list now
			const wrapper = editor.getWrapper();
			wrapper.findType('repeat').concat(wrapper.findType('data-table')).forEach(function (c) { c.syncAlias(); });
		}
		if (failed.length) {
			setStatus(failed.join(' '), true);
			return;
		}
		afterModelChange('Added ' + (added.length === 1 ? added[0] : added.length + ' properties') + ' to the model');
	}

	// The Data Tables / Repeats around a component: loop alias -> its list.
	function loopScope(component) {
		const scope = {};
		for (let p = component.parent(); p; p = p.parent()) {
			const alias = (p.is('repeat') || p.is('data-table')) && p.get('alias');
			if (alias && !Object.prototype.hasOwnProperty.call(scope, alias)) scope[alias] = p.get('collection');
		}
		return scope;
	}

	// An example value for a new property, from how the document uses it.
	function exampleFor(component, path) {
		if (component.is('data-image')) return 'data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7';
		if (component.is('calc-field')) return 0;
		if (component.is('conditional') || component.is('choice-branch')) {
			const operator = component.get('operator');
			const value = String(component.get('value') == null ? '' : component.get('value'));
			if (operator === 'gt' || operator === 'lt') return Number(value) || 0;
			if (operator === 'eq' || operator === 'ne' || operator === 'contains') {
				return /^-?\d+(\.\d+)?$/.test(value) ? Number(value) : value || labelFor(path);
			}
			return true;
		}
		if (component.is('legacy-field') && component.get('check')) return true;
		const format = component.get('format') || '';
		if (/^(currency|dollars|percent|number|decimal)$/.test(format)) return 0;
		if (format === 'shortdate') return new Date().toISOString().slice(0, 10);
		return labelFor(path);
	}

	// ---- Adding to the model. Paths reach list items through the loop alias (auto.x), so a property under an alias is
	// added to every item of its list (an empty list gets one item). scope: loop aliases -> lists, from loopScope.
	function hasOwn(obj, key) {
		return Object.prototype.hasOwnProperty.call(obj, key);
	}

	function describeValue(value) {
		return Array.isArray(value) ? 'a list' : isObject(value) ? 'a group' : 'a value';
	}

	function aliasList(model, head, scope) {
		if (scope && hasOwn(scope, head)) return scope[head];
		if (hasOwn(model, head)) return null;
		const owner = buildSchema(model).collections.find(function (c) { return c.alias === head; });
		return owner ? owner.path : null;
	}

	function checkKey(key) {
		if (!KEY_PATTERN.test(key)) throw new Error('"' + key + '" is not a usable property name (letters, numbers and _).');
	}

	// The groups at a path, created where missing.
	function groupsAt(model, path, scope, depth) {
		if (!path) return [model];
		if (depth > 8) throw new Error('the path nests too deeply.');
		const parts = path.split('.');
		const list = aliasList(model, parts[0], scope);
		let groups = list ? itemsAt(model, list, scope, depth + 1) : [model];
		for (let i = list ? 1 : 0; i < parts.length; i++) {
			const key = parts[i];
			checkKey(key);
			groups = groups.map(function (group) {
				if (group[key] === undefined || group[key] === null) group[key] = {};
				if (!isObject(group[key])) throw new Error('"' + parts.slice(0, i + 1).join('.') + '" is ' + describeValue(group[key]) + ' in the model, not a group.');
				return group[key];
			});
		}
		return groups;
	}

	// The lists at a path, created empty where missing.
	function listsAt(model, path, scope, depth) {
		const cut = path.lastIndexOf('.');
		const key = path.slice(cut + 1);
		checkKey(key);
		return groupsAt(model, cut < 0 ? '' : path.slice(0, cut), scope, depth).map(function (group) {
			if (group[key] === undefined || group[key] === null) group[key] = [];
			if (!Array.isArray(group[key])) throw new Error('"' + path + '" is ' + describeValue(group[key]) + ' in the model, not a list.');
			return group[key];
		});
	}

	function itemsAt(model, listPath, scope, depth) {
		const items = [];
		listsAt(model, listPath, scope, depth).forEach(function (list) {
			if (!list.length) list.push({});
			list.forEach(function (item) {
				if (!isObject(item)) throw new Error('the items of "' + listPath + '" are values, not groups.');
				items.push(item);
			});
		});
		return items;
	}

	// Adds path with the example value (an array: an empty list). list.size makes the list; list.first.x adds x to its items.
	function addToModel(model, path, example, scope) {
		const size = /^(.+)\.size$/.exec(path);
		if (size || Array.isArray(example)) {
			listsAt(model, size ? size[1] : path, scope, 0);
			return;
		}
		const first = /^(.+?)\.(first|last)\.(.+)$/.exec(path);
		if (first) {
			addToModel(model, '#item.' + first[3], example, Object.assign({}, scope, { '#item': first[1] }));
			return;
		}
		const cut = path.lastIndexOf('.');
		const key = path.slice(cut + 1);
		checkKey(key);
		let added = false;
		let existing;
		groupsAt(model, cut < 0 ? '' : path.slice(0, cut), scope, 0).forEach(function (group) {
			if (group[key] === undefined || group[key] === null) {
				group[key] = JSON.parse(JSON.stringify(example));
				added = true;
			} else {
				existing = group[key];
			}
		});
		if (!added) throw new Error('it is already in the model as ' + describeValue(existing) + '.');
	}

	// ---- The + on a group or list in the Model tree (and in the panel header, for the top level) ---------------------
	const NEW_PROPERTY_TYPES = [
		{ id: 'text', label: 'Text' },
		{ id: 'number', label: 'Number' },
		{ id: 'date', label: 'Date' },
		{ id: 'yesno', label: 'Yes / No' },
		{ id: 'group', label: 'Group (has its own properties)' },
		{ id: 'list', label: 'List (repeats)' }
	];

	function newPropertyValue(type, name) {
		if (type === 'number') return 0;
		if (type === 'date') return new Date().toISOString().slice(0, 10);
		if (type === 'yesno') return true;
		if (type === 'group') return {};
		if (type === 'list') return [];
		return labelFor(name);
	}

	// node: a Model tree group or list, or null for the top level.
	function openAddProperty(node) {
		document.querySelectorAll('.add-property').forEach(function (el) { el.remove(); });
		const wrap = document.createElement('div');
		wrap.className = 'add-property';
		const help = document.createElement('p');
		help.className = 'model-help';
		help.textContent = !node ? 'Add a property at the top level of the model.'
			: node.kind === 'array' ? 'Add a property to each item of ' + node.path + ' (an item is "' + node.alias + '").'
			: 'Add a property to ' + node.path + '.';
		const nameLabel = document.createElement('label');
		nameLabel.className = 'scenario-name-field';
		nameLabel.appendChild(document.createTextNode('Name '));
		const name = document.createElement('input');
		name.id = 'newPropertyName';
		name.placeholder = 'e.g. PolicyNumber';
		nameLabel.appendChild(name);
		const typeLabel = document.createElement('label');
		typeLabel.className = 'scenario-name-field';
		typeLabel.appendChild(document.createTextNode('Type '));
		const type = document.createElement('select');
		type.id = 'newPropertyType';
		NEW_PROPERTY_TYPES.forEach(function (t) {
			const option = document.createElement('option');
			option.value = t.id;
			option.textContent = t.label;
			type.appendChild(option);
		});
		typeLabel.appendChild(type);
		const error = document.createElement('div');
		error.className = 'model-error';
		const actions = document.createElement('div');
		actions.className = 'model-actions';
		const add = modalButton('Add', 'newPropertyAdd', 'primary');
		const cancel = modalButton('Cancel');
		actions.appendChild(add);
		actions.appendChild(cancel);
		[help, nameLabel, typeLabel, error, actions].forEach(function (el) { wrap.appendChild(el); });

		function submit() {
			const key = name.value.trim();
			const path = (node ? (node.kind === 'array' ? node.alias : node.path) + '.' : '') + key;
			const model = JSON.parse(JSON.stringify(modelData));
			try {
				if (!key) throw new Error('Type a name.');
				addToModel(model, path, newPropertyValue(type.value, key), node && node.kind === 'array' ? { [node.alias]: node.path } : {});
			} catch (err) {
				error.textContent = err.message.charAt(0).toUpperCase() + err.message.slice(1);
				return;
			}
			editor.Modal.close();
			applyModel(model, true);
			afterModelChange('Added ' + path + ' to the model');
		}
		add.addEventListener('click', submit);
		name.addEventListener('keydown', function (e) { if (e.key === 'Enter') { e.preventDefault(); submit(); } });
		cancel.addEventListener('click', function () { editor.Modal.close(); });
		editor.Modal.open({ title: 'Add a property', content: wrap });
		editor.Modal.onceClose(function () { wrap.remove(); });
		name.focus();
	}

	// ---- Form field mapping suggestions: unmapped form fields matched to model properties by name (server-side) -----
	// Properties inside lists are left out: a form field on a sheet has no list item to read them from.
	function mappablePaths() {
		const inLists = {};
		schema.collections.forEach(function (c) { c.fields.forEach(function (f) { inLists[f] = true; }); });
		return schema.fields
			.filter(function (f) { return !inLists[f] && schema.kinds[f] !== 'image'; })
			.map(function (f) { return { path: f, kind: schema.kinds[f] || null }; });
	}

	async function suggestMappings() {
		const unmapped = editor.getWrapper().findType('legacy-field').filter(function (c) { return !c.get('field'); });
		const byName = {};
		unmapped.forEach(function (c) { (byName[c.get('legacyName')] = byName[c.get('legacyName')] || []).push(c); });
		const names = Object.keys(byName);
		if (!names.length) return 'Every form field is mapped.';
		// the words printed next to each field: how fields named like "f1_01[0]" can still be matched
		const labels = names.map(function (n) {
			const labelled = byName[n].find(function (c) { return c.get('formLabel'); });
			return labelled ? labelled.get('formLabel') : null;
		});
		const response = await fetch('/api/mapping/suggest', {
			method: 'POST',
			headers: { 'Content-Type': 'application/json' },
			body: JSON.stringify({ fields: names, paths: mappablePaths(), labels: labels })
		});
		const suggestions = await (await ensureOk(response, 'Suggest mappings')).json();

		const wrap = document.createElement('div');
		wrap.className = 'map-suggest';
		const intro = document.createElement('p');
		intro.textContent = 'Ticked rows are applied. Confident matches are ticked for you; pick another property or untick to leave a field unmapped.';
		wrap.appendChild(intro);
		const table = document.createElement('table');
		const head = table.insertRow();
		['', 'Form field', 'Model property', 'Match'].forEach(function (h) {
			const th = document.createElement('th');
			th.textContent = h;
			head.appendChild(th);
		});
		suggestions.forEach(function (s) {
			const row = table.insertRow();
			row.setAttribute('data-field', s.field);
			const accept = document.createElement('input');
			accept.type = 'checkbox';
			accept.className = 'map-accept';
			accept.checked = !!s.recommended;
			accept.disabled = !s.candidates.length;
			row.insertCell().appendChild(accept);
			const fieldCell = row.insertCell();
			fieldCell.textContent = s.field + (byName[s.field].length > 1 ? ' (\u00d7' + byName[s.field].length + ')' : '');
			if (s.label) {
				const label = document.createElement('small');
				label.className = 'map-label';
				label.textContent = s.label;
				label.title = 'Printed next to the field on the form';
				fieldCell.appendChild(label);
			}
			const select = document.createElement('select');
			select.className = 'map-path';
			s.candidates.forEach(function (c) {
				const option = document.createElement('option');
				option.value = c.path;
				option.textContent = c.path;
				option.setAttribute('data-score', c.score);
				select.appendChild(option);
			});
			if (!s.candidates.length) {
				const none = document.createElement('option');
				none.textContent = '(no likely property)';
				select.appendChild(none);
				select.disabled = true;
			}
			row.insertCell().appendChild(select);
			const score = row.insertCell();
			score.className = 'map-score';
			const showScore = function () {
				const opt = select.selectedOptions[0];
				score.textContent = opt && opt.getAttribute('data-score') ? Math.round(parseFloat(opt.getAttribute('data-score')) * 100) + '%' : '';
			};
			select.addEventListener('change', function () { accept.checked = true; showScore(); });
			showScore();
		});
		wrap.appendChild(table);
		const actions = document.createElement('div');
		actions.className = 'import-options-actions';
		const apply = document.createElement('button');
		apply.type = 'button';
		apply.id = 'mapApply';
		apply.className = 'md-btn filled';
		apply.textContent = 'Apply';
		actions.appendChild(apply);
		wrap.appendChild(actions);

		return await new Promise(function (resolve) {
			let applied = null;
			apply.addEventListener('click', function () {
				let count = 0;
				Array.prototype.forEach.call(table.querySelectorAll('tr[data-field]'), function (row) {
					if (!row.querySelector('.map-accept').checked) return;
					const path = row.querySelector('.map-path').value;
					byName[row.getAttribute('data-field')].forEach(function (component) {
						component.set('format', defaultFormat(schema, path));
						component.set('field', path);
						count++;
					});
				});
				applied = count;
				editor.Modal.close();
			});
			editor.Modal.open({ title: 'Suggested form field mappings', content: wrap });
			editor.Modal.onceClose(function () {
				wrap.remove();
				validateBindings();
				resolve(applied === null ? 'No mappings applied.' : 'Mapped ' + applied + ' form field(s).');
			});
		});
	}

	let validateTimer = 0;
	editor.on('update component:add component:remove', function () {
		clearTimeout(validateTimer);
		validateTimer = setTimeout(validateBindings, 150);
	});

	// ---- Import / Edit ---------------------------------------------------------------------------------------------
	const MAX_MODEL_BYTES = 2 * 1024 * 1024;

	function parseModel(text) {
		const json = JSON.parse(text);
		const model = looksLikeJsonSchema(json) ? sampleFromSchema(json, json, 0) : json;
		if (!isObject(model)) throw new Error('The model must be a JSON object (or a JSON Schema of an object).');
		return { model: model, fromSchema: looksLikeJsonSchema(json) };
	}

	function modelSummary() {
		return schema.fields.length + ' fields, ' + schema.collections.length + ' lists';
	}

	function afterModelChange(prefix) {
		const problems = validateBindings();
		setStatus(prefix + ' (' + modelSummary() + ').' +
			(problems.length ? ' ' + problems.length + ' binding problem(s) on the document; see the Model panel.' : ''), problems.length > 0);
	}

	const modelFile = document.getElementById('modelFile');
	document.getElementById('btnImportModel').addEventListener('click', function () {
		modelFile.value = '';
		modelFile.click();
	});

	modelFile.addEventListener('change', async function () {
		const file = modelFile.files && modelFile.files[0];
		if (!file) return;
		let parsed;
		try {
			if (file.size > MAX_MODEL_BYTES) throw new Error('file is too large (max 2 MB).');
			parsed = parseModel(await file.text());
		} catch (err) {
			setStatus('Could not import ' + file.name + ': ' + err.message, true);
			return;
		}
		applyModel(parsed.model, true);
		afterModelChange('Imported ' + file.name + (parsed.fromSchema ? ' (JSON Schema, example values generated)' : ''));
	});

	document.getElementById('btnAddProperty').addEventListener('click', function () { openAddProperty(null); });

	document.getElementById('btnEditModel').addEventListener('click', function () {
		const wrap = document.createElement('div');
		const help = document.createElement('p');
		help.className = 'model-help';
		help.textContent = 'Example message data for this form (JSON). Its shape drives the Model tree; its values are used for Preview PDF. A JSON Schema is also accepted.';
		const area = document.createElement('textarea');
		area.className = 'model-editor';
		area.spellcheck = false;
		area.value = JSON.stringify(modelData, null, 2);
		const error = document.createElement('div');
		error.className = 'model-error';
		const actions = document.createElement('div');
		actions.className = 'model-actions';
		const apply = document.createElement('button');
		apply.type = 'button';
		apply.className = 'primary';
		apply.textContent = 'Apply';
		const cancel = document.createElement('button');
		cancel.type = 'button';
		cancel.textContent = 'Cancel';
		actions.appendChild(apply);
		actions.appendChild(cancel);
		wrap.appendChild(help);
		wrap.appendChild(area);
		wrap.appendChild(error);
		wrap.appendChild(actions);

		apply.addEventListener('click', function () {
			let parsed;
			try {
				parsed = parseModel(area.value);
			} catch (err) {
				error.textContent = err.message;
				return;
			}
			editor.Modal.close();
			applyModel(parsed.model, true);
			afterModelChange('Model updated' + (parsed.fromSchema ? ' from JSON Schema' : ''));
		});
		cancel.addEventListener('click', function () { editor.Modal.close(); });
		editor.Modal.open({ title: 'Edit data model', content: wrap });
		// GrapesJS keeps replaced modal content in a hidden collector; drop ours so copies don't pile up.
		editor.Modal.onceClose(function () { wrap.remove(); });
		area.focus();
	});

	// ---- Test-data scenarios: named payloads per template; pick one to see it on the canvas, or preview them all -------
	const scenarioSelect = document.getElementById('scenarioSelect');
	const SCENARIO_PATTERN = /^[A-Za-z0-9](?:[A-Za-z0-9 _-]{0,62}[A-Za-z0-9_-])?$/;
	const EXAMPLE_LABEL = 'Model example';
	const MAX_PREVIEW_ALL = 20;

	function scenarioUrl(name) {
		return apiBase() + '/scenarios' + (name ? '/' + encodeURIComponent(name) : '');
	}

	async function refreshScenarios() {
		let list = [];
		try {
			const response = await fetch(scenarioUrl());
			if (response.ok) list = await response.json();
		} catch (e) { /* no valid name yet: no scenarios */ }
		scenarios = Array.isArray(list) ? list : [];
		if (activeScenario && !scenarios.some(function (s) { return s.name === activeScenario; })) activeScenario = '';
		fillScenarioSelect();
		applySample();
	}

	function fillScenarioSelect() {
		scenarioSelect.innerHTML = '';
		[{ name: '', label: EXAMPLE_LABEL }].concat(scenarios.map(function (s) { return { name: s.name, label: s.name }; })).forEach(function (s) {
			const option = document.createElement('option');
			option.value = s.name;
			option.textContent = s.label;
			scenarioSelect.appendChild(option);
		});
		scenarioSelect.value = activeScenario;
	}

	// Repaint what shows sample values (fields, data images) from the selected data.
	function applySample() {
		const wrapper = editor.getWrapper();
		if (wrapper) wrapper.findType('data-image').forEach(function (c) { if (c.view) c.view.updateAttributes(); });
		paintFields();
	}

	function gapText(gaps) {
		return (gaps.fields.length ? ' No value for ' + gaps.fields.length + ' field(s): ' + gaps.fields.join(', ') + '.' : '') +
			(gaps.emptyLists.length ? ' Empty list(s): ' + gaps.emptyLists.join(', ') + '.' : '');
	}

	function useScenario(name) {
		activeScenario = name;
		scenarioSelect.value = name;
		applySample();
		setStatus('Showing ' + (name ? 'scenario "' + name + '"' : 'the model example data') + '.' + gapText(dataGaps(sampleData())));
	}

	scenarioSelect.addEventListener('change', function () { useScenario(scenarioSelect.value); });

	function modalButton(text, id, className) {
		const button = document.createElement('button');
		button.type = 'button';
		button.textContent = text;
		if (id) button.id = id;
		if (className) button.className = className;
		return button;
	}

	document.getElementById('btnScenarios').addEventListener('click', function () {
		try {
			templateName();
		} catch (err) {
			setStatus(err.message, true);
			return;
		}
		openScenarios();
	});

	function openScenarios() {
		const wrap = document.createElement('div');
		wrap.className = 'scenario-manager';
		const help = document.createElement('p');
		help.className = 'model-help';
		help.textContent = 'Test data to try this ' + (isClause() ? 'clause' : 'template') + ' with (a minimal policy, many locations, missing optional data...). ' +
			'Scenarios are saved straight away for "' + templateName() + '" and kept across versions; they are never published.';
		const list = document.createElement('div');
		list.className = 'scenario-list';
		[{ name: '', data: modelData }].concat(scenarios).forEach(function (s) {
			const row = document.createElement('div');
			row.className = 'scenario-row' + (s.name === activeScenario ? ' active' : '');
			row.setAttribute('data-scenario', s.name);
			const label = document.createElement('span');
			label.className = 'scenario-name';
			label.textContent = s.name || EXAMPLE_LABEL;
			const gaps = dataGaps(s.data);
			const info = document.createElement('small');
			info.textContent = (s.name ? 'saved ' + formatWhen(s.savedUtc) : 'the data model\'s example data') +
				(gaps.fields.length ? ' \u00b7 ' + gaps.fields.length + ' field(s) without a value' : '') +
				(gaps.emptyLists.length ? ' \u00b7 empty: ' + gaps.emptyLists.join(', ') : '');
			const use = modalButton(s.name === activeScenario ? 'Showing' : 'Show', null, 'scenario-use');
			use.disabled = s.name === activeScenario;
			use.addEventListener('click', function () { useScenario(s.name); editor.Modal.close(); });
			row.appendChild(label);
			row.appendChild(info);
			row.appendChild(use);
			if (s.name) {
				const edit = modalButton('Edit', null, 'scenario-edit');
				edit.addEventListener('click', function () { editScenario(s); });
				const remove = modalButton('Delete', null, 'scenario-delete danger');
				remove.addEventListener('click', function () {
					if (!window.confirm('Delete scenario "' + s.name + '"?')) return;
					run('Deleting scenario', async function () {
						await ensureOk(await fetch(scenarioUrl(s.name), { method: 'DELETE' }), 'Delete');
						await refreshScenarios();
						openScenarios();
						return 'Deleted scenario "' + s.name + '".';
					});
				});
				row.appendChild(edit);
				row.appendChild(remove);
			}
			list.appendChild(row);
		});
		const actions = document.createElement('div');
		actions.className = 'model-actions';
		const add = modalButton('New scenario', 'scenarioNew');
		add.addEventListener('click', function () { editScenario(null); });
		const all = modalButton('Preview all', 'scenarioPreviewAll', 'primary');
		all.addEventListener('click', function () { run('Rendering every scenario', previewAll); });
		actions.appendChild(add);
		actions.appendChild(all);
		wrap.appendChild(help);
		wrap.appendChild(list);
		wrap.appendChild(actions);
		editor.Modal.open({ title: 'Test data scenarios', content: wrap });
		editor.Modal.onceClose(function () { wrap.remove(); });
	}

	// New (original = null; starts from the data on show) or edit/rename an existing scenario.
	function editScenario(original) {
		const wrap = document.createElement('div');
		wrap.className = 'scenario-editor';
		const nameLabel = document.createElement('label');
		nameLabel.className = 'scenario-name-field';
		nameLabel.textContent = 'Name ';
		const nameInput2 = document.createElement('input');
		nameInput2.id = 'scenarioName';
		nameInput2.maxLength = 64;
		nameInput2.placeholder = 'e.g. Many locations';
		nameInput2.value = original ? original.name : '';
		nameLabel.appendChild(nameInput2);
		const area = document.createElement('textarea');
		area.id = 'scenarioData';
		area.className = 'model-editor';
		area.spellcheck = false;
		area.value = JSON.stringify(original ? original.data : sampleData(), null, 2);
		const error = document.createElement('div');
		error.id = 'scenarioError';
		error.className = 'model-error';
		const actions = document.createElement('div');
		actions.className = 'model-actions';
		const save = modalButton('Save scenario', 'scenarioSave', 'primary');
		const back = modalButton('Back', 'scenarioBack');
		actions.appendChild(save);
		actions.appendChild(back);
		wrap.appendChild(nameLabel);
		wrap.appendChild(area);
		wrap.appendChild(error);
		wrap.appendChild(actions);

		save.addEventListener('click', async function () {
			const name = nameInput2.value.trim();
			if (!SCENARIO_PATTERN.test(name)) {
				error.textContent = 'Name: letters, numbers, spaces, "-" and "_" (at most 64), starting with a letter or number.';
				return;
			}
			if (area.value.length > MAX_MODEL_BYTES) {
				error.textContent = 'The data is too large (max 2 MB).';
				return;
			}
			let data;
			try {
				data = JSON.parse(area.value);
			} catch (err) {
				error.textContent = 'Not valid JSON: ' + err.message;
				return;
			}
			if (!isObject(data)) {
				error.textContent = 'The data must be a JSON object.';
				return;
			}
			const renamed = original && original.name.toLowerCase() !== name.toLowerCase();
			const clash = scenarios.find(function (s) { return s.name.toLowerCase() === name.toLowerCase(); });
			if (clash && (!original || renamed) && !window.confirm('Replace the existing scenario "' + clash.name + '"?')) return;
			save.disabled = true;
			try {
				const response = await fetch(scenarioUrl(name), {
					method: 'PUT',
					headers: { 'Content-Type': 'application/json' },
					body: JSON.stringify({ data: data })
				});
				await ensureOk(response, 'Save scenario');
				if (renamed) await fetch(scenarioUrl(original.name), { method: 'DELETE' });
				if (original && activeScenario === original.name) activeScenario = name;
				await refreshScenarios();
				openScenarios();
				setStatus('Saved scenario "' + name + '".' + gapText(dataGaps(data)));
			} catch (err) {
				error.textContent = err.message;
				save.disabled = false;
			}
		});
		back.addEventListener('click', openScenarios);
		editor.Modal.open({ title: original ? 'Edit scenario "' + original.name + '"' : 'New scenario', content: wrap });
		editor.Modal.onceClose(function () { wrap.remove(); });
		nameInput2.focus();
	}

	function pdfUrl(base64) {
		const binary = atob(base64);
		const bytes = new Uint8Array(binary.length);
		for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
		return URL.createObjectURL(new Blob([bytes], { type: 'application/pdf' }));
	}

	// Renders the canvas once per scenario (the model example first) and shows the PDFs side by side with a result list.
	async function previewAll() {
		const all = [{ name: EXAMPLE_LABEL, data: modelData }].concat(scenarios.map(function (s) { return { name: s.name, data: s.data }; }));
		const list = all.slice(0, MAX_PREVIEW_ALL);
		const response = await fetch('/api/render/scenarios', {
			method: 'POST',
			headers: { 'Content-Type': 'application/json' },
			body: JSON.stringify({ html: editor.getHtml(), css: editor.getCss(), scenarios: list })
		});
		const results = await (await ensureOk(response, 'Preview all')).json();

		const wrap = document.createElement('div');
		wrap.className = 'scenario-preview';
		const side = document.createElement('div');
		side.className = 'scenario-results';
		const view = document.createElement('div');
		view.className = 'scenario-view';
		const links = document.createElement('div');
		links.className = 'pdf-links';
		const frame = document.createElement('iframe');
		frame.id = 'scenarioPdf';
		frame.className = 'pdf-frame';
		view.appendChild(links);
		view.appendChild(frame);
		wrap.appendChild(side);
		wrap.appendChild(view);
		const urls = [];

		function show(result, url, button) {
			side.querySelectorAll('.scenario-result').forEach(function (b) { b.classList.toggle('active', b === button); });
			links.innerHTML = '';
			if (!url) {
				frame.removeAttribute('src');
				frame.hidden = true;
				const message = document.createElement('div');
				message.className = 'model-error';
				message.textContent = result.error;
				links.appendChild(message);
				return;
			}
			frame.hidden = false;
			frame.src = url;
			const open = document.createElement('a');
			open.href = url;
			open.target = '_blank';
			open.rel = 'noopener';
			open.textContent = 'Open in new tab';
			const download = document.createElement('a');
			download.href = url;
			download.download = templateName() + ' - ' + result.name + '.pdf';
			download.textContent = 'Download';
			links.appendChild(open);
			links.appendChild(download);
		}

		let first = null;
		results.forEach(function (result, i) {
			const url = result.pdf ? pdfUrl(result.pdf) : null;
			if (url) urls.push(url);
			const button = document.createElement('button');
			button.type = 'button';
			button.className = 'scenario-result ' + (result.error ? 'failed' : 'ok');
			button.setAttribute('data-scenario', result.name);
			const title = document.createElement('strong');
			title.textContent = result.name;
			const detail = document.createElement('small');
			const gaps = dataGaps(list[i].data);
			detail.textContent = result.error ? 'Failed: ' + result.error
				: result.pages + ' page' + (result.pages === 1 ? '' : 's') +
					(gaps.fields.length ? ' \u00b7 no value: ' + gaps.fields.join(', ') : '') +
					(gaps.emptyLists.length ? ' \u00b7 empty: ' + gaps.emptyLists.join(', ') : '');
			button.appendChild(title);
			button.appendChild(detail);
			button.addEventListener('click', function () { show(result, url, button); });
			side.appendChild(button);
			if (!first) first = function () { show(result, url, button); };
		});
		if (first) first();
		editor.Modal.open({ title: 'Preview all scenarios', content: wrap });
		editor.Modal.onceClose(function () { wrap.remove(); urls.forEach(function (u) { URL.revokeObjectURL(u); }); });

		const failed = results.filter(function (r) { return r.error; }).length;
		return 'Rendered ' + (results.length - failed) + ' of ' + results.length + ' scenario(s)' + (failed ? '; ' + failed + ' failed' : '') + '.' +
			(all.length > list.length ? ' Only the first ' + MAX_PREVIEW_ALL + ' are previewed.' : '');
	}

	document.getElementById('btnSave').addEventListener('click', function () {
		run('Saving', async function () { return (await saveDraft()) || 'Save cancelled.'; });
	});

	// ---- Field usage: which templates and clauses read a data path ------------------------------------------------
	// Model paths inside lists use the loop alias (location.name); usage is searched by list (locations[].name).
	function canonicalPath(path, depth) {
		const head = path.split('.')[0];
		const owner = schema.collections.find(function (c) { return c.alias === head; });
		if (!owner || (depth || 0) > 8) return path;
		return canonicalPath(owner.path, (depth || 0) + 1) + '[]' + path.slice(head.length);
	}

	document.getElementById('btnFieldUsage').addEventListener('click', function () { openFieldUsage(''); });

	function openFieldUsage(path) {
		const wrap = document.createElement('div');
		wrap.className = 'field-usage';
		const form = document.createElement('div');
		form.className = 'usage-form';
		const input = document.createElement('input');
		input.id = 'usagePath';
		input.placeholder = 'e.g. policy.number or locations[].name';
		input.value = path;
		input.setAttribute('list', 'usagePaths');
		input.spellcheck = false;
		const suggestions = document.createElement('datalist');
		suggestions.id = 'usagePaths';
		const allLabel = document.createElement('label');
		const all = document.createElement('input');
		all.type = 'checkbox';
		all.id = 'usageAll';
		allLabel.appendChild(all);
		allLabel.appendChild(document.createTextNode(' All versions'));
		allLabel.title = 'Search every saved version, not just the newest and the published one';
		const go = modalButton('Search', 'usageSearch', 'primary');
		form.appendChild(input);
		form.appendChild(suggestions);
		form.appendChild(allLabel);
		form.appendChild(go);
		const help = document.createElement('p');
		help.className = 'model-help';
		help.textContent = 'Finds the templates and clauses that read a field, or anything under it ("policy" finds policy.number too). ' +
			'Fields inside lists are written with [] (locations[].name). The newest and the published version of each are searched.';
		const results = document.createElement('div');
		results.id = 'usageResults';
		wrap.appendChild(form);
		wrap.appendChild(help);
		wrap.appendChild(results);

		fetch('/api/usage/fields').then(function (r) { return r.ok ? r.json() : []; }).then(function (index) {
			index.forEach(function (entry) {
				const option = document.createElement('option');
				option.value = entry.path;
				option.label = entry.documents + ' document' + (entry.documents === 1 ? '' : 's');
				suggestions.appendChild(option);
			});
		}).catch(function () { /* suggestions are optional */ });

		async function search() {
			results.innerHTML = '';
			const response = await fetch('/api/usage/fields', {
				method: 'POST',
				headers: { 'Content-Type': 'application/json' },
				body: JSON.stringify({ path: input.value.trim(), html: exportForSave().html, allVersions: all.checked })
			});
			const found = await (await ensureOk(response, 'Search')).json();
			const canvasUses = (found.canvas || []).reduce(function (n, p) { return n + p.count; }, 0);
			const summary = document.createElement('p');
			summary.className = 'usage-summary';
			summary.textContent = '"' + found.path + '" is used in ' + found.documents.length + ' document version' + (found.documents.length === 1 ? '' : 's') +
				'. This canvas (unsaved): ' + canvasUses + ' place' + (canvasUses === 1 ? '' : 's') + '.';
			results.appendChild(summary);
			if (found.documents.length) results.appendChild(usageTable(found.documents));
			return summary.textContent;
		}

		function runSearch() {
			run('Searching', search);
		}
		go.addEventListener('click', runSearch);
		input.addEventListener('keydown', function (e) { if (e.key === 'Enter') runSearch(); });

		editor.Modal.open({ title: 'Field usage', content: wrap });
		editor.Modal.onceClose(function () { wrap.remove(); });
		input.focus();
		if (path) runSearch();
	}

	function usageTable(documents) {
		const table = document.createElement('table');
		table.className = 'usage-table';
		const head = table.createTHead().insertRow();
		['Kind', 'Name', 'Version', 'Fields', 'Through clauses', ''].forEach(function (h) {
			const th = document.createElement('th');
			th.textContent = h;
			head.appendChild(th);
		});
		const body = table.createTBody();
		const openName = nameInput.value.trim();
		documents.forEach(function (d) {
			const row = body.insertRow();
			row.className = 'usage-row';
			row.setAttribute('data-doc', d.kind + '/' + d.name + '/' + d.version + (d.language ? '/' + d.language : ''));
			if (d.kind === docKind.value && d.name === openName && (d.language || '') === docLang()) row.classList.add('open');
			const language = d.language && LANGUAGES[d.language] ? ' (' + LANGUAGES[d.language].name + ')' : '';
			[d.kind === 'clauses' ? 'Clause' : 'Template', d.name + language, 'v' + d.version + ' ' + d.status,
				d.paths.map(function (p) { return p.path + (p.count > 1 ? ' \u00d7' + p.count : ''); }).join(', '),
				d.via.join(', ')].forEach(function (text) {
				row.insertCell().textContent = text;
			});
			const open = modalButton('Open', null, 'usage-open');
			open.addEventListener('click', function () { openDocument(d.kind, d.name, d.version, d.language); });
			row.insertCell().appendChild(open);
		});
		return table;
	}

	// ---- Calculation builder: type or click together an expression, see the result with the test data -------------
	// "|" marks where the cursor goes after inserting.
	const CALC_FUNCTIONS = [
		{ name: 'sum', insert: 'sum(|)', title: 'Total of a list field, e.g. sum(claims.amount)' },
		{ name: 'count', insert: 'count(|)', title: 'Number of items in a list, e.g. count(locations)' },
		{ name: 'average', insert: 'average(|)', title: 'Average of a list field, e.g. average(locations.tiv)' },
		{ name: 'min', insert: 'min(|, )', title: 'The smallest of two or more values' },
		{ name: 'max', insert: 'max(|, )', title: 'The largest of two or more values' },
		{ name: 'round', insert: 'round(|, 2)', title: 'Round to a number of decimal places (halves round up)' },
		{ name: 'abs', insert: 'abs(|)', title: 'The value without its minus sign' },
		{ name: 'concat', insert: "concat(|, ' ', )", title: 'Join text and values, e.g. concat(insured.first, \' \', insured.last)' },
		{ name: 'default', insert: "default(|, 'Not applicable')", title: 'A value, or a replacement when it is empty' },
		{ name: 'days_between', insert: 'days_between(|, )', title: 'Whole days from the first date to the second' }
	];
	const CALC_OPERATORS = ['+', '\u2212', '\u00d7', '\u00f7', '(', ')'];

	function insertAtCursor(area, text) {
		const marker = text.indexOf('|');
		const clean = text.replace('|', '');
		const start = area.selectionStart;
		const end = area.selectionEnd;
		area.value = area.value.slice(0, start) + clean + area.value.slice(end);
		const caret = start + (marker >= 0 ? marker : clean.length);
		area.focus();
		area.setSelectionRange(caret, caret);
		area.dispatchEvent(new Event('input'));
	}

	function openCalcBuilder(component) {
		const wrap = document.createElement('div');
		wrap.className = 'calc-builder';
		const help = document.createElement('p');
		help.className = 'model-help';
		help.textContent = 'Write a calculation with fields from the data model, numbers, + \u2212 \u00d7 \u00f7, parentheses and functions. ' +
			'Click a field or function to insert it. Dividing by zero or by an empty value gives 0.';
		const area = document.createElement('textarea');
		area.id = 'calcExpression';
		area.className = 'calc-expression';
		area.spellcheck = false;
		area.rows = 3;
		area.placeholder = 'e.g. policy.premium + policy.fees - policy.discount';
		area.value = component.get('expression') || '';

		const tools = document.createElement('div');
		tools.className = 'calc-tools';
		CALC_OPERATORS.forEach(function (op) {
			const b = modalButton(op, null, 'calc-op');
			b.setAttribute('data-insert', op);
			b.addEventListener('click', function () { insertAtCursor(area, op === '(' ? '(|)' : ' ' + op + ' '); });
			tools.appendChild(b);
		});
		CALC_FUNCTIONS.forEach(function (f) {
			const b = modalButton(f.name + '( )', null, 'calc-fn');
			b.setAttribute('data-function', f.name);
			b.title = f.title;
			b.addEventListener('click', function () { insertAtCursor(area, f.insert); });
			tools.appendChild(b);
		});

		const fieldsHead = document.createElement('div');
		fieldsHead.className = 'calc-fields-head';
		const filter = document.createElement('input');
		filter.id = 'calcFieldFilter';
		filter.placeholder = 'Find a field';
		fieldsHead.appendChild(filter);
		const fields = document.createElement('div');
		fields.id = 'calcFields';
		fields.className = 'calc-fields';
		const entries = schema.collections.map(function (c) { return { path: c.path, kind: 'list' }; })
			.concat(schema.fields.map(function (p) { return { path: p, kind: schema.kinds[p] || 'text' }; }));
		function paintPicks() {
			const q = filter.value.trim().toLowerCase();
			fields.innerHTML = '';
			entries.filter(function (e) { return !q || e.path.toLowerCase().indexOf(q) >= 0; }).forEach(function (e) {
				const b = modalButton(e.path, null, 'calc-field-pick calc-kind-' + e.kind);
				b.setAttribute('data-path', e.path);
				b.title = e.kind === 'list' ? 'List: use with sum, count or average' : e.kind;
				b.addEventListener('click', function () { insertAtCursor(area, e.path); });
				fields.appendChild(b);
			});
		}
		filter.addEventListener('input', paintPicks);
		paintPicks();

		const formatLabel = document.createElement('label');
		formatLabel.className = 'scenario-name-field';
		formatLabel.textContent = 'Format ';
		const format = document.createElement('select');
		format.id = 'calcFormat';
		FORMATS.forEach(function (f) {
			const option = document.createElement('option');
			option.value = f.id;
			option.textContent = f.label;
			format.appendChild(option);
		});
		format.value = component.get('format') || '';
		formatLabel.appendChild(format);

		const result = document.createElement('div');
		result.id = 'calcResult';
		result.className = 'calc-result';
		const error = document.createElement('pre');
		error.id = 'calcError';
		error.className = 'model-error calc-error';

		const actions = document.createElement('div');
		actions.className = 'model-actions';
		const apply = modalButton('Apply', 'calcApply', 'primary');
		const cancel = modalButton('Cancel', 'calcCancel');
		actions.appendChild(apply);
		actions.appendChild(cancel);
		[help, area, tools, fieldsHead, fields, formatLabel, result, error, actions].forEach(function (el) { wrap.appendChild(el); });

		let compiled = null;
		let check = 0;
		let timer = 0;
		async function refresh() {
			const ticket = ++check;
			const expression = area.value;
			compiled = null;
			apply.disabled = true;
			result.textContent = '';
			error.textContent = '';
			if (!expression.trim()) {
				result.textContent = 'Type a calculation.';
				return;
			}
			const r = await compileExpression(expression, schema.collections.map(function (c) { return c.path; }));
			if (ticket !== check) return;
			if (r.error) {
				const pos = Math.max(0, Math.min(r.position || 0, expression.length));
				const line = expression.replace(/\s/g, ' ');
				error.textContent = r.error + '\n' + line + '\n' + ' '.repeat(pos) + '^';
				return;
			}
			compiled = r;
			apply.disabled = false;
			const liquid = editor.__calcInLoops(component, r.liquid + liquidExpression(r.result, format.value, component.get('ifEmpty')));
			const preview = await previewLiquid(liquid, sampleData());
			if (ticket !== check) return;
			result.textContent = preview.error
				? 'Could not preview: ' + preview.error
				: '= ' + (preview.text === '' ? '(empty)' : preview.text) + '   with ' + (activeScenario ? 'scenario "' + activeScenario + '"' : 'the model example data');
		}
		function schedule() {
			clearTimeout(timer);
			timer = setTimeout(refresh, 200);
		}
		area.addEventListener('input', schedule);
		format.addEventListener('change', schedule);

		apply.addEventListener('click', function () {
			if (!compiled) return;
			component.set({
				expression: area.value.trim(),
				format: format.value,
				liquid: compiled.liquid,
				calcError: '',
				calcPaths: compiled.paths || [],
				calcLists: compiled.lists || []
			});
			editor.Modal.close();
			validateBindings();
			setStatus('Calculation applied.');
		});
		cancel.addEventListener('click', function () { editor.Modal.close(); });
		editor.Modal.open({ title: 'Calculated field', content: wrap });
		editor.Modal.onceClose(function () { clearTimeout(timer); wrap.remove(); });
		area.focus();
		refresh();
	}

	// ---- Text on the page: find & replace and spell check work on the author's own text, never on data fields,
	// calculations, clauses or other generated content. ------------------------------------------------------------
	const GENERATED_TYPES = ['data-field', 'calc-field', 'total-field', 'legacy-field', 'no-liquid', 'clause', 'data-image', 'icon'];

	function textNodes() {
		const nodes = [];
		const wrapper = editor.getWrapper();
		if (!wrapper) return nodes;
		(function walk(component, generated) {
			const type = component.get('type');
			const skip = generated || GENERATED_TYPES.indexOf(type) >= 0;
			if (type === 'textnode') {
				if (!skip) nodes.push(component);
				return;
			}
			// Text components that were never edited keep their text in `content` instead of child text nodes.
			if (!skip && component.get('content') && !component.components().length) nodes.push(component);
			component.components().forEach(function (child) { walk(child, skip); });
		})(wrapper, false);
		return nodes;
	}

	function setNodeText(node, text) {
		node.set('content', text);
		if (node.view) node.view.render();
		const em = editor.getModel();
		em.set('changesCount', (em.get('changesCount') || 0) + 1);
	}

	// The element to select for a text node: its nearest component that isn't a bare text node.
	function ownerOf(node) {
		return node.get('type') === 'textnode' ? node.parent() : node;
	}

	function escapeRegex(text) {
		return text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
	}

	function searchPattern(query, matchCase, wholeWord) {
		const source = escapeRegex(query);
		return new RegExp(wholeWord ? '(?<![\\p{L}\\p{N}_])' + source + '(?![\\p{L}\\p{N}_])' : source, (matchCase ? 'g' : 'gi') + 'u');
	}

	function findMatches(query, matchCase, wholeWord) {
		if (!query) return [];
		const re = searchPattern(query, matchCase, wholeWord);
		const matches = [];
		textNodes().forEach(function (node) {
			const text = node.get('content') || '';
			re.lastIndex = 0;
			let m;
			while ((m = re.exec(text))) {
				matches.push({ node: node, index: m.index, length: m[0].length });
				if (!m[0].length) re.lastIndex++;
			}
		});
		return matches;
	}

	// Replacement text is typed by the author: it must not turn into Liquid.
	function replacementProblem(text) {
		return /\{\{|\{%/.test(text) ? 'The replacement can\'t contain {{ or {% (template code).' : '';
	}

	function replaceAll(query, replacement, matchCase, wholeWord) {
		const re = searchPattern(query, matchCase, wholeWord);
		let count = 0;
		textNodes().forEach(function (node) {
			const text = node.get('content') || '';
			const next = text.replace(re, function () { count++; return replacement; });
			if (next !== text) setNodeText(node, next);
		});
		return count;
	}

	function showMatch(match) {
		const owner = ownerOf(match.node);
		if (!owner) return;
		editor.select(owner);
		const el = owner.getEl();
		if (el && el.scrollIntoView) el.scrollIntoView({ block: 'center' });
	}

	function openFind() {
		// Reopened while open: GrapesJS parks the old dialog in the page, so drop it (its inputs have the same ids).
		document.querySelectorAll('.find-replace').forEach(function (el) { el.remove(); });
		const wrap = document.createElement('div');
		wrap.className = 'find-replace';
		function field(labelText, id, type) {
			const label = document.createElement('label');
			label.className = type === 'checkbox' ? 'find-option' : 'scenario-name-field';
			const input = document.createElement('input');
			input.id = id;
			input.type = type || 'text';
			if (type === 'checkbox') {
				label.appendChild(input);
				label.appendChild(document.createTextNode(' ' + labelText));
			} else {
				label.appendChild(document.createTextNode(labelText + ' '));
				label.appendChild(input);
			}
			wrap.appendChild(label);
			return input;
		}
		const find = field('Find', 'findText');
		const replace = field('Replace with', 'replaceText');
		const matchCase = field('Match case', 'findCase', 'checkbox');
		const wholeWord = field('Whole words', 'findWord', 'checkbox');
		const count = document.createElement('div');
		count.id = 'findCount';
		count.className = 'find-count';
		const error = document.createElement('div');
		error.id = 'findError';
		error.className = 'model-error';
		const actions = document.createElement('div');
		actions.className = 'model-actions';
		const replaceAllBtn = modalButton('Replace all', 'replaceAll');
		const replaceOne = modalButton('Replace', 'replaceOne');
		const next = modalButton('Next', 'findNext', 'primary');
		const prev = modalButton('Previous', 'findPrev');
		[next, prev, replaceOne, replaceAllBtn].forEach(function (b) { actions.appendChild(b); });
		wrap.appendChild(count);
		wrap.appendChild(error);
		wrap.appendChild(actions);

		let matches = [];
		let current = -1;
		function update(keepPosition) {
			matches = findMatches(find.value, matchCase.checked, wholeWord.checked);
			if (!keepPosition || current >= matches.length) current = -1;
			const total = matches.length + ' match' + (matches.length === 1 ? '' : 'es');
			count.textContent = !find.value ? 'Type the text to find.'
				: !matches.length ? 'No matches in the page text.'
				: current < 0 ? total : (current + 1) + ' of ' + total;
			[next, prev, replaceOne, replaceAllBtn].forEach(function (b) { b.disabled = !matches.length; });
		}
		function go(step) {
			if (!matches.length) return;
			current = current < 0 ? (step > 0 ? 0 : matches.length - 1) : (current + step + matches.length) % matches.length;
			update(true);
			showMatch(matches[current]);
		}
		[find, matchCase, wholeWord].forEach(function (el) { el.addEventListener(el.type === 'checkbox' ? 'change' : 'input', function () { update(false); }); });
		find.addEventListener('keydown', function (e) { if (e.key === 'Enter') { e.preventDefault(); go(e.shiftKey ? -1 : 1); } });
		next.addEventListener('click', function () { go(1); });
		prev.addEventListener('click', function () { go(-1); });
		replaceOne.addEventListener('click', function () {
			error.textContent = replacementProblem(replace.value);
			if (error.textContent || !matches.length) return;
			if (current < 0) current = 0;
			const m = matches[current];
			const text = m.node.get('content') || '';
			setNodeText(m.node, text.slice(0, m.index) + replace.value + text.slice(m.index + m.length));
			update(true);
			if (matches.length) showMatch(matches[current]);
			setStatus('Replaced 1 occurrence.');
		});
		replaceAllBtn.addEventListener('click', function () {
			error.textContent = replacementProblem(replace.value);
			if (error.textContent) return;
			const n = replaceAll(find.value, replace.value, matchCase.checked, wholeWord.checked);
			update(false);
			setStatus('Replaced ' + n + ' occurrence' + (n === 1 ? '' : 's') + '.');
		});
		editor.Modal.open({ title: 'Find and replace', content: wrap });
		editor.Modal.onceClose(function () { wrap.remove(); });
		update(false);
		find.focus();
	}

	document.getElementById('btnFind').addEventListener('click', openFind);
	editor.Keymaps.add('doc:find', '\u2318+f, ctrl+f', function () { if (featureOn('FindReplace')) openFind(); }, { prevent: true });

	// ---- Spell check -------------------------------------------------------------------------------------------
	const ignoredWords = {};

	// Words of the page text with how often each appears (Liquid in typed text is skipped).
	function documentWords() {
		const words = {};
		textNodes().forEach(function (node) {
			const text = String(node.get('content') || '').replace(/\{\{[\s\S]*?\}\}|\{%[\s\S]*?%\}/g, ' ');
			(text.match(/\p{L}[\p{L}'\u2019-]*/gu) || []).forEach(function (raw) {
				const word = raw.replace(/['\u2019-]+$/, '');
				if (word) words[word] = (words[word] || 0) + 1;
			});
		});
		return words;
	}

	async function checkSpelling() {
		const words = documentWords();
		const list = Object.keys(words).filter(function (w) { return !ignoredWords[w]; }).slice(0, 5000);
		const response = await fetch('/api/spelling/check', {
			method: 'POST',
			headers: { 'Content-Type': 'application/json' },
			body: JSON.stringify({ words: list })
		});
		return { words: words, misspelled: await (await ensureOk(response, 'Spell check')).json() };
	}

	function openSpelling() {
		run('Checking spelling', async function () {
			const result = await checkSpelling();
			document.querySelectorAll('.spelling').forEach(function (el) { el.remove(); });
			const wrap = document.createElement('div');
			wrap.className = 'spelling';
			const summary = document.createElement('p');
			summary.className = 'spell-summary';
			const list = document.createElement('div');
			list.className = 'scenario-list';
			function updateSummary() {
				const left = list.querySelectorAll('.spell-row').length;
				summary.textContent = left ? left + ' word' + (left === 1 ? '' : 's') + ' to check. Click a suggestion to replace the word everywhere on the page.'
					: 'No spelling mistakes found in the page text.';
			}
			result.misspelled.forEach(function (m) {
				const row = document.createElement('div');
				row.className = 'scenario-row spell-row';
				row.setAttribute('data-word', m.word);
				const word = document.createElement('span');
				word.className = 'scenario-name spell-word';
				word.textContent = m.word;
				const info = document.createElement('small');
				info.className = 'spell-count';
				info.textContent = (result.words[m.word] || 1) + '\u00d7';
				const suggestions = document.createElement('span');
				suggestions.className = 'spell-suggestions';
				m.suggestions.forEach(function (s) {
					const b = modalButton(s, null, 'spell-suggestion');
					b.addEventListener('click', function () {
						const n = replaceAll(m.word, s, true, true);
						row.remove();
						updateSummary();
						setStatus('Replaced "' + m.word + '" with "' + s + '" (' + n + '\u00d7).');
					});
					suggestions.appendChild(b);
				});
				if (!m.suggestions.length) suggestions.textContent = 'no suggestions';
				const ignore = modalButton('Ignore', null, 'spell-ignore');
				ignore.addEventListener('click', function () { ignoredWords[m.word] = true; row.remove(); updateSummary(); });
				const add = modalButton('Add to dictionary', null, 'spell-add');
				add.addEventListener('click', function () {
					run('Adding to the dictionary', async function () {
						await ensureOk(await fetch('/api/spelling/words', {
							method: 'POST',
							headers: { 'Content-Type': 'application/json' },
							body: JSON.stringify({ word: m.word })
						}), 'Add word');
						row.remove();
						updateSummary();
						return '"' + m.word + '" added to the shared dictionary.';
					});
				});
				[word, info, suggestions, ignore, add].forEach(function (el) { row.appendChild(el); });
				list.appendChild(row);
			});
			wrap.appendChild(summary);
			wrap.appendChild(list);
			updateSummary();
			editor.Modal.open({ title: 'Spelling', content: wrap });
			editor.Modal.onceClose(function () { wrap.remove(); });
			const n = result.misspelled.length;
			return n ? n + ' possible spelling mistake' + (n === 1 ? '' : 's') + '.' : 'No spelling mistakes found.';
		});
	}

	document.getElementById('btnSpelling').addEventListener('click', openSpelling);

	// ---- Reviewer comments: notes on elements, kept per template name (not per version), never printed ------------
	let comments = [];
	const commentList = document.getElementById('commentList');
	const commentText = document.getElementById('commentText');
	const commentAuthor = document.getElementById('commentAuthor');
	const commentFilter = document.getElementById('commentFilter');
	try { commentAuthor.value = localStorage.getItem('designer.reviewer') || ''; } catch (e) { /* not remembered */ }
	// Signed in: comments are by you.
	if (me.securityEnabled) {
		commentAuthor.value = me.name;
		commentAuthor.readOnly = true;
	}
	commentAuthor.addEventListener('change', function () {
		try { localStorage.setItem('designer.reviewer', commentAuthor.value.trim()); } catch (e) { /* not remembered */ }
	});
	commentFilter.addEventListener('change', renderComments);

	function commentsUrl(suffix) {
		return apiBase() + '/comments' + (suffix || '');
	}

	function allComponents() {
		const all = [];
		const wrapper = editor.getWrapper();
		if (wrapper) (function walk(c) { all.push(c); c.components().forEach(walk); })(wrapper);
		return all;
	}

	function componentPath(component) {
		const indexes = [];
		for (let c = component; c && c.parent(); c = c.parent()) indexes.unshift(c.index());
		return indexes.join('.');
	}

	// The element a comment is on: by its anchor (saved with the project), else the element at the position it had
	// when the comment was made, if it still looks the same (anchors on a version that was never saved again).
	function commentTarget(comment) {
		const anchored = allComponents().find(function (c) { return c.get('commentAnchor') === comment.anchor; });
		if (anchored) return anchored;
		if (!comment.path) return null;
		let c = editor.getWrapper();
		const indexes = comment.path.split('.');
		for (let i = 0; c && i < indexes.length; i++) c = c.components().at(Number(indexes[i]));
		return c && !c.get('commentAnchor') && describeElement(c) === comment.element ? c : null;
	}

	function describeElement(component) {
		const el = component.getEl();
		const text = el ? el.textContent.replace(/\s+/g, ' ').trim() : '';
		return (component.getName() || component.get('type') || 'Element') + (text ? ': ' + text.slice(0, 60) : '');
	}

	async function refreshComments() {
		let list = [];
		try {
			const response = await fetch(commentsUrl());
			if (response.ok) list = await response.json();
		} catch (e) { /* no valid name yet */ }
		comments = Array.isArray(list) ? list : [];
		renderComments();
	}

	function markComments() {
		const doc = editor.Canvas.getDocument();
		if (!doc) return;
		doc.querySelectorAll('[data-comments]').forEach(function (el) { el.removeAttribute('data-comments'); });
		comments.filter(function (c) { return !c.resolved; }).forEach(function (comment) {
			const target = commentTarget(comment);
			const el = target && target.getEl();
			if (el && el.setAttribute) el.setAttribute('data-comments', String(Number(el.getAttribute('data-comments') || 0) + 1));
		});
	}

	function renderComments() {
		const open = comments.filter(function (c) { return !c.resolved; }).length;
		document.getElementById('commentsTabLabel').textContent = open ? 'Comments (' + open + ')' : 'Comments';
		commentList.innerHTML = '';
		const shown = comments.filter(function (c) { return commentFilter.value === 'all' || !c.resolved; });
		if (!shown.length) {
			const empty = document.createElement('p');
			empty.className = 'pane-empty';
			empty.textContent = comments.length ? 'No open comments.' : 'No comments yet.';
			commentList.appendChild(empty);
		}
		shown.forEach(function (comment) {
			const item = document.createElement('div');
			item.className = 'comment' + (comment.resolved ? ' resolved' : '');
			item.setAttribute('data-comment', comment.id);
			const target = commentTarget(comment);
			const head = document.createElement(target ? 'button' : 'span');
			head.className = target ? 'comment-target' : 'comment-orphan';
			head.textContent = comment.element || 'Element';
			if (target) {
				head.type = 'button';
				head.title = 'Select it on the page';
				head.addEventListener('click', function () {
					editor.select(target);
					const el = target.getEl();
					if (el && el.scrollIntoView) el.scrollIntoView({ block: 'center' });
				});
			} else {
				head.textContent += ' (no longer on the page)';
			}
			const meta = document.createElement('div');
			meta.className = 'comment-meta';
			meta.textContent = comment.author + ' \u00b7 ' + formatWhen(comment.createdUtc) +
				(comment.resolved ? ' \u00b7 Resolved' + (comment.resolvedBy ? ' by ' + comment.resolvedBy : '') : '');
			const text = document.createElement('div');
			text.className = 'comment-text';
			text.textContent = comment.text;
			const replies = document.createElement('div');
			replies.className = 'comment-replies';
			(comment.replies || []).forEach(function (reply) {
				const r = document.createElement('div');
				r.className = 'comment-reply';
				const who = document.createElement('strong');
				who.textContent = reply.author + ': ';
				r.appendChild(who);
				r.appendChild(document.createTextNode(reply.text));
				replies.appendChild(r);
			});
			const buttons = document.createElement('div');
			buttons.className = 'comment-buttons';
			const replyToggle = modalButton('Reply', null, 'comment-reply-toggle');
			const resolve = modalButton(comment.resolved ? 'Reopen' : 'Resolve', null, 'comment-resolve');
			const remove = modalButton('Delete', null, 'comment-delete danger');
			[replyToggle, resolve, remove].forEach(function (b) { buttons.appendChild(b); });
			const replyBox = document.createElement('div');
			replyBox.className = 'comment-reply-box';
			replyBox.hidden = true;
			const replyText = document.createElement('textarea');
			replyText.className = 'comment-reply-text';
			replyText.maxLength = 2000;
			replyText.rows = 2;
			const replySend = modalButton('Send reply', null, 'comment-reply-send');
			replyBox.appendChild(replyText);
			replyBox.appendChild(replySend);
			replyToggle.addEventListener('click', function () { replyBox.hidden = !replyBox.hidden; if (!replyBox.hidden) replyText.focus(); });
			replySend.addEventListener('click', function () {
				run('Replying', async function () {
					await ensureOk(await fetch(commentsUrl('/' + comment.id + '/replies'), {
						method: 'POST',
						headers: { 'Content-Type': 'application/json' },
						body: JSON.stringify({ author: commentAuthor.value.trim(), text: replyText.value })
					}), 'Reply');
					await refreshComments();
					markComments();
					return 'Reply added.';
				});
			});
			resolve.addEventListener('click', function () {
				run(comment.resolved ? 'Reopening' : 'Resolving', async function () {
					await ensureOk(await fetch(commentsUrl('/' + comment.id + '/resolved'), {
						method: 'POST',
						headers: { 'Content-Type': 'application/json' },
						body: JSON.stringify({ resolved: !comment.resolved, author: commentAuthor.value.trim() })
					}), 'Update');
					await refreshComments();
					markComments();
					return comment.resolved ? 'Comment reopened.' : 'Comment resolved.';
				});
			});
			remove.addEventListener('click', function () {
				if (!window.confirm('Delete this comment and its replies?')) return;
				run('Deleting comment', async function () {
					await ensureOk(await fetch(commentsUrl('/' + comment.id), { method: 'DELETE' }), 'Delete');
					await refreshComments();
					markComments();
					return 'Comment deleted.';
				});
			});
			[head, meta, text, replies, buttons, replyBox].forEach(function (el) { item.appendChild(el); });
			commentList.appendChild(item);
		});
	}

	document.getElementById('btnAddComment').addEventListener('click', function () {
		run('Adding comment', async function () {
			const selected = editor.getSelected();
			if (!selected || selected.is('wrapper')) throw new Error('Select an element on the page to comment on.');
			if (!commentAuthor.value.trim()) throw new Error('Enter your name first.');
			if (!commentText.value.trim()) throw new Error('Write the comment first.');
			// Kept on the element without marking the template changed; it is saved with the next draft.
			if (!selected.get('commentAnchor')) selected.set('commentAnchor', Math.random().toString(36).slice(2, 10).padEnd(8, '0'), { silent: true });
			await ensureOk(await fetch(commentsUrl(), {
				method: 'POST',
				headers: { 'Content-Type': 'application/json' },
				body: JSON.stringify({
					anchor: selected.get('commentAnchor'),
					path: componentPath(selected),
					element: describeElement(selected),
					author: commentAuthor.value.trim(),
					text: commentText.value
				})
			}), 'Comment');
			commentText.value = '';
			await refreshComments();
			markComments();
			return 'Comment added.';
		});
	});

	// "Comment" on the selection toolbar opens the Comments tab for the selected element.
	let commentsTimer = 0;
	editor.on('component:add component:remove', function () {
		if (!comments.length) return;
		clearTimeout(commentsTimer);
		commentsTimer = setTimeout(function () { renderComments(); markComments(); }, 150);
	});
	editor.Commands.add('add-comment', { run: function () { selectTab('tabComments'); commentText.focus(); } });
	editor.on('component:selected', function () {
		const selected = editor.getSelected();
		if (!selected || !selected.get('toolbar') || selected.is('wrapper')) return;
		const toolbar = selected.get('toolbar');
		if (toolbar.some(function (t) { return t.id === 'add-comment'; })) return;
		selected.set('toolbar', toolbar.concat([{
			id: 'add-comment',
			command: 'add-comment',
			label: uiIcon('notes'),
			attributes: { title: 'Comment', class: 'tb-add-comment' }
		}]));
	});

	// ---- Gallery: every template and clause with a live thumbnail; open or duplicate -----------------------------------
	document.getElementById('btnGallery').addEventListener('click', function () { run('Loading the gallery', openGallery); });

	function statusLine(entry) {
		const parts = [];
		if (entry.publishedVersion) parts.push('v' + entry.publishedVersion + ' Published');
		if (entry.latestVersion !== entry.publishedVersion) parts.push('v' + entry.latestVersion + ' ' + entry.latestStatus);
		return parts.join(' \u00b7 ');
	}

	async function openGallery() {
		// without clauses (feature off) the gallery shows templates only
		const entries = (await (await ensureOk(await fetch('/api/gallery'), 'Gallery')).json())
			.filter(function (e) { return featureOn('Clauses') || e.kind !== 'clauses'; });
		document.querySelectorAll('.gallery').forEach(function (el) { el.remove(); });
		const wrap = document.createElement('div');
		wrap.className = 'gallery';
		const bar = document.createElement('div');
		bar.className = 'gallery-bar';
		const filter = document.createElement('input');
		filter.id = 'galleryFilter';
		filter.placeholder = 'Find by name';
		const kind = document.createElement('select');
		kind.id = 'galleryKind';
		if (!featureOn('Clauses')) kind.classList.add('feature-off');
		[['', 'Templates and clauses'], ['templates', 'Templates'], ['clauses', 'Clauses']].forEach(function (o) {
			const option = document.createElement('option');
			option.value = o[0];
			option.textContent = o[1];
			kind.appendChild(option);
		});
		const sort = document.createElement('select');
		sort.id = 'gallerySort';
		[['name', 'By name'], ['recent', 'Recently saved']].forEach(function (o) {
			const option = document.createElement('option');
			option.value = o[0];
			option.textContent = o[1];
			sort.appendChild(option);
		});
		[filter, kind, sort].forEach(function (el) { bar.appendChild(el); });
		const grid = document.createElement('div');
		grid.className = 'gallery-grid';
		wrap.appendChild(bar);
		wrap.appendChild(grid);
		const names = entries.map(function (e) { return e.kind + '/' + e.name; });

		function paint() {
			const q = filter.value.trim().toLowerCase();
			const shown = entries
				.filter(function (e) { return (!kind.value || e.kind === kind.value) && (!q || e.name.toLowerCase().indexOf(q) >= 0); })
				.sort(function (a, b) {
					return sort.value === 'recent' ? Date.parse(b.savedUtc) - Date.parse(a.savedUtc) : a.name.localeCompare(b.name);
				});
			grid.innerHTML = '';
			if (!shown.length) {
				const empty = document.createElement('p');
				empty.className = 'pane-empty';
				empty.textContent = entries.length ? 'Nothing matches.' : 'No templates saved yet.';
				grid.appendChild(empty);
			}
			shown.forEach(function (entry) {
				const card = document.createElement('div');
				card.className = 'gallery-card';
				card.setAttribute('data-doc', entry.kind + '/' + entry.name);
				const thumb = document.createElement('div');
				thumb.className = 'gallery-thumb';
				const frame = document.createElement('iframe');
				frame.className = 'gallery-frame';
				// No scripts, no forms, no navigation: just the rendered page.
				frame.setAttribute('sandbox', '');
				frame.setAttribute('loading', 'lazy');
				frame.tabIndex = -1;
				frame.title = 'Preview of ' + entry.name;
				frame.src = '/api/' + entry.kind + '/' + encodeURIComponent(entry.name) + '/preview.html';
				thumb.appendChild(frame);
				const title = document.createElement('h4');
				title.className = 'gallery-name';
				title.textContent = entry.name;
				const kindLabel = document.createElement('span');
				kindLabel.className = 'gallery-kind';
				kindLabel.textContent = entry.kind === 'clauses' ? 'Clause' : 'Template';
				const status = document.createElement('div');
				status.className = 'gallery-status';
				status.textContent = statusLine(entry);
				const meta = document.createElement('small');
				meta.className = 'gallery-meta';
				meta.textContent = 'Saved ' + formatWhen(entry.savedUtc) +
					(entry.scenarios ? ' \u00b7 ' + entry.scenarios + ' scenario' + (entry.scenarios === 1 ? '' : 's') : '') +
					(entry.openComments ? ' \u00b7 ' + entry.openComments + ' open comment' + (entry.openComments === 1 ? '' : 's') : '') +
					(featureOn('Languages') && entry.languages && entry.languages.length ? ' \u00b7 also in ' + entry.languages.map(function (c) { return LANGUAGES[c] ? LANGUAGES[c].name : c; }).join(', ') : '');
				const actions = document.createElement('div');
				actions.className = 'gallery-actions';
				const open = modalButton('Open', null, 'gallery-open');
				open.addEventListener('click', function () { openDocument(entry.kind, entry.name, entry.latestVersion); });
				const duplicate = modalButton('Duplicate', null, 'gallery-duplicate');
				duplicate.addEventListener('click', function () { openDuplicate(entry, names); });
				actions.appendChild(open);
				if (featureOn('Duplicate')) actions.appendChild(duplicate);
				[thumb, kindLabel, title, status, meta, actions].forEach(function (el) { card.appendChild(el); });
				grid.appendChild(card);
			});
		}
		filter.addEventListener('input', paint);
		kind.addEventListener('change', paint);
		sort.addEventListener('change', paint);
		paint();
		editor.Modal.open({ title: 'Gallery', content: wrap });
		editor.Modal.onceClose(function () { wrap.remove(); });
		filter.focus();
		return entries.length + ' template' + (entries.length === 1 ? '' : 's') + ' and clause' + (entries.length === 1 ? '' : 's') + ' in the gallery.';
	}

	// A free name for a copy: name-copy, name-copy-2, ...
	function copyName(name, kind, taken) {
		const base = (name.slice(0, 58) + '-copy');
		let candidate = base;
		for (let i = 2; taken.indexOf(kind + '/' + candidate) >= 0; i++) candidate = base + '-' + i;
		return candidate;
	}

	function openDuplicate(entry, taken) {
		document.querySelectorAll('.duplicate').forEach(function (el) { el.remove(); });
		const wrap = document.createElement('div');
		wrap.className = 'duplicate';
		const what = entry.kind === 'clauses' ? 'clause' : 'template';
		const help = document.createElement('p');
		help.className = 'model-help';
		help.textContent = 'Copies a saved version of "' + entry.name + '" into a new ' + what + ' as draft v1, with its data model' +
			' and, if you like, its test scenarios. Comments are not copied.' + (entry.unsaved ? ' Unsaved changes on the canvas are not included.' : '');
		const nameField = document.createElement('label');
		nameField.className = 'scenario-name-field';
		nameField.textContent = 'New name ';
		const nameBox = document.createElement('input');
		nameBox.id = 'duplicateName';
		nameBox.maxLength = 64;
		nameBox.value = copyName(entry.name, entry.kind, taken || []);
		nameField.appendChild(nameBox);
		const versionField = document.createElement('label');
		versionField.className = 'scenario-name-field';
		versionField.textContent = 'Copy ';
		const version = document.createElement('select');
		version.id = 'duplicateVersion';
		if (entry.publishedVersion) {
			const option = document.createElement('option');
			option.value = String(entry.publishedVersion);
			option.textContent = 'v' + entry.publishedVersion + ' (Published)';
			version.appendChild(option);
		}
		if (entry.latestVersion !== entry.publishedVersion) {
			const option = document.createElement('option');
			option.value = String(entry.latestVersion);
			option.textContent = 'v' + entry.latestVersion + ' (' + entry.latestStatus + ', newest)';
			version.appendChild(option);
		}
		versionField.appendChild(version);
		const scenariosField = document.createElement('label');
		scenariosField.className = 'find-option';
		const scenarios = document.createElement('input');
		scenarios.type = 'checkbox';
		scenarios.id = 'duplicateScenarios';
		scenarios.checked = true;
		scenariosField.appendChild(scenarios);
		scenariosField.appendChild(document.createTextNode(' Copy test data scenarios'));
		const error = document.createElement('div');
		error.id = 'duplicateError';
		error.className = 'model-error';
		const actions = document.createElement('div');
		actions.className = 'model-actions';
		const go = modalButton('Duplicate', 'duplicateGo', 'primary');
		const cancel = modalButton('Cancel', 'duplicateCancel');
		actions.appendChild(go);
		actions.appendChild(cancel);
		[help, nameField, versionField, scenariosField, error, actions].forEach(function (el) { wrap.appendChild(el); });

		go.addEventListener('click', async function () {
			const newName = nameBox.value.trim();
			if (!NAME_PATTERN.test(newName)) {
				error.textContent = 'The new name may only contain letters, numbers, "-" and "_".';
				return;
			}
			go.disabled = true;
			try {
				const response = await fetch('/api/' + entry.kind + '/' + encodeURIComponent(entry.name) + '/duplicate', {
					method: 'POST',
					headers: { 'Content-Type': 'application/json' },
					body: JSON.stringify({ newName: newName, version: Number(version.value), includeScenarios: scenarios.checked })
				});
				const result = await (await ensureOk(response, 'Duplicate')).json();
				if (!confirmDiscardChanges()) {
					editor.Modal.close();
					setStatus('Duplicated as ' + newName + '. It was not opened because of your unsaved changes.');
					return;
				}
				editor.Modal.close();
				docKind.value = entry.kind;
				updateKindUi();
				nameInput.value = newName;
				resetLanguage();
				current = null;
				await refreshVersions();
				await openVersion(result.version);
				const translated = (result.languagesCopied || []).map(function (c) { return LANGUAGES[c] ? LANGUAGES[c].name : c; });
				setStatus('Duplicated ' + entry.name + ' v' + result.from.version + ' as ' + newName + ' (draft v' + result.version + ')' +
					(result.scenariosCopied ? ', with ' + result.scenariosCopied + ' scenario' + (result.scenariosCopied === 1 ? '' : 's') : '') +
					(translated.length ? (result.scenariosCopied ? ' and its ' : ', with its ') + translated.join(' and ') + ' version' + (translated.length === 1 ? '' : 's') : '') + '.');
			} catch (err) {
				error.textContent = err.message;
				go.disabled = false;
			}
		});
		cancel.addEventListener('click', function () { editor.Modal.close(); });
		editor.Modal.open({ title: 'Duplicate ' + what, content: wrap });
		editor.Modal.onceClose(function () { wrap.remove(); });
		nameBox.focus();
		nameBox.select();
	}

	document.getElementById('btnDuplicate').addEventListener('click', function () {
		run('Preparing to duplicate', async function () {
			const name = templateName();
			if (!versions.length) throw new Error('Save Draft first: only saved versions can be duplicated.');
			const latest = versions[versions.length - 1];
			const published = versions.find(function (v) { return v.status === 'Published'; });
			const taken = (await (await ensureOk(await fetch('/api/gallery'), 'Gallery')).json()).map(function (e) { return e.kind + '/' + e.name; });
			openDuplicate({
				kind: isClause() ? 'clauses' : 'templates',
				name: name,
				latestVersion: latest.version,
				latestStatus: latest.status,
				publishedVersion: published ? published.version : null,
				unsaved: isDirty()
			}, taken);
			return 'Choose a name for the copy.';
		});
	});

	// ---- Page setup dialog --------------------------------------------------------------------------------------
	const HF_ROWS = [
		{ part: 'header', id: 'Header', label: 'Header' },
		{ part: 'footer', id: 'Footer', label: 'Footer' },
		{ part: 'firstHeader', id: 'FirstHeader', label: 'First page header', group: 'first' },
		{ part: 'firstFooter', id: 'FirstFooter', label: 'First page footer', group: 'first' },
		{ part: 'evenHeader', id: 'EvenHeader', label: 'Even page header', group: 'even' },
		{ part: 'evenFooter', id: 'EvenFooter', label: 'Even page footer', group: 'even' }
	];
	const SLOT_IDS = { left: 'Left', center: 'Center', right: 'Right' };

	function pageSetupComponent() {
		return editor.getWrapper().findType('page-setup')[0] || null;
	}

	// Small form helpers for the page setup and watermark dialogs.
	function labelled(text, control) {
		const label = document.createElement('label');
		label.className = 'ps-field';
		const span = document.createElement('span');
		span.textContent = text;
		label.appendChild(span);
		label.appendChild(control);
		return label;
	}

	function selectBox(id, options, value) {
		const el = document.createElement('select');
		el.id = id;
		options.forEach(function (o) {
			const option = document.createElement('option');
			option.value = o.value;
			option.textContent = o.label;
			el.appendChild(option);
		});
		el.value = value;
		return el;
	}

	function openPageSetup() {
		document.querySelectorAll('.page-setup-dialog').forEach(function (el) { el.remove(); });
		const existing = pageSetupComponent();
		const setup = normalizeSetup(existing ? existing.get('setup') : null);
		const wrap = document.createElement('div');
		wrap.className = 'page-setup-dialog';
		const help = document.createElement('p');
		help.className = 'model-help';
		help.textContent = 'Paper, margins and the header and footer printed on every page of the PDF. ' +
			'In the header and footer, [Page] and [Pages] print "Page 2 of 5" numbers, [Date] prints today\'s date, ' +
			'[Logo] prints the company logo and {field.path} prints a field from the data.' +
			(existing ? '' : ' This template has no page setup yet, so it prints on Letter paper with the standard footer.');
		wrap.appendChild(help);

		const select = selectBox;
		function number(id, value, min, max, step) {
			const el = document.createElement('input');
			el.type = 'number';
			el.id = id;
			el.min = String(min);
			el.max = String(max);
			el.step = String(step);
			el.value = String(value);
			return el;
		}

		const paper = document.createElement('div');
		paper.className = 'ps-row';
		const size = select('psSize', Object.keys(PAGE_SIZES).map(function (k) { return { value: k, label: PAGE_SIZES[k].label }; }), setup.size);
		const orientation = select('psOrientation', [{ value: 'portrait', label: 'Portrait' }, { value: 'landscape', label: 'Landscape' }], setup.orientation);
		const fontSize = number('psFontSize', setup.fontSize, 6, 16, 0.5);
		paper.appendChild(labelled('Paper', size));
		paper.appendChild(labelled('Orientation', orientation));
		paper.appendChild(labelled('Header/footer text (pt)', fontSize));
		wrap.appendChild(paper);

		const marginRow = document.createElement('div');
		marginRow.className = 'ps-row';
		const margins = {};
		['top', 'right', 'bottom', 'left'].forEach(function (side) {
			margins[side] = number('psMargin' + side.charAt(0).toUpperCase() + side.slice(1), setup.margins[side], 0, 3, 0.05);
			marginRow.appendChild(labelled(side.charAt(0).toUpperCase() + side.slice(1) + ' margin (in)', margins[side]));
		});
		wrap.appendChild(marginRow);

		// Token bar: inserts into whichever header/footer box was used last.
		let lastSlot = null;
		const tokens = document.createElement('div');
		tokens.className = 'ps-tokens';
		const tokenLabel = document.createElement('span');
		tokenLabel.textContent = 'Insert: ';
		tokens.appendChild(tokenLabel);
		function insertToken(token) {
			const box = lastSlot || document.getElementById('psFooterRight');
			const start = box.selectionStart == null ? box.value.length : box.selectionStart;
			const end = box.selectionEnd == null ? box.value.length : box.selectionEnd;
			box.value = box.value.slice(0, start) + token + box.value.slice(end);
			box.focus();
			box.setSelectionRange(start + token.length, start + token.length);
		}
		['[Page]', '[Pages]', '[Date]', '[Logo]'].forEach(function (token) {
			const button = document.createElement('button');
			button.type = 'button';
			button.className = 'ps-token';
			button.setAttribute('data-token', token);
			button.textContent = token;
			button.addEventListener('mousedown', function (e) { e.preventDefault(); });
			button.addEventListener('click', function () { insertToken(token); });
			tokens.appendChild(button);
		});
		const fieldPick = select('psField', [{ value: '', label: 'Field\u2026' }].concat(mappablePaths().map(function (p) {
			return { value: p.path, label: labelFor(p.path) + ' (' + p.path + ')' };
		})), '');
		fieldPick.addEventListener('change', function () {
			if (!fieldPick.value) return;
			insertToken('{' + fieldPick.value + '}');
			fieldPick.value = '';
		});
		tokens.appendChild(fieldPick);
		wrap.appendChild(tokens);

		const grid = document.createElement('div');
		grid.className = 'ps-hf';
		['', 'Left', 'Center', 'Right'].forEach(function (text) {
			const head = document.createElement('div');
			head.className = 'ps-hf-head';
			head.textContent = text;
			grid.appendChild(head);
		});
		const slotBoxes = {};
		HF_ROWS.forEach(function (row) {
			const name = document.createElement('div');
			name.className = 'ps-hf-name' + (row.group ? ' ps-group-' + row.group : '');
			name.textContent = row.label;
			grid.appendChild(name);
			slotBoxes[row.part] = {};
			HF_SLOTS.forEach(function (slot) {
				const box = document.createElement('input');
				box.type = 'text';
				box.id = 'ps' + row.id + SLOT_IDS[slot];
				box.maxLength = 200;
				box.value = setup[row.part][slot];
				box.className = 'ps-slot' + (row.group ? ' ps-group-' + row.group : '');
				box.setAttribute('aria-label', row.label + ' ' + slot);
				box.addEventListener('focus', function () { lastSlot = box; });
				slotBoxes[row.part][slot] = box;
				grid.appendChild(box);
			});
		});
		wrap.appendChild(grid);

		const options = document.createElement('div');
		options.className = 'ps-row';
		function check(id, text, checked) {
			const label = document.createElement('label');
			label.className = 'find-option';
			const box = document.createElement('input');
			box.type = 'checkbox';
			box.id = id;
			box.checked = checked;
			label.appendChild(box);
			label.appendChild(document.createTextNode(' ' + text));
			options.appendChild(label);
			return box;
		}
		const differentFirst = check('psDifferentFirst', 'Different first page', setup.differentFirst);
		const differentEven = check('psDifferentEven', 'Different odd and even pages', setup.differentEven);
		wrap.appendChild(options);
		function showGroups() {
			grid.querySelectorAll('.ps-group-first').forEach(function (el) { el.hidden = !differentFirst.checked; });
			grid.querySelectorAll('.ps-group-even').forEach(function (el) { el.hidden = !differentEven.checked; });
		}
		differentFirst.addEventListener('change', showGroups);
		differentEven.addEventListener('change', showGroups);
		showGroups();

		const error = document.createElement('div');
		error.id = 'psError';
		error.className = 'model-error';
		wrap.appendChild(error);

		const actions = document.createElement('div');
		actions.className = 'model-actions';
		const apply = modalButton('Apply', 'psApply', 'primary');
		const reset = modalButton('Use Standard Setup', 'psReset');
		reset.title = 'Remove this page setup: Letter paper with the standard footer';
		reset.hidden = !existing;
		const cancel = modalButton('Cancel', 'psCancel');
		actions.appendChild(apply);
		actions.appendChild(reset);
		actions.appendChild(cancel);
		wrap.appendChild(actions);

		function readForm() {
			const result = {
				size: size.value, orientation: orientation.value, fontSize: Number(fontSize.value),
				margins: {}, differentFirst: differentFirst.checked, differentEven: differentEven.checked
			};
			const problems = [];
			['top', 'right', 'bottom', 'left'].forEach(function (side) {
				const n = Number(margins[side].value);
				if (margins[side].value === '' || !isFinite(n) || n < 0 || n > 3) problems.push('The ' + side + ' margin must be between 0 and 3 inches.');
				result.margins[side] = n;
			});
			if (!isFinite(result.fontSize) || result.fontSize < 6 || result.fontSize > 16) problems.push('Header/footer text must be between 6 and 16 pt.');
			HF_ROWS.forEach(function (row) {
				result[row.part] = {};
				HF_SLOTS.forEach(function (slot) { result[row.part][slot] = slotBoxes[row.part][slot].value; });
			});
			return { setup: result, problems: problems };
		}

		apply.addEventListener('click', function () {
			const form = readForm();
			if (form.problems.length) {
				error.textContent = form.problems.join(' ');
				return;
			}
			const next = normalizeSetup(form.setup);
			const component = pageSetupComponent();
			if (component) component.set('setup', next);
			else editor.getWrapper().append({ type: 'page-setup', setup: next }, { at: 0 });
			fitDevice();
			editor.Modal.close();
			setStatus('Page setup: ' + PAGE_SIZES[next.size].label + ', ' + next.orientation + '. Preview to see the header and footer.');
		});
		reset.addEventListener('click', function () {
			const component = pageSetupComponent();
			if (component) component.remove();
			fitDevice();
			editor.Modal.close();
			setStatus('Page setup removed: the standard Letter page and footer are used.');
		});
		cancel.addEventListener('click', function () { editor.Modal.close(); });
		editor.Modal.open({ title: 'Page setup', content: wrap });
		editor.Modal.onceClose(function () { wrap.remove(); });
		size.focus();
	}

	document.getElementById('btnPageSetup').addEventListener('click', function () {
		if (isClause()) {
			setStatus('Page setup belongs to templates: a clause prints with the template that includes it.');
			return;
		}
		openPageSetup();
	});

	// ---- Watermark dialog ---------------------------------------------------------------------------------------
	function watermarkComponent() {
		return editor.getWrapper().findType('watermark')[0] || null;
	}

	function openWatermark() {
		document.querySelectorAll('.watermark-dialog').forEach(function (el) { el.remove(); });
		const existing = watermarkComponent();
		const w = normalizeWatermark(existing ? existing.get('watermark') : null);
		const wrap = document.createElement('div');
		wrap.className = 'watermark-dialog';
		const help = document.createElement('p');
		help.className = 'model-help';
		help.textContent = 'A large see-through word printed across the middle of every page, over the content. ' +
			'Print it always, or only when the data says so (for example VOID when the policy is cancelled).';
		wrap.appendChild(help);

		const custom = WM_PRESETS.indexOf(w.text) < 0;
		const textRow = document.createElement('div');
		textRow.className = 'ps-row';
		const preset = selectBox('wmPreset', WM_PRESETS.map(function (p) { return { value: p, label: p }; })
			.concat([{ value: 'custom', label: 'Other text\u2026' }]), custom ? 'custom' : w.text);
		const customText = document.createElement('input');
		customText.type = 'text';
		customText.id = 'wmCustom';
		customText.maxLength = 40;
		customText.value = custom ? w.text : '';
		const customField = labelled('Text', customText);
		textRow.appendChild(labelled('Watermark', preset));
		textRow.appendChild(customField);
		wrap.appendChild(textRow);
		function showCustom() { customField.hidden = preset.value !== 'custom'; }
		preset.addEventListener('change', function () {
			showCustom();
			if (preset.value === 'custom') customText.focus();
		});
		showCustom();

		const look = document.createElement('div');
		look.className = 'ps-row';
		const boxes = {};
		[['color', 'Color'], ['strength', 'Strength'], ['size', 'Size'], ['angle', 'Angle']].forEach(function (pair) {
			const key = pair[0];
			boxes[key] = selectBox('wm' + pair[1], WM_OPTIONS[key].map(function (o) { return { value: o.id, label: o.label }; }), w[key]);
			look.appendChild(labelled(pair[1], boxes[key]));
		});
		wrap.appendChild(look);

		const whenRow = document.createElement('div');
		whenRow.className = 'ps-row';
		const when = selectBox('wmWhen', [{ value: 'always', label: 'On every document' }, { value: 'condition', label: 'Only when\u2026' }],
			w.field ? 'condition' : 'always');
		const field = selectBox('wmField', [{ value: '', label: 'Choose a field\u2026' }].concat(mappablePaths().map(function (p) {
			return { value: p.path, label: labelFor(p.path) + ' (' + p.path + ')' };
		})), '');
		if (w.field && !Array.prototype.some.call(field.options, function (o) { return o.value === w.field; })) {
			const option = document.createElement('option');
			option.value = w.field;
			option.textContent = w.field;
			field.appendChild(option);
		}
		field.value = w.field;
		const operator = selectBox('wmOperator', CONDITIONS.map(function (c) { return { value: c.id, label: c.label }; }), w.operator);
		const value = document.createElement('input');
		value.type = 'text';
		value.id = 'wmValue';
		value.maxLength = 100;
		value.value = w.value;
		const fieldLabel = labelled('Field', field);
		const operatorLabel = labelled('Condition', operator);
		const valueLabel = labelled('Value', value);
		whenRow.appendChild(labelled('Print', when));
		whenRow.appendChild(fieldLabel);
		whenRow.appendChild(operatorLabel);
		whenRow.appendChild(valueLabel);
		wrap.appendChild(whenRow);
		function showCondition() {
			const conditional = when.value === 'condition';
			fieldLabel.hidden = !conditional;
			operatorLabel.hidden = !conditional;
			valueLabel.hidden = !conditional || !CONDITION_OPERATORS[operator.value];
		}
		when.addEventListener('change', showCondition);
		operator.addEventListener('change', showCondition);
		showCondition();

		const error = document.createElement('div');
		error.id = 'wmError';
		error.className = 'model-error';
		wrap.appendChild(error);

		const actions = document.createElement('div');
		actions.className = 'model-actions';
		const apply = modalButton('Apply', 'wmApply', 'primary');
		const remove = modalButton('Remove Watermark', 'wmRemove');
		remove.hidden = !existing;
		const cancel = modalButton('Cancel', 'wmCancel');
		actions.appendChild(apply);
		actions.appendChild(remove);
		actions.appendChild(cancel);
		wrap.appendChild(actions);

		apply.addEventListener('click', function () {
			const text = preset.value === 'custom' ? customText.value.replace(/\s+/g, ' ').trim() : preset.value;
			const problems = [];
			if (!text) problems.push('Type the watermark text.');
			if (when.value === 'condition' && !field.value) problems.push('Choose the field the watermark depends on.');
			if (problems.length) {
				error.textContent = problems.join(' ');
				return;
			}
			const conditional = when.value === 'condition';
			const next = normalizeWatermark({
				text: text, color: boxes.color.value, strength: boxes.strength.value, size: boxes.size.value, angle: boxes.angle.value,
				field: conditional ? field.value : '', operator: conditional ? operator.value : 'present',
				value: conditional && CONDITION_OPERATORS[operator.value] ? value.value : ''
			});
			const component = watermarkComponent();
			if (component) component.set('watermark', next);
			else editor.getWrapper().append({ type: 'watermark', watermark: next });
			editor.Modal.close();
			setStatus('Watermark: ' + next.text + (next.field ? ', when ' + watermarkCondition(next) : ', on every page') + '.');
		});
		remove.addEventListener('click', function () {
			const component = watermarkComponent();
			if (component) component.remove();
			editor.Modal.close();
			setStatus('Watermark removed.');
		});
		cancel.addEventListener('click', function () { editor.Modal.close(); });
		editor.Modal.open({ title: 'Watermark', content: wrap });
		editor.Modal.onceClose(function () { wrap.remove(); });
		preset.focus();
	}

	document.getElementById('btnWatermark').addEventListener('click', function () {
		if (isClause()) {
			setStatus('Watermarks belong to templates: a clause prints with the template that includes it.');
			return;
		}
		openWatermark();
	});

	// ---- Format dialog: number (decimals, thousands, negatives, zero text), date or mask, with a language ---------
	const DATE_PRESETS = [
		{ value: 'MM/dd/yyyy', label: '07/01/2026' },
		{ value: 'M/d/yyyy', label: '7/1/2026' },
		{ value: 'MMMM d, yyyy', label: 'July 1, 2026' },
		{ value: 'MMM d, yyyy', label: 'Jul 1, 2026' },
		{ value: 'dddd, MMMM d, yyyy', label: 'Wednesday, July 1, 2026' },
		{ value: 'yyyy-MM-dd', label: '2026-07-01' },
		{ value: "d 'de' MMMM 'de' yyyy", label: '1 de julio de 2026 (with Spanish)' },
		{ value: '', label: 'Other (type the pattern below)' }
	];
	const MASK_PRESETS = [
		{ value: '(###) ###-####', label: 'Phone (555) 123-4567' },
		{ value: '#####-####', label: 'ZIP+4 98022-1234' },
		{ value: '##-#######', label: 'FEIN 91-1234567' },
		{ value: '***-**-####', label: 'SSN, last four only ***-**-6789' },
		{ value: '', label: 'Other (type the mask below)' }
	];

	// Number builder => .NET pattern: positive;negative;zero sections when negatives or zero need their own look.
	function numberPattern(o) {
		const base = (o.thousands ? '#,##0' : '0') + (o.decimals > 0 ? '.' + '0'.repeat(o.decimals) : '');
		const positive = (o.style === 'currency' ? '$' : '') + base + (o.style === 'percent' ? '%' : '');
		const zero = String(o.zero || '').replace(/['"{}\\;]/g, '').trim();
		if (!zero && o.negative !== 'parentheses') return positive;
		const negative = o.negative === 'parentheses' ? '(' + positive + ')' : '-' + positive;
		return positive + ';' + negative + (zero ? ";'" + zero + "'" : '');
	}

	// The builder settings a number pattern was most likely made from (to reopen the dialog).
	function numberOptions(pattern) {
		const sections = pattern.split(';');
		const first = sections[0] || '';
		const decimals = (/\.(0+)/.exec(first) || ['', ''])[1].length;
		const zero = sections[2] ? sections[2].replace(/^'|'$/g, '') : '';
		return {
			style: first.indexOf('$') >= 0 ? 'currency' : first.indexOf('%') >= 0 ? 'percent' : 'number',
			decimals: Math.min(decimals, 4),
			thousands: first.indexOf(',') >= 0,
			negative: (sections[1] || '').indexOf('(') >= 0 ? 'parentheses' : 'minus',
			zero: zero
		};
	}

	function openFormatDialog(component) {
		document.querySelectorAll('.format-dialog').forEach(function (el) { el.remove(); });
		const current = parseCustomFormat(component.get('format')) || { kind: 'number', culture: 'en-US', pattern: '#,##0.00' };
		const kind0 = current.kind === 'num' ? 'number' : current.kind;
		const field = component.get('field');
		const sample = field ? sampleValue(field) : undefined;
		const wrap = document.createElement('div');
		wrap.className = 'format-dialog';
		const help = document.createElement('p');
		help.className = 'model-help';
		help.textContent = 'Choose how the value prints. The same format is used in the PDF and in the Word template export ' +
			'(masks and languages other than English (US) are PDF only for now).';
		wrap.appendChild(help);

		function number(id, value, min, max) {
			const el = document.createElement('input');
			el.type = 'number';
			el.id = id;
			el.min = String(min);
			el.max = String(max);
			el.value = String(value);
			return el;
		}
		function text(id, value, max) {
			const el = document.createElement('input');
			el.type = 'text';
			el.id = id;
			el.maxLength = max;
			el.value = value;
			return el;
		}
		function row() {
			const el = document.createElement('div');
			el.className = 'ps-row';
			wrap.appendChild(el);
			return el;
		}

		const kindRow = row();
		const kind = selectBox('fmtKind', [{ value: 'number', label: 'Number' }, { value: 'date', label: 'Date' }, { value: 'mask', label: 'Text mask (phone, ZIP...)' }], kind0);
		const culture = selectBox('fmtCulture', FORMAT_CULTURES.map(function (c) { return { value: c.id, label: c.label }; }), current.culture);
		kindRow.appendChild(labelled('Kind', kind));
		const cultureField = labelled('Language', culture);
		kindRow.appendChild(cultureField);

		const numberRow = row();
		numberRow.classList.add('fmt-number');
		const n = numberOptions(current.kind === 'num' ? current.pattern : '#,##0.00');
		const style = selectBox('fmtStyle', [{ value: 'number', label: 'Number' }, { value: 'currency', label: 'Currency ($)' }, { value: 'percent', label: 'Percent (x 100)' }], n.style);
		const decimals = number('fmtDecimals', n.decimals, 0, 4);
		const thousands = document.createElement('input');
		thousands.type = 'checkbox';
		thousands.id = 'fmtThousands';
		thousands.checked = n.thousands;
		const negative = selectBox('fmtNegative', [{ value: 'minus', label: '-1,234' }, { value: 'parentheses', label: '(1,234)' }], n.negative);
		const zero = text('fmtZero', n.zero, 30);
		zero.placeholder = 'e.g. None or Included';
		numberRow.appendChild(labelled('Style', style));
		numberRow.appendChild(labelled('Decimals', decimals));
		numberRow.appendChild(labelled('Thousands separator', thousands));
		numberRow.appendChild(labelled('Negative numbers', negative));
		numberRow.appendChild(labelled('When zero, print', zero));

		const dateRow = row();
		dateRow.classList.add('fmt-date');
		const datePreset = selectBox('fmtDatePreset', DATE_PRESETS, current.kind === 'date' && DATE_PRESETS.some(function (p) { return p.value === current.pattern; }) ? current.pattern : current.kind === 'date' ? '' : 'MMMM d, yyyy');
		dateRow.appendChild(labelled('Date style', datePreset));

		const maskRow = row();
		maskRow.classList.add('fmt-mask');
		const maskPreset = selectBox('fmtMaskPreset', MASK_PRESETS, current.kind === 'mask' && MASK_PRESETS.some(function (p) { return p.value === current.pattern; }) ? current.pattern : current.kind === 'mask' ? '' : '(###) ###-####');
		maskRow.appendChild(labelled('Mask (# a digit, * a hidden digit)', maskPreset));

		const patternRow = row();
		const pattern = text('fmtPattern', current.pattern, 100);
		pattern.className = 'fmt-pattern';
		const sampleBox = text('fmtSample', sample === undefined || sample === null || typeof sample === 'object' ? '' : String(sample), 60);
		sampleBox.placeholder = 'A value to try';
		patternRow.appendChild(labelled('Pattern', pattern));
		patternRow.appendChild(labelled('Try with', sampleBox));

		const preview = document.createElement('div');
		preview.id = 'fmtPreview';
		preview.className = 'fmt-preview';
		wrap.appendChild(preview);
		const error = document.createElement('div');
		error.id = 'fmtError';
		error.className = 'model-error';
		wrap.appendChild(error);

		const actions = document.createElement('div');
		actions.className = 'model-actions';
		const apply = modalButton('Apply', 'fmtApply', 'primary');
		const cancel = modalButton('Cancel', 'fmtCancel');
		actions.appendChild(apply);
		actions.appendChild(cancel);
		wrap.appendChild(actions);

		function custom() {
			const k = kind.value === 'number' ? 'num' : kind.value;
			return { kind: k, culture: k === 'mask' ? 'en-US' : culture.value, pattern: cleanPattern(pattern.value).slice(0, k === 'mask' ? 40 : 100) };
		}
		function build() {
			if (kind.value === 'number') {
				pattern.value = numberPattern({ style: style.value, decimals: Math.max(0, Math.min(4, Number(decimals.value) || 0)), thousands: thousands.checked, negative: negative.value, zero: zero.value });
			} else if (kind.value === 'date' && datePreset.value) {
				pattern.value = datePreset.value;
			} else if (kind.value === 'mask' && maskPreset.value) {
				pattern.value = maskPreset.value;
			}
			refresh();
		}
		function sampleValues() {
			const raw = sampleBox.value.trim();
			if (kind.value === 'number') {
				const v = raw !== '' && !isNaN(Number(raw)) ? Number(raw) : 1234.5;
				return [v, -Math.abs(v) || -1234.5, 0];
			}
			if (kind.value === 'date') return [raw || '2026-07-01'];
			return [raw || '5551234567'];
		}
		let timer = null;
		function refresh() {
			numberRow.hidden = kind.value !== 'number';
			dateRow.hidden = kind.value !== 'date';
			maskRow.hidden = kind.value !== 'mask';
			cultureField.hidden = kind.value === 'mask';
			clearTimeout(timer);
			timer = setTimeout(async function () {
				const c = custom();
				const values = sampleValues();
				error.textContent = '';
				if (!c.pattern) {
					preview.innerHTML = '';
					error.textContent = 'Type a pattern.';
					return;
				}
				try {
					const results = await previewFormats(values.map(function (v) { return { value: v, custom: c }; }));
					preview.innerHTML = '';
					results.forEach(function (r, i) {
						const line = document.createElement('div');
						line.className = 'fmt-preview-line';
						const from = document.createElement('code');
						from.textContent = String(values[i]);
						const to = document.createElement('strong');
						to.textContent = r.error ? '' : (r.text === '' ? '(blank)' : r.text);
						line.appendChild(from);
						line.appendChild(document.createTextNode(' \u2192 '));
						line.appendChild(to);
						preview.appendChild(line);
						if (r.error) error.textContent = r.error;
					});
				} catch (e) {
					error.textContent = e.message;
				}
			}, 150);
		}
		[style, negative, datePreset, maskPreset].forEach(function (el) { el.addEventListener('change', build); });
		[decimals, zero].forEach(function (el) { el.addEventListener('input', build); });
		thousands.addEventListener('change', build);
		kind.addEventListener('change', build);
		culture.addEventListener('change', refresh);
		pattern.addEventListener('input', refresh);
		sampleBox.addEventListener('input', refresh);

		apply.addEventListener('click', async function () {
			const c = custom();
			if (!c.pattern) {
				error.textContent = 'Type a pattern.';
				return;
			}
			try {
				const check = await previewFormats([{ value: 1, custom: c }]);
				if (check[0].error) {
					error.textContent = check[0].error;
					return;
				}
			} catch (e) {
				error.textContent = e.message;
				return;
			}
			component.set('format', encodeCustomFormat(c));
			editor.Modal.close();
			setStatus('Format: ' + describeCustomFormat(c) + '.');
		});
		cancel.addEventListener('click', function () { editor.Modal.close(); });
		editor.Modal.open({ title: 'Custom format', content: wrap });
		editor.Modal.onceClose(function () { wrap.remove(); });
		refresh();
		kind.focus();
	}

	// ---- Conditional styling dialog -----------------------------------------------------------------------------
	// Fields a rule on this element can test: the document's own, plus the item fields of the repeats around it.
	function fieldsInScope(component) {
		const fields = mappablePaths().map(function (p) { return p.path; });
		for (let p = component; p; p = p.parent()) {
			if ((p.is('repeat') || p.is('data-table')) && p.get('collection') && p.get('alias')) {
				const list = schema.collections.find(function (c) { return c.path === p.get('collection'); });
				if (list) {
					list.fields.forEach(function (f) {
						const path = p.get('alias') + f.slice(list.alias.length);
						if (fields.indexOf(path) < 0) fields.unshift(path);
					});
				}
			}
		}
		return fields;
	}

	function openCondStyles(component) {
		document.querySelectorAll('.cond-style-dialog').forEach(function (el) { el.remove(); });
		let rules = normalizeCondStyles(component.get('condStyles'));
		const fields = fieldsInScope(component);
		const wrap = document.createElement('div');
		wrap.className = 'cond-style-dialog';
		const help = document.createElement('p');
		help.className = 'model-help';
		help.textContent = 'Style the selected ' + (component.getName ? component.getName() : 'element').toLowerCase() +
			' differently when the data says so: for example red text when the loss ratio is greater than 1, or a yellow row for open claims. ' +
			'Each rule that holds adds its style; inside a list, the rules are checked for every item.';
		wrap.appendChild(help);
		const list = document.createElement('div');
		list.className = 'cs-rules';
		wrap.appendChild(list);
		const add = modalButton('Add Rule', 'csAdd');
		wrap.appendChild(add);
		const error = document.createElement('div');
		error.id = 'csError';
		error.className = 'model-error';
		wrap.appendChild(error);
		const actions = document.createElement('div');
		actions.className = 'model-actions';
		const apply = modalButton('Apply', 'csApply', 'primary');
		const clear = modalButton('Remove All Rules', 'csClear');
		const cancel = modalButton('Cancel', 'csCancel');
		actions.appendChild(apply);
		actions.appendChild(clear);
		actions.appendChild(cancel);
		wrap.appendChild(actions);

		function read() {
			return Array.prototype.map.call(list.querySelectorAll('.cs-rule'), function (row, i) {
				return {
					field: document.getElementById('csField' + i).value,
					operator: document.getElementById('csOperator' + i).value,
					value: document.getElementById('csValue' + i).value,
					style: document.getElementById('csStyle' + i).value
				};
			});
		}
		function render() {
			list.innerHTML = '';
			if (!rules.length) {
				const none = document.createElement('p');
				none.className = 'cs-none';
				none.textContent = 'No rules yet. Click Add Rule.';
				list.appendChild(none);
			}
			rules.forEach(function (rule, i) {
				const row = document.createElement('div');
				row.className = 'cs-rule ps-row';
				const fieldOptions = [{ value: '', label: 'Choose a field\u2026' }].concat(fields.map(function (f) { return { value: f, label: labelFor(f) + ' (' + f + ')' }; }));
				if (rule.field && fields.indexOf(rule.field) < 0) fieldOptions.push({ value: rule.field, label: rule.field });
				const field = selectBox('csField' + i, fieldOptions, rule.field || '');
				const operator = selectBox('csOperator' + i, CONDITIONS.map(function (c) { return { value: c.id, label: c.label }; }), rule.operator || 'eq');
				const value = document.createElement('input');
				value.type = 'text';
				value.id = 'csValue' + i;
				value.maxLength = 100;
				value.value = rule.value || '';
				const style = selectBox('csStyle' + i, COND_STYLES.map(function (s) { return { value: s.id, label: s.label }; }), rule.style || 'red-text');
				const remove = document.createElement('button');
				remove.type = 'button';
				remove.className = 'cs-remove';
				remove.textContent = 'Remove';
				remove.addEventListener('click', function () {
					rules = read();
					rules.splice(i, 1);
					render();
				});
				const valueField = labelled('Value', value);
				function showValue() { valueField.hidden = !CONDITION_OPERATORS[operator.value]; }
				operator.addEventListener('change', showValue);
				showValue();
				row.appendChild(labelled('Style', style));
				row.appendChild(labelled('When', field));
				row.appendChild(labelled('Condition', operator));
				row.appendChild(valueField);
				row.appendChild(remove);
				list.appendChild(row);
			});
			add.disabled = rules.length >= MAX_COND_STYLES;
		}
		add.addEventListener('click', function () {
			rules = read();
			rules.push({ field: '', operator: 'eq', value: '', style: 'red-text' });
			render();
		});
		apply.addEventListener('click', function () {
			const given = read();
			if (given.some(function (r) { return !r.field; })) {
				error.textContent = 'Choose the field for every rule (or remove the rule).';
				return;
			}
			const next = normalizeCondStyles(given);
			component.set('condStyles', next.length ? next : null);
			editor.Modal.close();
			setStatus(next.length ? 'Conditional styling: ' + next.map(describeCondStyle).join('; ') + '.' : 'Conditional styling removed.');
		});
		clear.addEventListener('click', function () {
			component.set('condStyles', null);
			editor.Modal.close();
			setStatus('Conditional styling removed.');
		});
		cancel.addEventListener('click', function () { editor.Modal.close(); });
		render();
		editor.Modal.open({ title: 'Conditional styling', content: wrap });
		editor.Modal.onceClose(function () { wrap.remove(); });
	}

	document.getElementById('btnCondStyle').addEventListener('click', function () {
		const selected = editor.getSelected();
		if (!selected || selected.is('wrapper')) {
			setStatus('Select an element on the page first, then choose Conditional Styling.');
			return;
		}
		openCondStyles(selected);
	});

	// ---- Themes: choose the template's theme; Publishers manage themes and fonts ---------------------------------
	function themeRefComponent() {
		return editor.getWrapper().findType('theme-ref')[0] || null;
	}

	async function loadThemes() {
		return (await ensureOk(await fetch('/api/themes'), 'Themes')).json();
	}

	// A row of color swatches and the fonts: what the theme looks like.
	function themePreview(info, theme) {
		const box = document.createElement('div');
		box.className = 'theme-preview';
		const swatches = document.createElement('div');
		swatches.className = 'theme-swatches';
		info.tokens.forEach(function (token) {
			const value = (theme && theme.colors[token.name]) || token.default;
			const swatch = document.createElement('span');
			swatch.className = 'theme-swatch' + (theme && theme.colors[token.name] ? ' changed' : '');
			swatch.style.background = value;
			swatch.title = token.name.replace(/^moe-/, '') + ' ' + value;
			swatch.setAttribute('data-token', token.name);
			swatches.appendChild(swatch);
		});
		box.appendChild(swatches);
		const fonts = document.createElement('div');
		fonts.className = 'theme-fonts';
		fonts.textContent = 'Text: ' + ((theme && theme.bodyFont) || 'Figtree') + ' \u00b7 Headings: ' + ((theme && (theme.headingFont || theme.bodyFont)) || 'Figtree');
		box.appendChild(fonts);
		if (theme && theme.description) {
			const description = document.createElement('div');
			description.className = 'model-help';
			description.textContent = theme.description;
			box.appendChild(description);
		}
		return box;
	}

	async function openThemePicker() {
		const info = await loadThemes();
		document.querySelectorAll('.theme-dialog').forEach(function (el) { el.remove(); });
		const ref = themeRefComponent();
		const current = ref ? ref.get('theme') : '';
		const wrap = document.createElement('div');
		wrap.className = 'theme-dialog';
		const help = document.createElement('p');
		help.className = 'model-help';
		help.textContent = 'A theme changes the brand colors and fonts of the whole document, for a brand or a line of business. ' +
			'Templates without a theme use the standard MOE look.';
		wrap.appendChild(help);

		const options = [{ value: '', label: 'MOE standard' }].concat(info.themes.map(function (t) { return { value: t.name, label: t.label }; }));
		if (current && !info.themes.some(function (t) { return t.name === current; })) {
			options.push({ value: current, label: current + ' (missing: the PDF can\'t be made)' });
		}
		const select = selectBox('themeSelect', options, current);
		wrap.appendChild(labelled('Theme', select));
		const preview = document.createElement('div');
		wrap.appendChild(preview);
		function showPreview() {
			preview.innerHTML = '';
			preview.appendChild(themePreview(info, info.themes.find(function (t) { return t.name === select.value; }) || null));
		}
		select.addEventListener('change', showPreview);
		showPreview();

		const actions = document.createElement('div');
		actions.className = 'model-actions';
		const apply = modalButton('Apply', 'themeApply', 'primary');
		const manage = modalButton('Manage Themes\u2026', 'themeManage');
		if (!hasRole('Publisher')) {
			manage.disabled = true;
			manage.title = 'needs the Publisher role';
		}
		const cancel = modalButton('Cancel', 'themeCancel');
		actions.appendChild(apply);
		actions.appendChild(manage);
		actions.appendChild(cancel);
		wrap.appendChild(actions);

		apply.addEventListener('click', function () {
			const name = select.value;
			const component = themeRefComponent();
			if (!name) {
				if (component) component.remove();
			} else if (component) {
				component.set('theme', name);
			} else {
				editor.getWrapper().append({ type: 'theme-ref', theme: name }, { at: 0 });
			}
			editor.Modal.close();
			setStatus('Theme: ' + (select.options[select.selectedIndex].textContent) + '.');
		});
		manage.addEventListener('click', function () {
			editor.Modal.close();
			run('Loading themes', async function () {
				await openThemeManager(select.value);
				return 'Edit a theme, or start a new one.';
			});
		});
		cancel.addEventListener('click', function () { editor.Modal.close(); });
		editor.Modal.open({ title: 'Theme', content: wrap });
		editor.Modal.onceClose(function () { wrap.remove(); });
		select.focus();
	}

	async function openThemeManager(selected) {
		let info = await loadThemes();
		document.querySelectorAll('.theme-manager').forEach(function (el) { el.remove(); });
		const wrap = document.createElement('div');
		wrap.className = 'theme-manager';

		const pickRow = document.createElement('div');
		pickRow.className = 'ps-row';
		const pick = selectBox('tmTheme', [], '');
		pickRow.appendChild(labelled('Theme', pick));
		wrap.appendChild(pickRow);

		const fieldsRow = document.createElement('div');
		fieldsRow.className = 'ps-row';
		function textBox(id, max) {
			const el = document.createElement('input');
			el.type = 'text';
			el.id = id;
			el.maxLength = max;
			return el;
		}
		const nameBox = textBox('tmName', 64);
		const labelBox = textBox('tmLabel', 60);
		const descriptionBox = textBox('tmDescription', 200);
		descriptionBox.className = 'tm-description';
		fieldsRow.appendChild(labelled('Short name (in templates)', nameBox));
		fieldsRow.appendChild(labelled('Name', labelBox));
		fieldsRow.appendChild(labelled('Description (brand, line of business)', descriptionBox));
		wrap.appendChild(fieldsRow);

		const colors = document.createElement('div');
		colors.className = 'tm-colors';
		const colorBoxes = {};
		info.tokens.forEach(function (token) {
			const row = document.createElement('label');
			row.className = 'tm-color';
			const input = document.createElement('input');
			input.type = 'color';
			input.id = 'tmColor-' + token.name;
			input.setAttribute('data-token', token.name);
			const text = document.createElement('span');
			text.textContent = token.name.replace(/^moe-/, '').replace(/-/g, ' ');
			const reset = document.createElement('button');
			reset.type = 'button';
			reset.className = 'tm-reset';
			reset.textContent = '\u21ba';
			reset.title = 'Back to the standard ' + token.default;
			reset.addEventListener('click', function (e) {
				e.preventDefault();
				input.value = token.default;
				row.classList.remove('changed');
			});
			input.addEventListener('input', function () { row.classList.toggle('changed', input.value.toLowerCase() !== token.default); });
			row.appendChild(input);
			row.appendChild(text);
			row.appendChild(reset);
			colors.appendChild(row);
			colorBoxes[token.name] = { input: input, row: row, token: token };
		});
		wrap.appendChild(colors);

		const fontRow = document.createElement('div');
		fontRow.className = 'ps-row';
		const bodyFont = selectBox('tmBodyFont', [], '');
		const headingFont = selectBox('tmHeadingFont', [], '');
		fontRow.appendChild(labelled('Text font', bodyFont));
		fontRow.appendChild(labelled('Heading font', headingFont));
		wrap.appendChild(fontRow);

		// Uploaded fonts: list + upload.
		const fontBox = document.createElement('details');
		fontBox.className = 'tm-font-box';
		const fontSummary = document.createElement('summary');
		fontSummary.textContent = 'Uploaded fonts';
		fontBox.appendChild(fontSummary);
		const fontList = document.createElement('ul');
		fontList.className = 'tm-fonts';
		fontBox.appendChild(fontList);
		const uploadRow = document.createElement('div');
		uploadRow.className = 'ps-row';
		const fontFile = document.createElement('input');
		fontFile.type = 'file';
		fontFile.id = 'tmFontFile';
		fontFile.accept = '.woff2,.woff,.ttf,.otf';
		const fontFamily = textBox('tmFontFamily', 60);
		const fontWeight = selectBox('tmFontWeight', [100, 200, 300, 400, 500, 600, 700, 800, 900].map(function (w) { return { value: String(w), label: String(w) + (w === 400 ? ' (regular)' : w === 700 ? ' (bold)' : '') }; }), '400');
		const fontStyle = selectBox('tmFontStyle', [{ value: 'normal', label: 'Normal' }, { value: 'italic', label: 'Italic' }], 'normal');
		const upload = modalButton('Upload Font', 'tmFontUpload');
		fontFile.addEventListener('change', function () {
			const file = fontFile.files && fontFile.files[0];
			if (file && !fontFamily.value) fontFamily.value = file.name.replace(/\.[^.]+$/, '').replace(/[-_](regular|bold|italic|light|medium|semibold|black|thin|\d+)+$/i, '').replace(/[^A-Za-z0-9 -]/g, ' ').trim();
		});
		uploadRow.appendChild(labelled('Font file (WOFF2, WOFF, TTF, OTF)', fontFile));
		uploadRow.appendChild(labelled('Family', fontFamily));
		uploadRow.appendChild(labelled('Weight', fontWeight));
		uploadRow.appendChild(labelled('Style', fontStyle));
		uploadRow.appendChild(upload);
		fontBox.appendChild(uploadRow);
		wrap.appendChild(fontBox);

		const error = document.createElement('div');
		error.id = 'tmError';
		error.className = 'model-error';
		wrap.appendChild(error);

		const actions = document.createElement('div');
		actions.className = 'model-actions';
		const save = modalButton('Save Theme', 'tmSave', 'primary');
		const remove = modalButton('Delete Theme', 'tmDelete');
		const close = modalButton('Close', 'tmClose');
		actions.appendChild(save);
		actions.appendChild(remove);
		actions.appendChild(close);
		wrap.appendChild(actions);

		function families() {
			const uploaded = [];
			info.fonts.forEach(function (f) { if (uploaded.indexOf(f.family) < 0) uploaded.push(f.family); });
			return info.systemFonts.filter(function (f) { return f !== 'Figtree'; }).concat(uploaded);
		}
		function fillFonts(select, value, standardLabel) {
			const keep = value != null ? value : select.value;
			select.innerHTML = '';
			[{ value: '', label: standardLabel }].concat(families().map(function (f) { return { value: f, label: f }; })).forEach(function (o) {
				const option = document.createElement('option');
				option.value = o.value;
				option.textContent = o.label;
				select.appendChild(option);
			});
			select.value = keep || '';
		}
		function fillFontList() {
			fontList.innerHTML = '';
			if (!info.fonts.length) {
				const none = document.createElement('li');
				none.textContent = 'No fonts uploaded yet.';
				fontList.appendChild(none);
			}
			info.fonts.forEach(function (f) {
				const item = document.createElement('li');
				item.textContent = f.family + ' ' + f.weight + (f.style === 'italic' ? ' italic' : '') + ' (' + f.format + ', ' + Math.round(f.bytes / 1024) + ' KB) ';
				const del = document.createElement('button');
				del.type = 'button';
				del.className = 'tm-font-delete';
				del.setAttribute('data-font-id', f.id);
				del.textContent = 'Delete';
				del.addEventListener('click', async function () {
					error.textContent = '';
					const response = await fetch('/api/themes/fonts/' + encodeURIComponent(f.id), { method: 'DELETE' });
					if (!response.ok) {
						error.textContent = await errorText(response, 'Delete font');
						return;
					}
					info = await loadThemes();
					refresh(pick.value);
				});
				item.appendChild(del);
				fontList.appendChild(item);
			});
		}
		function fillPick(value) {
			pick.innerHTML = '';
			[{ value: '', label: 'New theme\u2026' }].concat(info.themes.map(function (t) { return { value: t.name, label: t.label + ' (' + t.name + ')' }; })).forEach(function (o) {
				const option = document.createElement('option');
				option.value = o.value;
				option.textContent = o.label;
				pick.appendChild(option);
			});
			pick.value = info.themes.some(function (t) { return t.name === value; }) ? value : '';
		}
		function showTheme() {
			const theme = info.themes.find(function (t) { return t.name === pick.value; }) || null;
			nameBox.value = theme ? theme.name : '';
			nameBox.disabled = !!theme;
			labelBox.value = theme ? theme.label : '';
			descriptionBox.value = theme ? (theme.description || '') : '';
			Object.keys(colorBoxes).forEach(function (key) {
				const box = colorBoxes[key];
				box.input.value = (theme && theme.colors[key]) || box.token.default;
				box.row.classList.toggle('changed', !!(theme && theme.colors[key] && theme.colors[key] !== box.token.default));
			});
			fillFonts(bodyFont, theme ? (theme.bodyFont || '') : '', 'Figtree (standard)');
			fillFonts(headingFont, theme ? (theme.headingFont || '') : '', 'Same as the text');
			remove.hidden = !theme;
			error.textContent = '';
		}
		function refresh(value) {
			fillPick(value);
			fillFontList();
			showTheme();
		}
		pick.addEventListener('change', showTheme);

		upload.addEventListener('click', async function () {
			error.textContent = '';
			const file = fontFile.files && fontFile.files[0];
			if (!file) {
				error.textContent = 'Choose a font file to upload.';
				return;
			}
			upload.disabled = true;
			try {
				const query = '?family=' + encodeURIComponent(fontFamily.value.trim()) + '&weight=' + fontWeight.value + '&style=' + fontStyle.value;
				const response = await fetch('/api/themes/fonts' + query, {
					method: 'POST',
					headers: { 'Content-Type': 'application/octet-stream' },
					body: file
				});
				if (!response.ok) {
					error.textContent = await errorText(response, 'Upload font');
					return;
				}
				const font = await response.json();
				info = await loadThemes();
				const body = bodyFont.value;
				const heading = headingFont.value;
				fillFontList();
				fillFonts(bodyFont, body, 'Figtree (standard)');
				fillFonts(headingFont, heading, 'Same as the text');
				fontFile.value = '';
				fontFamily.value = '';
				setStatus('Uploaded ' + font.family + ' ' + font.weight + (font.style === 'italic' ? ' italic' : '') + '.');
			} finally {
				upload.disabled = false;
			}
		});

		save.addEventListener('click', async function () {
			error.textContent = '';
			const name = nameBox.value.trim();
			if (!THEME_NAME.test(name)) {
				error.textContent = 'The short name may only contain letters, numbers, "-" and "_".';
				return;
			}
			const overrides = {};
			Object.keys(colorBoxes).forEach(function (key) {
				const value = colorBoxes[key].input.value.toLowerCase();
				if (value !== colorBoxes[key].token.default) overrides[key] = value;
			});
			const response = await fetch('/api/themes/' + encodeURIComponent(name), {
				method: 'PUT',
				headers: { 'Content-Type': 'application/json' },
				body: JSON.stringify({ label: labelBox.value, description: descriptionBox.value, colors: overrides, bodyFont: bodyFont.value, headingFont: headingFont.value })
			});
			if (!response.ok) {
				error.textContent = await errorText(response, 'Save theme');
				return;
			}
			const saved = await response.json();
			info = await loadThemes();
			refresh(saved.name);
			themeCssVersion = Date.now();
			applyCanvasTheme();
			setStatus('Saved theme ' + saved.label + '. Templates that use it print with these colors and fonts from now on.');
		});

		remove.addEventListener('click', async function () {
			error.textContent = '';
			const name = pick.value;
			if (!name || !window.confirm('Delete the theme "' + name + '"?')) return;
			const response = await fetch('/api/themes/' + encodeURIComponent(name), { method: 'DELETE' });
			if (!response.ok) {
				const body = await response.json().catch(function () { return {}; });
				error.textContent = (body.error || 'Delete theme failed.') +
					(body.usedBy ? ' ' + body.usedBy.map(function (u) { return u.template; }).join(', ') : '');
				return;
			}
			info = await loadThemes();
			refresh('');
			setStatus('Deleted theme ' + name + '.');
		});
		close.addEventListener('click', function () { editor.Modal.close(); });

		refresh(selected);
		editor.Modal.open({ title: 'Manage themes', content: wrap });
		editor.Modal.onceClose(function () { wrap.remove(); });
	}

	async function errorText(response, what) {
		const body = await response.json().catch(function () { return {}; });
		return body.error || (what + ' failed (' + response.status + ').');
	}

	document.getElementById('btnTheme').addEventListener('click', function () {
		if (isClause()) {
			setStatus('Themes belong to templates: a clause prints with the theme of the template that includes it.');
			return;
		}
		run('Loading themes', async function () {
			await openThemePicker();
			return 'Choose the theme for this template.';
		});
	});

	// ---- Custom blocks: save a selection to the shared block library, reuse it in any template ---------------------
	const CUSTOM_BLOCK_PREFIX = 'custom-';

	function escapeHtml(text) {
		return String(text).replace(/[&<>"']/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]; });
	}

	async function refreshCustomBlocks() {
		let list = [];
		try {
			const response = featureOn('CustomBlocks') ? await fetch('/api/blocks') : null;
			if (response && response.ok) list = await response.json();
		} catch (e) { /* library unavailable: built-in blocks only */ }
		editor.Blocks.getAll().filter(function (b) { return b.get('customBlock'); }).forEach(function (b) { editor.Blocks.remove(b.getId()); });
		list.forEach(function (b) {
			// GrapesJS shows labels and categories as HTML: saved names are text.
			editor.Blocks.add(CUSTOM_BLOCK_PREFIX + b.name, {
				label: escapeHtml(b.label),
				category: escapeHtml(b.category),
				content: b.components,
				customBlock: b.name,
				customCss: b.css,
				attributes: { title: b.label + ' (saved block)', 'data-custom-block': b.name }
			});
		});
		return list;
	}

	// The class rules a dropped block needs (id styles travel inline in the components).
	editor.on('block:drag:stop', function (component, block) {
		if (component && block && block.get('customBlock') && block.get('customCss')) editor.Css.addRules(block.get('customCss'));
	});

	// A copy of the selection that can be dropped anywhere: id-based styles become the component's own style (so every
	// copy gets fresh ids), and the rules of the classes it uses come along as CSS.
	function blockFromSelection(component) {
		const json = JSON.parse(JSON.stringify(component.toJSON()));
		const classes = {};
		(function walk(node) {
			const id = node.attributes && node.attributes.id;
			if (id) {
				const rule = editor.Css.getIdRule(id);
				if (rule) node.style = Object.assign({}, rule.getStyle(), node.style || {});
				delete node.attributes.id;
			}
			(node.classes || []).forEach(function (c) { classes[typeof c === 'string' ? c : c.name] = true; });
			(node.components || []).forEach(walk);
		})(json);
		const css = editor.Css.getAll().filter(function (rule) {
			const selectors = rule.get('selectors');
			return selectors.length > 0 && !rule.get('state') && !rule.get('mediaText') && !rule.get('selectorsAdd') &&
				selectors.every(function (s) { return s.get('type') === 1 && classes[s.get('name')]; });
		}).map(function (rule) { return rule.toCSS(); }).join('\n');
		return { components: json, css: css };
	}

	function blockName(label) {
		return label.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '').slice(0, 64) || 'block';
	}

	editor.Commands.add('save-as-block', { run: function (ed) { const c = ed.getSelected(); if (c) openSaveBlock(c); } });

	// "Save as block" on the selected element's toolbar.
	editor.on('component:selected', function () {
		const selected = editor.getSelected();
		if (!selected || !selected.get('toolbar') || selected.is('wrapper')) return;
		const toolbar = selected.get('toolbar');
		if (toolbar.some(function (t) { return t.id === 'save-block'; })) return;
		selected.set('toolbar', toolbar.concat([{
			id: 'save-block',
			command: 'save-as-block',
			label: uiIcon('widgets'),
			attributes: { title: 'Save as block', class: 'tb-save-block' }
		}]));
	});

	document.getElementById('btnSaveBlock').addEventListener('click', function () {
		const selected = editor.getSelected();
		if (!selected || selected.is('wrapper')) {
			setStatus('Select something on the page first, then save it as a block.', true);
			return;
		}
		openSaveBlock(selected);
	});

	function openSaveBlock(component) {
		const wrap = document.createElement('div');
		wrap.className = 'save-block';
		const help = document.createElement('p');
		help.className = 'model-help';
		help.textContent = 'Saves the selected ' + (component.getName() || 'element') + ' and everything inside it, with its styles, ' +
			'to the shared block library. Drag it from the Blocks panel into any template; each drop is an independent copy.';
		const labelField = document.createElement('label');
		labelField.className = 'scenario-name-field';
		labelField.textContent = 'Name ';
		const label = document.createElement('input');
		label.id = 'blockLabel';
		label.maxLength = 60;
		label.placeholder = 'e.g. Policy summary box';
		labelField.appendChild(label);
		const categoryField = document.createElement('label');
		categoryField.className = 'scenario-name-field';
		categoryField.textContent = 'Category ';
		const category = document.createElement('input');
		category.id = 'blockCategory';
		category.maxLength = 40;
		category.value = 'My blocks';
		category.setAttribute('list', 'blockCategories');
		const categories = document.createElement('datalist');
		categories.id = 'blockCategories';
		const seen = {};
		editor.Blocks.getAll().forEach(function (b) {
			const c = b.get('category');
			const name = c && (typeof c === 'string' ? c : c.get('label') || c.get('id'));
			if (!name || seen[name]) return;
			seen[name] = true;
			const option = document.createElement('option');
			option.value = name;
			categories.appendChild(option);
		});
		categoryField.appendChild(category);
		categoryField.appendChild(categories);
		const error = document.createElement('div');
		error.id = 'blockError';
		error.className = 'model-error';
		const actions = document.createElement('div');
		actions.className = 'model-actions';
		const save = modalButton('Save block', 'blockSave', 'primary');
		const cancel = modalButton('Cancel', 'blockCancel');
		actions.appendChild(save);
		actions.appendChild(cancel);
		[help, labelField, categoryField, error, actions].forEach(function (el) { wrap.appendChild(el); });

		save.addEventListener('click', async function () {
			const text = label.value.trim();
			if (!text) {
				error.textContent = 'Give the block a name.';
				return;
			}
			const name = blockName(text);
			if (editor.Blocks.get(CUSTOM_BLOCK_PREFIX + name) && !window.confirm('Replace the saved block "' + text + '"?')) return;
			const block = blockFromSelection(component);
			save.disabled = true;
			try {
				const response = await fetch('/api/blocks/' + encodeURIComponent(name), {
					method: 'PUT',
					headers: { 'Content-Type': 'application/json' },
					body: JSON.stringify({ label: text, category: category.value.trim(), components: block.components, css: block.css })
				});
				const saved = await (await ensureOk(response, 'Save block')).json();
				await refreshCustomBlocks();
				editor.Modal.close();
				setStatus('Saved block "' + saved.label + '". Find it under ' + saved.category + ' in the Blocks panel.');
			} catch (err) {
				error.textContent = err.message;
				save.disabled = false;
			}
		});
		cancel.addEventListener('click', function () { editor.Modal.close(); });
		editor.Modal.open({ title: 'Save as block', content: wrap });
		editor.Modal.onceClose(function () { wrap.remove(); });
		label.focus();
	}

	document.getElementById('btnManageBlocks').addEventListener('click', function () { run('Loading saved blocks', openManageBlocks); });

	async function openManageBlocks() {
		const list = await refreshCustomBlocks();
		// Reopened after a delete: GrapesJS parks replaced modal content, so drop the old list.
		document.querySelectorAll('.manage-blocks').forEach(function (el) { el.remove(); });
		const wrap = document.createElement('div');
		wrap.className = 'manage-blocks';
		const help = document.createElement('p');
		help.className = 'model-help';
		help.textContent = list.length
			? 'Saved blocks are shared by every template. Deleting one removes it from the Blocks panel; copies already placed in templates stay.'
			: 'No saved blocks yet. Select something on the page and use Save as block (selection toolbar or the three-dot menu).';
		wrap.appendChild(help);
		const rows = document.createElement('div');
		rows.className = 'scenario-list';
		list.forEach(function (b) {
			const row = document.createElement('div');
			row.className = 'scenario-row custom-block-row';
			row.setAttribute('data-block', b.name);
			const name = document.createElement('span');
			name.className = 'scenario-name';
			name.textContent = b.label;
			const info = document.createElement('small');
			info.textContent = b.category + ' \u00b7 saved ' + formatWhen(b.savedUtc);
			const remove = modalButton('Delete', null, 'custom-block-delete danger');
			remove.addEventListener('click', function () {
				if (!window.confirm('Delete the saved block "' + b.label + '"?')) return;
				run('Deleting block', async function () {
					await ensureOk(await fetch('/api/blocks/' + encodeURIComponent(b.name), { method: 'DELETE' }), 'Delete');
					await openManageBlocks();
					return 'Deleted block "' + b.label + '".';
				});
			});
			row.appendChild(name);
			row.appendChild(info);
			row.appendChild(remove);
			rows.appendChild(row);
		});
		wrap.appendChild(rows);
		editor.Modal.open({ title: 'Saved blocks', content: wrap });
		editor.Modal.onceClose(function () { wrap.remove(); });
		return list.length + ' saved block' + (list.length === 1 ? '' : 's') + '.';
	}

	// Opens a template or clause version found elsewhere (e.g. by a field usage search), in its language ('' = English).
	function openDocument(kind, name, version, language) {
		if (!confirmDiscardChanges()) return;
		editor.Modal.close();
		run('Opening', async function () {
			docKind.value = kind;
			updateKindUi();
			nameInput.value = name;
			resetLanguage();
			if (language && LANGUAGES[language]) {
				languageSelect.value = language;
				openLang = language;
			}
			current = null;
			await refreshVersions();
			await openVersion(version);
			return 'Opened ' + (kind === 'clauses' ? 'clause ' : '') + name + (docLang() ? ' (' + langInfo().name + ')' : '') +
				' v' + current.version + ' (' + current.status + ').';
		});
	}

	// ---- Diff before publish: the canvas vs the published version (markup, CSS, printed text and both PDFs) ----------
	async function fetchDiff() {
		const exported = exportForSave();
		const response = await fetch(apiBase() + '/diff' + langQuery(), {
			method: 'POST',
			headers: { 'Content-Type': 'application/json' },
			body: JSON.stringify({ html: exported.html, css: exported.css, data: sampleData() })
		});
		return (await ensureOk(response, 'Compare')).json();
	}

	function changeCount(diff) {
		if (!diff) return 'could not be compared';
		return diff.identical ? 'no changes' : '+' + diff.added + ' \u2212' + diff.removed + ' line' + (diff.added + diff.removed === 1 ? '' : 's');
	}

	function diffSummary(d) {
		if (d.identical && d.text && d.text.identical) return 'No differences from v' + d.against.version + ' (' + d.against.status + ').';
		const pages = d.published.error || d.current.error ? ''
			: d.published.pages === d.current.pages ? ' ' + d.current.pages + ' page(s).' : ' Pages ' + d.published.pages + ' \u2192 ' + d.current.pages + '.';
		return 'Compared with v' + d.against.version + ' (' + d.against.status + '): printed text ' + changeCount(d.text) +
			', HTML ' + changeCount(d.html) + ', CSS ' + changeCount(d.css) + '.' + pages;
	}

	function diffLines(diff) {
		const pre = document.createElement('pre');
		pre.className = 'diff-lines';
		if (!diff) return pre;
		if (diff.identical) {
			pre.textContent = 'No changes.';
			return pre;
		}
		diff.lines.forEach(function (line) {
			const row = document.createElement('div');
			row.className = 'diff-' + line.type;
			row.textContent = line.type === 'skip'
				? '\u2026 ' + line.count + ' unchanged line' + (line.count === 1 ? '' : 's')
				: (line.type === 'added' ? '+ ' : line.type === 'removed' ? '\u2212 ' : '  ') + line.text;
			pre.appendChild(row);
		});
		return pre;
	}

	// Shows a diff. With publish options it asks to publish: resolves true for Publish, false for Cancel / close.
	function openDiff(d, publish) {
		const wrap = document.createElement('div');
		wrap.className = 'diff-view';
		const summary = document.createElement('p');
		summary.className = 'diff-summary';
		summary.textContent = diffSummary(d) + ' Rendered with ' + (activeScenario ? 'scenario "' + activeScenario + '"' : 'the model example data') + '.';
		wrap.appendChild(summary);
		if (publish) {
			const question = document.createElement('p');
			question.className = 'model-help';
			question.textContent = publish.question;
			wrap.appendChild(question);
		}
		const tabs = document.createElement('div');
		tabs.className = 'diff-tabs';
		const panes = document.createElement('div');
		panes.className = 'diff-panes';
		const urls = [];

		function pdfPane() {
			const pane = document.createElement('div');
			pane.className = 'diff-pdfs';
			[['Published v' + d.against.version, d.published, 'diffPublishedPdf'], ['This version', d.current, 'diffCurrentPdf']].forEach(function (side) {
				const column = document.createElement('div');
				const head = document.createElement('h4');
				head.textContent = side[0] + (side[1].error ? '' : ' \u00b7 ' + side[1].pages + ' page' + (side[1].pages === 1 ? '' : 's'));
				column.appendChild(head);
				if (side[1].error) {
					const error = document.createElement('div');
					error.className = 'model-error';
					error.textContent = side[1].error;
					column.appendChild(error);
				} else {
					const url = pdfUrl(side[1].pdf);
					urls.push(url);
					const frame = document.createElement('iframe');
					frame.id = side[2];
					frame.className = 'pdf-frame';
					frame.src = url;
					column.appendChild(frame);
				}
				pane.appendChild(column);
			});
			return pane;
		}

		const sections = [
			{ id: 'text', label: 'Printed text (' + changeCount(d.text) + ')', build: function () {
				if (d.text) return diffLines(d.text);
				const error = document.createElement('div');
				error.className = 'model-error diff-render-error';
				error.textContent = 'The printed text could not be compared. ' +
					(d.published.error ? 'Published v' + d.against.version + ': ' + d.published.error + ' ' : '') +
					(d.current.error ? 'This version: ' + d.current.error : '');
				return error;
			} },
			{ id: 'pdf', label: 'PDFs side by side', build: pdfPane },
			{ id: 'html', label: 'HTML (' + changeCount(d.html) + ')', build: function () { return diffLines(d.html); } },
			{ id: 'css', label: 'CSS (' + changeCount(d.css) + ')', build: function () { return diffLines(d.css); } }
		];
		sections.forEach(function (section, i) {
			const tab = document.createElement('button');
			tab.type = 'button';
			tab.className = 'diff-tab';
			tab.setAttribute('data-pane', section.id);
			tab.textContent = section.label;
			const pane = document.createElement('div');
			pane.className = 'diff-pane';
			pane.setAttribute('data-pane', section.id);
			pane.appendChild(section.build());
			pane.hidden = i !== 0;
			tab.classList.toggle('active', i === 0);
			tab.addEventListener('click', function () {
				tabs.querySelectorAll('.diff-tab').forEach(function (t) { t.classList.toggle('active', t === tab); });
				panes.querySelectorAll('.diff-pane').forEach(function (p) { p.hidden = p !== pane; });
			});
			tabs.appendChild(tab);
			panes.appendChild(pane);
		});
		wrap.appendChild(tabs);
		wrap.appendChild(panes);

		return new Promise(function (resolve) {
			let answer = false;
			if (publish) {
				const actions = document.createElement('div');
				actions.className = 'model-actions';
				const go = modalButton(publish.verb + ' v' + current.version, 'diffPublish', 'primary');
				const cancel = modalButton('Cancel', 'diffCancel');
				go.addEventListener('click', function () { answer = true; editor.Modal.close(); });
				cancel.addEventListener('click', function () { editor.Modal.close(); });
				actions.appendChild(go);
				actions.appendChild(cancel);
				wrap.appendChild(actions);
			}
			editor.Modal.open({ title: publish ? 'Review changes before publishing' : 'Compare with published', content: wrap });
			editor.Modal.onceClose(function () {
				wrap.remove();
				urls.forEach(function (u) { URL.revokeObjectURL(u); });
				resolve(answer);
			});
		});
	}

	// Publishing over a published version shows what changes first; the first publish just asks.
	async function confirmPublish(verb) {
		const question = verb + ' v' + current.version + '? Documents generated from now on will use it; the current published version will be retired.';
		if (!versions.some(function (v) { return v.status === 'Published'; })) return window.confirm(question);
		const d = await fetchDiff();
		if (!d.against) return window.confirm(question);
		setStatus('Review the changes, then ' + verb.toLowerCase() + ' or cancel.');
		return openDiff(d, { verb: verb, question: question });
	}

	// ---- History: the audit log for this document (or all activity) ---------------------------------------------------
	const AUDIT_ACTIONS = {
		'draft.saved': 'Saved draft',
		'draft.discarded': 'Discarded draft',
		'version.published': 'Published',
		'version.rolled-back': 'Rolled back to',
		'template.duplicated': 'Created as a copy',
		'scenario.saved': 'Saved test scenario',
		'scenario.deleted': 'Deleted test scenario',
		'block.saved': 'Saved block',
		'block.deleted': 'Deleted block',
		'theme.saved': 'Saved theme',
		'theme.deleted': 'Deleted theme',
		'font.uploaded': 'Uploaded font',
		'font.deleted': 'Deleted font',
		'comment.added': 'Commented',
		'comment.replied': 'Replied to a comment',
		'comment.resolved': 'Resolved a comment',
		'comment.reopened': 'Reopened a comment',
		'comment.deleted': 'Deleted a comment',
		'dictionary.word-added': 'Added to the dictionary',
		'user.signed-in': 'Signed in',
		'user.signed-out': 'Signed out',
		'access.denied': 'Was refused'
	};

	document.getElementById('btnHistory').addEventListener('click', function () { run('Loading history', function () { return openHistory(false); }); });

	async function openHistory(all) {
		const kind = isClause() ? 'clauses' : 'templates';
		const name = all ? '' : templateName();
		const query = all ? '?take=300' : '?kind=' + kind + '&name=' + encodeURIComponent(name) + '&take=300';
		const entries = await (await ensureOk(await fetch('/api/audit' + query), 'History')).json();
		document.querySelectorAll('.history').forEach(function (el) { el.remove(); });
		const wrap = document.createElement('div');
		wrap.className = 'history';
		const bar = document.createElement('label');
		bar.className = 'find-option';
		const toggle = document.createElement('input');
		toggle.type = 'checkbox';
		toggle.id = 'historyAll';
		toggle.checked = all;
		toggle.addEventListener('change', function () { run('Loading history', function () { return openHistory(toggle.checked); }); });
		bar.appendChild(toggle);
		bar.appendChild(document.createTextNode(' All activity (every document and person)'));
		wrap.appendChild(bar);
		if (!entries.length) {
			const empty = document.createElement('p');
			empty.className = 'pane-empty';
			empty.textContent = 'No recorded activity yet.';
			wrap.appendChild(empty);
		} else {
			const table = document.createElement('table');
			table.className = 'usage-table history-table';
			const head = table.createTHead().insertRow();
			['When', 'Who', 'What'].concat(all ? ['Document'] : []).concat(['Version', 'Details']).forEach(function (h) {
				const th = document.createElement('th');
				th.textContent = h;
				head.appendChild(th);
			});
			const body = table.createTBody();
			entries.forEach(function (e) {
				const row = body.insertRow();
				row.className = 'history-row';
				row.setAttribute('data-action', e.action);
				[formatWhen(e.timeUtc), e.user, AUDIT_ACTIONS[e.action] || e.action]
					.concat(all ? [e.name ? (e.kind === 'clauses' ? 'Clause ' : '') + e.name : ''] : [])
					.concat([e.version ? 'v' + e.version : '', e.detail || ''])
					.forEach(function (text) { row.insertCell().textContent = text; });
			});
			wrap.appendChild(table);
		}
		editor.Modal.open({ title: all ? 'All activity' : 'History of ' + name, content: wrap });
		editor.Modal.onceClose(function () { wrap.remove(); });
		return entries.length + ' recorded action' + (entries.length === 1 ? '' : 's') + '.';
	}

	document.getElementById('btnDiff').addEventListener('click', function () {
		run('Comparing with the published version', async function () {
			const d = await fetchDiff();
			if (!d.against) return 'Nothing is published for "' + templateName() + '" yet.';
			openDiff(d, null);
			return diffSummary(d);
		});
	});

	document.getElementById('btnPublish').addEventListener('click', function () {
		run('Publishing', publishCurrent);
	});

	async function publishCurrent() {
		if (!current || isDirty() || current.status === 'Published') {
			if (current && current.status === 'Published' && !isDirty()) return 'v' + current.version + ' is already published.';
			if (!(await saveDraft())) return 'Publish cancelled.';
		}
		const verb = current.status === 'Retired' ? 'Roll back to' : 'Publish';
		if (!(await confirmPublish(verb))) {
			return 'Publish cancelled.';
		}
		const info = await (await ensureOk(await fetch(apiBase() + '/versions/' + current.version + '/publish' + langQuery(), { method: 'POST' }), 'Publish')).json();
		current = { version: info.version, status: info.status };
		await refreshVersions();
		if (isClause()) {
			await repaintClauses();
			return 'Clause ' + templateName() + ' v' + info.version + ' is now published. Templates that include it (unpinned) use it from now on.';
		}
		return (docLang() ? langInfo().name + ' ' : '') + 'v' + info.version + ' is now published.';
	}

	document.getElementById('btnDiscard').addEventListener('click', function () {
		run('Discarding draft', async function () {
			const latest = versions[versions.length - 1];
			if (!latest || latest.status !== 'Draft') return 'There is no draft.';
			if (!window.confirm('Delete draft v' + latest.version + '? This cannot be undone.')) return 'Discard cancelled.';
			await ensureOk(await fetch(apiBase() + '/draft' + langQuery(), { method: 'DELETE' }), 'Discard');
			current = null;
			await refreshVersions();
			if (versions.length) {
				await openVersion(versions[versions.length - 1].version);
			} else {
				await loadStarter();
			}
			return 'Draft v' + latest.version + ' discarded.';
		});
	});

	document.getElementById('btnOpen').addEventListener('click', function () {
		run('Opening', async function () {
			if (!versionSelect.value) return 'Nothing saved yet for this template.';
			if (!confirmDiscardChanges()) return 'Open cancelled.';
			await openVersion(Number(versionSelect.value));
			return 'Opened v' + current.version + ' (' + current.status + ').';
		});
	});

	nameInput.addEventListener('change', function () {
		run('Loading versions', async function () {
			resetLanguage();
			current = null;
			showDetails();
			await refreshVersions();
			return versions.length ? versions.length + ' version(s) found. Select one and click Open.' : 'New ' + (isClause() ? 'clause' : 'template') + '. Save Draft to create v1.';
		});
	});

	// Clause mode: the same canvas, versions and publish flow, against /api/clauses. A clause has no PDF of its own.
	function updateKindUi() {
		document.getElementById('templateNameLabel').textContent = isClause() ? 'Clause' : 'Template';
		document.getElementById('btnRenderPublished').disabled = isClause();
		document.getElementById('btnPageSetup').disabled = isClause() || !hasRole('Author');
		document.getElementById('btnWatermark').disabled = isClause() || !hasRole('Author');
		document.getElementById('btnTheme').disabled = isClause() || !hasRole('Author');
		document.body.classList.toggle('clause-mode', isClause());
	}

	docKind.addEventListener('change', function () {
		run('Loading versions', async function () {
			updateKindUi();
			resetLanguage();
			current = null;
			await refreshVersions();
			if (isClause()) await refreshClauseList();
			return versions.length ? versions.length + ' version(s) found. Select one and click Open.' : 'New ' + (isClause() ? 'clause' : 'template') + '. Save Draft to create v1.';
		});
	});
	updateKindUi();

	document.getElementById('btnRenderPublished').addEventListener('click', function () {
		run('Rendering published version', async function () {
			const response = await fetch(apiBase() + '/pdf' + langQuery(), { method: 'POST' });
			await showPdf(response, 'Server render of published version ' + (response.headers.get('X-Template-Version') || '') +
				(docLang() && response.headers.get('X-Template-Language') !== docLang() ? ' (English: no ' + langInfo().name + ' version is published)' : ''));
		});
	});

	document.getElementById('btnPreview').addEventListener('click', function () {
		run('Rendering preview', async function () {
			const response = await fetch('/api/render', {
				method: 'POST',
				headers: { 'Content-Type': 'application/json' },
				body: JSON.stringify({ html: editor.getHtml(), css: editor.getCss(), data: sampleData() })
			});
			await showPdf(response, 'Preview of canvas (' + (activeScenario ? 'scenario "' + activeScenario + '"' : 'model example data') + ')');
		});
	});

	// ---- Export: Word template (.docx for DocGen) and HTML (placeholders kept, or filled with the example data) ----
	function exportName() {
		try { return templateName(); } catch (err) { return 'template'; }
	}
	function downloadBlob(blob, fileName) {
		const url = URL.createObjectURL(blob);
		const link = document.createElement('a');
		link.href = url;
		link.download = fileName;
		document.body.appendChild(link);
		link.click();
		link.remove();
		setTimeout(function () { URL.revokeObjectURL(url); }, 10000);
	}
	function exportWarnings(response) {
		const header = response.headers.get('X-Export-Warnings');
		if (!header) return [];
		try { return JSON.parse(new TextDecoder().decode(Uint8Array.from(atob(header), function (c) { return c.charCodeAt(0); }))); } catch (err) { return []; }
	}
	function showExportWarnings(fileName, warnings) {
		const list = document.createElement('ul');
		warnings.forEach(function (w) {
			const item = document.createElement('li');
			item.textContent = w;
			list.appendChild(item);
		});
		const wrap = document.createElement('div');
		const intro = document.createElement('p');
		intro.textContent = fileName + ' was downloaded. These parts of the design could not be carried into the Word template, so check them in the file:';
		wrap.appendChild(intro);
		wrap.appendChild(list);
		editor.Modal.open({ title: 'Word export notes', content: wrap });
		editor.Modal.onceClose(function () { wrap.remove(); });
	}
	function exportRequest(path, fileName, withData) {
		return run('Exporting ' + fileName, async function () {
			const response = await fetch(path, {
				method: 'POST',
				headers: { 'Content-Type': 'application/json' },
				body: JSON.stringify({ html: editor.getHtml(), css: editor.getCss(), data: withData ? sampleData() : null })
			});
			if (!response.ok) {
				let detail = '';
				try { detail = (await response.json()).error; } catch (err) { /* not JSON */ }
				throw new Error('Export failed' + (detail ? ': ' + detail : '.'));
			}
			const warnings = exportWarnings(response);
			downloadBlob(await response.blob(), fileName);
			if (warnings.length) showExportWarnings(fileName, warnings);
			return fileName + ' exported' + (warnings.length ? ' (' + warnings.length + ' note' + (warnings.length === 1 ? '' : 's') + ').' : '.');
		});
	}
	document.getElementById('btnExportDocx').addEventListener('click', function () {
		const name = exportName();
		exportRequest('/api/export/docx?name=' + encodeURIComponent(name), name + '.docx', false);
	});
	document.getElementById('btnExportHtml').addEventListener('click', function () {
		const name = exportName();
		exportRequest('/api/export/html?name=' + encodeURIComponent(name), name + '.template.html', false);
	});
	document.getElementById('btnExportHtmlData').addEventListener('click', function () {
		const name = exportName();
		exportRequest('/api/export/html?withData=true&name=' + encodeURIComponent(name), name + '.html', true);
	});

	document.getElementById('btnLiquid').addEventListener('click', function () {
		const pre = document.createElement('pre');
		pre.className = 'liquid-view';
		pre.textContent = editor.getHtml() + '\n\n/* ---- CSS ---- */\n' + editor.getCss();
		editor.Modal.open({ title: 'Exported template (HTML + Liquid)', content: pre });
		editor.Modal.onceClose(function () { pre.remove(); });
	});

	// ---- Legacy form import (HTML from fact-pdf-tools "demo emit-html") -------------------------------------------
	const legacyFile = document.getElementById('legacyFile');
	document.getElementById('btnImportLegacy').addEventListener('click', function () {
		if (!confirmDiscardChanges()) return;
		legacyFile.value = '';
		legacyFile.click();
	});

	function legacyTemplateName(fileName) {
		const name = fileName.replace(/\.html?$/i, '').replace(/-(poc|populated)$/i, '').replace(/[^A-Za-z0-9_-]+/g, '-').slice(0, 64);
		return NAME_PATTERN.test(name) ? name : 'legacy-form';
	}

	// The canvas loaded fonts.css before the import registered the form's fonts.
	function reloadLegacyFonts() {
		const doc = editor.Canvas.getDocument();
		const link = doc && doc.querySelector('link[href^="/api/legacy/fonts.css"]');
		if (link) link.href = '/api/legacy/fonts.css?v=' + Date.now();
	}

	legacyFile.addEventListener('change', function () {
		const file = legacyFile.files[0];
		if (!file) return;
		run('Importing ' + file.name, async function () {
			const response = await fetch('/api/legacy/import', { method: 'POST', headers: { 'Content-Type': 'text/html' }, body: file });
			const result = await (await ensureOk(response, 'Import')).json();
			nameInput.value = legacyTemplateName(file.name);
			current = null;
			await refreshVersions();
			// Styles first: the positions in the imported HTML become CSS rules, which setStyle would wipe.
			editor.setStyle('');
			editor.setComponents(result.html);
			reloadLegacyFonts();
			await afterLoad();
			// Nothing is saved yet.
			editor.getModel().set('changesCount', 1);
			return 'Imported ' + result.pages + ' page(s): ' + result.texts + ' text, ' + result.fields + ' fields, ' +
				result.shapes + ' lines/boxes, ' + result.images + ' image(s).' +
				(result.missingFonts.length ? ' Fonts not in the file (substituted): ' + result.missingFonts.join(', ') + '.' : '') +
				(versions.length ? ' "' + nameInput.value + '" already has saved versions; Save Draft adds a draft.' : '');
		});
	});

	window.addEventListener('beforeunload', function (e) {
		if (isDirty()) { e.preventDefault(); e.returnValue = ''; }
	});

	// ---- PDF / Word import (server converts: PDF => fixed form pages, .docx => flowing HTML with merge fields) -------
	const docFile = document.getElementById('docFile');
	const DOCUMENT_TYPES = {
		pdf: 'application/pdf',
		docx: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document'
	};
	document.getElementById('btnImportDocument').addEventListener('click', function () {
		if (!confirmDiscardChanges()) return;
		docFile.value = '';
		docFile.click();
	});

	// Word only: which typed placeholders become data fields. Resolves to the ?placeholders= value, or null if cancelled.
	function wordImportOptions() {
		return new Promise(function (resolve) {
			const box = document.createElement('div');
			box.className = 'import-options';
			box.innerHTML =
				'<p>Turn placeholder text typed in the document into data fields:</p>' +
				'<label><input type="checkbox" name="braces" checked> <code>{{ name }}</code> (also <code>{{ premium | currency }}</code>)</label>' +
				'<label><input type="checkbox" name="chevrons" checked> <code>\u00abName\u00bb</code></label>' +
				'<label><input type="checkbox" name="brackets"> <code>[Name]</code> \u2014 also matches ordinary bracketed wording</label>' +
				'<p class="import-options-hint">Word merge fields and tagged content controls always become data fields.</p>' +
				'<div class="import-options-actions"><button type="button" id="docImportGo" class="md-btn filled">Import</button></div>';
			let chosen = null;
			box.querySelector('#docImportGo').addEventListener('click', function () {
				const picked = Array.prototype.map.call(box.querySelectorAll('input:checked'), function (i) { return i.name; });
				chosen = picked.length ? picked.join(',') : 'none';
				editor.Modal.close();
			});
			editor.Modal.open({ title: 'Word import options', content: box });
			editor.Modal.onceClose(function () { box.remove(); resolve(chosen); });
		});
	}

	// A PDF over the import limit: which pages to import. Resolves to "first-last", or null if cancelled.
	function pdfPageRange(pageCount, maxPages) {
		return new Promise(function (resolve) {
			const box = document.createElement('div');
			box.className = 'import-options';
			const limit = maxPages || 100;
			box.innerHTML =
				'<p>This PDF has <b class="pdf-page-count"></b> pages. Up to <b class="pdf-page-limit"></b> can be imported at once.</p>' +
				'<label>From page <input type="number" id="pdfFrom" min="1"></label>' +
				'<label>To page <input type="number" id="pdfTo" min="1"></label>' +
				'<p class="import-options-hint pdf-range-error" hidden></p>' +
				'<div class="import-options-actions"><button type="button" id="pdfPagesGo" class="md-btn filled">Import pages</button></div>';
			box.querySelector('.pdf-page-count').textContent = String(pageCount);
			box.querySelector('.pdf-page-limit').textContent = String(limit);
			const from = box.querySelector('#pdfFrom');
			const to = box.querySelector('#pdfTo');
			from.max = to.max = String(pageCount);
			from.value = '1';
			to.value = String(Math.min(pageCount, limit));
			const error = box.querySelector('.pdf-range-error');
			let chosen = null;
			box.querySelector('#pdfPagesGo').addEventListener('click', function () {
				const a = parseInt(from.value, 10);
				const b = parseInt(to.value, 10);
				const problem = !(a >= 1 && b >= a && b <= pageCount) ? 'Choose pages between 1 and ' + pageCount + ', first page first.'
					: b - a + 1 > limit ? 'That is ' + (b - a + 1) + ' pages; choose at most ' + limit + '.' : '';
				if (problem) {
					error.textContent = problem;
					error.hidden = false;
					return;
				}
				chosen = a + '-' + b;
				editor.Modal.close();
			});
			editor.Modal.open({ title: 'Choose PDF pages', content: box });
			editor.Modal.onceClose(function () { box.remove(); resolve(chosen); });
		});
	}

	docFile.addEventListener('change', function () {
		const file = docFile.files[0];
		if (!file) return;
		run('Importing ' + file.name, async function () {
			const extension = (/\.([a-z0-9]+)$/i.exec(file.name) || [])[1];
			const type = DOCUMENT_TYPES[(extension || '').toLowerCase()];
			if (!type) throw new Error('Choose a .pdf or .docx file.');
			let url = '/api/import';
			if (type === DOCUMENT_TYPES.docx) {
				const placeholders = await wordImportOptions();
				if (placeholders === null) return 'Import cancelled.';
				url += '?placeholders=' + encodeURIComponent(placeholders);
			}
			let response = await fetch(url, { method: 'POST', headers: { 'Content-Type': type }, body: file });
			if (response.status === 400 && type === DOCUMENT_TYPES.pdf) {
				const problem = await response.clone().json().catch(function () { return {}; });
				if (problem.pageCount) {
					const pages = await pdfPageRange(problem.pageCount, problem.maxPages);
					if (pages === null) return 'Import cancelled.';
					response = await fetch(url + '?pages=' + encodeURIComponent(pages), { method: 'POST', headers: { 'Content-Type': type }, body: file });
				}
			}
			const result = await (await ensureOk(response, 'Import')).json();
			const name = file.name.replace(/\.[^.]+$/, '').replace(/[^A-Za-z0-9_-]+/g, '-').replace(/^-+|-+$/g, '').slice(0, 64);
			nameInput.value = NAME_PATTERN.test(name) ? name : 'imported-document';
			current = null;
			await refreshVersions();
			// Model first so the merge-field bindings validate against it; styles before components.
			const hasModel = isObject(result.model) && Object.keys(result.model).length > 0;
			if (hasModel) applyModel(result.model, false);
			editor.setStyle(result.css || '');
			editor.setComponents(result.html);
			if (result.format === 'pdf') reloadLegacyFonts();
			await afterLoad();
			// Nothing is saved yet.
			if (hasModel) modelDirty = true;
			editor.getModel().set('changesCount', 1);
			updateBadge();
			const counts = result.counts || {};
			const summary = Object.keys(counts).filter(function (k) { return counts[k] > 0; })
				.map(function (k) { return counts[k] + ' ' + k; }).join(', ');
			const notes = result.notes || [];
			return 'Imported ' + file.name + ' (' + result.pages + ' page(s)): ' + (summary || 'no content') + '.' +
				(hasModel ? ' Model built from ' + result.fields.length + ' merge field(s).' : '') +
				(notes.length ? ' ' + notes.join(' ') : '') +
				(versions.length ? ' "' + nameInput.value + '" already has saved versions; Save Draft adds a draft.' : '');
		});
	});

	// ---- GhostDraft import (JSON from tools/gd2designer.py: components, css and a sample model) ---------------------
	const gdFile = document.getElementById('gdFile');
	document.getElementById('btnImportGhostDraft').addEventListener('click', function () {
		if (!confirmDiscardChanges()) return;
		gdFile.value = '';
		gdFile.click();
	});

	gdFile.addEventListener('change', function () {
		const file = gdFile.files[0];
		if (!file) return;
		run('Importing ' + file.name, async function () {
			return importGhostDraft(JSON.parse(await file.text()), file.name);
		});
	});

	// Loads a gd2designer conversion onto the canvas as a new, unsaved template named after the form.
	async function importGhostDraft(result, source) {
		if (!result || !Array.isArray(result.components) || typeof result.css !== 'string' || !result.model || typeof result.model !== 'object') {
			throw new Error(source + ' is not a gd2designer export.');
		}
		docKind.value = 'templates';
		updateKindUi();
		resetLanguage();
		const name = String(result.name || '').replace(/[^A-Za-z0-9_-]+/g, '-').replace(/^-+|-+$/g, '').slice(0, 64);
		nameInput.value = NAME_PATTERN.test(name) ? name : 'ghostdraft-form';
		current = null;
		await refreshVersions();
		// Model first so bindings validate against it; styles before components (inline styles become rules).
		applyModel(result.model, false);
		editor.setStyle(result.css);
		editor.setComponents(upgradeLayout(result.components));
		setDetails({ title: result.title });
		await afterLoad();
		// Nothing is saved yet.
		modelDirty = true;
		editor.getModel().set('changesCount', 1);
		updateBadge();
		const counts = (result.report && result.report.counts) || {};
		const notes = (result.report && result.report.notes) || [];
		return 'Imported "' + (result.title || result.name) + '": ' +
			Object.keys(counts).map(function (k) { return counts[k] + ' ' + k; }).join(', ') + '.' +
			(notes.length ? ' ' + notes.length + ' conversion note(s): ' + notes.join('; ') + '.' : '') +
			(versions.length ? ' "' + nameInput.value + '" already has saved versions; Save Draft adds a draft.' : '');
	}

	// Add to Designer Library (review page): /?addToLibrary={case} imports that GhostDraft case's conversion and saves it
	// as a draft template.
	async function addToLibrary(caseName) {
		const response = await fetch('/api/review/cases/' + encodeURIComponent(caseName) + '/designer');
		const result = await (await ensureOk(response, 'Loading ' + caseName)).json();
		await importGhostDraft(result, caseName);
		const saved = await saveDraft();
		if (!saved) return 'Not saved: ' + nameInput.value + ' is on the canvas; Save Draft adds it to the library.';
		return 'Added ' + caseName + ' to the library as template "' + nameInput.value + '": ' + saved +
			' Check it with Preview PDF, then Publish.';
	}
	const libraryCase = new URLSearchParams(location.search).get('addToLibrary');
	if (libraryCase) history.replaceState(null, '', location.pathname);

	// ---- Embedded mode (/?embed=1): a host app (e.g. Commercial Web) drives the designer with window messages -----------
	// Host -> designer: { source: 'moe-designer-host', type, id, ... } (commands below).
	// Designer -> host: { source: 'moe-designer', type: 'ready' | 'state' | 'status' | 'result', ... }; every command gets a
	// 'result' with its id, ok, message and the new state. Only a parent page from an allowed origin
	// (Embed:AllowedOrigins) is listened to and told anything; the server only lets those origins frame the page.
	function postToHost(type, payload) {
		if (!embedHost) return;
		window.parent.postMessage(Object.assign({ source: 'moe-designer', type: type }, payload || {}), embedHost);
	}

	function hostState() {
		return {
			name: nameInput.value,
			kind: docKind.value,
			version: current ? current.version : null,
			status: current ? current.status : null,
			dirty: isDirty(),
			details: Object.assign({}, templateDetails),
			versions: versions.map(function (v) { return { version: v.version, status: v.status }; }),
			record: externalRecord ? externalRecord.label : null,
			fieldMode: fieldModeSelect.value
		};
	}

	// State changes come in bursts (load, typing): the host hears the settled state.
	function notifyHost() {
		if (!embedHost) return;
		clearTimeout(hostStateTimer);
		hostStateTimer = setTimeout(function () { postToHost('state', { state: hostState() }); }, 50);
	}

	async function allowedHostOrigin() {
		if (window.parent === window) return null;
		const config = await fetch('/api/embed').then(function (r) { return r.ok ? r.json() : {}; }).catch(function () { return {}; });
		const allowed = config.allowedOrigins || [];
		let parent = location.ancestorOrigins && location.ancestorOrigins.length ? location.ancestorOrigins[0] : '';
		if (!parent && document.referrer) {
			try { parent = new URL(document.referrer).origin; } catch (e) { parent = ''; }
		}
		return allowed.indexOf(parent) >= 0 ? parent : null;
	}

	function checkName(name) {
		if (!NAME_PATTERN.test(String(name || ''))) throw new Error('A template name may only contain letters, numbers, "-" and "_" (at most 64).');
	}

	function checkUnsaved(m) {
		if (isDirty() && !m.discardChanges) throw new Error('There are unsaved changes. Save them first, or send the command again with discardChanges.');
	}

	async function useTemplateName(m) {
		docKind.value = m.kind === 'clauses' ? 'clauses' : 'templates';
		updateKindUi();
		resetLanguage();
		nameInput.value = m.name;
		current = null;
		await refreshVersions();
	}

	const HOST_COMMANDS = {
		// { name, version?, kind? ('templates' | 'clauses'), discardChanges? }: the given or the newest version.
		open: async function (m) {
			checkName(m.name);
			checkUnsaved(m);
			await useTemplateName(m);
			if (!versions.length) throw new Error('"' + m.name + '" has no saved versions.');
			const version = m.version ? Number(m.version) : versions[versions.length - 1].version;
			if (!versions.some(function (v) { return v.version === version; })) throw new Error('"' + m.name + '" has no version ' + version + '.');
			await openVersion(version);
			return 'Opened ' + m.name + ' v' + current.version + ' (' + current.status + ').';
		},
		// { name, model?, details?, discardChanges? }: an empty template (with the given data model), not saved yet.
		new: async function (m) {
			checkName(m.name);
			checkUnsaved(m);
			if (m.model !== undefined && !isObject(m.model)) throw new Error('The model must be a JSON object.');
			await useTemplateName(m);
			if (versions.length) throw new Error('"' + m.name + '" already exists: open it instead.');
			applyModel(isObject(m.model) ? m.model : defaultModel, false);
			editor.setComponents('');
			editor.setStyle('');
			await afterLoad();
			setDetails(m.details);
			return 'New template "' + m.name + '". Save to create v1.';
		},
		save: async function () {
			const saved = await saveDraft();
			if (!saved) throw new Error('Not saved.');
			return saved;
		},
		publish: publishCurrent,
		preview: function () {
			document.getElementById('btnPreview').click();
			return 'Rendering preview...';
		},
		// { data, label? }: show a real record (e.g. a policy) instead of the test data; data: null goes back to the test data.
		setData: function (m) {
			if (m.data === null || m.data === undefined) {
				externalRecord = null;
				applySample();
				notifyHost();
				return 'Showing the ' + (activeScenario ? 'scenario "' + activeScenario + '"' : 'model example data') + '.';
			}
			if (!isObject(m.data)) throw new Error('The record must be a JSON object.');
			externalRecord = { data: m.data, label: String(m.label || 'Record from the host app').slice(0, 120) };
			applySample();
			notifyHost();
			return 'Showing ' + externalRecord.label + '.' + gapText(dataGaps(externalRecord.data));
		},
		// { model }: the template's data model (an example payload).
		setModel: function (m) {
			if (!isObject(m.model)) throw new Error('The model must be a JSON object.');
			applyModel(m.model, true);
			const problems = validateBindings();
			return 'Model updated (' + modelSummary() + ').' + (problems.length ? ' ' + problems.length + ' binding problem(s).' : '');
		},
		// { details: { title?, formCode?, edition?, type?, category? } }: merged into the template's details.
		setDetails: function (m) {
			if (!isObject(m.details)) throw new Error('The details must be a JSON object.');
			setDetails(Object.assign({}, templateDetails, m.details));
			detailsDirty = true;
			updateBadge();
			return detailsProblem() || 'Details updated.';
		},
		// { mode: 'sample' | 'name' | 'liquid' }: what data fields show on the canvas.
		view: function (m) {
			const option = Array.prototype.find.call(fieldModeSelect.options, function (o) { return o.value === m.mode; });
			if (!option || option.classList.contains('feature-off')) throw new Error('Unknown view "' + m.mode + '".');
			fieldModeSelect.value = option.value;
			fieldModeSelect.dispatchEvent(new Event('change'));
			notifyHost();
			return 'Data fields show ' + option.textContent.toLowerCase() + '.';
		},
		getState: function () { return ''; }
	};

	window.addEventListener('message', function (event) {
		if (!embedHost || event.source !== window.parent || event.origin !== embedHost) return;
		const m = event.data;
		if (!isObject(m) || m.source !== 'moe-designer-host' || typeof m.type !== 'string') return;
		const command = Object.prototype.hasOwnProperty.call(HOST_COMMANDS, m.type) ? HOST_COMMANDS[m.type] : null;
		function reply(ok, message) {
			postToHost('result', { id: m.id, command: m.type, ok: ok, message: message || '', state: hostState() });
		}
		if (!command) {
			reply(false, 'Unknown command "' + m.type + '".');
			return;
		}
		Promise.resolve().then(function () { return command(m); }).then(function (message) {
			if (message) setStatus(message);
			reply(true, message);
		}, function (err) {
			setStatus(err.message, true);
			reply(false, err.message);
		});
	});

	if (EMBEDDED) {
		await refreshCustomBlocks();
		nameInput.value = '';
		applyModel(defaultModel);
		editor.setComponents('');
		setDetails(null);
		await afterLoad();
		embedHost = await allowedHostOrigin();
		if (!embedHost) {
			setStatus('This page may only be embedded by an app listed in the designer\'s Embed:AllowedOrigins setting.', true);
			return;
		}
		postToHost('ready', { user: me ? { name: me.name, roles: me.roles } : null, features: features, state: hostState() });
		return;
	}

	// Startup: open the newest saved version if there is one, otherwise the starter layout.
	await refreshCustomBlocks();
	if (libraryCase) {
		await loadStarter();
		await run('Adding ' + libraryCase + ' to the library', function () { return addToLibrary(libraryCase); });
		return;
	}
	try {
		await refreshVersions();
		if (versions.length) {
			await openVersion(versions[versions.length - 1].version);
			setStatus('Opened v' + current.version + ' (' + current.status + ').');
		} else {
			await loadStarter();
			setStatus('Starter layout loaded. Save Draft to create v1.');
		}
	} catch (err) {
		await loadStarter();
		setStatus(err.message, true);
	}
})();
