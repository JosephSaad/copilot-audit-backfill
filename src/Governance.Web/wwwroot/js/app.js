// Governance Workbench: small progressive enhancements. Every page works without this file.
(function () {
    'use strict';

    // Theme: light, dark, or follow the system (no attribute).
    document.querySelectorAll('.theme-toggle').forEach(function (b) {
        b.addEventListener('click', function () {
            var root = document.documentElement;
            var current = root.getAttribute('data-theme');
            var systemDark = window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches;
            var next = current ? (current === 'dark' ? 'light' : 'dark') : (systemDark ? 'light' : 'dark');
            root.setAttribute('data-theme', next);
            try { localStorage.setItem('cgw-theme', next); } catch (e) { }
        });
    });

    // Mobile navigation.
    var toggle = document.querySelector('.nav-toggle');
    if (toggle) {
        toggle.addEventListener('click', function () {
            var open = document.body.classList.toggle('nav-open');
            toggle.setAttribute('aria-expanded', open ? 'true' : 'false');
        });
    }

    // Keep a shown tooltip inside the window (it is centered under its icon by default).
    function place(help) {
        var tip = help.querySelector('.help-tip');
        if (!tip) return;
        tip.style.left = ''; tip.style.right = ''; tip.style.transform = '';
        var r = tip.getBoundingClientRect();
        if (r.right > window.innerWidth - 8) { tip.style.left = 'auto'; tip.style.right = '0'; tip.style.transform = 'none'; }
        else if (r.left < 8) { tip.style.left = '0'; tip.style.transform = 'none'; }
    }
    document.querySelectorAll('.help').forEach(function (h) {
        h.addEventListener('mouseenter', function () { place(h); });
        h.addEventListener('focusin', function () { place(h); });
    });

    // Help tooltips: tap to open on touch, Escape to close.
    document.addEventListener('click', function (e) {
        var btn = e.target.closest ? e.target.closest('.help-btn') : null;
        document.querySelectorAll('.help.open').forEach(function (h) {
            if (!btn || h !== btn.parentElement) { h.classList.remove('open'); h.querySelector('.help-btn').setAttribute('aria-expanded', 'false'); }
        });
        if (btn) {
            var open = btn.parentElement.classList.toggle('open');
            btn.setAttribute('aria-expanded', open ? 'true' : 'false');
            if (open) place(btn.parentElement);
            e.preventDefault();
        }
    });
    document.addEventListener('keydown', function (e) {
        if (e.key === 'Escape') document.querySelectorAll('.help.open').forEach(function (h) { h.classList.remove('open'); });
    });

    // Confirmation for irreversible actions.
    document.querySelectorAll('form[data-confirm]').forEach(function (f) {
        f.addEventListener('submit', function (e) {
            if (!window.confirm(f.getAttribute('data-confirm'))) e.preventDefault();
        });
    });

    // Prevent double submission: the idempotency key protects the server; this protects the user's patience.
    document.querySelectorAll('form[method=post]').forEach(function (f) {
        f.addEventListener('submit', function () {
            setTimeout(function () { f.querySelectorAll('button[type=submit]').forEach(function (b) { b.disabled = true; }); }, 0);
        });
    });

    // Selects that save on change (RACI cells). Inline handlers are blocked by the Content Security Policy.
    document.querySelectorAll('select[data-autosubmit]').forEach(function (s) {
        s.addEventListener('change', function () { if (s.form.requestSubmit) s.form.requestSubmit(); else s.form.submit(); });
    });

    // Show the fields that apply to the choice made (data-show-when="selectName=value1|value2").
    function refreshConditional() {
        document.querySelectorAll('[data-show-when]').forEach(function (el) {
            var rule = el.getAttribute('data-show-when').split('=');
            var scope = el.closest('form') || document;
            var input = scope.querySelector('[name="' + rule[0] + '"]');
            if (!input) return;
            var value = input.type === 'checkbox' ? (input.checked ? 'true' : 'false') : input.value;
            el.hidden = rule[1].split('|').indexOf(value) < 0;
        });
    }
    document.addEventListener('change', refreshConditional);
    refreshConditional();

    // Copy table cell labels for the phone layout.
    document.querySelectorAll('table.stack').forEach(function (t) {
        var heads = Array.prototype.map.call(t.querySelectorAll('thead th'), function (th) {
            if (th.getAttribute('data-label')) return th.getAttribute('data-label');
            var c = th.cloneNode(true);
            c.querySelectorAll('.help, .sr-only').forEach(function (x) { x.remove(); });
            return c.textContent.replace(/\s+/g, ' ').trim();
        });
        t.querySelectorAll('tbody tr').forEach(function (tr) {
            Array.prototype.forEach.call(tr.children, function (td, i) { if (!td.hasAttribute('data-label') && heads[i]) td.setAttribute('data-label', heads[i]); });
        });
    });
})();
