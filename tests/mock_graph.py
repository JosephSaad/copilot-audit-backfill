#!/usr/bin/env python3
"""
Mock of the Microsoft Graph Audit Search API, for testing the backfill engine.

Simulates the documented service behaviour:
  * async audit searches: POST query -> poll status -> page records
  * per-query record cap: records stop at CAP; beta status reports
    isRecordCountLimitExceeded / recordCountLimit / approximateReturnedRecordCount
    (v1.0 status does not)
  * rolling submission budget and a cap on non-terminal queries (429 + Retry-After)
  * page throttling: more than PAGE_CONCURRENCY concurrent record requests -> 429
  * bearer token validation, rotatable mid-run (forces 401 handling)
  * random 503s on pages, and one audit search that fails server-side

Usage: mock_graph.py <port-file> [json-config-overrides]
"""
import json
import math
import re
import random
import sys
import threading
import time
from datetime import datetime, timedelta, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, unquote, urlparse

CFG = {
    "range_start": "2026-08-01T00:00:00Z",
    "days": 30,
    "first_day_count": 2000,        # adoption ramps linearly...
    "last_day_count": 40000,        # ...to this many records per day
    "cap": 50000,
    "page_concurrency": 5,
    "page_retry_after": 1,
    "max_active_queries": 50,
    "submission_budget": 200,
    "polls_to_succeed": 2,
    "fail_first_query_covering": "2026-08-20T12:00:00Z",
    "page_latency_ms": 15,
    "page_503_rate": 0.01,
    "beta_supported": True,
    "default_page_size": 100,
    "max_page_size": 1000,
    "rotate_after_pages": 0,        # >0: invalidate the current token once, after this many pages
    "report_approx_count": True,    # False: beta status omits approximateReturnedRecordCount, as a live tenant did
}
if len(sys.argv) > 2:
    CFG.update(json.loads(sys.argv[2]))

UTC = timezone.utc
RANGE_START = datetime.fromisoformat(CFG["range_start"].replace("Z", "+00:00"))
DAYS = CFG["days"]


def day_count(i):
    if DAYS == 1:
        return CFG["last_day_count"]
    return int(round(CFG["first_day_count"] + (CFG["last_day_count"] - CFG["first_day_count"]) * i / (DAYS - 1)))


DAY_COUNTS = [day_count(i) for i in range(DAYS)]

lock = threading.Lock()
state = {
    "queries": {},
    "next_q": 0,
    "submits": [],
    "token_gen": 1,
    "failed_once": False,
    "pages_in_flight": 0,
    "max_pages_in_flight": 0,
    "pages_served": 0,
    "pages_429": 0,
    "pages_503": 0,
    "submits_429": 0,
    "unauthorized": 0,
    "status_calls": 0,
    "records_served": 0,
}


def current_token():
    return "tok-%d" % state["token_gen"]


def parse_ts(s):
    return datetime.fromisoformat(s.replace("Z", "+00:00"))


def records_in(start, end):
    """All (id, timestamp) in [start, end), deterministic."""
    out = []
    for i in range(DAYS):
        d0 = RANGE_START + timedelta(days=i)
        d1 = d0 + timedelta(days=1)
        if d1 <= start or d0 >= end:
            continue
        n = DAY_COUNTS[i]
        step = 86400.0 / n
        lo = max(0.0, (start - d0).total_seconds())
        hi = min(86400.0, (end - d0).total_seconds())
        j0 = max(0, math.ceil(lo / step - 0.5))
        j1 = min(n, math.ceil(hi / step - 0.5))
        for j in range(j0, j1):
            t = d0 + timedelta(seconds=(j + 0.5) * step)
            if start <= t < end:
                out.append(("%s-%06d" % (d0.strftime("%Y%m%d"), j), t))
    return out


def count_in(start, end):
    return len(records_in(start, end))


def seq(rid):
    """Record number within its day. Deterministic, unlike hash() on a str,
    whose seed is randomized per process - the flatten assertions count on it."""
    return int(rid.split("-")[1])


