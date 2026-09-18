'use strict';
// Lean Navigator front end: a static page over the bundle the generator writes. No framework, no build step.

const DATA = 'data/';
const KIND = { a: 'axiom', d: 'def', t: 'theorem', o: 'opaque', q: 'quot', i: 'inductive', c: 'constructor', r: 'recursor', '?': 'unknown' };
const S = { manifest: null, modules: null, names: null, kinds: null, used: null, shards: new Map(), namesPromise: null };

const $ = (sel) => document.querySelector(sel);
const esc = (s) => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
const fmt = (n) => Number(n).toLocaleString('en-US');

async function fetchJson(path) {
  const r = await fetch(DATA + path);
  if (!r.ok) throw new Error(`${path}: ${r.status}`);
  return r.json();
}

// ---------------------------------------------------------------- data

function loadNames() {
  if (S.namesPromise) return S.namesPromise;
  setStatus('loading names…');
  S.namesPromise = Promise.all([
    fetch(DATA + 'names.txt').then(r => r.text()),
    fetch(DATA + 'kinds.txt').then(r => r.text()),
    fetch(DATA + 'used.bin').then(r => r.arrayBuffer()),
  ]).then(([names, kinds, used]) => {
    S.names = names.split('\n');
    if (S.names[S.names.length - 1] === '') S.names.pop();
    S.kinds = kinds;
    S.used = new Uint32Array(used);
    setStatus('');
  });
  return S.namesPromise;
}

function moduleOfId(id) {
  const m = S.modules;
  let lo = 0, hi = m.length - 1;
  while (lo < hi) {
    const mid = (lo + hi + 1) >> 1;
    if (m[mid].s <= id) lo = mid; else hi = mid - 1;
  }
  return lo;
}

async function shard(mi) {
  const name = S.modules[mi].n;
  if (S.shards.has(name)) return S.shards.get(name);
  const p = fetchJson('m/' + encodeURIComponent(name) + '.json');
  S.shards.set(name, p);
  if (S.shards.size > 60) S.shards.delete(S.shards.keys().next().value);
  return p;
}

async function decl(id) {
  const mi = moduleOfId(id);
  const arr = await shard(mi);
  return arr[id - S.modules[mi].s];
}

function idOfName(name) {
  return S.names.indexOf(name);
}

function search(q) {
  const names = S.names;
  if (!q) return [];
  const out = [];
  const lower = q.toLowerCase();
  const insensitive = q === lower;
  // rank: exact, then the last component starts with it, then any component starts with it, then substring
  const buckets = [[], [], [], []];
  for (let i = 0; i < names.length && buckets[3].length < 400; i++) {
    const n = names[i];
    const hay = insensitive ? n.toLowerCase() : n;
    const at = hay.indexOf(lower);
    if (at < 0) continue;
    if (hay === lower) buckets[0].push(i);
    else if (at === hay.lastIndexOf('.') + 1) buckets[1].push(i);
    else if (at === 0 || hay[at - 1] === '.') buckets[2].push(i);
    else buckets[3].push(i);
  }
  const byUse = (a, b) => S.used[b] - S.used[a];
  for (const b of buckets) { b.sort(byUse); for (const i of b) { out.push(i); if (out.length >= 50) return out; } }
  return out;
}

function libraryOf(moduleName) {
  const top = moduleName.split('.')[0];
  const libs = S.manifest.libraries;
  return libs.find(l => l.prefixes.includes(top)) || libs.find(l => l.prefixes.length === 0) || null;
}

function sourceUrl(moduleName, lines) {
  const lib = libraryOf(moduleName);
  if (!lib || !lib.url || !lib.rev) return null;
  let u = `${lib.url}/blob/${lib.rev}/${lib.path}${moduleName.replace(/\./g, '/')}.lean`;
  if (lines) u += `#L${lines[0]}-L${lines[1]}`;
  return u;
}

// ---------------------------------------------------------------- rendering helpers

