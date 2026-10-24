using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

// Mock ITSM for the local test environment: a ServiceNow-style table API (sc_task, incident) and a page where a person
// plays Platform Operations (start work, resolve, close with "closed by" and notes). Basic authentication with a
// password generated into config\appsettings.Local.json (git-ignored) on first start, so the worker reads the same one.
var builder = WebApplication.CreateBuilder(args);
var localSettings = builder.Configuration["Mock:LocalSettingsPath"] ?? FindUpward("config", "appsettings.Local.json");
if (localSettings is not null) builder.Configuration.AddJsonFile(localSettings, optional: true, reloadOnChange: false);
builder.WebHost.UseUrls(builder.Configuration["Mock:Urls"] ?? "http://localhost:5470");

var username = builder.Configuration["Itsm:Username"];
var password = builder.Configuration["Itsm:Password"];
if (string.IsNullOrEmpty(password))
{
    username = "mock-itsm";
    password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', 'x').Replace('/', 'y');
    if (localSettings is not null) SaveCredentials(localSettings, username, password);
    Console.WriteLine(localSettings is null
        ? "No config\\appsettings.Local.json found: the generated password is in memory only; set Itsm:Username and Itsm:Password."
        : $"Generated mock ITSM credentials into {localSettings} (Itsm:Username, Itsm:Password).");
}

var store = new TicketStore(builder.Configuration["Mock:StorePath"] ?? Path.Combine(builder.Environment.ContentRootPath, ".run", "mock-itsm.json"));
var app = builder.Build();

app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path == "/health") { await next(); return; }
    var header = ctx.Request.Headers.Authorization.ToString();
    var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
    if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(header), Encoding.UTF8.GetBytes(expected)))
    {
        ctx.Response.StatusCode = 401;
        ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"Mock ITSM\"";
        return;
    }
    await next();
});

app.MapGet("/health", () => "ok");

app.MapGet("/api/now/table/{table}", (string table, string? sysparm_query, int? sysparm_limit) =>
{
    if (!TicketStore.Tables.Contains(table)) return Results.NotFound(new { error = new { message = "Unknown table" } });
    var rows = store.All().Where(t => t.Table == table);
    foreach (var clause in (sysparm_query ?? "").Split('^', StringSplitOptions.RemoveEmptyEntries))
    {
        var parts = clause.Split('=', 2);
        if (parts.Length != 2) continue;
        rows = parts[0] switch
        {
            "correlation_id" => rows.Where(t => string.Equals(t.CorrelationId, parts[1], StringComparison.OrdinalIgnoreCase)),
            "number" => rows.Where(t => string.Equals(t.Number, parts[1], StringComparison.OrdinalIgnoreCase)),
            _ => rows,
        };
    }
    return Results.Json(new { result = rows.Take(sysparm_limit ?? 100).Select(t => t.ToApi()) });
});

app.MapPost("/api/now/table/{table}", async (string table, HttpRequest request) =>
{
    if (!TicketStore.Tables.Contains(table)) return Results.NotFound(new { error = new { message = "Unknown table" } });
    var body = await JsonNode.ParseAsync(request.Body) as JsonObject ?? new JsonObject();
    string? S(string k) => body[k]?.GetValue<string>();
    var ticket = store.Create(table, S("short_description") ?? "(no description)", S("description") ?? "", S("correlation_id"),
        S("assignment_group") ?? "Platform Operations", S("urgency") ?? "3", S("u_workbench_payload"));
    return Results.Json(new { result = ticket.ToApi() }, statusCode: 201);
});

app.MapGet("/", () => Results.Content(Page(store.All()), "text/html; charset=utf-8"));

app.MapPost("/ui/tickets/{number}/state", async (string number, HttpRequest request) =>
{
    var form = await request.ReadFormAsync();
    store.SetState(number, form["state"].ToString(), form["closed_by"].ToString(), form["close_notes"].ToString());
    return Results.Redirect("/");
});