def record_json(rid, t):
    ts = t.strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z"
    n = seq(rid)
    # Every Nth record carries one of the security signals, so the flattening
    # procedure's paths are all exercised at a known, checkable rate.
    jailbreak = (n % 13 == 0)
    xpia = (n % 7 == 0)
    blocked = (n % 11 == 0)
    web = (n % 3 == 0)
    resource = {
        "Action": "Read", "Id": "AAA%06d" % n, "Name": 'Q3, "final".xlsx', "Type": "xlsx",
        "SensitivityLabelId": "11111111-2222-3333-4444-%012d" % (n % 5),
        "SiteUrl": "https://contoso.sharepoint.com/sites/finance/Shared%%20Documents/Q3-%06d.xlsx" % n,
        "listItemUniqueId": "AAA%06d" % n,
    }
    if xpia:
        resource["XPIADetected"] = True
    if blocked:
        resource["PolicyDetails"] = [{"PolicyId": "pol-1", "PolicyName": "Restricted site"}]
        resource["Status"] = "Failure"
    resources = [resource]
    # Every 19th record reads a second resource that a Purview policy blocked:
    # PolicyDetails as JSON text inside the JSON, the shape live records use.
    if n % 19 == 0:
        resources.append({
            "Action": "Read", "Type": "EmailMessage", "Name": "Payroll", "XPIADetected": False,
            "SiteUrl": "https://outlook.office365.com/owa/?ItemID=%06d" % n,
            "PolicyDetails": json.dumps([{"PolicyType": "Purview", "PolicyOutcomes": ["Blocked"], "AuditLog": ""}]),
        })
    audit = {
        "CreationTime": ts, "Id": rid, "Operation": "CopilotInteraction", "RecordType": 261,
        "Workload": "Copilot", "UserId": "user%d@contoso.com" % (n % 50),
        "AppIdentity": "Copilot.MicrosoftCopilot.BizChat", "ClientRegion": "CA",
        "OrganizationId": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "UserKey": "00000000-0000-0000-0000-%012d" % (n % 50),
        "UserType": 0, "Version": 1, "ClientIP": "203.0.113.7", "CopilotLogVersion": "1.0.0.0",
        "CopilotEventData": {
            "AppHost": ["Word", "Teams", "BizChat", "Outlook"][n % 4],
            "ThreadId": "19:%s@thread.v2" % rid,
            "ConversationId": "conv-%06d" % n,
            "LicenseType": "Premium" if n % 2 else "Starter",
            "MemoryUpdated": n % 5 == 0,
            "DLPEvaluationDeferred": 1 if n % 17 == 0 else 0,
            **({"SensitivityLabelId": "11111111-2222-3333-4444-000000000009"} if n % 4 == 0 else {}),
            "Messages": [
                dict({"Id": "1", "isPrompt": True}, **({"JailbreakDetected": True} if jailbreak else {})),
                {"Id": "2", "isPrompt": False},
            ],
            "AccessedResources": resources,
            "AISystemPlugin": [{"Id": "BingWebSearch", "Name": "BuiltIn"}] if web else [],
            "Contexts": [{"Id": "https://contoso.sharepoint.com/sites/finance/Q3-%06d.xlsx" % n, "Type": "xlsx"}],
            "ModelTransparencyDetails": [{"ModelName": "gpt-5", "ModelProviderName": "OpenAI", "ModelVersion": "2025-08-07"}],
        },
    }
    return {
        "@odata.type": "#microsoft.graph.security.auditLogRecord",
        "id": rid, "createdDateTime": ts, "auditLogRecordType": "copilotInteraction",
        "operation": "CopilotInteraction", "organizationId": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
        "userType": "regular", "userId": audit["UserId"], "userPrincipalName": audit["UserId"],
        "service": "Copilot", "objectId": "thread,%s" % rid, "clientIp": "203.0.113.7",
        "administrativeUnits": [ADMIN_UNIT_ID] if seq(rid) % 3 == 0 else [],
        "auditData": audit,
    }


