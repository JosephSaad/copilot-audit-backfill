"""Writes docs/SCHEMA.md from the same data build.py uses for the HTML page."""
import os, re, runpy

HERE = os.path.dirname(os.path.abspath(__file__))
# Reuse build.py's data and helpers without re-running its HTML output side effects on the wrong file.
g = runpy.run_path(os.path.join(HERE, 'build.py'))
AREAS, LEGACY, SCRIPTS = g['AREAS'], g['LEGACY'], g['SCRIPTS']
cols_of, typ, clean = g['cols_of'], g['typ'], g['clean']
PRIMARY, ALSO, scr, notes = g['PRIMARY'], g['ALSO'], g['scr'], g['notes']
OVERRIDE, AD_ATTR, BOTH, LIC_ONLY = g['OVERRIDE'], g['AD_ATTR'], g['BOTH'], g['LIC_ONLY']

# Every table and view in the databases needs a description in build.py, and every description an object.
documented = {o for _, _, _, objs in AREAS for o, _ in objs} | {o for o, _ in LEGACY}
installed = {f"{c['sch']}.{c['obj']}" for db in g['meta'].values() for c in db['columns']}
if installed - documented:
    raise SystemExit('No description in build.py for: ' + ', '.join(sorted(installed - documented)))
if documented - installed:
    raise SystemExit('Described in build.py but not installed: ' + ', '.join(sorted(documented - installed)))


def md(s):
    """Escape for a Markdown table cell or line: pipes, and < which GitHub reads as HTML."""
    return (s or '').replace('\\', '\\\\').replace('|', '\\|').replace('<', '&lt;').replace('>', '&gt;')


def code(s):
    return '`' + (s or '').replace('`', "'").replace('|', '\\|') + '`'


def anchor(name):
    return name.replace('.', '').lower()


out = []
out.append('# Schema reference\n')
out.append('Every table and view copilot-audit-backfill 2.3.0 creates in SQL Server, column by column, and the script behind each. '
           'Generated from the metadata of copies of the lab\'s Copilot and license-event databases upgraded by the 2.3.0 installer (27 September 2026), with the notes the scripts\' DDL carries. '
           'Types are SQL Server\'s; **no** under Null means NOT NULL.\n')

counts = {'tables': 0, 'views': 0, 'columns': 0}
for _, _, _, objs in AREAS:
    for o, _ in objs:
        rows = cols_of(o)[0]
        if rows:
            counts['views' if rows[0]['otype'] == 'V' else 'tables'] += 1
            counts['columns'] += len(rows)
out.append(f"{counts['tables']} tables (plus 2 left over from 1.x), {counts['views']} views, {counts['columns']} columns, from {len(SCRIPTS)} scripts.\n")

out.append('## Contents\n')
out.append('- [The scripts](#the-scripts)')
for key, title, _, objs in AREAS:
    out.append(f'- [{md(title)}](#{re.sub(r"[^a-z0-9 -]", "", title.lower()).replace(" ", "-")}): ' +
               ', '.join(f'[`{o}`](#{anchor(o)})' for o, _ in objs))
out.append('- [Left over from 1.x](#left-over-from-1x)\n')

out.append('## The scripts\n')
out.append('Every table, view, procedure and function comes from a script in `sql/`. Each is idempotent: it creates what is missing, adds '
           'columns a newer version needs, and never drops data. Three run themselves; the rest are run once, in the order '
           '[DEPLOYMENT.md](DEPLOYMENT.md) gives (steps 7 and 8).\n')
out.append('| Script | Database | Run | Creates |\n|---|---|---|---|')
for s, db, when, what in SCRIPTS:
    out.append(f'| `sql/{s}` | {md(db)} | {md(when)} | {md(what)} |')
out.append('')
out.append('**Two databases.** The Copilot database (`CopilotAudit`) holds everything below except the license-event section. The license-event '
           'database (`CopilotLicenseEvents`) holds the same loading, state and final-record tables, filled with directory events instead, plus the '
           'license-event tables and views.\n')
