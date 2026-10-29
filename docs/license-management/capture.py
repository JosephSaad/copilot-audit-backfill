"""
Captures the License Management walkthrough: signs in as each role (test sign-in, Development only), does the
step, highlights what matters and saves a screenshot plus its subtitle into scenes.json.

Needs: the web on http://localhost:5483 with Testing:TestAuthentication against a freshly seeded walkthrough
database (BSAM without a reservation or seat admin), Python with Playwright, and Microsoft Edge.
    python docs/license-management/capture.py
"""
import json, re, sys, pathlib
from playwright.sync_api import sync_playwright

BASE = "http://localhost:5483"
OUT = pathlib.Path(__file__).parent / "shots"
OUT.mkdir(parents=True, exist_ok=True)

P = {  # persona keys -> (upn, display name, role)
    "admin":     ("leo.fischer@example.invalid",          "Leo Fischer",     "Workbench administrator"),
    "owner":     ("diane.moreau@example.invalid",         "Diane Moreau",    "Platform Owner (Digital Workplace)"),
    "operator":  ("kevin.brooks@example.invalid",         "Kevin Brooks",    "Platform operator"),
    "finops":    ("carlos.mendes@example.invalid",        "Carlos Mendes",   "FinOps lead (Finance)"),
    "intake":    ("aisha.rahman@example.invalid",         "Aisha Rahman",    "Intake analyst"),
    "bsamOwner": ("seatlob.bsam.owner@example.invalid",   "Dev Duarte",      "BSAM business owner"),
    "bsamAdmin": ("seatlob.bsam.seatadmin@example.invalid","Farid Ferreira", "BSAM seat admin"),
    "amgAdmin":  ("seatlob.amg.seatadmin@example.invalid", "Keiko Kowalski", "AMG seat admin"),
    "amgOwner":  ("seatlob.amg.owner@example.invalid",    "Ines Ibarra",     "AMG business owner"),
    "retAdmin":  ("seatlob.retail.seatadmin@example.invalid","Bruno Carvalho","Retail seat admin"),
    "auditor":   ("ruth.campbell@example.invalid",        "Ruth Campbell",   "Auditor"),
}

scenes = []
state = {}

HIGHLIGHT_CSS = """
.wt-hl { outline: 4px solid #f59e0b !important; outline-offset: 4px !important; border-radius: 8px;
         box-shadow: 0 0 0 9999px rgba(15,23,42,.10) !important; position: relative; z-index: 5; }
"""


def lob_id(page, name):
    page.goto(f"{BASE}/Registry/Seats", wait_until="networkidle")
    href = page.locator("a", has_text=name).first.get_attribute("href")
    return href.split("?")[0]


def shot(page, key, section, persona, title, narration, highlight=None, top=None):
    page.add_style_tag(content=HIGHLIGHT_CSS)
    target = None
    if highlight:
        target = page.locator(highlight).first
        if target.count() == 0:
            print(f"  ! highlight not found: {highlight}")
            target = None
    if target is not None:
        target.evaluate("e => e.classList.add('wt-hl')")
        target.evaluate("e => { const r = e.getBoundingClientRect(); window.scrollBy(0, r.top - 140); }")
    elif top:
        el = page.locator(top).first
        if el.count():
            el.evaluate("e => { const r = e.getBoundingClientRect(); window.scrollBy(0, r.top - 100); }")
    page.wait_for_timeout(250)
    n = len(scenes) + 1
    file = f"{n:02d}-{key}.png"
    page.screenshot(path=str(OUT / file))
    upn, name, role = P[persona]
    scenes.append({"n": n, "file": file, "section": section, "persona": name, "role": role,
                   "title": title, "narration": narration, "url": page.url.replace(BASE, "")})
    print(f"{n:02d} {key}: {title}")


def as_(browser, persona):
    ctx = browser.new_context(viewport={"width": 1440, "height": 900}, bypass_csp=True)
    page = ctx.new_page()
    page.on("dialog", lambda d: d.accept())
    page.goto(f"{BASE}/Start?testUpn={P[persona][0]}", wait_until="networkidle")
    return ctx, page


def flash(page):
    t = page.locator(".flash, .alert, .notice, [role=alert], [role=status]").first
    return t.inner_text().strip()[:200] if t.count() else ""


