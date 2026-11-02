"""
Builds License-Management.pptx (16:9) from the walkthrough screenshots and scenes.json.
    python docs/license-management/build_deck.py
"""
import json, pathlib, copy
from pptx import Presentation
from pptx.util import Inches, Pt, Emu
from pptx.dml.color import RGBColor
from pptx.enum.shapes import MSO_SHAPE, MSO_CONNECTOR
from pptx.enum.text import PP_ALIGN, MSO_ANCHOR
from pptx.oxml.ns import qn
from lxml import etree

HERE = pathlib.Path(__file__).parent
SHOTS = HERE / "shots"
SCENES = {s["file"]: s for s in json.loads((HERE / "scenes.json").read_text(encoding="utf-8"))}

# Palette: deep ink-teal dominant, pale mist, amber accent (the screenshots' highlight color).
INK = RGBColor(0x12, 0x34, 0x3B)
TEAL = RGBColor(0x2D, 0x7D, 0x8A)
MIST = RGBColor(0xEE, 0xF4, 0xF5)
AMBER = RGBColor(0xF5, 0x9E, 0x0B)
WHITE = RGBColor(0xFF, 0xFF, 0xFF)
TEXT = RGBColor(0x1F, 0x29, 0x33)
MUTED = RGBColor(0x5B, 0x67, 0x70)
LINE = RGBColor(0xD5, 0xDE, 0xE1)
FONT = "Calibri"

ROLE_COLORS = {
    "Workbench administrator": RGBColor(0x47, 0x55, 0x69),
    "Platform Owner (Digital Workplace)": RGBColor(0x2D, 0x7D, 0x8A),
    "Platform operator": RGBColor(0x0E, 0x74, 0x90),
    "FinOps lead (Finance)": RGBColor(0xB4, 0x53, 0x09),
    "Intake analyst": RGBColor(0x6D, 0x28, 0xD9),
    "LOB business owner": RGBColor(0x15, 0x80, 0x3D),
    "LOB seat admin": RGBColor(0x1D, 0x4E, 0xD8),
    "Auditor": RGBColor(0x37, 0x41, 0x51),
}


def role_color(role):
    for k, v in ROLE_COLORS.items():
        if role == k: return v
    if "business owner" in role: return ROLE_COLORS["LOB business owner"]
    if "seat admin" in role: return ROLE_COLORS["LOB seat admin"]
    return TEAL


prs = Presentation()
prs.slide_width, prs.slide_height = Inches(13.333), Inches(7.5)
BLANK = prs.slide_layouts[6]
SW, SH = 13.333, 7.5
sections = []   # (title, [slide ids])


def new_slide(section, dark=False):
    s = prs.slides.add_slide(BLANK)
    bg = s.background.fill
    bg.solid(); bg.fore_color.rgb = INK if dark else WHITE
    if not sections or sections[-1][0] != section:
        sections.append((section, []))
    sections[-1][1].append(s.slide_id)
    return s


def text(slide, x, y, w, h, runs, size=16, color=TEXT, bold=False, align=PP_ALIGN.LEFT, anchor=MSO_ANCHOR.TOP,
         name=None, spacing=None, italic=False):
    """runs: str, or list of paragraphs; a paragraph is a str or a list of (text, {opts})."""
    tb = slide.shapes.add_textbox(Inches(x), Inches(y), Inches(w), Inches(h))
    if name: tb.name = name
    tf = tb.text_frame
    tf.word_wrap = True
    tf.margin_left = tf.margin_right = tf.margin_top = tf.margin_bottom = 0
    tf.vertical_anchor = anchor
    paras = runs if isinstance(runs, list) else [runs]
    for i, p in enumerate(paras):
        para = tf.paragraphs[0] if i == 0 else tf.add_paragraph()
        para.alignment = align
        if spacing: para.space_after = Pt(spacing)
        parts = p if isinstance(p, list) else [(p, {})]
        for t, o in parts:
            r = para.add_run()
            r.text = t
            f = r.font
            f.name = FONT
            f.size = Pt(o.get("size", size))
            f.bold = o.get("bold", bold)
            f.italic = o.get("italic", italic)
            f.color.rgb = o.get("color", color)
    return tb