app.Run();

static string Page(IReadOnlyList<Ticket> tickets)
{
    var h = HtmlEncoder.Default;
    var sb = new StringBuilder();
    sb.Append("""
        <!DOCTYPE html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <title>Mock ITSM</title>
        <style>
        :root{--bg:#f6f7fb;--card:#fff;--ink:#1b2333;--muted:#566079;--line:#dde2ec;--accent:#2563eb}
        @media (prefers-color-scheme:dark){:root{--bg:#10141c;--card:#171d29;--ink:#e7ebf3;--muted:#9aa6bd;--line:#2a3344;--accent:#6ea0ff}}
        body{margin:0;background:var(--bg);color:var(--ink);font:15px/1.5 Segoe UI,system-ui,sans-serif}
        main{max-width:1000px;margin:0 auto;padding:24px 16px}
        h1{font-size:22px;margin:0 0 4px} p.lead{color:var(--muted);margin:0 0 20px}
        .t{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:16px;margin-bottom:12px}
        .row{display:flex;flex-wrap:wrap;gap:8px 16px;align-items:baseline}
        .num{font-weight:600} .pill{border:1px solid var(--line);border-radius:999px;padding:1px 10px;font-size:13px}
        details{margin-top:8px} pre{white-space:pre-wrap;word-break:break-word;font-size:13px;background:var(--bg);padding:8px;border-radius:6px}
        form{display:flex;flex-wrap:wrap;gap:8px;margin-top:10px;align-items:end}
        label{display:flex;flex-direction:column;font-size:13px;color:var(--muted)}
        input,select{font:inherit;padding:6px 8px;border:1px solid var(--line);border-radius:6px;background:var(--card);color:var(--ink);min-width:180px}
        button{font:inherit;padding:7px 14px;border-radius:6px;border:0;background:var(--accent);color:#fff;cursor:pointer}
        </style></head><body><main>
        <h1>Mock ITSM</h1>
        <p class="lead">Local stand-in for the institution's ITSM. Play Platform Operations: configure in the admin center, then close the task with your sign-in name. A closed task sends the request to verification in the Workbench (within 5 minutes).</p>
        """);
    if (tickets.Count == 0) sb.Append("<p class=\"lead\">No tickets yet.</p>");
    foreach (var t in tickets.OrderByDescending(t => t.CreatedUtc))
    {
        sb.Append("<section class=\"t\"><div class=\"row\">")
          .Append($"<span class=\"num\">{h.Encode(t.Number)}</span><span class=\"pill\">{h.Encode(Ticket.Label(t.State))}</span>")
          .Append($"<span>{h.Encode(t.ShortDescription)}</span><span style=\"color:var(--muted)\">{h.Encode(t.AssignmentGroup)} · {t.CreatedUtc:yyyy-MM-dd HH:mm} UTC</span></div>");
        if (!string.IsNullOrEmpty(t.ClosedBy)) sb.Append($"<div style=\"color:var(--muted)\">Closed by {h.Encode(t.ClosedBy)}: {h.Encode(t.CloseNotes ?? "")}</div>");
        sb.Append($"<details><summary>Settings to apply</summary><pre>{h.Encode(t.Description)}</pre></details>");
        if (t.State is not ("3" or "4" or "7"))
            sb.Append($"<form method=\"post\" action=\"/ui/tickets/{h.Encode(t.Number)}/state\">")
              .Append("<label>New state<select name=\"state\"><option value=\"2\">In progress</option><option value=\"6\">Resolved</option><option value=\"3\" selected>Closed complete</option><option value=\"4\">Closed incomplete (cancel)</option></select></label>")
              .Append("<label>Closed by (UPN)<input name=\"closed_by\" placeholder=\"operator@contoso.com\"></label>")
              .Append("<label>Closure notes<input name=\"close_notes\" placeholder=\"What was configured\"></label>")
              .Append("<button type=\"submit\">Update</button></form>");
        sb.Append("</section>");
    }
    sb.Append("</main></body></html>");
    return sb.ToString();
}

