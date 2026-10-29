"""Builds index.html (the narrated walkthrough player) from scenes.json. Run after capture.py and make-voice.ps1."""
import json, pathlib, html

here = pathlib.Path(__file__).parent
scenes = json.loads((here / "scenes.json").read_text(encoding="utf-8-sig"))
data = json.dumps(scenes, ensure_ascii=False)

page = r"""<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>License Management walkthrough</title>
<style>
:root {
  --bg: #f4f6fb; --panel: #ffffff; --ink: #0f172a; --muted: #5b6475; --line: #e3e7ef;
  --accent: #2563eb; --accent-soft: #e8efff; --sub-bg: rgba(15,23,42,.86); --sub-ink: #ffffff;
  --s1: #0e7490; --s2: #7c3aed; --s3: #b45309; --s4: #15803d;
}
@media (prefers-color-scheme: dark) {
  :root:not([data-theme="light"]) {
    --bg: #0b1020; --panel: #121a2e; --ink: #e8ecf5; --muted: #9aa4b8; --line: #243049;
    --accent: #6ea0ff; --accent-soft: #1b2a4a; --sub-bg: rgba(0,0,0,.82);
  }
}
* { box-sizing: border-box; }
body { margin: 0; background: var(--bg); color: var(--ink); font: 15px/1.5 "Segoe UI", system-ui, sans-serif; }
header { display: flex; align-items: center; gap: 16px; padding: 14px 24px; border-bottom: 1px solid var(--line); background: var(--panel); }
header h1 { font-size: 18px; margin: 0; font-weight: 650; }
header .sub { color: var(--muted); font-size: 13px; }
header .spacer { flex: 1; }
.layout { display: grid; grid-template-columns: 320px 1fr; min-height: calc(100vh - 58px); }
nav { border-right: 1px solid var(--line); background: var(--panel); overflow: auto; max-height: calc(100vh - 58px); padding: 8px 0 24px; }
nav h2 { font-size: 11px; letter-spacing: .08em; text-transform: uppercase; color: var(--muted); margin: 18px 18px 6px; }
nav button { all: unset; display: grid; grid-template-columns: 26px 1fr; gap: 8px; width: calc(100% - 16px); margin: 0 8px; padding: 8px 10px;
  border-radius: 8px; cursor: pointer; box-sizing: border-box; }
nav button:hover { background: var(--accent-soft); }
nav button[aria-current="true"] { background: var(--accent-soft); outline: 2px solid var(--accent); }
nav .num { font-weight: 650; color: var(--muted); font-variant-numeric: tabular-nums; }
nav .t { font-size: 13.5px; }
nav .who { display: block; font-size: 12px; color: var(--muted); }
main { padding: 20px 28px 28px; display: flex; flex-direction: column; gap: 14px; min-width: 0; }
.meta { display: flex; flex-wrap: wrap; align-items: center; gap: 10px; }
.chip { display: inline-flex; align-items: center; gap: 6px; padding: 3px 10px; border-radius: 999px; font-size: 12.5px; font-weight: 600; color: #fff; }
.role { background: var(--panel); color: var(--ink); border: 1px solid var(--line); font-weight: 500; }
.meta h2 { margin: 0; font-size: 20px; font-weight: 650; flex-basis: 100%; }
.stage { position: relative; border-radius: 12px; overflow: hidden; border: 1px solid var(--line); background: #000; box-shadow: 0 8px 30px rgba(15,23,42,.12); }
.stage img { display: block; width: 100%; height: auto; }
.subtitle { position: absolute; left: 50%; bottom: 22px; transform: translateX(-50%); width: min(88%, 980px); text-align: center;
  background: var(--sub-bg); color: var(--sub-ink); padding: 10px 18px; border-radius: 10px; font-size: 19px; line-height: 1.4; min-height: 1em; }
.subtitle:empty { display: none; }
.controls { display: flex; flex-wrap: wrap; align-items: center; gap: 10px; }
.controls button, .controls select { font: inherit; padding: 7px 14px; border-radius: 8px; border: 1px solid var(--line); background: var(--panel); color: var(--ink); cursor: pointer; }
.controls button.primary { background: var(--accent); border-color: var(--accent); color: #fff; min-width: 96px; }
.controls label { display: inline-flex; align-items: center; gap: 6px; color: var(--muted); font-size: 13px; }
.bar { flex: 1; min-width: 160px; height: 6px; background: var(--line); border-radius: 99px; overflow: hidden; }
.bar > div { height: 100%; width: 0; background: var(--accent); transition: width .2s linear; }
.counter { color: var(--muted); font-variant-numeric: tabular-nums; font-size: 13px; }
details { background: var(--panel); border: 1px solid var(--line); border-radius: 10px; padding: 10px 14px; }
details summary { cursor: pointer; color: var(--muted); font-size: 13px; }
details p { margin: 8px 0 0; }
.keys { color: var(--muted); font-size: 12px; }
@media (max-width: 900px) {
  .layout { grid-template-columns: 1fr; }
  nav { max-height: none; border-right: 0; border-bottom: 1px solid var(--line); }
  main { padding: 14px 16px; }
  .subtitle { position: static; transform: none; width: auto; border-radius: 0; font-size: 16px; }
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
    <div class="stage"><img id="shot" alt=""><div class="subtitle" id="subtitle" aria-live="polite"></div></div>
    <div class="controls">
      <button id="prev" title="Previous step (Left arrow)">Previous</button>
      <button id="play" class="primary" title="Play or pause (Space)">Play</button>
      <button id="next" title="Next step (Right arrow)">Next</button>
      <div class="bar" aria-hidden="true"><div id="progress"></div></div>
      <span class="counter" id="counter"></span>
      <label><input type="checkbox" id="auto" checked> Continue to next step</label>
      <label><input type="checkbox" id="subs" checked> Subtitles</label>
      <label>Speed <select id="speed"><option>0.9</option><option selected>1</option><option>1.15</option><option>1.3</option></select></label>
    </div>
    <details><summary>Full narration for this step</summary><p id="narration"></p></details>
    <div class="keys">Keys: Space play or pause · Left and Right arrows previous and next step · S subtitles on or off</div>
    <audio id="audio" preload="auto"></audio>
  </main>
</div>
<script>
const SCENES = __DATA__;
const SECTION_COLORS = { "1": "var(--s1)", "2": "var(--s2)", "3": "var(--s3)", "4": "var(--s4)" };
const $ = id => document.getElementById(id);
const audio = $("audio");
let i = 0, cues = [], playing = false;

// Subtitle cues: sentences timed by their share of the clip's characters.
function makeCues(s) {
  const parts = s.narration.match(/[^.!?]+[.!?]+(\s|$)|[^.!?]+$/g).map(t => t.trim()).filter(Boolean);
  const chunks = [];
  for (const p of parts) {
    if (p.length <= 150) { chunks.push(p); continue; }
    const words = p.split(" "); let cur = "";
    for (const w of words) { if ((cur + " " + w).length > 120 && cur) { chunks.push(cur); cur = w; } else cur = cur ? cur + " " + w : w; }
    if (cur) chunks.push(cur);
  }
  const total = chunks.reduce((a, c) => a + c.length, 0);
  const dur = (s.durationMs || 15000) / 1000;
  let t = 0;
  return chunks.map(c => { const start = t; t += dur * c.length / total; return { start, end: t, text: c }; });
}

function buildNav() {
  let last = "";
  const nav = $("nav");
  SCENES.forEach((s, k) => {
    if (s.section !== last) { const h = document.createElement("h2"); h.textContent = s.section; nav.appendChild(h); last = s.section; }
    const b = document.createElement("button");
    b.innerHTML = `<span class="num">${String(s.n).padStart(2, "0")}</span><span class="t">${s.title}<span class="who">${s.role} · ${s.persona}</span></span>`;
    b.onclick = () => go(k, true);
    nav.appendChild(b);
  });
  $("total").textContent = `${SCENES.length} steps · about ${Math.round(SCENES.reduce((a, s) => a + (s.durationMs || 0), 0) / 60000)} minutes`;
}

function go(k, autoplay) {
  i = Math.max(0, Math.min(SCENES.length - 1, k));
  const s = SCENES[i];
  $("shot").src = "shots/" + s.file;
  $("shot").alt = s.title + " (screen of the Workbench as " + s.role + ")";
  $("section").textContent = s.section;
  $("section").style.background = SECTION_COLORS[s.section[0]] || "var(--accent)";
  $("role").textContent = s.role + " · " + s.persona;
  $("title").textContent = s.title;
  $("narration").textContent = s.narration;
  $("counter").textContent = `Step ${i + 1} of ${SCENES.length}`;
  $("subtitle").textContent = "";
  $("progress").style.width = "0";
  cues = makeCues(s);
  document.querySelectorAll("nav button").forEach((b, k2) => b.setAttribute("aria-current", k2 === i ? "true" : "false"));
  document.querySelectorAll("nav button")[i].scrollIntoView({ block: "nearest" });
  audio.src = s.audio;
  audio.playbackRate = parseFloat($("speed").value);
  if (autoplay) play(); else pause();
}

function play() { audio.play().then(() => { playing = true; $("play").textContent = "Pause"; }).catch(() => {}); }
function pause() { audio.pause(); playing = false; $("play").textContent = "Play"; }

audio.addEventListener("timeupdate", () => {
  const t = audio.currentTime;
  const c = cues.find(c => t >= c.start && t < c.end) || (t > 0 ? cues[cues.length - 1] : null);
  $("subtitle").textContent = $("subs").checked && c ? c.text : "";
  if (audio.duration) $("progress").style.width = (100 * t / audio.duration) + "%";
});
audio.addEventListener("loadedmetadata", () => {
  // Rescale cues to the real clip length.
  const s = SCENES[i]; const real = audio.duration;
  if (real && isFinite(real)) { const k = real / ((s.durationMs || real * 1000) / 1000); cues.forEach(c => { c.start *= k; c.end *= k; }); }
});
audio.addEventListener("ended", () => {
  $("progress").style.width = "100%";
  if ($("auto").checked && i < SCENES.length - 1) setTimeout(() => go(i + 1, true), 900);
  else pause();
});

$("play").onclick = () => playing ? pause() : play();
$("prev").onclick = () => go(i - 1, playing);
$("next").onclick = () => go(i + 1, playing);
$("speed").onchange = () => audio.playbackRate = parseFloat($("speed").value);
$("subs").onchange = () => { if (!$("subs").checked) $("subtitle").textContent = ""; };
document.addEventListener("keydown", e => {
  if (e.target.tagName === "SELECT" || e.target.tagName === "INPUT") return;
  if (e.code === "Space") { e.preventDefault(); playing ? pause() : play(); }
  else if (e.key === "ArrowRight") go(i + 1, playing);
  else if (e.key === "ArrowLeft") go(i - 1, playing);
  else if (e.key.toLowerCase() === "s") { $("subs").checked = !$("subs").checked; $("subs").onchange(); }
});

buildNav();
go(0, false);
</script>
</body>
</html>
"""
(here / "index.html").write_text(page.replace("__DATA__", data), encoding="utf-8")
print("index.html written:", len(scenes), "scenes")