def box(slide, x, y, w, h, fill, line=None, shape=MSO_SHAPE.ROUNDED_RECTANGLE, radius=0.08, name=None):
    s = slide.shapes.add_shape(shape, Inches(x), Inches(y), Inches(w), Inches(h))
    if name: s.name = name
    s.fill.solid(); s.fill.fore_color.rgb = fill
    if line is None: s.line.fill.background()
    else: s.line.color.rgb = line; s.line.width = Pt(1)
    s.shadow.inherit = False
    if shape == MSO_SHAPE.ROUNDED_RECTANGLE:
        s.adjustments[0] = radius
    s.text_frame.text = ""
    return s


def circle_label(slide, x, y, d, fill, label, size=14, color=WHITE):
    c = box(slide, x, y, d, d, fill, shape=MSO_SHAPE.OVAL)
    tf = c.text_frame
    tf.margin_left = tf.margin_right = tf.margin_top = tf.margin_bottom = 0
    tf.vertical_anchor = MSO_ANCHOR.MIDDLE
    p = tf.paragraphs[0]; p.alignment = PP_ALIGN.CENTER
    r = p.add_run(); r.text = label
    r.font.name = FONT; r.font.size = Pt(size); r.font.bold = True; r.font.color.rgb = color
    return c


def arrow(slide, x1, y1, x2, y2, color=TEAL):
    c = slide.shapes.add_connector(MSO_CONNECTOR.STRAIGHT, Inches(x1), Inches(y1), Inches(x2), Inches(y2))
    c.line.color.rgb = color; c.line.width = Pt(2)
    ln = c.line._get_or_add_ln()
    tail = etree.SubElement(ln, qn("a:tailEnd")); tail.set("type", "triangle"); tail.set("w", "med"); tail.set("len", "med")
    return c


def title(slide, t, sub=None, dark=False):
    text(slide, 0.6, 0.45, 12.1, 0.75, t, size=34, bold=True, color=WHITE if dark else INK, name="Title")
    if sub:
        text(slide, 0.6, 1.2, 12.1, 0.45, sub, size=16, color=MIST if dark else MUTED, name="Subtitle")


def footer(slide, label):
    text(slide, 0.6, 7.05, 8, 0.3, f"Copilot License Management  ·  {label}", size=10, color=MUTED, name="Footer")


def notes(slide, t):
    slide.notes_slide.notes_text_frame.text = t


def initials(name):
    return "".join(w[0] for w in name.split()[:2]).upper()


# ------------------------------------------------------------------------------------------------
# Slides
# ------------------------------------------------------------------------------------------------

def title_slide():
    s = new_slide("Overview", dark=True)
    circle_label(s, 0.6, 1.5, 0.9, AMBER, "✓", size=30, color=INK)
    text(s, 0.6, 2.7, 11.5, 1.2, "Copilot License Management", size=48, bold=True, color=WHITE)
    text(s, 0.6, 3.75, 11, 0.9, "Reserve, track and charge Microsoft 365 Copilot seats per line of business in the Governance Workbench",
         size=20, color=MIST)
    text(s, 0.6, 5.3, 11, 0.9, [
        [("Prerequisites  ·  Admin configuration  ·  Assignments  ·  Who does what", {"color": AMBER, "bold": True})],
        [("Walkthrough on demo data · 29 October 2026", {"color": MIST, "size": 14})],
    ], size=16, spacing=6)
    notes(s, "This deck covers only the License Management part of the Governance Workbench: the paid Microsoft 365 Copilot license, "
             "reserved per LOB and counted in seats. It walks through what must be in place first, how administrators configure it, "
             "how LOB seat admins are assigned, and who does what day to day. All screenshots use demo data.")


