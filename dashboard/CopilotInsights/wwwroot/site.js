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
    if (t) show(e, t.getAttribute('data-tip')); else tip.hidden = true;
  });
  document.addEventListener('mouseleave', function () { tip.hidden = true; });

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
  // The admin forms put the question on the button.
  document.querySelectorAll('button[data-confirm]').forEach(function (b) {
    b.addEventListener('click', function (e) { if (!window.confirm(b.getAttribute('data-confirm'))) e.preventDefault(); });
  });
})();
