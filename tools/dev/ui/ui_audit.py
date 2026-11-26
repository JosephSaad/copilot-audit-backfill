"""
Layout audit of every dashboard page in headless Edge (DevTools protocol):
text clipped or spilling out of its box, chart labels that overlap or leave the chart,
labels cut with an ellipsis and no hover text, links and buttons with no space between
them, overlapping boxes, chart text outside 9-16px, and horizontal page scroll.
Writes ui-audit/report.json, and a screenshot of each page with an issue. Exits 1 if any.

    python tools/dev/ui/ui_audit.py [width ...]          (default 1440 and 1024)

Run it against a dashboard on this machine signed in with Windows authentication as someone
who sees every page (an administrator). Needs Microsoft Edge and `pip install websocket-client`.

    INSIGHTS_URL     the dashboard (default https://localhost:7291)
    AUDIT_PAGES      comma-separated paths to check instead of every page in Nav.cs
    AUDIT_SHOT_ALL   set to 1 to screenshot every page, not only those with issues
    EDGE_PATH        msedge.exe, if not in the usual place
"""
import base64, json, os, re, subprocess, sys, time, urllib.request
import websocket

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(HERE, '..', '..', '..'))
BASE = os.environ.get('INSIGHTS_URL', 'https://localhost:7291').rstrip('/')
EDGE = os.environ.get('EDGE_PATH', r'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe')
PORT = 9333
OUT = os.path.join(HERE, 'ui-audit')
os.makedirs(OUT, exist_ok=True)

EXTRA = ['/apps/detail?app=1', '/agents/detail?agent=1', '/forecasting/actions?kind=revoke', '/forecasting/actions?kind=train',
         '/licensing/ranking?by=free', '/admin/values?col=30', '/admin/directory?check=nomail', '/compliance/person?upn=alex.saleh@contoso.com',
         '/security/flags?status=any', '/adoption/leaderboard?by=Country', '/licensing/chargeback?by=Division']