const declHref = (name) => '#/d/' + encodeURIComponent(name);
const modHref = (name) => '#/m/' + encodeURIComponent(name);
const kindOf = (id) => KIND[S.kinds[id]] || 'unknown';
const kindBadge = (k) => `<span class="kind ${esc(k)}">${esc(k)}</span>`;
const shortName = (name) => { const i = name.lastIndexOf('.'); return i < 0 ? name : name.slice(i + 1); };

function nameLink(id) {
  const n = S.names[id];
  const mod = S.modules[moduleOfId(id)].n;
  return `<li>${kindBadge(kindOf(id))} <a class="nm" href="${declHref(n)}">${esc(n)}</a> <span class="mod">${esc(mod)}</span> <span class="n" title="declarations that reference it">${fmt(S.used[id])}</span></li>`;
}

function renderDoc(doc) {
  // the light subset of markdown docstrings actually use: paragraphs, code spans, fenced code
  const parts = doc.split(/```/);
  let html = '';
  parts.forEach((part, i) => {
    if (i % 2 === 1) { html += `<pre>${esc(part.replace(/^[a-z]*\n/, ''))}</pre>`; return; }
    for (const para of part.split(/\n\s*\n/)) {
      const t = para.trim();
      if (!t) continue;
      html += '<p>' + esc(t).replace(/`([^`]+)`/g, '<code>$1</code>') + '</p>';
    }
  });
  return html;
}