def overview_slide():
    s = new_slide("Overview")
    title(s, "What License Management does", "The Workbench reads who holds a seat; it never assigns licenses itself")
    steps = [
        ("OIM role", "One role per LOB, provisioned through ServiceNow"),
        ("AD groups", "One shared license group, plus one tracking group per LOB"),
        ("Entra ID", "Groups synced from AD; group-based licensing assigns the Copilot seat"),
        ("Graph read-back", "Daily and read-only: purchased seats, group members, license states, Copilot usage"),
    ]
    x, y, w, h, gap = 0.6, 2.0, 2.75, 1.9, 0.42
    for i, (t, d) in enumerate(steps):
        bx = x + i * (w + gap)
        box(s, bx, y, w, h, MIST)
        circle_label(s, bx + 0.2, y + 0.2, 0.5, TEAL, str(i + 1), size=14)
        text(s, bx + 0.85, y + 0.25, w - 1.0, 0.45, t, size=17, bold=True, color=INK)
        text(s, bx + 0.2, y + 0.85, w - 0.4, h - 0.95, d, size=13, color=TEXT)
        if i < 3:
            arrow(s, bx + w + 0.05, y + h / 2, bx + w + gap - 0.05, y + h / 2)
    # Outcomes
    text(s, 0.6, 4.35, 12, 0.4, "What each LOB and Finance get in the Workbench", size=18, bold=True, color=INK)
    outs = [
        ("Reserved vs actual", "Seats reserved, seats assigned, over and unused, per LOB and month"),
        ("Change requests", "More seats any time; fewer only at the 1 April EA anniversary"),
        ("Findings and reclaim", "No-LOB seats, two LOBs, errors, leavers; unused seats proposed back"),
        ("Monthly chargeback", "Reservation plus seats above it, one rate; certified months never change"),
    ]
    for i, (t, d) in enumerate(outs):
        bx = x + i * (w + gap)
        box(s, bx, 4.9, w, 1.75, WHITE, line=LINE)
        text(s, bx + 0.2, 5.05, w - 0.4, 0.4, t, size=16, bold=True, color=TEAL)
        text(s, bx + 0.2, 5.5, w - 0.4, 1.1, d, size=13, color=TEXT)
    footer(s, "Overview")
    notes(s, "People get a seat the way they do today: an OIM role per LOB, provisioned by ServiceNow, adds them to the shared license group "
             "and their LOB's tracking group in AD. Entra group-based licensing assigns the seat. The Workbench reads this back from Microsoft Graph every day, "
             "read-only, and turns it into reservation versus actual per LOB, change requests, findings and reclaim, and a monthly chargeback.")


LOBS = [
    ("Asset Management Group (AMG)", "GSGu_MCO_AMGTracking", "Asset Management Group"),
    ("Corporate & Institutional Banking (C&IB)", "GSGu_MCO_CIBTracking", "Corporate and Institutional Banking"),
    ("Audit", "GSGu_MCO_AuditTracking", "Audit"),
    ("Corporate Responsibility Group", "GSGu_MCO_CorpRespTracking", "Corporate Responsibility"),
    ("Regional Presidents", "GSGu_MCO_RegPresTracking", "Regional President"),
    ("Technology", "GSGu_MCO_TechSysTracking", "Technology and Systems"),
    ("Independent Risk Management (IRM)", "GSGu_MCO_IRMTracking", "Independent Risk Management"),
    ("Retail", "GSGu_MCO_RetailTracking", "Retail"),
    ("Human Resources (HR)", "GSGu_MCO_HRTracking", "Human Resources"),
    ("Finance Investments", "GSGu_MCO_FinInvTracking", "Finance Investments"),
    ("Finance", "GSGu_MCO_FinanceTracking", "Finance"),
    ("Legal", "GSGu_MCO_LegalTracking", "Legal"),
    ("Marketing & Communications", "GSGu_MCO_MarComTracking", "Marketing and Communication"),
    ("BSAM (Balance Sheet Analytics and Modeling)", "GSGu_MCO_BSAMTracking", "Balance Sheet Analytics and Modeling"),
]


