"""Writes the Word versions of the two reporting guides: docs/REPORTING.docx and docs/REPORTING-REFERENCE.docx.

Run it after either guide changed (and after Build-ReportingReference.ps1, which fills the reference's generated
parts), and commit the two files:

    pip install python-docx
    python tools/dev/schema/build_docx.py

Handles what those documents use: headings, paragraphs, bold, italic, inline code, links (as their text),
tables, fenced code blocks, bulleted and numbered lists (nested), rules, and HTML comments (dropped). US Letter;
headings are Word's own, so the Navigation pane lists them; a table's heading row repeats on each page.
"""
import html
import os
import re

from docx import Document
from docx.enum.table import WD_TABLE_ALIGNMENT
from docx.enum.text import WD_BREAK
from docx.oxml import OxmlElement
from docx.oxml.ns import qn
from docx.shared import Inches, Pt, RGBColor

CODE_FONT = 'Consolas'
INLINE = re.compile(r'(\*\*.+?\*\*|`[^`]+`|\[[^\]]+\]\([^)]+\)|(?<![\w*])\*[^*\s][^*]*?\*(?![\w*]))')


def shade(element, fill):
    pr = element.get_or_add_tcPr() if element.tag.endswith('}tc') else element.get_or_add_pPr()
    shd = OxmlElement('w:shd')
    shd.set(qn('w:val'), 'clear')
    shd.set(qn('w:color'), 'auto')
    shd.set(qn('w:fill'), fill)
    pr.append(shd)


def add_inline(par, text, size=None, bold=False):
    """Writes Markdown inline text into a paragraph as runs."""
    for part in INLINE.split(text):
        if not part:
            continue
        run_bold, italic, code = bold, False, False
        if part.startswith('**') and part.endswith('**') and len(part) > 4:
            # Bold may wrap a link or code: write its inside with bold on.
            add_inline(par, part[2:-2], size, True)
            continue
        if part.startswith('`') and part.endswith('`') and len(part) > 2:
            part, code = part[1:-1], True
        elif part.startswith('[') and '](' in part and part.endswith(')'):
            add_inline(par, part[1:part.index('](')], size, bold)
            continue
        elif part.startswith('*') and part.endswith('*') and len(part) > 2:
            part, italic = part[1:-1], True
        if not code:
            part = part.replace('\\|', '|').replace('\\\\', '\\')
        run = par.add_run(html.unescape(part))
        run.bold = run_bold or None
        run.italic = italic or None
        if code:
            run.font.name = CODE_FONT
            run._element.rPr.rFonts.set(qn('w:eastAsia'), CODE_FONT)
            run.font.size = Pt((size or 10.5) - 1)
        elif size:
            run.font.size = Pt(size)


def split_row(line):
    cells = re.split(r'(?<!\\)\|', line.strip().strip('|'))
    return [c.strip() for c in cells]


def add_table(doc, rows, width):
    cols = max(len(r) for r in rows)
    table = doc.add_table(rows=len(rows), cols=cols)
    table.style = 'Table Grid'
    table.alignment = WD_TABLE_ALIGNMENT.LEFT
    table.autofit = True
    # Column widths by the longest cell, within bounds, summing to the text width.
    longest = [max((len(r[c]) if c < len(r) else 0) for r in rows) for c in range(cols)]
    weights = [min(max(n, 6), 60) for n in longest]
    total = sum(weights)
    widths = [width * w / total for w in weights]
    for ri, r in enumerate(rows):
        row = table.rows[ri]
        for c in range(cols):
            cell = row.cells[c]
            cell.width = Inches(widths[c])
            par = cell.paragraphs[0]
            par.paragraph_format.space_after = Pt(1)
            par.paragraph_format.space_before = Pt(1)
            add_inline(par, r[c] if c < len(r) else '', 8.5, bold=(ri == 0))
            if ri == 0:
                shade(cell._element, 'E7EEF4')
    # Repeat the header row on each page.
    tr_pr = table.rows[0]._tr.get_or_add_trPr()
    header = OxmlElement('w:tblHeader')
    header.set(qn('w:val'), 'true')
    tr_pr.append(header)
    doc.add_paragraph().paragraph_format.space_after = Pt(2)


def add_code(doc, lines):
    par = doc.add_paragraph()
    par.paragraph_format.space_after = Pt(6)
    par.paragraph_format.left_indent = Inches(0.1)
    shade(par._element, 'F4F4F4')
    for i, line in enumerate(lines):
        run = par.add_run(line.replace('\t', '    '))
        run.font.name = CODE_FONT
        run._element.rPr.rFonts.set(qn('w:eastAsia'), CODE_FONT)
        run.font.size = Pt(7.5)
        if i < len(lines) - 1:
            run.add_break(WD_BREAK.LINE)