function linkStatement(text, ids) {
  // names referenced from the statement become links; longest first so `List.map` beats `List`
  const names = ids.map(i => S.names[i]).sort((a, b) => b.length - a.length);
  let html = esc(text);
  const isNameChar = (c) => /[\p{L}\p{N}_'.!?₀-₉ₐ-ₜ]/u.test(c);
  for (const n of names) {
    const shown = displayName(n);
    if (!shown) continue;
    const e = esc(shown);
    let at = 0, out = '';
    while (true) {
      const i = html.indexOf(e, at);
      if (i < 0) { out += html.slice(at); break; }
      const before = i > 0 ? html[i - 1] : ' ';
      const after = i + e.length < html.length ? html[i + e.length] : ' ';
      const inTag = html.lastIndexOf('<', i) > html.lastIndexOf('>', i);
      if (inTag || isNameChar(before) || isNameChar(after) || before === '✝') { out += html.slice(at, i + e.length); at = i + e.length; continue; }
      out += html.slice(at, i) + `<a href="${declHref(n)}">${e}</a>`;
      at = i + e.length;
    }
    html = out;
  }
  return html;
}

function linkDocNames(html) {
  // a code span that is exactly a known declaration name becomes a link
  return html.replace(/<code>([^<]+)<\/code>/g, (m, inner) => {
    const raw = inner.replace(/&amp;/g, '&').replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"');
    if (!/^[\p{L}_][\p{L}\p{N}_'.!?₀-₉]*$/u.test(raw)) return m;
    return idOfName(raw) >= 0 ? `<a href="${declHref(raw)}"><code>${inner}</code></a>` : m;
  });
}

function displayName(n) {
  // mirror the generator's display rules for private names; hygienic names never appear in statements
  const parts = n.split('.');
  if (parts[0] === '_private') { const z = parts.indexOf('0', 1); if (z > 0) return parts.slice(z + 1).join('.'); }
  return n;
}

function setStatus(s) { $('#status').textContent = s; }

// ---------------------------------------------------------------- pages

async function pageHome() {
  const m = S.manifest;
  const starts = ['Nat.add_comm', 'Real.sqrt', 'deriv', 'MeasureTheory.integral', 'Complex.exp', 'Finset.sum_comm', 'Polynomial.eval', 'Matrix.det', 'List.map_append'];
  $('#main').innerHTML = `
    <h1 style="font-family:inherit">Every declaration in Mathlib, and what it rests on</h1>
    <p class="dim">Type a name above. A declaration page shows its statement, what it uses, what uses it, and the axioms it rests on, with a picture you can walk one step at a time.</p>
    <ul class="stats">
      <div><b>${fmt(m.declarations)}</b>declarations</div>
      <div><b>${fmt(m.modules)}</b>modules</div>
      <div><b>${fmt(m.references)}</b>references between them</div>
      <div><b>${esc(m.lean)}</b>Lean</div>
    </ul>
    ${checkLine(m)}
    <h2>Start somewhere</h2>
    <p class="start">${starts.map(s => `<a class="mono" href="${declHref(s)}">${esc(s)}</a>`).join('')}</p>
    <h2>Most depended upon <small>the declarations the rest of the library leans on</small></h2>
    <ul class="list" id="top">${'<li class="dim">loading…</li>'}</ul>
    <h2>Or browse by module</h2>
    <div class="tree" id="tree"></div>`;
  renderTree();
  loadNames().then(() => {
    const top = [];
    for (let i = 0; i < S.used.length; i++) {
      if (S.kinds[i] === 'c' || S.kinds[i] === 'r') continue; // constructors and recursors are used by everything, by construction
      const u = S.used[i];
      if (top.length < 25 || u > S.used[top[top.length - 1]]) {
        let k = top.length;
        while (k > 0 && S.used[top[k - 1]] < u) k--;
        top.splice(k, 0, i);
        if (top.length > 25) top.pop();
      }
    }
    const el = $('#top');
    if (el) el.innerHTML = top.map(nameLink).join('');
  });
}

function checkLine(m) {
  if (!m.check) return `<p class="dim">This bundle was not re-checked by Tenet; the axiom verdicts report what each proof cites, not that it holds.</p>`;
  const c = m.check;
  const verdict = c.failed === 0
    ? `<span class="verdict ok">every one of ${fmt(c.checked)} declarations re-checked by Tenet ${esc(c.tenet)}, none rejected</span>`
    : `<span class="verdict bad">${fmt(c.checked)} declarations re-checked by Tenet ${esc(c.tenet)}, ${fmt(c.failed)} rejected</span>`;
  return `<p>${verdict} <span class="dim">independent kernel, ${Math.round(c.seconds / 60)} min, ${esc(c.date)}</span></p>
    <p class="dim">The verdict is the <a href="${DATA}check.json">check report</a>, SHA-256 <code>${esc(c.sha256 || '')}</code>, over inputs it names by hash.${verifyHint(m)}</p>`;
}

function verifyHint(m) {
  if (!m.repository) return ' It was produced on a private machine and carries no attestation.';
  const owner = m.repository.replace(/^https?:\/\/github\.com\//, '').split('/')[0];
  return ` It was produced by <a href="${esc(m.repository)}/actions">a public workflow</a> and signed: download it and run <code>gh attestation verify check.json --owner ${esc(owner)}</code>, or see <a href="${esc(m.repository)}/attestations">the attestations</a>.`;
}

function renderTree() {
  const root = { children: new Map(), count: 0, module: null };
  for (const mod of S.modules) {
    let at = root;
    for (const part of mod.n.split('.')) {
      if (!at.children.has(part)) at.children.set(part, { children: new Map(), count: 0, module: null });
      at = at.children.get(part);
      at.count += mod.c;
    }
    at.module = mod.n;
    root.count += mod.c;
  }
  const html = (node, depth) => {
    const kids = [...node.children.entries()].sort((a, b) => a[0].localeCompare(b[0]));
    if (kids.length === 0) return '';
    return '<ul' + (depth > 0 ? ' hidden' : '') + '>' + kids.map(([name, k]) => {
      const has = k.children.size > 0;
      const label = k.module ? `<a class="mono" href="${modHref(k.module)}">${esc(name)}</a>` : `<span class="mono">${esc(name)}</span>`;
      return `<li><span class="tog">${has ? '▸' : ''}</span>${label}<span class="cnt">${fmt(k.count)}</span>${html(k, depth + 1)}</li>`;
    }).join('') + '</ul>';
  };
  const tree = $('#tree');
  tree.innerHTML = html(root, 0);
  tree.addEventListener('click', (ev) => {
    const t = ev.target.closest('.tog');
    if (!t) return;
    const ul = t.parentElement.querySelector('ul');
    if (!ul) return;
    ul.hidden = !ul.hidden;
    t.textContent = ul.hidden ? '▸' : '▾';
  });
}

async function pageModule(name) {
  const mi = S.modules.findIndex(m => m.n === name);
  if (mi < 0) { $('#main').innerHTML = `<p>No module named <code>${esc(name)}</code>.</p>`; return; }
  const mod = S.modules[mi];
  const arr = await shard(mi);
  const src = sourceUrl(name, null);
  const importers = S.modules.filter(m => m.i.includes(mi)).map(m => m.n);
  $('#main').innerHTML = `
    <h1>${esc(name)}</h1>
    <p class="sub"><span>module, ${fmt(mod.c)} declarations</span>${src ? `<a href="${esc(src)}" target="_blank" rel="noopener">source ↗</a>` : ''}</p>
    <div class="cols">
      <div><h2>Imports <small>(${mod.i.length})</small></h2><ul class="list">${mod.i.map(i => `<li><a class="nm" href="${modHref(S.modules[i].n)}">${esc(S.modules[i].n)}</a></li>`).join('')}</ul></div>
      <div><h2>Imported by <small>(${importers.length})</small></h2><ul class="list">${importers.slice(0, 200).map(n => `<li><a class="nm" href="${modHref(n)}">${esc(n)}</a></li>`).join('')}${importers.length > 200 ? `<li class="dim">and ${fmt(importers.length - 200)} more</li>` : ''}</ul></div>
    </div>
    <h2>Declarations</h2>
    <ul class="list">${arr.map(d => `<li>${kindBadge(d.k)} <a class="nm" href="${declHref(d.n)}">${esc(d.n)}</a> <span class="stmt-line">${esc(d.s || '')}</span> <span class="n">${fmt(d.bc)}</span></li>`).join('')}</ul>`;
}

async function pageDecl(name) {
  await loadNames();
  const id = idOfName(name);
  if (id < 0) { $('#main').innerHTML = `<p>No declaration named <code>${esc(name)}</code> in this bundle.</p>`; return; }
  const d = await decl(id);
  const mod = S.modules[moduleOfId(id)].n;
  const axioms = d.a.map(i => S.manifest.axioms[i]);
  const std = new Set(S.manifest.standardAxioms);
  const extra = axioms.filter(a => !std.has(a));
  const hasSorry = extra.includes('sorryAx');
  const verdict = d.k === 'axiom' ? `<span class="verdict warn">an axiom: this is an assumption, not a result</span>`
    : axioms.length === 0 ? `<span class="verdict ok">rests on no axioms at all</span>`
    : extra.length === 0 ? `<span class="verdict ok">rests on nothing beyond propext, Classical.choice and Quot.sound</span>`
    : `<span class="verdict ${hasSorry ? 'bad' : 'warn'}">rests on ${extra.length} assumption${extra.length === 1 ? '' : 's'} beyond the standard three${hasSorry ? ', including sorry' : ''}</span>`;
  const rejected = d.f !== undefined
    ? `<div class="card bad-card"><b>Rejected by Tenet's kernel.</b> The checker did not accept this declaration: <code>${esc(d.f)}</code></div>`
    : '';
  const checkedNote = S.manifest.check && d.f === undefined && d.k !== 'axiom'
    ? `<span class="dim" title="re-checked by Tenet ${esc(S.manifest.check.tenet)} on ${esc(S.manifest.check.date)}">✓ re-checked</span>` : '';
  const src = sourceUrl(mod, d.l);
  const ns = name.lastIndexOf('.');
  const title = ns < 0 ? esc(name) : `<span class="ns">${esc(name.slice(0, ns + 1))}</span>${esc(name.slice(ns + 1))}`;

  const usedBy = d.b; // the generator stores the most used first
  const stmt = d.t, proof = d.u;
  const bySide = (ids) => [...ids].sort((a, b) => S.used[b] - S.used[a]);

  $('#main').innerHTML = `
    <h1>${title}</h1>
    <p class="sub">${kindBadge(d.k)} <span>in <a class="mono" href="${modHref(mod)}">${esc(mod)}</a></span>${src ? `<a href="${esc(src)}" target="_blank" rel="noopener">source${d.l ? ` line ${d.l[0]}` : ''} ↗</a>` : ''}</p>
    ${verdict} ${checkedNote}
    ${rejected}
    <h2>Statement</h2>
    <pre>${d.s ? linkStatement(d.s, d.t) : '<span class="dim">not decodable</span>'}</pre>
    ${d.d ? `<h2>Docstring</h2><div class="doc">${linkDocNames(renderDoc(d.d))}</div>` : ''}
    <h2>Neighborhood <small>click a node to move there</small></h2>
    <div id="graph"></div>
    <div class="cols">
      <div>
        <h2>Uses <small>${fmt(stmt.length + proof.length)} constants</small></h2>
        ${stmt.length ? `<p class="dim" style="margin:0 0 4px">in the statement (${stmt.length})</p><ul class="list">${bySide(stmt).map(nameLink).join('')}</ul>` : ''}
        ${proof.length ? `<p class="dim" style="margin:12px 0 4px">only in the ${d.k === 'theorem' ? 'proof' : 'body'} (${proof.length})</p><ul class="list" id="proof-list">${bySide(proof).slice(0, 40).map(nameLink).join('')}</ul>${proof.length > 40 ? `<p><button class="more" id="more-proof">show all ${fmt(proof.length)}</button></p>` : ''}` : ''}
        ${stmt.length + proof.length === 0 ? '<p class="dim">nothing: this is a leaf</p>' : ''}
      </div>
      <div>
        <h2>Used by <small>${fmt(d.bc)} declarations${d.bc > d.b.length ? `, the ${d.b.length} most used shown` : ''}</small></h2>
        ${usedBy.length ? `<ul class="list">${usedBy.map(nameLink).join('')}</ul>` : '<p class="dim">nothing yet</p>'}
      </div>
    </div>
    <h2>Axioms <small>${axioms.length === 0 ? 'none' : `${axioms.length} in the transitive closure`}</small></h2>
    <ul class="list">${axioms.map(a => `<li><a class="nm" href="${declHref(a)}">${esc(a)}</a><span class="mod">${std.has(a) ? 'standard: part of Lean\'s logic' : a === 'sorryAx' ? 'an incomplete proof somewhere below' : 'an assumption this declaration carries'}</span></li>`).join('')}</ul>`;
  const more = $('#more-proof');
  if (more) more.onclick = () => { $('#proof-list').innerHTML = bySide(proof).map(nameLink).join(''); more.remove(); };
  renderGraph(id, name, bySide(stmt), bySide(proof), usedBy);
  const two = $('#two-steps');
  if (two) two.onclick = async () => {
    two.disabled = true;
    two.textContent = 'loading…';
    try {
      await renderGraph(id, name, bySide(stmt), bySide(proof), usedBy, true);
    } catch (e) {
      two.textContent = 'could not load the second step';
      console.error(e);
    }
  };
}

// ---------------------------------------------------------------- the picture

async function renderGraph(id, name, stmt, proof, usedBy, deep = false) {
  const CAP = 14;
  const left = [...stmt.slice(0, CAP).map(i => ({ id: i, stmt: true })), ...proof.slice(0, Math.max(0, CAP - stmt.length)).map(i => ({ id: i, stmt: false }))];
  const right = usedBy.slice(0, CAP).map(i => ({ id: i }));
  // the second step: for each first-step node, its own few most-used neighbors in the same direction, deduplicated
  const seen = new Set([id, ...left.map(n => n.id), ...right.map(n => n.id)]);
  const left2 = [], right2 = [];
  if (deep) {
    const PER = 3, MAX2 = 24;
    for (const n of left) {
      const d = await decl(n.id);
      const cands = [...d.t, ...d.u].sort((a, b) => S.used[b] - S.used[a]);
      let k = 0;
      for (const c of cands) {
        if (seen.has(c) || k >= PER || left2.length >= MAX2) continue;
        seen.add(c); left2.push({ id: c, from: n.id }); k++;
      }
    }
    for (const n of right) {
      const d = await decl(n.id);
      let k = 0;
      for (const c of d.b) {
        if (seen.has(c) || k >= PER || right2.length >= MAX2) continue;
        seen.add(c); right2.push({ id: c, from: n.id }); k++;
      }
    }
  }
  const rows = Math.max(left.length, right.length, left2.length, right2.length, 1);
  const W = 1140, rowH = deep ? 26 : 30, H = Math.max(160, rows * rowH + 84);
  const colW = deep ? 250 : 360, midX = W / 2;
  const cy = H / 2;
  // anchor: 'c' centered on x, 'r' right edge at x (the uses column), 'l' left edge at x (the used-by column)
  const box = (x, y, id, cls, anchor) => {
    const label = displayName(S.names[id]);
    const shown = label.length > 44 ? '…' + label.slice(-42) : label;
    const w = Math.min(colW - 10, Math.max(60, shown.length * 7.4 + 22));
    const x0 = anchor === 'c' ? x - w / 2 : anchor === 'r' ? x - w : x;
    const color = `var(--k-${kindOf(id)})`;
    return { x: x0, y: y - 11, w, h: 22, html: `<a class="node ${cls}" href="${declHref(S.names[id])}"><title>${esc(S.names[id])} (${kindOf(id)}, used by ${fmt(S.used[id])})</title><rect x="${x0}" y="${y - 11}" width="${w}" height="${22}"></rect><line class="swatch" x1="${x0 + 1}" y1="${y - 8}" x2="${x0 + 1}" y2="${y + 8}" stroke="${color}"></line><text x="${x0 + 10}" y="${y + 4}">${esc(shown)}</text></a>` };
  };
  const nodes = [], edges = [];
  const center = box(midX, cy, id, 'center', 'c');
  const y0 = (n) => cy - ((n - 1) * rowH) / 2;
  const gap = deep ? 110 : 150;
  const at = new Map();
  const curve = (a, b, cls) => `<path class="edge ${cls}" d="M${a.x + a.w},${a.y + 11} C${a.x + a.w + 40},${a.y + 11} ${b.x - 40},${b.y + 11} ${b.x},${b.y + 11}"></path>`;
  left.forEach((n, i) => {
    const b = box(midX - gap, y0(left.length) + i * rowH, n.id, '', 'r');
    nodes.push(b); at.set(n.id, b);
    edges.push(curve(b, center, n.stmt ? 'stmt' : ''));
  });
  right.forEach((n, i) => {
    const b = box(midX + gap, y0(right.length) + i * rowH, n.id, '', 'l');
    nodes.push(b); at.set(n.id, b);
    edges.push(curve(center, b, ''));
  });
  left2.forEach((n, i) => {
    const b = box(midX - gap - colW - 20, y0(left2.length) + i * rowH, n.id, 'far', 'r');
    nodes.push(b);
    edges.push(curve(b, at.get(n.from), 'far'));
  });
  right2.forEach((n, i) => {
    const b = box(midX + gap + colW + 20, y0(right2.length) + i * rowH, n.id, 'far', 'l');
    nodes.push(b);
    edges.push(curve(at.get(n.from), b, 'far'));
  });
  const note = (x, text) => `<text class="lbl" x="${x}" y="18" text-anchor="middle">${esc(text)}</text>`;
  const legend = ['theorem', 'def', 'inductive', 'constructor', 'axiom'].map((k, i) =>
    `<line class="swatch" x1="${16 + i * 112}" y1="${H - 12}" x2="${16 + i * 112}" y2="${H - 24}" stroke="var(--k-${k})"></line><text class="lbl" x="${24 + i * 112}" y="${H - 14}">${k}</text>`).join('');
  const W2 = deep ? W + 2 * (colW + 20) : W;
  const shift = deep ? colW + 20 : 0;
  $('#graph').innerHTML = `${deep ? '' : '<p style="margin:0 0 6px"><button class="more" id="two-steps">show two steps</button></p>'}<div class="graph-scroll"><svg class="graph" viewBox="${-shift} 0 ${W2} ${H}"${deep ? ` style="width:${W2}px;height:${H}px"` : ''} xmlns="http://www.w3.org/2000/svg">
    ${note(midX - colW + 80, left.length ? `uses (${stmt.length ? 'blue edges: in the statement' : 'in the body'})` : 'uses nothing')}
    ${note(midX + colW - 80, right.length ? `used by (${fmt(S.used[id])} in all, most used first)` : 'used by nothing')}
    ${legend}
    ${edges.join('')}${nodes.map(n => n.html).join('')}${center.html}</svg></div>`;
  if (deep) {
    const sc = $('.graph-scroll');
    sc.scrollLeft = Math.max(0, (W2 - sc.clientWidth) / 2); // start centered on the declaration
  }
}

// ---------------------------------------------------------------- routing and search box

async function route() {
  const h = location.hash || '#/';
  try {
    if (h.startsWith('#/d/')) await pageDecl(decodeURIComponent(h.slice(4)));
    else if (h.startsWith('#/m/')) await pageModule(decodeURIComponent(h.slice(4)));
    else await pageHome();
    window.scrollTo(0, 0);
  } catch (e) {
    $('#main').innerHTML = `<p>Something went wrong: <code>${esc(e.message)}</code></p>`;
    console.error(e);
  }
}

function wireSearch() {
  const q = $('#q'), box = $('#results');
  let active = -1, timer = null;
  const render = (ids) => {
    if (ids.length === 0) { box.hidden = true; return; }
    box.innerHTML = ids.map((i, k) => `<a href="${declHref(S.names[i])}" class="${k === active ? 'active' : ''}">${kindBadge(kindOf(i))} <span class="nm">${esc(S.names[i])}</span><span class="mod">${esc(S.modules[moduleOfId(i)].n)}</span></a>`).join('');
    box.hidden = false;
  };
  let last = [];
  q.addEventListener('input', () => {
    clearTimeout(timer);
    timer = setTimeout(async () => {
      await loadNames();
      active = -1;
      last = search(q.value.trim());
      render(last);
    }, 120);
  });
  q.addEventListener('keydown', (ev) => {
    if (ev.key === 'ArrowDown') { active = Math.min(active + 1, last.length - 1); render(last); ev.preventDefault(); }
    else if (ev.key === 'ArrowUp') { active = Math.max(active - 1, 0); render(last); ev.preventDefault(); }
    else if (ev.key === 'Enter' && last.length) { location.hash = declHref(S.names[last[Math.max(active, 0)]]); box.hidden = true; }
    else if (ev.key === 'Escape') { box.hidden = true; }
  });
  q.addEventListener('focus', () => loadNames());
  document.addEventListener('click', (ev) => { if (!ev.target.closest('.search')) box.hidden = true; });
  box.addEventListener('click', () => { box.hidden = true; });
}

(async function init() {
  try {
    [S.manifest, S.modules] = await Promise.all([fetchJson('manifest.json'), fetchJson('modules.json')]);
  } catch (e) {
    $('#main').innerHTML = `<p>No bundle found under <code>data/</code>. Run the generator first.</p>`;
    return;
  }
  const lib = S.manifest.libraries.find(l => l.prefixes.length === 0);
  const c = S.manifest.check;
  $('#foot').textContent = `${lib ? lib.name + ' at ' + (lib.rev || '').slice(0, 10) + ', ' : ''}Lean ${S.manifest.lean}, generated ${S.manifest.generated}.`
    + (c ? ` Re-checked by Tenet ${c.tenet}: ${fmt(c.checked)} declarations, ${fmt(c.failed)} rejected.` : ' Not re-checked.');
  wireSearch();
  window.addEventListener('hashchange', route);
  await route();
})();