def task_form(page, handler, button_text=None):
    forms = page.locator(f"form[action*='handler={handler}']")
    if button_text:
        forms = forms.filter(has=page.locator("button", has_text=button_text))
    return forms.first


def run():
    with sync_playwright() as pw:
        b = pw.chromium.launch(channel="msedge", headless=True)

        # ---------------- 1. Prerequisites -------------------------------------------------------
        S = "1. Prerequisites"
        ctx, pg = as_(b, "operator")
        pg.goto(f"{BASE}/Operations/Sync", wait_until="networkidle")
        shot(pg, "seat-read", S, "operator", "Check the seat read-back is connected",
             "Before anyone uses License Management, three things are set up outside the Workbench. "
             "First, the Worker app gets two read-only Microsoft Graph permissions, LicenseAssignment dot Read dot All and Reports dot Read dot All, "
             "and an Entra administrator grants admin consent. Second, user names are shown in Microsoft 365 usage reports, so unused seats can be matched to people. "
             "Third, the license group and the fourteen LOB tracking groups exist in AD, provisioned by OIM roles and synced to Entra. "
             "The platform operator then checks the Copilot seats read on Operations, Sync, and can run it on demand.",
             highlight="form[action*='handler=Seats']")
        ctx.close()

        # ---------------- 2. Admin configuration -------------------------------------------------
        S = "2. Admin configuration"
        ctx, pg = as_(b, "admin")
        pg.goto(f"{BASE}/Admin/SeatGroups", wait_until="networkidle")
        shot(pg, "seat-groups", S, "admin", "Map the license group and the LOB tracking groups",
             "The Workbench administrator opens Admin, Seat groups. Each group has a purpose. "
             "GSGu MCO Copilot M365 is the shared license group. Each GSGu MCO Tracking group is tied to one LOB and to its OIM role. "
             "The read-back shows whether each group was found in Entra, whether it is synced from AD, and how many members it has.",
             highlight="table")
        # Open an unconfirmed exclusion group to confirm its purpose.
        change = pg.locator("tr", has_text="GSGu_MCO_ExclusionLic").locator("a", has_text="Change").first
        if change.count():
            change.click(); pg.wait_for_load_state("networkidle")
        shot(pg, "confirm-purpose", S, "admin", "Confirm what the other MCO groups are for",
             "Groups whose purpose is not confirmed yet, such as the exclusion and Chat-only groups, raise no findings. "
             "Once their owner confirms what a group does, the administrator ticks Purpose confirmed. "
             "From then on, anyone holding a seat while in a confirmed exclusion group is flagged.",
             highlight="#group-form")
        pg.goto(f"{BASE}/Admin/SeatGroups", wait_until="networkidle")
        shot(pg, "skus-settings", S, "admin", "Check the Copilot SKUs and the seat settings",
             "Below the groups are the Copilot SKUs. The add-on is counted as a seat. A suite that already includes Copilot, such as E7, "
             "is marked so that anyone holding both is flagged as paid twice. The seat settings follow the reservation policy: "
             "the EA anniversary on the first of April, the forty-five day true-down window, the operational buffer, one seat rate for every LOB, "
             "and sixty days without Copilot use before a seat is proposed for reclaim.",
             top="h2:has-text('Copilot SKUs')")
        ctx.close()

        ctx, pg = as_(b, "finops")
        bsam = lob_id(pg, "BSAM")
        state["bsam"] = bsam
        pg.goto(f"{BASE}{bsam}", wait_until="networkidle")
        f = pg.locator("form[action*='handler=Baseline']").first
        if f.count():
            f.locator("[name=seatCount]").fill("15")
            f.locator("[name=notes]").fill("FY reservation schedule agreed with BSAM.")
            shot(pg, "baseline-form", S, "finops", "Record each LOB's first reservation",
                 "For an LOB that has no reservation yet, the FinOps lead records the first one, here fifteen seats for BSAM, from the agreed reservation schedule. "
                 "This is allowed once per LOB. Every later change goes through a reservation change request, so it has an approver and a record.",
                 highlight="form[action*='handler=Baseline']")
            f.locator("button[type=submit]").click(); pg.wait_for_load_state("networkidle")
        print("   baseline:", flash(pg))
        ctx.close()

        # ---------------- 3. Assignments ----------------------------------------------------------
        S = "3. Assignments"
        ctx, pg = as_(b, "bsamOwner")
        pg.goto(f"{BASE}{bsam}#admins", wait_until="networkidle")
        f = pg.locator("form[action*='handler=AddAdmin']").first
        opt = pg.locator("#people-list option", has_text="Farid Ferreira").first
        val = opt.get_attribute("value") if opt.count() else "seatlob.bsam.seatadmin@example.invalid"
        f.locator("[name=person]").fill(val)
        shot(pg, "first-admin", S, "bsamOwner", "The business owner names the LOB's first seat admin",
             "Assignments start with the LOB. The BSAM business owner, or the Platform Owner, names the first LOB seat admin. "
             "The role is scoped to one LOB. It can see that LOB's seats, ask for reservation changes and name other seat admins, but it approves nothing.",
             highlight="#admins")
        f.locator("button[type=submit]").click(); pg.wait_for_load_state("networkidle")
        print("   first admin:", flash(pg))
        ctx.close()

        ctx, pg = as_(b, "bsamAdmin")
        pg.goto(f"{BASE}{bsam}#admins", wait_until="networkidle")
        f = pg.locator("form[action*='handler=AddAdmin']").first
        opt = pg.locator("#people-list option", has_text="Umar Ueda").first
        val = opt.get_attribute("value") if opt.count() else "seat.demo.0939@example.invalid"
        f.locator("[name=person]").fill(val)
        f.locator("button[type=submit]").click(); pg.wait_for_load_state("networkidle")
        print("   second admin:", flash(pg))
        pg.goto(f"{BASE}{bsam}#admins", wait_until="networkidle")
        shot(pg, "more-admins", S, "bsamAdmin", "Seat admins name other seat admins for their own LOB",
             "The new seat admin can add more seat admins, but only for BSAM and only this role. Nobody can name themselves, "
             "and the last seat admin cannot remove themselves. Every assignment records who made it, and is audited.",
             highlight="#admins")
        pg.goto(f"{BASE}/Registry/Seats", wait_until="networkidle")
        shot(pg, "own-lob-only", S, "bsamAdmin", "Each seat admin sees only their own LOB",
             "On Registry, Copilot seats, a seat admin sees only the LOBs they administer. Row-level security in the database "
             "keeps the other thirteen LOBs out of view.",
             highlight="table")
        ctx.close()

        # ---------------- 4. Who does what --------------------------------------------------------
        S = "4. Who does what"
        ctx, pg = as_(b, "amgAdmin")
        amg = lob_id(pg, "Asset Management Group")
        pg.goto(f"{BASE}{amg}", wait_until="networkidle")
        shot(pg, "reserved-vs-actual", S, "amgAdmin", "Track the reservation against actual seats",
             "The AMG seat admin opens their LOB. Reserved is what AMG committed to. Assigned counts the people in the AMG tracking group "
             "who actually hold a Copilot seat from the shared license group. Over is billed on top, and Unused reserved is paid for but not used.",
             highlight="main .grid-4")
        shot(pg, "trend", S, "amgAdmin", "Follow the monthly trend and open findings",
             "Below, the monthly chart compares reserved and assigned seats from the chargeback snapshots. "
             "Open findings for the LOB are listed with them, for example people who are in two LOB tracking groups.",
             top="h2:has-text('Reserved against assigned')")
        pg.goto(f"{BASE}/Requests/New/J9?fundingOwner={amg.rsplit('/',1)[-1]}", wait_until="networkidle")
        pg.locator("[name='Input.Title']").fill("AMG Copilot seats from 80 to 95")
        pg.locator("[name='Input.RequestedSeats']").fill("95")
        pg.locator("[name='Input.SeatDemand']").fill("Fifteen portfolio analysts join the Copilot pilot from December.")
        shot(pg, "ask-increase", S, "amgAdmin", "Ask for more seats at any time",
             "To add seats, the seat admin starts a reservation change. The current reservation is filled in by the Workbench, not typed. "
             "They enter the new total and who needs the seats. Increases can be asked for at any time.",
             highlight="form")
        pg.locator("button", has_text="Submit").first.click(); pg.wait_for_load_state("networkidle")
        state["inc"] = pg.url.split("?")[0].replace(BASE, "")
        print("   increase:", state["inc"], flash(pg))
        ctx.close()

        ctx, pg = as_(b, "intake")
        pg.goto(f"{BASE}{state['inc']}", wait_until="networkidle")
        f = task_form(pg, "Complete", "Complete")
        if f.count() == 0:
            f = task_form(pg, "Complete")
        shot(pg, "intake", S, "intake", "Intake checks the request is complete",
             "The intake analyst checks the request: the LOB, the new total, and who needs the seats. "
             "If something is missing, they return it to the requester; otherwise they mark it complete.",
             highlight="form[action*='handler=Complete']")
        btn = f.locator("button[value=COMPLETE]").first
        if btn.count() == 0: btn = f.locator("button[name=outcome]").first
        btn.click(); pg.wait_for_load_state("networkidle")
        print("   intake:", flash(pg))
        ctx.close()

        ctx, pg = as_(b, "owner")
        pg.goto(f"{BASE}{state['inc']}", wait_until="networkidle")
        f = task_form(pg, "Complete")
        if f.count():
            notes = f.locator("[name=notes]")
            if notes.count(): notes.fill("Checked in the admin center: 200 seats unallocated after the 25-seat buffer; 15 more are available.")
            shot(pg, "dw-consult", S, "owner", "Digital Workplace confirms the seats are available",
                 "Before Finance can decide, Digital Workplace answers a blocking consult. They confirm unallocated tenant inventory, "
                 "approved pending demand and the operational buffer. The decision waits for this answer.",
                 highlight="form[action*='handler=Complete']")
            f.locator("button[name=outcome]").first.click(); pg.wait_for_load_state("networkidle")
        print("   consult:", flash(pg))
        ctx.close()

        ctx, pg = as_(b, "finops")
        pg.goto(f"{BASE}{state['inc']}", wait_until="networkidle")
        f = pg.locator("form[action*='handler=Decide']").first
        f.locator("select[name=outcome]").select_option("APPROVED")
        f.locator("[name=flexPoolReview]").fill("200 unallocated, 25 kept as buffer, no other pending demand. Flex pool not needed.")
        r = f.locator("[name=reason]")
        if r.count(): r.fill("Within available inventory; AMG funds the 15 seats from December.")
        shot(pg, "finops-decide", S, "finops", "Finance decides, with a flex-pool review",
             "The FinOps lead decides. The form shows unallocated seats, purchased less reserved, buffer and pending requests. "
             "A flex-pool review is required to approve. If the request is above what is unallocated, Finance can still approve it with conditions, "
             "and the Workbench notes that a purchase decision is needed.",
             highlight="form[action*='handler=Decide']")
        f.locator("button[type=submit]").first.click(); pg.wait_for_load_state("networkidle")
        print("   decide:", flash(pg))
        pg.goto(f"{BASE}{amg}", wait_until="networkidle")
        shot(pg, "history", S, "finops", "The new reservation is recorded",
             "The approved increase becomes AMG's reservation from its effective date. The history keeps the previous and new quantity, "
             "the approver, the flex-pool review and the request, as the policy requires.",
             top="h2:has-text('Reservation history')")
        ctx.close()

        # A decrease outside the window is held.
        ctx, pg = as_(b, "retAdmin")
        ret = lob_id(pg, "Retail")
        pg.goto(f"{BASE}/Requests/New/J9?fundingOwner={ret.rsplit('/',1)[-1]}", wait_until="networkidle")
        pg.locator("[name='Input.Title']").fill("Retail gives back 20 Copilot seats")
        pg.locator("[name='Input.RequestedSeats']").fill("160")
        pg.locator("[name='Input.SeatDemand']").fill("The branch pilot ended; 20 seats are no longer needed.")
        pg.locator("button", has_text="Submit").first.click(); pg.wait_for_load_state("networkidle")
        state["dec"] = pg.url.split("?")[0].replace(BASE, "")
        print("   decrease:", state["dec"], flash(pg))
        ctx.close()
        for who in ("intake", "owner"):
            ctx, pg = as_(b, who)
            pg.goto(f"{BASE}{state['dec']}", wait_until="networkidle")
            f = task_form(pg, "Complete")
            if f.count():
                n = f.locator("[name=notes]")
                if n.count(): n.fill("Checked.")
                btn = f.locator("button[value=COMPLETE]").first
                if btn.count() == 0: btn = f.locator("button[name=outcome]").first
                btn.click(); pg.wait_for_load_state("networkidle")
            print(f"   {who}:", flash(pg))
            ctx.close()
        ctx, pg = as_(b, "finops")
        pg.goto(f"{BASE}{state['dec']}", wait_until="networkidle")
        pg.locator("form[action*='handler=Decide'] select[name=outcome]").first.select_option("HOLD")
        rr = pg.locator("form[action*='handler=Decide'] [name=reason]").first
        if rr.count(): rr.fill("Outside the true-down window; held until 15 February.")
        shot(pg, "decrease-window", S, "finops", "Decreases wait for the April true-down window",
             "When an LOB asks to give seats back, the request shows the true-down window. Decreases can only be approved from the fifteenth of February, "
             "forty-five days before the April EA anniversary, and take effect on the first of April. "
             "Until then the reservation does not change, and the LOB keeps paying for it.",
             highlight="form[action*='handler=Decide']")
        f = pg.locator("form[action*='handler=Decide']").first
        f.locator("select[name=outcome]").select_option("HOLD")
        r = f.locator("[name=reason]")
        if r.count(): r.fill("Outside the true-down window; held until 15 February.")
        ev = f.locator("[name=evidence]")
        if ev.count(): ev.fill("Decide again when the true-down window opens on 15 February 2027.")
        f.locator("button[type=submit]").first.click(); pg.wait_for_load_state("networkidle")
        print("   hold:", flash(pg))
        shot(pg, "held", S, "finops", "The decrease is held, and Finance is reminded when the window opens",
             "Finance holds the request. Its decision task is due on the day the window opens, so the FinOps lead is reminded then, "
             "and Digital Workplace is consulted again because the seat picture may have changed.",
             top="main")
        ctx.close()

        # Reclaim unused seats.
        ctx, pg = as_(b, "amgOwner")
        pg.goto(f"{BASE}{amg}#reclaim", wait_until="networkidle")
        f = pg.locator("form[action*='handler=Reclaim']").first
        if f.count():
            rs = f.locator("[name=reason]")
            if rs.count(): rs.fill("No Copilot use since July; the analyst moved to a role without Copilot.")
            shot(pg, "reclaim", S, "amgOwner", "Decide on unused seats: keep or reclaim",
                 "Seats with no Copilot use for sixty days are proposed for reclaim. The AMG business owner answers each one. "
                 "Keep leaves the seat. Reclaim opens an ITSM task to remove the person's OIM role, Asset Management Group, M365 Copilot License, Role. "
                 "The proposal closes when the read-back shows the seat is gone.",
                 highlight="#reclaim")
            f.locator("button[value=true]").click(); pg.wait_for_load_state("networkidle")
        print("   reclaim:", flash(pg))
        ctx.close()

        ctx, pg = as_(b, "operator")
        pg.goto(f"{BASE}/Operations/Findings?Type=SEAT_NO_LOB", wait_until="networkidle")
        shot(pg, "findings", S, "operator", "Operations works the seat findings",
             "The platform operator follows up the seat findings: seats no LOB tracking group claims, people in two LOBs, "
             "direct assignments outside the license group, license errors such as a missing usage location, leavers still holding a seat, "
             "and LOBs over their reservation.",
             highlight="table")
        ctx.close()

        ctx, pg = as_(b, "finops")
        pg.goto(f"{BASE}/Reports/SeatChargeback", wait_until="networkidle")
        shot(pg, "chargeback", S, "finops", "Finance certifies the monthly chargeback",
             "Each month the worker takes a draft chargeback. Each LOB pays its full reservation plus any seats above it, at one rate for every LOB. "
             "The FinOps lead checks the draft and certifies the month. A certified month never changes; later changes apply from their effective date.",
             highlight="form[action*='handler=Certify']")
        ctx.close()

        ctx, pg = as_(b, "auditor")
        pg.goto(f"{BASE}/Reports/SeatChargeback", wait_until="networkidle")
        shot(pg, "auditor", S, "auditor", "Auditors read everything, change nothing",
             "Auditors can read the chargeback, the reservation history and the decisions, and export them. "
             "Every reservation decision is an append-only ledger row, so the record cannot be edited after the fact.",
             top="main")
        ctx.close()
        b.close()

    (OUT.parent / "scenes.json").write_text(json.dumps(scenes, indent=2), encoding="utf-8")
    print(f"{len(scenes)} scenes written")


if __name__ == "__main__":
    run()