def table_rows(slide, x, y, colw, rows, header, size=11, rowh=0.33, header_fill=INK, vpad=0.03):
    tbl = slide.shapes.add_table(len(rows) + 1, len(colw), Inches(x), Inches(y), Inches(sum(colw)), Inches(rowh * (len(rows) + 1))).table
    for j, w in enumerate(colw): tbl.columns[j].width = Inches(w)
    def cell(i, j, t, bold=False, fill=None, color=TEXT, align=PP_ALIGN.LEFT):
        c = tbl.cell(i, j)
        c.margin_left = c.margin_right = Inches(0.08); c.margin_top = c.margin_bottom = Inches(vpad)
        c.vertical_anchor = MSO_ANCHOR.MIDDLE
        tf = c.text_frame; tf.word_wrap = True
        p = tf.paragraphs[0]; p.alignment = align
        p.text = ""
        r = p.add_run(); r.text = t
        r.font.name = FONT; r.font.size = Pt(size); r.font.bold = bold; r.font.color.rgb = color
        c.fill.solid(); c.fill.fore_color.rgb = fill or WHITE
    for j, h in enumerate(header): cell(0, j, h, bold=True, fill=header_fill, color=WHITE)
    for i, row in enumerate(rows, start=1):
        for j, v in enumerate(row):
            cell(i, j, v, fill=MIST if i % 2 == 0 else WHITE)
    for i in range(len(rows) + 1): tbl.rows[i].height = Inches(rowh)
    return tbl


def lob_slide():
    s = new_slide("Overview")
    title(s, "Fourteen LOBs, one tracking group each", "The LOB comes from the tracking group; the seat comes from the shared GSGu_MCO_CopilotM365 group")
    rows = [(l, g, f"{o} . M365 Copilot License . Role") for l, g, o in LOBS]
    table_rows(s, 0.6, 1.85, [4.1, 3.2, 4.8], rows, ["LOB", "Tracking group (AD)", "OIM role that adds people"], size=11, rowh=0.335)
    footer(s, "Overview")
    notes(s, "All fourteen LOBs map one-to-one to a GSGu MCO Tracking group created by ServiceNow. Note that the OIM role names do not always match the LOB name, "
             "for example Technology's role is Technology and Systems. The Workbench records the exact role, because a reclaim ticket must name it.")


def divider(num, name, sub):
    s = new_slide(name, dark=True)
    circle_label(s, 0.6, 2.3, 1.2, AMBER, str(num), size=40, color=INK)
    text(s, 2.1, 2.35, 10, 1.0, name, size=44, bold=True, color=WHITE)
    text(s, 2.1, 3.35, 10, 1.2, sub, size=20, color=MIST)
    notes(s, f"Section {num}: {name}. {sub}")
    return s


def prereq_slide():
    s = new_slide("Prerequisites")
    title(s, "Five things in place before go-live", "Most happen outside the Workbench; each has one owner")
    items = [
        ("AD groups and OIM roles", "License group and 14 tracking groups exist, provisioned by OIM roles through ServiceNow, synced to Entra",
         "Identity / OIM team"),
        ("Graph permissions", "Worker app gets LicenseAssignment.Read.All and Reports.Read.All (application, read-only) and admin consent",
         "Entra administrator"),
        ("Usage report names", "Turn off “Display concealed user, group, and site names” so unused seats match people",
         "Microsoft 365 administrator"),
        ("Workbench running", "Web, worker and SQL deployed; the worker reads seats daily and on demand",
         "Platform operations"),
        ("Policy settings agreed", "EA anniversary 1 April, 45-day true-down window, buffer of 25 seats, one seat rate",
         "Finance with Digital Workplace"),
    ]
    y = 1.9
    for i, (t, d, who) in enumerate(items):
        yy = y + i * 1.0
        box(s, 0.6, yy, 12.13, 0.86, MIST)
        circle_label(s, 0.8, yy + 0.18, 0.5, TEAL, str(i + 1), size=14)
        text(s, 1.55, yy + 0.12, 3.0, 0.62, t, size=16, bold=True, color=INK, anchor=MSO_ANCHOR.MIDDLE)
        text(s, 4.6, yy + 0.1, 5.4, 0.66, d, size=13, color=TEXT, anchor=MSO_ANCHOR.MIDDLE)
        pill = box(s, 10.2, yy + 0.2, 2.35, 0.46, WHITE, line=LINE, radius=0.5)
        text(s, 10.25, yy + 0.2, 2.25, 0.46, who, size=12, bold=True, color=TEAL, align=PP_ALIGN.CENTER, anchor=MSO_ANCHOR.MIDDLE)
    footer(s, "1. Prerequisites")
    notes(s, "Before go-live: the AD groups and OIM roles exist and sync to Entra; an Entra administrator grants the Worker app two read-only Graph permissions; "
             "a Microsoft 365 administrator makes sure usage reports show names; Platform operations runs the Workbench; and Finance with Digital Workplace agree the policy settings.")