out.append('**Procedures and function.** `audit.fnShredCopilotInteraction` (inline; JSON to interaction columns), `audit.usp_MergeCopilotLoad`, '
           '`audit.usp_MergeCopilotCsv`, `audit.usp_FlattenCopilotAudit`, `audit.usp_PurgeCopilotAuditJson`, `audit.usp_BuildCopilotSessions`, '
           '`audit.usp_ShredLicenseEvents`, `bi.usp_RefreshBi` and `dash.usp_RefreshDashboard`, from the scripts above. '
           '`dash.fnScopeUsers(@RootUpn)` (inline) returns the `bi.DimUser` keys of a person and everyone under them: the dashboard joins every '
           'query for a part of the organization to it.\n')


def section(name, purpose, legacy=False):
    rows, idx, chk = cols_of(name)
    if not rows:
        return
    kind = 'view' if rows[0]['otype'] == 'V' else 'table'
    script = PRIMARY.get(name, scr['created'].get(name, '1.x (no current script)' if legacy else ''))
    added = [s for s in scr['altered'].get(name, []) if s != script]
    dbs = 'Copilot and license-event databases' if name in BOTH else ('License-event database' if name in LIC_ONLY else 'Copilot database')
    pk = next((i for i in idx if i['pk']), None)
    pkcols = set(pk['keys'].split(', ')) if pk else set()
    n = notes.get(name, {})
    out.append(f'### {name}\n')
    out.append(f'*{kind.capitalize()}.* {md(purpose)}\n')
    meta = [f'**Script** `{script}`' if script.endswith('.sql') else f'**Script** {md(script)}', f'**In** {dbs}']
    if name in ALSO:
        meta.append(f'**Also** {md(ALSO[name])}')
    if added:
        meta.append('**Columns added by** ' + ', '.join(f'`{a}`' for a in added))
    out.append(' · '.join(meta) + '\n')
    out.append('| # | Column | Type | Null | Default / key | Notes |\n|--:|---|---|---|---|---|')
    for c in rows:
        flags = []
        if c['col'] in pkcols:
            flags.append('**PK**')
        if c['identity']:
            flags.append('identity')
        if c['dflt']:
            flags.append(code(c['dflt'].strip('()')))
        if c['computed']:
            flags.append('computed ' + code(c['computed']))
        note = OVERRIDE.get((name, c['col'])) or clean(n.get(c['col'], ''))
        if name == 'audit.UserDirectory' and note in AD_ATTR:
            note = 'AD attribute: ' + note
        out.append(f"| {c['id']} | `{c['col']}` | {typ(c)} | {'yes' if c['nullable'] else '**no**'} | {' '.join(flags)} | {md(note)} |")
    out.append('')
    extras = []
    for i in sorted(idx, key=lambda i: (not i['pk'], i['idx'])):
        what = i['type'].replace('_', ' ').lower()
        keys = f" ({i['keys']})" if i['keys'] else ''
        incl = f" include ({i['incl']})" if i['incl'] else ''
        filt = f" where {i['filter']}" if i['filter'] else ''
        tag = 'primary key, ' if i['pk'] else ('unique, ' if i['unique'] else '')
        extras.append(f"- `{i['idx']}`: {tag}{what}{md(keys)}{md(incl)}{md(filt)}")
    for k in chk:
        extras.append(f"- `{k['chk']}`: check {code(k['def'])}")
    if extras:
        out.append('Indexes and constraints:\n')
        out.extend(extras)
        out.append('')


for key, title, intro, objs in AREAS:
    out.append(f'## {title}\n')
    out.append(md(intro) + '\n')
    for o, p in objs:
        section(o, p)
out.append('## Left over from 1.x\n')
out.append('Present in a database upgraded from 1.x, as the lab is. No current script creates them.\n')
for o, p in LEGACY:
    section(o, p, legacy=True)

text = '\n'.join(out).rstrip() + '\n'
dst = os.path.join(HERE, '..', '..', '..', 'docs', 'SCHEMA.md')
open(dst, 'w', encoding='utf-8', newline='\n').write(text)
print(counts, len(text.splitlines()), 'lines')