AUDIT_JS = r"""
(() => {
  const issues = [];
  const R = e => e.getBoundingClientRect();
  const name = e => (e.tagName.toLowerCase() + (e.className && typeof e.className === 'string' ? '.' + e.className.trim().split(/\s+/).join('.') : '')) ;
  const txt = e => (e.textContent || '').trim().replace(/\s+/g, ' ').slice(0, 80);
  // What a closed <details> holds is not on screen, though Edge still lays it out: only its summary counts.
  const folded = e => { const d = e.closest('details:not([open])'); return !!d && !e.closest('summary'); };
  const visible = e => { const r = R(e); const s = getComputedStyle(e); return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none' && !folded(e); };
  const hasHover = e => !!(e.closest('[title]') || e.closest('[data-tip]') || e.querySelector && e.querySelector('title'));
  // 1. page scrolls sideways
  if (document.documentElement.scrollWidth > innerWidth + 1)
    issues.push({kind: 'page-scroll', detail: `page is ${document.documentElement.scrollWidth}px wide in a ${innerWidth}px window`});
  // 2. HTML text clipped (hidden overflow) or spilling past its container
  const sel = 'main th, main td, .klabel, .kvalue, .knote, main a, main button, main label, main h1, main h2, main h3, .sub, .legend span, nav.side a, .crumbs a, .period, .scope-pill, .notice, .intro';
  for (const e of document.querySelectorAll(sel)) {
    if (!visible(e)) continue;
    const s = getComputedStyle(e);
    if (e.scrollWidth > e.clientWidth + 2 && e.clientWidth > 1 && (s.overflowX === 'hidden' || s.overflowX === 'clip' || s.textOverflow === 'ellipsis'))
      issues.push({kind: 'clipped', el: name(e), text: txt(e), hover: hasHover(e), detail: `${e.scrollWidth}px of text in ${e.clientWidth}px`});
    const box = e.closest('.kpi, .panel, td, th, .card, form.inline, .page-head');
    if (box && box !== e) {
      const r = R(e), b = R(box);
      if (r.right > b.right + 2 || r.left < b.left - 2)
        issues.push({kind: 'spills', el: name(e), text: txt(e), box: name(box), detail: `${Math.round(r.right - b.right)}px past the right of its ${name(box)}`});
    }
  }
  // tables wider than their panel (they scroll inside .table-wrap; flag when there is no wrapper)
  for (const t of document.querySelectorAll('main table')) {
    const p = t.closest('.panel'); if (!p) continue;
    if (R(t).width > R(p).width + 2 && !t.closest('.table-wrap'))
      issues.push({kind: 'table-overflow', text: txt(t.querySelector('th') || t), detail: `table ${Math.round(R(t).width)}px in panel ${Math.round(R(p).width)}px`});
  }
  // 3. charts: labels overlapping each other, leaving the chart, or cut with no hover
  document.querySelectorAll('svg.chart').forEach((svg, ci) => {
    const sr = R(svg);
    const texts = [...svg.querySelectorAll('text')].filter(visible).map(t => ({t, r: R(t), s: (t.textContent || '').trim()}));
    for (const a of texts) {
      if (a.r.left < sr.left - 1 || a.r.right > sr.right + 1 || a.r.top < sr.top - 1 || a.r.bottom > sr.bottom + 1)
        issues.push({kind: 'chart-label-outside', chart: ci, text: a.s, detail: `label reaches ${Math.round(Math.max(sr.left - a.r.left, a.r.right - sr.right, sr.top - a.r.top, a.r.bottom - sr.bottom))}px outside the chart`});
      if (a.s.endsWith('…') && !hasHover(a.t))
        issues.push({kind: 'chart-label-cut-no-hover', chart: ci, text: a.s});
    }
    for (let i = 0; i < texts.length; i++) for (let j = i + 1; j < texts.length; j++) {
      const a = texts[i].r, b = texts[j].r;
      const ox = Math.min(a.right, b.right) - Math.max(a.left, b.left), oy = Math.min(a.bottom, b.bottom) - Math.max(a.top, b.top);
      if (ox > 2 && oy > 2) issues.push({kind: 'chart-labels-overlap', chart: ci, text: texts[i].s + ' / ' + texts[j].s});
    }
  });
  // 4. links and buttons touching each other (no space between)
  const inl = [...document.querySelectorAll('main a, main button, main .csv')].filter(visible);
  for (let i = 0; i < inl.length; i++) for (let j = i + 1; j < inl.length; j++) {
    const a = R(inl[i]), b = R(inl[j]);
    const sameLine = Math.abs(a.top - b.top) < 4 && Math.abs(a.bottom - b.bottom) < 4;
    if (!sameLine) continue;
    const gap = b.left >= a.left ? b.left - a.right : a.left - b.right;
    if (gap < 3 && gap > -a.width) issues.push({kind: 'links-touching', text: txt(inl[i]) + ' | ' + txt(inl[j]), detail: `${Math.round(gap)}px apart`});
  }
  // 5. boxes overlapping: KPI tiles, panels, and the controls inside inline forms
  const groups = [['.kpi'], ['section.panel'], ['form.inline > *', true]];
  for (const [q, perParent] of groups) {
    const els = [...document.querySelectorAll(q)].filter(visible);
    for (let i = 0; i < els.length; i++) for (let j = i + 1; j < els.length; j++) {
      if (perParent && els[i].parentElement !== els[j].parentElement) continue;
      const a = R(els[i]), b = R(els[j]);
      const ox = Math.min(a.right, b.right) - Math.max(a.left, b.left), oy = Math.min(a.bottom, b.bottom) - Math.max(a.top, b.top);
      if (ox > 1 && oy > 1) issues.push({kind: 'boxes-overlap', el: q, text: txt(els[i]).slice(0, 40) + ' / ' + txt(els[j]).slice(0, 40)});
    }
  }
  // 7. chart text too small or too large once the chart is scaled to its panel
  document.querySelectorAll('svg.chart').forEach((svg, ci) => {
    const vb = svg.viewBox && svg.viewBox.baseVal; if (!vb || !vb.width) return;
    const scale = R(svg).width / vb.width;
    for (const t of svg.querySelectorAll('text')) {
      if (!visible(t)) continue;
      const px = parseFloat(getComputedStyle(t).fontSize) * scale;
      if (px < 9 || px > 16) { issues.push({kind: 'chart-text-size', chart: ci, text: (t.textContent || '').trim().slice(0, 30), detail: px.toFixed(1) + 'px'}); break; }
    }
  });
  // 6. anything shortened with an ellipsis, hover or not: labels are shown whole
  const walker = document.createTreeWalker(document.querySelector('main') || document.body, NodeFilter.SHOW_TEXT);
  for (let n = walker.nextNode(); n; n = walker.nextNode()) {
    if (n.parentElement && n.parentElement.closest('title, script, style, .intro, .sub, .notice')) continue;
    if ((n.nodeValue || '').includes('…')) issues.push({kind: 'ellipsis', text: n.nodeValue.trim().slice(0, 60)});
  }
  if (/^Something went wrong/.test(document.title) || /^Something went wrong/.test((document.querySelector('h1') || {}).textContent || ''))
    issues.push({kind: 'error-page', text: 'The page failed to build: it shows the error page.'});
  // A page that failed is an issue, not "ok": the developer error page, or a part replaced by an incident reference.
  const body = document.body.innerText;
  const failed = /unhandled exception occurred|finds the details under it in the web server's log/i.test(body);
  if (failed) { const at = Math.max(0, body.search(/unhandled exception occurred|Reference [0-9A-F]{6}/i)); issues.push({kind: 'page-failed', text: body.slice(at, at + 160)}); }
  return JSON.stringify({title: document.title, status: failed ? 'FAILED' : document.querySelector('.denied') ? 'denied' : 'ok', issues});
})()
"""


