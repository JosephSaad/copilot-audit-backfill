"""Maps each table/view to the script that creates it, and pulls the column comments out of the DDL."""
import json, os, re

HERE = os.path.dirname(os.path.abspath(__file__))
SQL = os.path.join(HERE, '..', '..', '..', 'sql')
OUT = os.path.join(HERE, 'out')
os.makedirs(OUT, exist_ok=True)

created = {}      # 'audit.X' -> script
altered = {}      # 'audit.X' -> set(scripts adding columns)
notes = {}        # 'audit.X' -> {col: note}

TYPE_WORDS = r'(?:BIGINT|INT|SMALLINT|TINYINT|BIT|DATE|DATETIME2|DATETIME|DECIMAL|NUMERIC|FLOAT|VARCHAR|NVARCHAR|CHAR|NCHAR|VARBINARY|SYSNAME|UNIQUEIDENTIFIER)'

for fn in sorted(os.listdir(SQL)):
    if not fn.endswith('.sql'):
        continue
    text = open(os.path.join(SQL, fn), encoding='utf-8').read().replace('\r\n', '\n')
    for m in re.finditer(r'CREATE\s+TABLE\s+((?:audit|bi|dash)\.\w+)', text):
        created.setdefault(m.group(1), fn)
        # the column list: from the first '(' after the name to the matching ')'
        i = text.index('(', m.end())
        depth, j = 0, i
        while j < len(text):
            if text[j] == '(':
                depth += 1
            elif text[j] == ')':
                depth -= 1
                if depth == 0:
                    break
            j += 1
        body = text[i + 1:j]
        obj = m.group(1)
        pending = []
        for line in body.split('\n'):
            s = line.strip()
            if not s:
                continue
            if s.startswith('/*') or pending and not pending[-1].endswith('*/'):
                pending.append(s)
                continue
            if s.startswith('--'):
                pending.append(s)
                continue
            cm = re.match(r'(\[?\w+\]?)\s+' + TYPE_WORDS + r'\b', s, re.I)
            if cm:
                col = cm.group(1).strip('[]')
                trail = ''
                if '--' in s:
                    trail = s[s.index('--') + 2:].strip()
                lead = ' '.join(p.strip('/* ').rstrip('*/ ').strip('-').strip() for p in pending)
                lead = re.sub(r'\s+', ' ', lead).strip()
                note = ' '.join(x for x in (lead, trail) if x)
                if note:
                    notes.setdefault(obj, {})[col] = note
                pending = []
            else:
                pending = []
        # continuation lines of a trailing comment ("-- ..." lines right after a column) are folded above
    for m in re.finditer(r'ALTER\s+TABLE\s+((?:audit|bi|dash)\.\w+)\s+ADD\s+(\w+)\s+([^;\n]*)', text, re.I):
        obj, col, rest = m.group(1), m.group(2), m.group(3)
        if col.upper() in ('CONSTRAINT',):
            continue
        altered.setdefault(obj, set()).add(fn)
        if '--' in rest:
            notes.setdefault(obj, {}).setdefault(col, rest[rest.index('--') + 2:].strip())
    for m in re.finditer(r'VIEW\s+((?:audit|bi|dash)\.\w+)', text):
        created.setdefault(m.group(1), fn)
    for m in re.finditer(r'(?:PROCEDURE|FUNCTION)\s+((?:audit|bi|dash)\.\w+)', text):
        created.setdefault(m.group(1), fn)

json.dump({'created': created, 'altered': {k: sorted(v) for k, v in altered.items()}, 'notes': notes},
          open(os.path.join(OUT, 'scripts.json'), 'w', encoding='utf-8'), indent=1)
for k in sorted(created):
    print(f'{k:40} {created[k]:42} notes={len(notes.get(k, {}))}')
