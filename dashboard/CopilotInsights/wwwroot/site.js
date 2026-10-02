// Tooltips for chart marks (data-tip), the crosshair on line charts,
// the theme toggle, and the filter controls. No inline script: the CSP forbids it.
(function () {
  var tip = document.getElementById('tip');

  function show(e, text) {
    tip.textContent = text;
    tip.hidden = false;
    var x = e.clientX + 14, y = e.clientY + 14;
    var r = tip.getBoundingClientRect();
    if (x + r.width > window.innerWidth - 8) x = e.clientX - r.width - 14;
    if (y + r.height > window.innerHeight - 8) y = e.clientY - r.height - 14;
    tip.style.left = x + 'px';
    tip.style.top = y + 'px';
  }

  document.addEventListener('mousemove', function (e) {
    var t = e.target.closest ? e.target.closest('[data-tip]') : null;
    var svg = e.target.closest ? e.target.closest('svg.chart') : null;
    if (svg) {
      var cross = svg.querySelector('.cross');
      if (cross) {
        if (t && t.hasAttribute('data-x')) {
          cross.setAttribute('x1', t.getAttribute('data-x'));
          cross.setAttribute('x2', t.getAttribute('data-x'));
          cross.style.visibility = 'visible';
        } else { cross.style.visibility = 'hidden'; }
      }
    }
    // An information icon's text sits under the icon, not under the pointer (2.4.5).
    if (t && t.classList.contains('infob')) { if (tip.hidden || tip.textContent !== t.getAttribute('data-tip')) showAt(t); }
    else if (t) show(e, t.getAttribute('data-tip')); else tip.hidden = true;
  });
  document.addEventListener('mouseleave', function () { tip.hidden = true; });

  // Information icons (2.4.3): the same tip on keyboard focus or a tap, under the icon and inside the window.
  function showAt(b) {
    tip.textContent = b.getAttribute('data-tip');
    tip.hidden = false;
    var r = b.getBoundingClientRect(), t = tip.getBoundingClientRect();
    var x = Math.max(8, Math.min(r.left, window.innerWidth - t.width - 8));
    var y = r.bottom + 6;
    if (y + t.height > window.innerHeight - 8) y = Math.max(8, r.top - t.height - 6);
    tip.style.left = x + 'px';
    tip.style.top = y + 'px';
  }
  document.querySelectorAll('button.infob').forEach(function (b) {
    b.addEventListener('focus', function () { showAt(b); });
    b.addEventListener('blur', function () { tip.hidden = true; });
    b.addEventListener('click', function (e) { e.preventDefault(); if (tip.hidden) showAt(b); else tip.hidden = true; });
    b.addEventListener('keydown', function (e) { if (e.key === 'Escape') tip.hidden = true; });
  });

  var toggle = document.getElementById('theme-toggle');
  if (toggle) toggle.addEventListener('click', function () {
    var root = document.documentElement;
    var dark = root.getAttribute('data-theme') === 'dark' ||
      (!root.getAttribute('data-theme') && window.matchMedia('(prefers-color-scheme: dark)').matches);
    var next = dark ? 'light' : 'dark';
    root.setAttribute('data-theme', next);
    try { localStorage.setItem('insights-theme', next); } catch (e) { }
  });

  document.querySelectorAll('select[data-autosubmit]').forEach(function (s) {
    s.addEventListener('change', function () { s.form.submit(); });
  });
  document.querySelectorAll('select[data-navigate]').forEach(function (s) {
    s.addEventListener('change', function () { if (s.value) location.href = s.value; });
  });
  document.querySelectorAll('[data-print]').forEach(function (b) {
    b.addEventListener('click', function () { window.print(); });
  });
  document.querySelectorAll('form[data-confirm]').forEach(function (f) {
    f.addEventListener('submit', function (e) { if (!window.confirm(f.getAttribute('data-confirm'))) e.preventDefault(); });
  });
  // Column headers sort a table in the page (2.4.5): by each cell's data-v when it has one (numbers,
  // dates), else by a number at the start of its text ("45%", "$1,200"), else by its text. Numbers and
  // dates open largest or latest first, text A to Z; a second click reverses. Rows marked data-keep stay last.
  function key(td) {
    if (!td) return { n: null, t: '' };
    var v = td.getAttribute('data-v');
    if (v !== null && v !== '') return { n: parseFloat(v), t: v };
    var t = td.textContent.trim();
    // A leading arrow carries the sign of a change ("▼ 12%" is -12); so does a minus, plain or typographic.
    var m = /^([▲▼↑↓]?)\s*([-+−]?)\s*[$€£]?\s?(\d[\d,]*(\.\d+)?)/.exec(t);
    var n = m ? parseFloat(m[3].replace(/,/g, '')) : null;
    if (m && (m[1] === '▼' || m[1] === '↓' || m[2] === '-' || m[2] === '−')) n = -n;
    return { n: n, t: t.toLowerCase() };
  }
  document.querySelectorAll('table.sortable').forEach(function (table) {
    var heads = table.tHead.rows[0].cells;
    Array.prototype.forEach.call(heads, function (th, col) {
      var b = th.querySelector('button.sort');
      if (!b) return;
      b.addEventListener('click', function () {
        var body = table.tBodies[0];
        var rows = Array.prototype.slice.call(body.rows);
        var keep = rows.filter(function (r) { return r.hasAttribute('data-keep'); });
        rows = rows.filter(function (r) { return !r.hasAttribute('data-keep'); });
        var keys = rows.map(function (r) { return key(r.cells[col]); });
        var filled = keys.filter(function (k) { return k.t !== '' && k.t !== '—'; });
        var numeric = filled.length > 0 && filled.every(function (k) { return k.n !== null && !isNaN(k.n); });
        var was = th.getAttribute('aria-sort');
        var desc = was ? was === 'ascending' : numeric;
        var order = rows.map(function (r, i) { return { r: r, k: keys[i], i: i }; });
        order.sort(function (a, b) {
          var x, y, c;
          if (numeric) {
            x = isNaN(a.k.n) || a.k.n === null ? -Infinity : a.k.n;
            y = isNaN(b.k.n) || b.k.n === null ? -Infinity : b.k.n;
            c = x < y ? -1 : x > y ? 1 : 0;
          } else c = a.k.t.localeCompare(b.k.t, undefined, { numeric: true, sensitivity: 'base' });
          return (desc ? -c : c) || a.i - b.i;
        });
        order.forEach(function (o) { body.appendChild(o.r); });
        keep.forEach(function (r) { body.appendChild(r); });
        Array.prototype.forEach.call(heads, function (h) { h.removeAttribute('aria-sort'); });
        th.setAttribute('aria-sort', desc ? 'descending' : 'ascending');
      });
    });
  });

  // Admin > Reports shown: a section's own box ticks or clears every report in it, and follows them.
  document.querySelectorAll('.card.pick').forEach(function (card) {
    var all = card.querySelector('input[data-all]');
    var boxes = card.querySelectorAll('input[name="show"]');
    if (!all) return;
    all.addEventListener('change', function () { Array.prototype.forEach.call(boxes, function (b) { b.checked = all.checked; }); });
    Array.prototype.forEach.call(boxes, function (b) {
      b.addEventListener('change', function () { all.checked = Array.prototype.every.call(boxes, function (x) { return x.checked; }); });
    });
  });

  // The admin forms put the question on the button.
  document.querySelectorAll('button[data-confirm]').forEach(function (b) {
    b.addEventListener('click', function (e) { if (!window.confirm(b.getAttribute('data-confirm'))) e.preventDefault(); });
  });
})();