CAPTIONS = {
    "01-seat-read.png": "Operations › Sync shows the daily seat read and can run it now",
    "02-seat-groups.png": "Admin › Seat groups: the license group and each LOB's tracking group",
    "03-confirm-purpose.png": "Exclusion and Chat-only groups raise findings only once confirmed",
    "04-skus-settings.png": "SKUs decide what counts as a seat; settings follow the policy",
    "05-baseline-form.png": "Each LOB's first reservation is recorded once by FinOps",
    "06-first-admin.png": "The business owner names the first LOB seat admin",
    "07-more-admins.png": "Seat admins add other seat admins, for their own LOB only",
    "08-own-lob-only.png": "Row-level security shows each seat admin only their LOB",
    "09-reserved-vs-actual.png": "Reserved, assigned, over and unused for the LOB",
    "10-trend.png": "Monthly reserved against assigned, with open findings",
    "11-ask-increase.png": "More seats can be asked for at any time",
    "12-intake.png": "Intake checks the request is complete",
    "13-dw-consult.png": "Digital Workplace confirms inventory before Finance decides",
    "14-finops-decide.png": "The FinOps lead decides, with a flex-pool review",
    "15-history.png": "History keeps previous and new seats, approver and review",
    "16-decrease-window.png": "A decrease shows the April true-down window",
    "17-held.png": "Outside the window the decrease is held, not refused",
    "18-reclaim.png": "Unused seats: the business owner keeps or reclaims",
    "19-findings.png": "Operations follows up seat findings",
    "20-chargeback.png": "FinOps certifies the monthly chargeback",
    "21-auditor.png": "Auditors read and export, and change nothing",
}


def shot_slide(file, section_label):
    sc = SCENES[file]
    s = new_slide(sc["section"].split(". ", 1)[1])
    text(s, 0.6, 0.4, 12.1, 0.7, sc["title"], size=28, bold=True, color=INK, name="Title")
    # Screenshot, framed.
    ix, iy, iw = 0.6, 1.35, 8.96
    ih = iw * 900 / 1440
    frame = box(s, ix - 0.04, iy - 0.04, iw + 0.08, ih + 0.08, LINE, shape=MSO_SHAPE.RECTANGLE)
    pic = s.shapes.add_picture(str(SHOTS / file), Inches(ix), Inches(iy), Inches(iw), Inches(ih))
    pic.name = "Screenshot"
    # Right panel: step, role badge, caption.
    px, pw = 9.95, 2.78
    text(s, px, 1.35, pw, 0.3, f"STEP {sc['n']} OF 21", size=11, bold=True, color=MUTED)
    rc = role_color(sc["role"])
    box(s, px, 1.75, pw, 1.3, MIST)
    circle_label(s, px + 0.18, 1.95, 0.62, rc, initials(sc["persona"]), size=15)
    text(s, px + 0.95, 1.92, pw - 1.05, 0.4, sc["persona"], size=15, bold=True, color=INK)
    text(s, px + 0.95, 2.3, pw - 1.05, 0.65, sc["role"], size=12, color=rc, bold=True)
    text(s, px, 3.35, pw, 2.2, CAPTIONS[file], size=16, color=TEXT)
    import re
    page = re.sub(r"[0-9a-f]{8}-[0-9a-f-]{27}", "{id}", sc["url"].split("?")[0].split("#")[0])
    text(s, px, 6.25, pw, 0.4, "Page: " + page, size=10, color=MUTED, italic=True)
    footer(s, section_label)
    s.shapes._spTree.remove(frame._element); s.shapes._spTree.insert(2, frame._element)
    notes(s, sc["narration"])
    return s


