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
    else if (t) show(e, t.getAttribute('data-tip'));
    else if (!(e.target.closest && e.target.closest('nav.side [data-what]'))) tip.hidden = true;   // the navigation shows its own
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

  // ---- The navigation (2.4.9) ----------------------------------------------------------------------
  var nav = document.querySelector('nav.side');
  if (nav) (function () {
    var grps = Array.prototype.slice.call(nav.querySelectorAll('.grp'));
    function fold(g, open) {
      g.classList.toggle('open', open);
      g.querySelector('.gname').setAttribute('aria-expanded', open ? 'true' : 'false');
    }
    // A narrow window: one button shows or hides the sections.
    var tog = nav.querySelector('.navtog');
    if (tog) tog.addEventListener('click', function (e) {
      e.stopPropagation();
      var on = !nav.classList.contains('shown');
      nav.classList.toggle('shown', on);
      tog.setAttribute('aria-expanded', on ? 'true' : 'false');
    });

    // A click on a section opens it, or folds it if it is open, and changes nothing else: the row clicked stays where
    // it is, under the pointer (folding the section above it would move it away). Every page starts with the section
    // it belongs to open and the rest folded, a page come back to with Back too; nothing is remembered between pages.
    // Where the list scrolls on its own, folding a section near its end would pull the rows down (there is less to
    // scroll): the list's scroll is put back so the row clicked stays put, with room left under the last section if needed.
    var room = document.createElement('div');
    room.setAttribute('aria-hidden', 'true');
    nav.appendChild(room);
    function steady(row, was) {
      if (getComputedStyle(nav).overflowY !== 'auto') return;   // a narrow window: the list is part of the page, and nothing above the row changes
      var left = row.getBoundingClientRect().top - was;
      if (Math.abs(left) > 0.5) {
        nav.scrollTop += left;
        left = row.getBoundingClientRect().top - was;
        if (left > 0.5) {   // nothing left to scroll: make room under the last section, then scroll
          room.style.height = (room.offsetHeight + left + nav.clientHeight) + 'px';
          nav.scrollTop += left;
        }
      }
      // No more room than the scroll position needs.
      var need = nav.scrollTop + nav.clientHeight - (nav.scrollHeight - room.offsetHeight);
      room.style.height = Math.max(0, Math.ceil(need)) + 'px';
    }
    grps.forEach(function (g) {
      var b = g.querySelector('.gname');
      b.addEventListener('click', function (e) {
        e.stopPropagation();
        close(false);
        var was = b.getBoundingClientRect().top;
        fold(g, !g.classList.contains('open'));
        steady(b, was);
      });
    });
    window.addEventListener('pageshow', function (e) {
      if (!e.persisted) return;
      close(false);
      grps.forEach(function (g) { fold(g, g.classList.contains('here') || (g.classList.contains('mine') && location.pathname === '/')); });
      var f = document.getElementById('nav-find');
      if (f && f.value) { f.value = ''; f.dispatchEvent(new Event('input')); }
    });

    // "About these reports" opens the section's panel. Escape, its close button or a click elsewhere closes it.
    var shown = null, at = [0, 0];
    // A scroll closes the panel: a real one, not the last event of a scroll that ended before it opened.
    function scrolled() { if (shown && (Math.abs(window.scrollY - at[0]) > 2 || Math.abs(nav.scrollTop - at[1]) > 2)) close(false); }
    function close(focus) {
      if (!shown) return;
      var b = shown.querySelector('.gabout');
      shown.querySelector('.gpanel').hidden = true;
      b.setAttribute('aria-expanded', 'false');
      shown = null;
      if (focus) b.focus({ preventScroll: true });
    }
    grps.forEach(function (g) {
      var b = g.querySelector('.gabout'), panel = g.querySelector('.gpanel');
      if (!b || !panel) return;   // Your most used has no panel: its reports are described in their own sections
      b.addEventListener('click', function (e) {
        e.stopPropagation();
        var mine = shown === g;
        close(false);
        if (mine) return;
        tip.hidden = true;
        at = [window.scrollY, nav.scrollTop];
        panel.hidden = false;
        // Beside the section it belongs to, level with its name, and wholly inside the window.
        if (window.innerWidth > 900) {
          var top = g.getBoundingClientRect().top, h = panel.getBoundingClientRect().height;
          panel.style.top = Math.max(58, Math.min(top, window.innerHeight - h - 8)) + 'px';
        } else panel.style.top = '';
        b.setAttribute('aria-expanded', 'true');
        shown = g;
        var first = panel.querySelector('a.on') || panel.querySelector('a');
        if (first) first.focus({ preventScroll: true });
      });
      panel.addEventListener('click', function (e) { e.stopPropagation(); });
      panel.querySelector('.pclose').addEventListener('click', function () { close(true); });
    });
    document.addEventListener('click', function () { close(false); });
    window.addEventListener('scroll', scrolled, { passive: true });
    nav.addEventListener('scroll', function () { scrolled(); tip.hidden = true; }, { passive: true });
    window.addEventListener('resize', function () { close(false); });
    document.addEventListener('keydown', function (e) { if (e.key === 'Escape' && shown) close(true); });

    // Find a report: by its name, by what it answers, or by its section ("licensing", "finance"). Enter opens the first
    // that matches; the arrow keys walk the matches; "/" from anywhere on the page puts the cursor in the box.
    var find = document.getElementById('nav-find'), none = document.getElementById('nav-none'), count = document.getElementById('nav-count');
    if (find) {
      var links = Array.prototype.slice.call(nav.querySelectorAll('.glinks a'));
      function matches() { return links.filter(function (x) { return !x.hidden && x.offsetParent !== null; }); }
      function apply() {
        var q = find.value.trim().toLowerCase();
        close(false);
        nav.classList.toggle('finding', q !== '');
        // A report matches by its name or by what it answers; a section's own name brings all its reports; and when
        // nothing matches at all, a section's audience does ("finops" for Licensing), so the box is forgiving but exact first.
        function pass(audience) {
          var n = 0;
          grps.forEach(function (g) {
            var mine = g.classList.contains('mine');
            var words = (g.getAttribute('data-grp') || '') + (audience ? ' ' + (g.getAttribute('data-who') || '') : '');
            var whole = q !== '' && !mine && words.toLowerCase().indexOf(q) >= 0;
            var hit = 0;
            Array.prototype.forEach.call(g.querySelectorAll('.glinks a'), function (a) {
              var m = q === '' || whole || (a.textContent + ' ' + (a.getAttribute('data-what') || '')).toLowerCase().indexOf(q) >= 0;
              a.hidden = !m;
              if (m) hit++;
            });
            g.hidden = q !== '' && (hit === 0 || mine);
            if (!mine) n += hit;
          });
          return n;
        }
        var n = pass(false);
        if (n === 0 && q !== '') n = pass(true);
        if (none) none.hidden = q === '' || n > 0;
        if (count) { count.hidden = q === '' || n === 0; count.textContent = n === 1 ? '1 report' : n + ' reports'; }
      }
      find.addEventListener('input', apply);
      find.addEventListener('keydown', function (e) {
        var m = matches();
        if (e.key === 'Enter') { if (m[0] && find.value.trim() !== '') location.href = m[0].href; }
        else if (e.key === 'Escape') { find.value = ''; apply(); }
        else if (e.key === 'ArrowDown' && m[0]) { e.preventDefault(); m[0].focus(); }
      });
      // Down and up move through the links showing, in the list or among the matches; up from the first goes back to the box.
      nav.addEventListener('keydown', function (e) {
        if ((e.key !== 'ArrowDown' && e.key !== 'ArrowUp') || e.target === find) return;
        var m = matches(), i = m.indexOf(document.activeElement);
        if (i < 0) return;
        e.preventDefault();
        if (e.key === 'ArrowDown') { if (m[i + 1]) m[i + 1].focus(); }
        else if (i === 0) find.focus(); else m[i - 1].focus();
      });
      document.addEventListener('keydown', function (e) {
        if (e.key !== '/' || e.ctrlKey || e.metaKey || e.altKey) return;
        var t = e.target, tag = t && t.tagName;
        if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || (t && t.isContentEditable)) return;
        e.preventDefault();
        find.focus();
        find.select();
      });
    }

    // What a report answers, beside its link: after a moment under the pointer, at once on keyboard focus.
    // To the right of the list, so it covers no other link; the pointer keeps its own shape.
    var wait = null;
    function what(el) {
      var text = el.getAttribute('data-what');
      if (!text || shown) return;
      tip.textContent = text;
      tip.hidden = false;
      var r = el.getBoundingClientRect(), n = nav.getBoundingClientRect(), t = tip.getBoundingClientRect();
      var x = n.right + 8, y = r.top;
      if (x + t.width > window.innerWidth - 8) { x = Math.max(8, r.left); y = r.bottom + 6; }   // a narrow window: under the link
      if (y + t.height > window.innerHeight - 8) y = Math.max(8, window.innerHeight - t.height - 8);
      tip.style.left = x + 'px';
      tip.style.top = y + 'px';
    }
    Array.prototype.forEach.call(nav.querySelectorAll('.glinks [data-what]'), function (el) {
      el.addEventListener('mouseenter', function () { clearTimeout(wait); wait = setTimeout(function () { what(el); }, 450); });
      el.addEventListener('mouseleave', function () { clearTimeout(wait); tip.hidden = true; });
      el.addEventListener('focus', function () { clearTimeout(wait); what(el); });
      el.addEventListener('blur', function () { clearTimeout(wait); tip.hidden = true; });
    });
  })();

  // The admin forms put the question on the button.
  document.querySelectorAll('button[data-confirm]').forEach(function (b) {
    b.addEventListener('click', function (e) { if (!window.confirm(b.getAttribute('data-confirm'))) e.preventDefault(); });
  });
})();
