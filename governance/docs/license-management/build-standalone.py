"""Builds License-Management-walkthrough.html: one self-contained file (screenshots embedded) that speaks the
narration with the browser's own voices, so the viewer picks the voice and the speed. Run after capture.py."""
import base64, json, pathlib

here = pathlib.Path(__file__).parent
scenes = json.loads((here / "scenes.json").read_text(encoding="utf-8-sig"))
for s in scenes:
    s["img"] = "data:image/png;base64," + base64.b64encode((here / "shots" / s["file"]).read_bytes()).decode()
    s.pop("audio", None)
data = json.dumps(scenes, ensure_ascii=False).replace("</", "<\\/")

page = r"""<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>License Management walkthrough</title>
<style>
:root {
  --bg: #f4f6fb; --panel: #ffffff; --ink: #0f172a; --muted: #5b6475; --line: #e3e7ef;
  --accent: #2563eb; --accent-ink: #ffffff; --accent-soft: #e8efff; --sub-bg: rgba(15,23,42,.88); --sub-ink: #ffffff;
  --s1: #0e7490; --s2: #7c3aed; --s3: #b45309; --s4: #15803d;
}
@media (prefers-color-scheme: dark) {
  :root:not([data-theme="light"]) {
    --bg: #0b1020; --panel: #121a2e; --ink: #e8ecf5; --muted: #9aa4b8; --line: #243049;
    --accent: #6ea0ff; --accent-ink: #0b1020; --accent-soft: #1b2a4a; --sub-bg: rgba(0,0,0,.84);
  }
}
* { box-sizing: border-box; }
body { margin: 0; background: var(--bg); color: var(--ink); font: 15px/1.5 "Segoe UI", system-ui, sans-serif; }
header { display: flex; align-items: center; gap: 16px; padding: 14px 24px; border-bottom: 1px solid var(--line); background: var(--panel); }
header h1 { font-size: 18px; margin: 0; font-weight: 650; }
header .sub { color: var(--muted); font-size: 13px; }
header .spacer { flex: 1; }
.layout { display: grid; grid-template-columns: 320px 1fr; min-height: calc(100vh - 62px); }
nav { border-right: 1px solid var(--line); background: var(--panel); overflow: auto; max-height: calc(100vh - 62px); padding: 8px 0 24px; position: sticky; top: 0; }
nav h2 { font-size: 11px; letter-spacing: .08em; text-transform: uppercase; color: var(--muted); margin: 18px 18px 6px; }
nav button { all: unset; display: grid; grid-template-columns: 26px 1fr; gap: 8px; width: calc(100% - 16px); margin: 0 8px; padding: 8px 10px;
  border-radius: 8px; cursor: pointer; box-sizing: border-box; }
nav button:hover { background: var(--accent-soft); }
nav button:focus-visible { outline: 2px solid var(--accent); }
nav button[aria-current="true"] { background: var(--accent-soft); outline: 2px solid var(--accent); }
nav .num { font-weight: 650; color: var(--muted); font-variant-numeric: tabular-nums; }
nav .t { font-size: 13.5px; }
nav .who { display: block; font-size: 12px; color: var(--muted); }
main { padding: 20px 28px 28px; display: flex; flex-direction: column; gap: 14px; min-width: 0; }
.meta { display: flex; flex-wrap: wrap; align-items: center; gap: 10px; }
.chip { display: inline-flex; align-items: center; padding: 3px 10px; border-radius: 999px; font-size: 12.5px; font-weight: 600; color: #fff; }
.role { background: var(--panel); color: var(--ink); border: 1px solid var(--line); font-weight: 500; }
.meta h2 { margin: 0; font-size: 20px; font-weight: 650; flex-basis: 100%; }
.stage { position: relative; border-radius: 12px; overflow: hidden; border: 1px solid var(--line); background: #000; box-shadow: 0 8px 30px rgba(15,23,42,.12); }
.stage img { display: block; width: 100%; height: auto; }
.bigplay { position: absolute; inset: 0; margin: auto; width: 96px; height: 96px; border-radius: 50%; border: 0; cursor: pointer;
  background: rgba(37,99,235,.92); color: #fff; box-shadow: 0 10px 40px rgba(0,0,0,.35); display: grid; place-items: center; transition: transform .15s; }
.bigplay:hover { transform: scale(1.06); }
.bigplay svg { width: 40px; height: 40px; margin-left: 6px; }
.bigplay[hidden] { display: none; }
.subtitle { position: absolute; left: 50%; bottom: 22px; transform: translateX(-50%); width: min(88%, 980px); text-align: center;
  background: var(--sub-bg); color: var(--sub-ink); padding: 10px 18px; border-radius: 10px; font-size: 19px; line-height: 1.4; }
.subtitle:empty { display: none; }
.controls { display: flex; flex-wrap: wrap; align-items: center; gap: 10px; background: var(--panel); border: 1px solid var(--line); border-radius: 12px; padding: 10px 12px; }
.controls button, .controls select { font: inherit; padding: 7px 12px; border-radius: 8px; border: 1px solid var(--line); background: var(--panel); color: var(--ink); cursor: pointer; }
.controls button:focus-visible, .controls select:focus-visible { outline: 2px solid var(--accent); outline-offset: 1px; }
.controls button.primary { background: var(--accent); border-color: var(--accent); color: var(--accent-ink); min-width: 104px; font-weight: 600; }
.controls label { display: inline-flex; align-items: center; gap: 6px; color: var(--muted); font-size: 13px; }
.controls select#voice { max-width: 300px; }
.bar { flex: 1; min-width: 140px; height: 6px; background: var(--line); border-radius: 99px; overflow: hidden; }
.bar > div { height: 100%; width: 0; background: var(--accent); transition: width .3s linear; }
.counter { color: var(--muted); font-variant-numeric: tabular-nums; font-size: 13px; }
.row { display: flex; flex-wrap: wrap; align-items: center; gap: 10px 18px; width: 100%; }
.note { color: var(--muted); font-size: 12.5px; }
.note[hidden] { display: none; }
details { background: var(--panel); border: 1px solid var(--line); border-radius: 10px; padding: 10px 14px; }
details summary { cursor: pointer; color: var(--muted); font-size: 13px; }
details p { margin: 8px 0 0; }
.keys { color: var(--muted); font-size: 12px; }
@media (max-width: 900px) {
  header { padding: 12px 16px; }
  .layout { grid-template-columns: 1fr; }
  nav { max-height: 240px; position: static; border-right: 0; border-bottom: 1px solid var(--line); }
  main { padding: 14px 16px; }
  .subtitle { position: static; transform: none; width: auto; border-radius: 0; font-size: 16px; }
  .controls select#voice { max-width: 100%; }
}
</style>
</head>
<body>
<header>
  <div><h1>License Management walkthrough</h1><div class="sub">Copilot Governance Workbench · Copilot seat licenses reserved per LOB · demo data</div></div>
  <div class="spacer"></div>
  <span class="counter" id="total"></span>
</header>
<div class="layout">
  <nav id="nav" aria-label="Steps"></nav>
  <main>
    <div class="meta"><span class="chip" id="section"></span><span class="chip role" id="role"></span><h2 id="title"></h2></div>
    <div class="stage">
      <img id="shot" alt="">
      <button class="bigplay" id="bigplay" aria-label="Play the walkthrough"><svg viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><path d="M7 4.5v15l13-7.5z"/></svg></button>
      <div class="subtitle" id="subtitle" aria-live="polite"></div>
    </div>
    <div class="controls">
      <div class="row">
        <button id="prev" title="Previous step (Left arrow)">◀ Previous</button>
        <button id="play" class="primary" title="Play or pause (Space)">▶ Play</button>
        <button id="next" title="Next step (Right arrow)">Next ▶</button>
        <div class="bar" aria-hidden="true"><div id="progress"></div></div>
        <span class="counter" id="counter"></span>
      </div>
      <div class="row">
        <label>Voice <select id="voice" aria-label="Narration voice"></select></label>
        <label>Speed <select id="speed" aria-label="Narration speed">
          <option value="0.75">0.75×</option><option value="0.9">0.9×</option><option value="1" selected>1×</option>
          <option value="1.15">1.15×</option><option value="1.3">1.3×</option><option value="1.5">1.5×</option><option value="1.75">1.75×</option></select></label>
        <label><input type="checkbox" id="auto" checked> Continue to next step</label>
        <label><input type="checkbox" id="subs" checked> Subtitles</label>
      </div>
      <div class="note" id="voicenote" hidden></div>
    </div>
    <details><summary>Full narration for this step</summary><p id="narration"></p></details>
    <div class="keys">Keys: Space play or pause · Left and Right arrows previous and next step · S subtitles on or off</div>
  </main>
</div>
<script>
const SCENES = __DATA__;
const SECTION_COLORS = { "1": "var(--s1)", "2": "var(--s2)", "3": "var(--s3)", "4": "var(--s4)" };
const $ = id => document.getElementById(id);
const tts = "speechSynthesis" in window ? window.speechSynthesis : null;
let i = 0, chunks = [], c = 0, playing = false, token = 0, voices = [], timer = null;

const store = {
  get(k) { try { return localStorage.getItem("lmw." + k); } catch { return null; } },
  set(k, v) { try { localStorage.setItem("lmw." + k, v); } catch {} }
};

// Narration split into subtitle-sized pieces; each piece is spoken on its own so the subtitle matches the voice.
function makeChunks(text) {
  const parts = (text.match(/[^.!?]+[.!?]+(\s|$)|[^.!?]+$/g) || [text]).map(t => t.trim()).filter(Boolean);
  const out = [];
  for (const p of parts) {
    if (p.length <= 160) { out.push(p); continue; }
    let cur = "";
    for (const w of p.split(" ")) { if ((cur + " " + w).length > 130 && cur) { out.push(cur); cur = w; } else cur = cur ? cur + " " + w : w; }
    if (cur) out.push(cur);
  }
  return out;
}

function loadVoices() {
  if (!tts) return;
  const all = tts.getVoices();
  if (!all.length) return;
  const en = all.filter(v => /^en(-|_|$)/i.test(v.lang));
  const list = (en.length ? en : all).slice().sort((a, b) => score(b) - score(a) || a.name.localeCompare(b.name));
  if (list.map(v => v.voiceURI).join() === voices.map(v => v.voiceURI).join()) return;
  voices = list;
  const sel = $("voice"), saved = store.get("voice") || sel.value;
  sel.innerHTML = "";
  for (const v of voices) {
    const o = document.createElement("option");
    o.value = v.voiceURI;
    o.textContent = v.name.replace(/^Microsoft /, "").replace(/ - English \(([^)]+)\)/, " · $1") + (v.localService ? "" : " (online)");
    sel.appendChild(o);
  }
  if (saved && voices.some(v => v.voiceURI === saved)) sel.value = saved;
  $("voicenote").hidden = true;
}
// Natural (online) voices first, then the US English ones.
function score(v) { return (/natural|neural|online/i.test(v.name) ? 4 : 0) + (/en-US/i.test(v.lang) ? 2 : 0) + (v.default ? 1 : 0); }
function currentVoice() { return voices.find(v => v.voiceURI === $("voice").value) || null; }
function rate() { return parseFloat($("speed").value); }

function buildNav() {
  let last = "";
  const nav = $("nav");
  SCENES.forEach((s, k) => {
    if (s.section !== last) { const h = document.createElement("h2"); h.textContent = s.section; nav.appendChild(h); last = s.section; }
    const b = document.createElement("button");
    b.innerHTML = `<span class="num">${String(s.n).padStart(2, "0")}</span><span class="t"></span>`;
    const t = b.querySelector(".t"); t.textContent = s.title;
    const who = document.createElement("span"); who.className = "who"; who.textContent = `${s.role} · ${s.persona}`; t.appendChild(who);
    b.onclick = () => go(k, true);
    nav.appendChild(b);
  });
  const words = SCENES.reduce((a, s) => a + s.narration.split(/\s+/).length, 0);
  $("total").textContent = `${SCENES.length} steps · about ${Math.round(words / 155)} minutes`;
}

function stopSpeech() { token++; clearTimeout(timer); if (tts) tts.cancel(); }

function go(k, autoplay) {
  stopSpeech();
  i = Math.max(0, Math.min(SCENES.length - 1, k));
  const s = SCENES[i];
  $("shot").src = s.img;
  $("shot").alt = s.title + " (screen of the Workbench as " + s.role + ")";
  $("section").textContent = s.section;
  $("section").style.background = SECTION_COLORS[s.section[0]] || "var(--accent)";
  $("role").textContent = s.role + " · " + s.persona;
  $("title").textContent = s.title;
  $("narration").textContent = s.narration;
  $("counter").textContent = `Step ${i + 1} of ${SCENES.length}`;
  $("subtitle").textContent = "";
  chunks = makeChunks(s.narration); c = 0;
  progress();
  document.querySelectorAll("nav button").forEach((b, k2) => b.setAttribute("aria-current", k2 === i ? "true" : "false"));
  document.querySelectorAll("nav button")[i].scrollIntoView({ block: "nearest" });
  if (autoplay) play(); else setPlaying(false);
}

function progress() { $("progress").style.width = chunks.length ? (100 * c / chunks.length) + "%" : "0"; }
function setPlaying(on) {
  playing = on;
  $("play").textContent = on ? "❚❚ Pause" : "▶ Play";
  $("bigplay").hidden = on;
}

function play() { setPlaying(true); speakFrom(c >= chunks.length ? 0 : c); }
function pause() { stopSpeech(); setPlaying(false); }

// Speaks chunk n, shows it as the subtitle, then moves on. Pause cancels and keeps the position.
function speakFrom(n) {
  stopSpeech();
  const my = token;
  c = n; progress();
  if (c >= chunks.length) return finished();
  const text = chunks[c];
  $("subtitle").textContent = $("subs").checked ? text : "";
  const next = () => { if (my !== token) return; c++; progress(); speakFrom(c); };
  if (!tts || !voices.length) {   // no voice available: subtitles only, timed by reading speed
    timer = setTimeout(next, Math.max(1800, text.length * 62 / rate()));
    return;
  }
  const u = new SpeechSynthesisUtterance(text);
  const v = currentVoice(); if (v) { u.voice = v; u.lang = v.lang; }
  u.rate = rate();
  u.onend = next;
  u.onerror = e => { if (my === token && e.error !== "interrupted" && e.error !== "canceled") next(); };
  tts.speak(u);
}

function finished() {
  $("progress").style.width = "100%";
  if ($("auto").checked && i < SCENES.length - 1) { const my = token; timer = setTimeout(() => { if (my === token) go(i + 1, true); }, 700); }
  else { setPlaying(false); c = 0; }
}

// A new voice or speed takes effect from the current sentence.
function restartIfPlaying() { if (playing) speakFrom(c); }

$("bigplay").onclick = () => play();
$("play").onclick = () => playing ? pause() : play();
$("prev").onclick = () => go(i - 1, playing);
$("next").onclick = () => go(i + 1, playing);
$("voice").onchange = () => { store.set("voice", $("voice").value); restartIfPlaying(); };
$("speed").onchange = () => { store.set("speed", $("speed").value); restartIfPlaying(); };
$("subs").onchange = () => { $("subtitle").textContent = $("subs").checked && playing && chunks[c] ? chunks[c] : ""; };
document.addEventListener("keydown", e => {
  if (e.target.tagName === "SELECT" || e.target.tagName === "INPUT") return;
  if (e.code === "Space") { e.preventDefault(); playing ? pause() : play(); }
  else if (e.key === "ArrowRight") go(i + 1, playing);
  else if (e.key === "ArrowLeft") go(i - 1, playing);
  else if (e.key.toLowerCase() === "s") { $("subs").checked = !$("subs").checked; $("subs").onchange(); }
});
window.addEventListener("beforeunload", () => { if (tts) tts.cancel(); });

const savedSpeed = store.get("speed");
if (savedSpeed && [...$("speed").options].some(o => o.value === savedSpeed)) $("speed").value = savedSpeed;
if (tts) {
  loadVoices();
  tts.addEventListener ? tts.addEventListener("voiceschanged", loadVoices) : (tts.onvoiceschanged = loadVoices);
  setTimeout(() => { if (!voices.length) { $("voicenote").hidden = false; $("voicenote").textContent = "No voices found in this browser. The walkthrough still plays with subtitles. Open it in Microsoft Edge for natural voices."; } }, 1500);
} else {
  $("voice").disabled = true;
  $("voicenote").hidden = false;
  $("voicenote").textContent = "This browser cannot speak. The walkthrough still plays with subtitles. Open it in Microsoft Edge or Chrome for a voice.";
}
buildNav();
go(0, false);
</script>
</body>
</html>
"""
out = here / "License-Management-walkthrough.html"
out.write_text(page.replace("__DATA__", data), encoding="utf-8")
print(f"{out.name} written: {len(scenes)} scenes, {out.stat().st_size / 1e6:.1f} MB")