def admin_rules_slide():
    s = new_slide("Assignments")
    title(s, "The LOB seat admin role", "Scoped to one LOB: tracks seats and names other seat admins; approves nothing")
    cards = [
        ("Who names the first one", "The LOB business owner or the Platform Owner"),
        ("Who can add more", "A seat admin, for the same LOB and only this role"),
        ("What they see", "Only their LOB's seats, holders, trend and findings"),
        ("What they can ask", "A reservation change for their LOB, at any time"),
        ("Guard rails", "No one names themselves; the last admin can't remove themselves"),
        ("Record", "Who assigned whom and when, in the audit trail"),
    ]
    cw, chh, gx, gy = 3.85, 2.05, 0.3, 0.3
    for i, (t, d) in enumerate(cards):
        cx = 0.6 + (i % 3) * (cw + gx); cy = 1.95 + (i // 3) * (chh + gy)
        box(s, cx, cy, cw, chh, MIST)
        circle_label(s, cx + 0.25, cy + 0.25, 0.5, ROLE_COLORS["LOB seat admin"], str(i + 1), size=14)
        text(s, cx + 0.25, cy + 0.9, cw - 0.5, 0.4, t, size=17, bold=True, color=INK)
        text(s, cx + 0.25, cy + 1.32, cw - 0.5, 0.65, d, size=14, color=TEXT)
    footer(s, "3. Assignments")
    notes(s, "LOB seat admins are named per LOB. The business owner or the Platform Owner names the first one; seat admins can then add others for the same LOB only. "
             "They see only their LOB, can ask for reservation changes, and approve nothing. Nobody can name themselves, the last seat admin cannot remove themselves, "
             "and every assignment is audited.")


def flow_slide():
    s = new_slide("Who does what")
    title(s, "How a reservation change moves", "Increases any time; decreases approved only from 15 February and effective 1 April")
    steps = [
        ("LOB seat admin", "Asks: new total and who needs the seats", ROLE_COLORS["LOB seat admin"]),
        ("Intake analyst", "Checks the request is complete", ROLE_COLORS["Intake analyst"]),
        ("Digital Workplace", "Confirms unallocated seats and buffer (blocking)", ROLE_COLORS["Platform Owner (Digital Workplace)"]),
        ("FinOps lead", "Decides, with a flex-pool review", ROLE_COLORS["FinOps lead (Finance)"]),
        ("Workbench", "Records the new reservation and its history", INK),
    ]
    w, gap, y = 2.2, 0.28, 2.15
    for i, (who, what, c) in enumerate(steps):
        x = 0.6 + i * (w + gap)
        box(s, x, y, w, 1.85, MIST)
        circle_label(s, x + 0.2, y + 0.2, 0.5, c, str(i + 1), size=14)
        text(s, x + 0.2, y + 0.8, w - 0.4, 0.4, who, size=15, bold=True, color=INK)
        text(s, x + 0.2, y + 1.18, w - 0.4, 0.65, what, size=12, color=TEXT)
        if i < 4: arrow(s, x + w + 0.03, y + 0.93, x + w + gap - 0.03, y + 0.93)
    # Decrease branch
    box(s, 0.6, 4.45, 12.13, 2.25, WHITE, line=AMBER)
    text(s, 0.9, 4.6, 11.5, 0.4, "When an LOB asks for fewer seats", size=17, bold=True, color=INK)
    text(s, 0.9, 5.1, 11.6, 1.5, [
        "Outside the window (before 15 February) Finance holds the request; the reservation does not change and the LOB keeps paying for it.",
        "When the window opens, Finance is reminded and Digital Workplace is consulted again.",
        "An approved decrease takes effect on 1 April; certified chargeback months before it never change.",
    ], size=14, color=TEXT, spacing=6)
    footer(s, "4. Who does what")
    notes(s, "A reservation change goes from the LOB seat admin to intake, then Digital Workplace confirms there are seats, then the FinOps lead decides with a flex-pool review, "
             "and the Workbench records the new reservation. A decrease asked for outside the true-down window is held, not refused; Finance is reminded when the window opens on 15 February, "
             "and the decrease takes effect on 1 April.")


RACI_ROLES = ["Workbench admin", "Platform Owner (DW)", "Platform operator", "FinOps lead", "Intake analyst", "LOB business owner", "LOB seat admin", "Auditor"]
RACI = [
    ("Map seat groups and SKUs",            ["R", "A", "C", "", "", "", "", "I"]),
    ("Run and monitor the seat read",       ["C", "I", "R", "", "", "", "", ""]),
    ("Record an LOB's first reservation",   ["", "I", "", "A/R", "", "C", "", "I"]),
    ("Name the first LOB seat admin",       ["", "R", "", "", "", "A/R", "", "I"]),
    ("Name more seat admins",               ["", "", "", "", "", "A", "R", "I"]),
    ("Track reserved against actual",       ["", "I", "", "I", "", "R", "R", "I"]),
    ("Ask for a reservation change",        ["", "", "", "", "", "A", "R", ""]),
    ("Check the request is complete",       ["", "", "", "", "R", "", "", ""]),
    ("Confirm inventory and buffer",        ["", "C", "", "", "", "", "", ""]),
    ("Decide the reservation change",       ["", "C", "", "A", "", "C", "I", "I"]),
    ("Keep or reclaim unused seats",        ["", "", "I", "", "", "A/R", "I", ""]),
    ("Work seat findings",                  ["", "A", "R", "", "", "I", "I", ""]),
    ("Certify the monthly chargeback",      ["", "I", "", "A/R", "", "I", "I", "I"]),
    ("Audit and export",                    ["", "", "", "", "", "", "", "R"]),
]


def raci_slide():
    s = new_slide("Who does what")
    title(s, "Who does what in License Management", "A decides  ·  R does the work  ·  C is consulted  ·  I is informed or can view")
    colw = [3.25] + [1.11] * 8
    tbl = table_rows(s, 0.6, 1.7, colw, [(a, *v) for a, v in RACI], ["Activity"] + RACI_ROLES, size=10.5, rowh=0.3, vpad=0.0)
    fills = {"A": AMBER, "A/R": AMBER, "R": TEAL, "C": RGBColor(0xCF, 0xE3, 0xE6)}
    for i, (_, vals) in enumerate(RACI, start=1):
        for j, v in enumerate(vals, start=1):
            c = tbl.cell(i, j)
            c.text_frame.paragraphs[0].alignment = PP_ALIGN.CENTER
            if v in fills:
                c.fill.solid(); c.fill.fore_color.rgb = fills[v]
                r = c.text_frame.paragraphs[0].runs[0]
                r.font.bold = True
                r.font.color.rgb = WHITE if v != "C" else INK
    for j in range(1, 9):
        tbl.cell(0, j).text_frame.paragraphs[0].alignment = PP_ALIGN.CENTER
    notes(s, "The matrix sums up the License Management activities by role. The FinOps lead decides reservation changes and certifies the chargeback. "
             "Digital Workplace is consulted on inventory. LOB business owners are accountable for their LOB's reservation and reclaim; LOB seat admins do the tracking and asking. "
             "Platform operators run the seat read and work findings; the Workbench administrator maps groups; auditors read and export.")


def decisions_slide():
    s = new_slide("Open decisions", dark=True)
    title(s, "Open decisions for Finance and Digital Workplace", "Each is a setting or a short rule; the Workbench runs with today's assumption until decided", dark=True)
    items = [
        ("Seats with no LOB", "Who pays for a seat held by someone in no tracking group?", "Today: shown as No LOB, charged to nobody"),
        ("Seat rate", "The one monthly rate for every LOB", "Today: $30.00 USD placeholder"),
        ("Increase effective date", "From the decision date or the first of next month?", "Today: decision date"),
        ("April cycle vs calendar chargeback", "How remaining months count after an April true-down", "Today: whole calendar months, certified monthly"),
        ("Other MCO groups", "What ExclusionLic, FinExclude, EmpRelExclude, ChatOnly and CopilotM365Mgr do", "Today: recorded, unconfirmed, no findings"),
    ]
    for i, (t, q, now) in enumerate(items):
        y = 1.9 + i * 1.0
        box(s, 0.6, y, 12.13, 0.86, RGBColor(0x1C, 0x48, 0x50))
        circle_label(s, 0.8, y + 0.18, 0.5, AMBER, str(i + 1), size=14, color=INK)
        text(s, 1.55, y + 0.1, 3.4, 0.66, t, size=16, bold=True, color=WHITE, anchor=MSO_ANCHOR.MIDDLE)
        text(s, 5.05, y + 0.1, 4.3, 0.66, q, size=13, color=MIST, anchor=MSO_ANCHOR.MIDDLE)
        text(s, 9.5, y + 0.1, 3.05, 0.66, now, size=12, color=AMBER, bold=True, anchor=MSO_ANCHOR.MIDDLE)
    notes(s, "Five points are still open. Who pays for seats no LOB claims; the real seat rate, today a thirty dollar placeholder; when an approved increase takes effect; "
             "how the April EA cycle lines up with calendar-year chargeback; and what the other MCO groups are for. Each is a setting or a short rule in the Workbench.")


def build():
    title_slide(); overview_slide(); lob_slide()
    divider(1, "Prerequisites", "What must exist before anyone uses License Management, and who sets it up")
    prereq_slide()
    shot_slide("01-seat-read.png", "1. Prerequisites")
    divider(2, "Admin configuration", "Map the groups, check SKUs and settings, record first reservations")
    for f in ["02-seat-groups.png", "03-confirm-purpose.png", "04-skus-settings.png", "05-baseline-form.png"]:
        shot_slide(f, "2. Admin configuration")
    divider(3, "Assignments", "Business owners name LOB seat admins; seat admins name more for their own LOB")
    admin_rules_slide()
    for f in ["06-first-admin.png", "07-more-admins.png", "08-own-lob-only.png"]:
        shot_slide(f, "3. Assignments")
    divider(4, "Who does what", "Track, ask, check, confirm, decide, reclaim, follow up and certify")
    flow_slide()
    for i in range(9, 22):
        f = next(k for k in SCENES if k.startswith(f"{i:02d}-"))
        shot_slide(f, "4. Who does what")
    raci_slide()
    decisions_slide()
    write_sections()
    out = HERE / "License-Management.pptx"
    prs.save(out)
    print(f"{len(prs.slides)} slides -> {out}")


def write_sections():
    """PowerPoint sections (p14:sectionLst) so the deck navigates by part."""
    pres = prs.part._element
    extLst = pres.find(qn("p:extLst"))
    if extLst is None:
        extLst = etree.SubElement(pres, qn("p:extLst"))
    P14 = "http://schemas.microsoft.com/office/powerpoint/2010/main"
    ext = etree.SubElement(extLst, qn("p:ext")); ext.set("uri", "{521415D9-36F7-43E2-AB2F-B90AF26B5E84}")
    lst = etree.SubElement(ext, f"{{{P14}}}sectionLst", nsmap={"p14": P14})
    import uuid
    for name, ids in sections:
        sec = etree.SubElement(lst, f"{{{P14}}}section"); sec.set("name", name); sec.set("id", "{" + str(uuid.uuid4()).upper() + "}")
        sl = etree.SubElement(sec, f"{{{P14}}}sldIdLst")
        for i in ids:
            e = etree.SubElement(sl, f"{{{P14}}}sldId"); e.set("id", str(i))


if __name__ == "__main__":
    build()