static string? FindUpward(string folder, string file)
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
    {
        var candidate = Path.Combine(dir.FullName, folder, file);
        if (File.Exists(candidate)) return candidate;
    }
    return null;
}

static void SaveCredentials(string path, string user, string pw)
{
    var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
    var itsm = root["Itsm"] as JsonObject ?? new JsonObject();
    itsm["Username"] = user;
    itsm["Password"] = pw;
    root["Itsm"] = itsm;
    File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
}

sealed class Ticket
{
    public string Table { get; set; } = "";
    public string SysId { get; set; } = "";
    public string Number { get; set; } = "";
    public string State { get; set; } = "1";
    public string ShortDescription { get; set; } = "";
    public string Description { get; set; } = "";
    public string? CorrelationId { get; set; }
    public string AssignmentGroup { get; set; } = "";
    public string Urgency { get; set; } = "3";
    public string? Payload { get; set; }
    public string? ClosedBy { get; set; }
    public string? CloseNotes { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? ClosedUtc { get; set; }

    public static string Label(string s) => s switch { "1" => "New", "2" => "In progress", "6" => "Resolved", "3" => "Closed complete", "4" => "Closed incomplete", "7" => "Closed skipped", _ => s };

    public object ToApi() => new Dictionary<string, string?>
    {
        ["sys_id"] = SysId, ["number"] = Number, ["state"] = State, ["short_description"] = ShortDescription, ["description"] = Description,
        ["correlation_id"] = CorrelationId, ["assignment_group"] = AssignmentGroup, ["urgency"] = Urgency, ["closed_by"] = ClosedBy,
        ["close_notes"] = CloseNotes, ["closed_at"] = ClosedUtc?.ToString("yyyy-MM-dd HH:mm:ss"), ["sys_created_on"] = CreatedUtc.ToString("yyyy-MM-dd HH:mm:ss"),
    };
}

sealed class TicketStore
{
    public static readonly HashSet<string> Tables = new() { "sc_task", "incident" };
    private readonly string _path;
    private readonly object _gate = new();
    private readonly List<Ticket> _tickets;

    public TicketStore(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _tickets = File.Exists(path) ? JsonSerializer.Deserialize<List<Ticket>>(File.ReadAllText(path)) ?? new() : new();
    }

    public IReadOnlyList<Ticket> All() { lock (_gate) return _tickets.ToList(); }

    public Ticket Create(string table, string shortDescription, string description, string? correlationId, string group, string urgency, string? payload)
    {
        lock (_gate)
        {
            var prefix = table == "incident" ? "INC" : "SCTASK";
            var next = _tickets.Count(t => t.Table == table) + 10001;
            var t = new Ticket
            {
                Table = table, SysId = Guid.NewGuid().ToString("n"), Number = $"{prefix}{next:0000000}", ShortDescription = shortDescription, Description = description,
                CorrelationId = correlationId, AssignmentGroup = group, Urgency = urgency, Payload = payload, CreatedUtc = DateTime.UtcNow,
            };
            _tickets.Add(t);
            Save();
            return t;
        }
    }

    public void SetState(string number, string state, string closedBy, string notes)
    {
        lock (_gate)
        {
            var t = _tickets.FirstOrDefault(x => x.Number == number) ?? throw new InvalidOperationException("Unknown ticket.");
            t.State = state;
            if (state is "3" or "4" or "7")
            {
                t.ClosedBy = string.IsNullOrWhiteSpace(closedBy) ? null : closedBy.Trim();
                t.CloseNotes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
                t.ClosedUtc = DateTime.UtcNow;
            }
            Save();
        }
    }

    private void Save() => File.WriteAllText(_path, JsonSerializer.Serialize(_tickets, new JsonSerializerOptions { WriteIndented = true }));
}