ADMIN_UNIT_ID = "00000000-0000-0000-0000-0000000000ae"
COPILOT_SKU_ID = "639dec6b-bb19-468b-871c-c5c441c4b0cb"   # Microsoft_365_Copilot
DIRECTORY_USERS = 50          # user0..user49 appear in the audit data
DIRECTORY_MISSING = {49}      # ...but this one has left: Graph 404s it
DEPARTMENTS = ["Retail Banking", "Risk", "Technology", "Operations"]


def directory_user(n):
    """The directory record for userN@contoso.com, or None if they have left."""
    if n in DIRECTORY_MISSING:
        return None
    licensed = (n % 3 == 0)
    u = {
        "userPrincipalName": "user%d@contoso.com" % n,
        "displayName": "User %d" % n,
        "department": DEPARTMENTS[n % 4],
            "employeeOrgData": {"division": "Retail" if n % 4 == 0 else "Corporate", "costCenter": None},
        "jobTitle": "Analyst" if n % 2 == 0 else "Manager",
        "officeLocation": "Floor %d" % (n % 5),
        "city": "Toronto",
        "country": "Canada",
        "companyName": "Contoso",
        "usageLocation": "CA",
        "userType": "Guest" if n % 25 == 0 else "Member",
        "accountEnabled": (n % 17 != 0),
        "createdDateTime": "2024-01-%02dT00:00:00Z" % ((n % 28) + 1),
        "employeeHireDate": ("2026-%02d-15T00:00:00Z" % ((n % 9) + 1)) if n % 2 == 0 else None,
        "assignedLicenses": ([{"skuId": COPILOT_SKU_ID, "disabledPlans": []}] if licensed else []),
        "assignedPlans": ([{
            "service": "Microsoft365Copilot",
            "servicePlanId": COPILOT_SKU_ID,
            "capabilityStatus": "Enabled",
            "assignedDateTime": "2026-0%d-01T00:00:00Z" % ((n % 6) + 1),
        }] if licensed else []),
    }
    return u


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *a):
        pass

    # ---- plumbing ----
    def send_json(self, code, obj, headers=None):
        body = json.dumps(obj).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        for k, v in (headers or {}).items():
            self.send_header(k, v)
        self.end_headers()
        try:
            self.wfile.write(body)
        except (BrokenPipeError, ConnectionResetError):
            pass   # client cancelled mid-response (the engine's stop/crash tests do this on purpose)

    def authorized(self):
        auth = self.headers.get("Authorization", "")
        with lock:
            ok = auth == "Bearer " + current_token()
            if not ok:
                state["unauthorized"] += 1
        if not ok:
            self.send_json(401, {"error": {"code": "InvalidAuthenticationToken", "message": "expired"}})
        return ok

    def base(self):
        return "http://127.0.0.1:%d" % self.server.server_address[1]

    # ---- routes ----
    def do_POST(self):
        path = urlparse(self.path).path
        length = int(self.headers.get("Content-Length", "0"))
        raw = self.rfile.read(length) if length else b""

        if path == "/_control/rotate":
            with lock:
                state["token_gen"] += 1
                tok = current_token()
            return self.send_json(200, {"token": tok})

        if path == "/v1.0/security/auditLog/queries":
            if not self.authorized():
                return
            body = json.loads(raw)
            now = time.time()
            with lock:
                state["submits"] = [t for t in state["submits"] if now - t < 86400]
                active = sum(1 for q in state["queries"].values() if q["status"] not in ("succeeded", "failed"))
                if len(state["submits"]) >= CFG["submission_budget"] or active >= CFG["max_active_queries"]:
                    state["submits_429"] += 1
                    return self.send_json(429, {"error": {"code": "TooManyRequests"}}, {"Retry-After": "2"})
                state["submits"].append(now)
                state["next_q"] += 1
                qid = "q%05d" % state["next_q"]
                s = parse_ts(body["filterStartDateTime"])
                e = parse_ts(body["filterEndDateTime"])
                assert "CopilotInteraction" in body["operationFilters"], body
                fail_at = parse_ts(CFG["fail_first_query_covering"])
                fail = (not state["failed_once"]) and s <= fail_at < e
                if fail:
                    state["failed_once"] = True
                state["queries"][qid] = {"start": s, "end": e, "polls": 0, "fail": fail,
                                         "status": "notStarted", "records": None}
            return self.send_json(201, {"id": qid, "status": "notStarted"})

        self.send_json(404, {"error": {"code": "NotFound", "message": path}})

    def directory_get(self, path, qs):
        """Directory endpoints used by tools/Update-CopilotAuditEnrichment.ps1.
        Returns True when it handled the request."""
        if path == "/v1.0/subscribedSkus":
            self.send_json(200, {"value": [
                {"skuId": COPILOT_SKU_ID, "skuPartNumber": "Microsoft_365_Copilot", "capabilityStatus": "Enabled",
                 "consumedUnits": 17, "prepaidUnits": {"enabled": 100, "warning": 5, "suspended": 0, "lockedOut": 0}},
                {"skuId": "00000000-0000-0000-0000-0000000e3105", "skuPartNumber": "ENTERPRISEPACK",
                 "consumedUnits": 50, "prepaidUnits": {"enabled": 100}},
            ]})
            return True

        if path == "/v1.0/directory/administrativeUnits":
            self.send_json(200, {"value": [
                {"id": ADMIN_UNIT_ID, "displayName": "AU-East",
                 "description": "Eastern region"},
            ]})
            return True

        # /v1.0/users?$filter=assignedLicenses/any(...)  - license holders
        if path == "/v1.0/users":
            self.send_json(200, {"value": [
                {"userPrincipalName": "user%d@contoso.com" % n}
                for n in range(DIRECTORY_USERS) if n % 3 == 0 and n not in DIRECTORY_MISSING
            ]})
            return True

        m = re.match(r"^/v1\.0/users/([^/]+)(/manager)?$", path)
        if m:
            upn = unquote(m.group(1))
            want_manager = bool(m.group(2))
            mm = re.match(r"^user(\d+)@contoso\.com$", upn)
            if not mm:
                self.send_json(404, {"error": {"code": "Request_ResourceNotFound",
                                               "message": "Resource '%s' does not exist." % upn}})
                return True
            n = int(mm.group(1))
            if want_manager:
                # user0 has no manager; everyone else reports to user0.
                if n == 0:
                    self.send_json(404, {"error": {"code": "Request_ResourceNotFound",
                                                   "message": "manager not found"}})
                else:
                    self.send_json(200, {"userPrincipalName": "user0@contoso.com"})
                return True
            u = directory_user(n)
            if u is None:
                self.send_json(404, {"error": {"code": "Request_ResourceNotFound",
                                               "message": "Resource '%s' does not exist." % upn}})
                return True
            self.send_json(200, u)
            return True

        return False

    def do_GET(self):
        u = urlparse(self.path)
        path = u.path
        qs = parse_qs(u.query)

        if path.startswith("/v1.0/users") or path.startswith("/v1.0/subscribedSkus") \
           or path.startswith("/v1.0/directory/"):
            if self.directory_get(path, qs):
                return

        if path == "/_control/token":
            with lock:
                return self.send_json(200, {"token": current_token()})
        if path == "/_control/stats":
            with lock:
                s = {k: v for k, v in state.items() if k not in ("queries", "submits")}
                s["queries_created"] = len(state["queries"])
            return self.send_json(200, s)
        if path == "/_control/daycounts":
            return self.send_json(200, {"range_start": CFG["range_start"], "counts": DAY_COUNTS})

        parts = path.strip("/").split("/")
        # /{ver}/security/auditLog/queries/{id}[/records]
        if len(parts) >= 5 and parts[1:4] == ["security", "auditLog", "queries"]:
            ver, qid = parts[0], parts[4]
            if not self.authorized():
                return
            with lock:
                q = state["queries"].get(qid)
            if q is None:
                return self.send_json(404, {"error": {"code": "NotFound"}})

            if len(parts) == 5:
                return self.status(ver, qid, q)
            if len(parts) == 6 and parts[5] == "records" and ver == "v1.0":
                return self.records(qid, q, qs)

        self.send_json(404, {"error": {"code": "NotFound", "message": path}})

    def status(self, ver, qid, q):
        if ver == "beta" and not CFG["beta_supported"]:
            return self.send_json(400, {"error": {"code": "BadRequest", "message": "Resource not found for the segment"}})
        with lock:
            state["status_calls"] += 1
            q["polls"] += 1
            if q["fail"]:
                q["status"] = "failed"
            elif q["polls"] >= CFG["polls_to_succeed"]:
                q["status"] = "succeeded"
            else:
                q["status"] = "running"
            status = q["status"]
        obj = {"id": qid, "status": status,
               "filterStartDateTime": q["start"].strftime("%Y-%m-%dT%H:%M:%SZ"),
               "filterEndDateTime": q["end"].strftime("%Y-%m-%dT%H:%M:%SZ")}
        if ver == "beta" and status == "succeeded":
            n = count_in(q["start"], q["end"])
            obj["recordCountLimit"] = CFG["cap"]
            obj["isRecordCountLimitExceeded"] = n > CFG["cap"]
            if CFG["report_approx_count"]:
                obj["approximateReturnedRecordCount"] = n
        return self.send_json(200, obj)

    def records(self, qid, q, qs):
        with lock:
            state["pages_in_flight"] += 1
            inflight = state["pages_in_flight"]
            state["max_pages_in_flight"] = max(state["max_pages_in_flight"], inflight)
            if inflight > CFG["page_concurrency"]:
                state["pages_in_flight"] -= 1
                state["pages_429"] += 1
                return self.send_json(429, {"error": {"code": "TooManyRequests"}},
                                      {"Retry-After": str(CFG["page_retry_after"])})
        try:
            if q["status"] != "succeeded":
                return self.send_json(400, {"error": {"code": "BadRequest", "message": "query not complete"}})
            if random.random() < CFG["page_503_rate"]:
                with lock:
                    state["pages_503"] += 1
                return self.send_json(503, {"error": {"code": "ServiceUnavailable"}})

            top = int(qs.get("$top", [CFG["default_page_size"]])[0])
            if top > CFG["max_page_size"]:
                return self.send_json(400, {"error": {"code": "BadRequest", "message": "$top too large"}})
            skip = int(qs.get("$skiptoken", ["0"])[0])

            with lock:
                if q["records"] is None:
                    q["records"] = records_in(q["start"], q["end"])[: CFG["cap"]]
                recs = q["records"]

            time.sleep(CFG["page_latency_ms"] / 1000.0)
            chunk = recs[skip: skip + top]
            obj = {"value": [record_json(rid, t) for rid, t in chunk]}
            if skip + top < len(recs):
                obj["@odata.nextLink"] = "%s/v1.0/security/auditLog/queries/%s/records?$top=%d&$skiptoken=%d" % (
                    self.base(), qid, top, skip + top)
            with lock:
                state["pages_served"] += 1
                state["records_served"] += len(chunk)
                if CFG["rotate_after_pages"] and state["pages_served"] == CFG["rotate_after_pages"]:
                    state["token_gen"] += 1
            return self.send_json(200, obj)
        finally:
            with lock:
                state["pages_in_flight"] -= 1


def main():
    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    server.daemon_threads = True
    with open(sys.argv[1], "w") as f:
        f.write(str(server.server_address[1]))
    server.serve_forever()


if __name__ == "__main__":
    main()