def convert(src, dst, title):
    text = open(src, encoding='utf-8').read().replace('\r\n', '\n')
    text = re.sub(r'<!--.*?-->\n?', '', text, flags=re.S)
    lines = text.split('\n')

    doc = Document()
    section = doc.sections[0]
    section.page_width, section.page_height = Inches(8.5), Inches(11)
    for side in ('left_margin', 'right_margin'):
        setattr(section, side, Inches(0.8))
    section.top_margin = section.bottom_margin = Inches(0.8)
    width = 8.5 - 1.6
    normal = doc.styles['Normal']
    normal.font.name = 'Calibri'
    normal.font.size = Pt(10.5)
    for level, size in ((1, 20), (2, 15), (3, 12.5), (4, 11)):
        style = doc.styles[f'Heading {level}']
        style.font.name = 'Calibri'
        style.font.size = Pt(size)
        style.font.color.rgb = RGBColor(0x1F, 0x3A, 0x5F)
    doc.core_properties.title = title
    footer = section.footer.paragraphs[0]
    footer.add_run(title + '  ·  page ').font.size = Pt(8)
    fld = OxmlElement('w:fldSimple')
    fld.set(qn('w:instr'), 'PAGE')
    footer._element.append(fld)

    i, first_heading = 0, True
    while i < len(lines):
        line = lines[i]
        fence = re.match(r'^(`{3,})(\w*)\s*$', line)
        if fence:
            mark, block = fence.group(1), []
            i += 1
            while i < len(lines) and lines[i].rstrip() != mark:
                block.append(lines[i])
                i += 1
            add_code(doc, block)
            i += 1
            continue
        heading = re.match(r'^(#{1,6}) (.+)$', line)
        if heading:
            level = min(len(heading.group(1)), 4)
            if level == 1 and not first_heading:
                doc.add_page_break()
            first_heading = False
            par = doc.add_heading(level=level)
            add_inline(par, heading.group(2))
            i += 1
            continue
        if line.startswith('|') and i + 1 < len(lines) and re.match(r'^\|[\s:|-]+\|\s*$', lines[i + 1]):
            rows = [split_row(line)]
            i += 2
            while i < len(lines) and lines[i].startswith('|'):
                rows.append(split_row(lines[i]))
                i += 1
            add_table(doc, rows, width)
            continue
        if re.match(r'^---+\s*$', line):
            i += 1
            continue
        item = re.match(r'^(\s*)([-*]|\d+\.) (.+)$', line)
        if item:
            depth = len(item.group(1)) // 2
            numbered = item.group(2)[0].isdigit()
            style = ('List Number' if numbered else 'List Bullet') + ('' if depth == 0 else f' {min(depth + 1, 3)}')
            body = item.group(3)
            i += 1
            # A wrapped item continues on indented lines.
            while i < len(lines) and lines[i].startswith(' ') and lines[i].strip() and not re.match(r'^\s*([-*]|\d+\.) ', lines[i]) and not lines[i].strip().startswith('```'):
                body += ' ' + lines[i].strip()
                i += 1
            par = doc.add_paragraph(style=style)
            par.paragraph_format.space_after = Pt(2)
            add_inline(par, body)
            continue
        if not line.strip():
            i += 1
            continue
        body = line.strip()
        i += 1
        while i < len(lines) and lines[i].strip() and not re.match(r'^(#{1,6} |\||```|---+\s*$|\s*([-*]|\d+\.) )', lines[i]):
            body += ' ' + lines[i].strip()
            i += 1
        par = doc.add_paragraph()
        par.paragraph_format.space_after = Pt(6)
        add_inline(par, body)

    doc.save(dst)
    print(f'{dst}: {len(doc.paragraphs):,} paragraphs, {len(doc.tables)} tables')


if __name__ == '__main__':
    docs = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', '..', 'docs')
    for name, title in (('REPORTING', 'Reporting on the Copilot data: the brief guide'),
                        ('REPORTING-REFERENCE', 'Reporting on the Copilot data: the reference')):
        src = os.path.join(docs, name + '.md')
        # The release the guide says it was written for goes in the title and the page footer.
        release = re.search(r'Written for release ([\d.]+\d)', open(src, encoding='utf-8').read())
        convert(src, os.path.join(docs, name + '.docx'), title + (f' ({release.group(1)})' if release else ''))