class Cdp:
    def __init__(self, ws_url):
        self.ws = websocket.create_connection(ws_url, timeout=120, suppress_origin=True)
        self.n = 0

    def call(self, method, **params):
        self.n += 1
        self.ws.send(json.dumps({'id': self.n, 'method': method, 'params': params}))
        while True:
            m = json.loads(self.ws.recv())
            if m.get('id') == self.n:
                if 'error' in m: raise RuntimeError(f"{method}: {m['error']}")
                return m.get('result', {})


def pages():
    if os.environ.get('AUDIT_PAGES'):
        return os.environ['AUDIT_PAGES'].split(',')
    src = open(os.path.join(REPO, 'dashboard', 'CopilotInsights', 'Pages', 'Nav.cs'), encoding='utf-8').read()
    paths = sorted(set(re.findall(r'new\("[^"]+", "(/[^"]*)"', src)))
    # The Governance Workbench's pages, from its menu (when the site has it set up; otherwise they answer 404).
    wb = open(os.path.join(REPO, 'dashboard', 'CopilotInsights', 'Workbench', 'WorkbenchNav.cs'), encoding='utf-8').read()
    paths += sorted(set('/workbench' + p for p in re.findall(r'new\("[^"]+", \$"\{P\}(/[^"]*)"', wb)))
    return ['/?home=1'] + [p for p in paths] + EXTRA


def main():
    widths = [int(w) for w in sys.argv[1:]] or [1440, 1024]
    prof = os.path.join(HERE, 'edge-audit')
    edge = subprocess.Popen([EDGE, '--headless=new', f'--remote-debugging-port={PORT}', f'--user-data-dir={prof}', '--ignore-certificate-errors',
                             '--auth-server-allowlist=localhost', '--auth-negotiate-delegate-allowlist=localhost', '--no-first-run', '--disable-gpu', 'about:blank'])
    try:
        for _ in range(60):
            try:
                targets = json.load(urllib.request.urlopen(f'http://127.0.0.1:{PORT}/json'))
                page = next(t for t in targets if t['type'] == 'page')
                break
            except Exception:
                time.sleep(0.5)
        cdp = Cdp(page['webSocketDebuggerUrl'])
        cdp.call('Page.enable')
        report = {}
        for w in widths:
            cdp.call('Emulation.setDeviceMetricsOverride', width=w, height=900, deviceScaleFactor=1, mobile=False)
            for p in pages():
                url = BASE + p + ('&' if '?' in p else '?') + ('d=30' if 'd=' not in p else '')
                cdp.call('Page.navigate', url=url)
                for _ in range(240):
                    time.sleep(0.25)
                    st = cdp.call('Runtime.evaluate', expression='document.readyState', returnByValue=True)['result'].get('value')
                    if st == 'complete': break
                time.sleep(0.3)
                res = json.loads(cdp.call('Runtime.evaluate', expression=AUDIT_JS, returnByValue=True)['result']['value'])
                key = f'{w} {p}'
                report[key] = res
                if res['issues'] or os.environ.get('AUDIT_SHOT_ALL'):
                    shot = cdp.call('Page.captureScreenshot', format='png', captureBeyondViewport=True)
                    fn = re.sub(r'[^A-Za-z0-9]+', '_', key).strip('_') + '.png'
                    open(os.path.join(OUT, fn), 'wb').write(base64.b64decode(shot['data']))
                print(f"{key:55} {res['status']:6} {len(res['issues'])} issue(s)", flush=True)
        json.dump(report, open(os.path.join(OUT, 'report.json'), 'w', encoding='utf-8'), indent=1)
    finally:
        edge.terminate()
    total = sum(len(r['issues']) for r in report.values())
    print(f'{total} issue(s) on {len(report)} page view(s). Report: {os.path.join(OUT, "report.json")}')
    return 1 if total else 0


if __name__ == '__main__':
    sys.exit(main())
