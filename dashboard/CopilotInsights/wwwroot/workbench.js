// Governance Workbench pages: small progressive enhancements. Every page works without this file. The theme and the
// menu are the site's (site.js, theme.js).
(function () {
    'use strict';

    // A seat reservation change: the chosen LOB's current reservation, shown as it is chosen.
    var seatLob = document.getElementById('seatLob'), seatNow = document.getElementById('seatNow');
    if (seatLob && seatNow) {
        seatLob.addEventListener('change', function () {
            var o = seatLob.options[seatLob.selectedIndex];
            seatNow.textContent = o && o.value ? (o.dataset.reserved + ' seats reserved; ' + o.dataset.assigned + ' assigned (' + o.dataset.group + ').') : '';
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

    // Pickers scoped to another field (data-filter-by="fieldName"): options carry data-fo; only the chosen
    // funding owner's people show. The server checks the choice again.
    document.querySelectorAll('select[data-filter-by]').forEach(function (sel) {
        var form = sel.form || document;
        var source = form.querySelector('[name="' + sel.getAttribute('data-filter-by') + '"]');
        if (!source) return;
        function apply() {
            var v = source.value;
            Array.prototype.forEach.call(sel.options, function (o) {
                if (!o.hasAttribute('data-fo')) return;
                var show = v !== '' && o.getAttribute('data-fo') === v;
                o.hidden = !show; o.disabled = !show;
                if (!show && o.selected) { o.selected = false; sel.value = ''; }
            });
        }
        source.addEventListener('change', apply);
        apply();
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
    // Person pickers: "Or search everyone in Active Directory" (_FindPerson.cshtml) searches the directory Copilot
    // Insights loads from AD, records the person chosen, and adds and chooses them in the picker above it.
    document.querySelectorAll('.find-person').forEach(function (box) {
        var target = document.getElementById(box.getAttribute('data-for'));
        var url = box.getAttribute('data-lookup');
        var byUpn = box.getAttribute('data-by') === 'upn';
        var open = box.querySelector('.find-open'), panel = box.querySelector('.find-box'), q = box.querySelector('.find-q'),
            list = box.querySelector('.find-results'), note = box.querySelector('.find-note');
        if (!target || !url || !panel || !q || !list || !note) return;
        var form = box.closest('form');
        var token = (form && form.querySelector('input[name="__RequestVerificationToken"]')) || document.querySelector('input[name="__RequestVerificationToken"]');
        var timer = null, asked = '';
        box.hidden = false;
        // The search is always open now; a page that still has the old toggle button keeps working.
        if (open) open.addEventListener('click', function () {
            panel.hidden = !panel.hidden;
            open.setAttribute('aria-expanded', panel.hidden ? 'false' : 'true');
            if (!panel.hidden) q.focus();
        });
        function show(rows) {
            while (list.firstChild) list.removeChild(list.firstChild);
            rows.forEach(function (r) {
                var li = document.createElement('li');
                var b = document.createElement('button');
                b.type = 'button'; b.className = 'find-pick';
                var n = document.createElement('strong'); n.textContent = r.name;
                var d = document.createElement('span'); d.className = 'small muted'; d.textContent = r.detail;
                b.appendChild(n); b.appendChild(document.createTextNode(' ')); b.appendChild(d);
                b.addEventListener('click', function () { pick(r); });
                li.appendChild(b); list.appendChild(li);
            });
        }
        function choose(value, text) {
            if (target.tagName === 'SELECT') {
                var o = null;
                for (var i = 0; i < target.options.length; i++) if (target.options[i].value === value) o = target.options[i];
                if (!o) { o = document.createElement('option'); o.value = value; o.textContent = text; target.appendChild(o); }
                target.value = value;
                target.dispatchEvent(new Event('change', { bubbles: true }));
            } else {
                var dl = document.getElementById(target.getAttribute('list'));
                if (dl) { var op = document.createElement('option'); op.value = value; op.textContent = text; dl.appendChild(op); }
                target.value = value;
            }
        }
        function pick(r) {
            note.textContent = 'Adding ' + r.name + '...';
            var body = new URLSearchParams();
            body.append('upn', r.upn);
            if (token) body.append('__RequestVerificationToken', token.value);
            fetch(url.replace('handler=Find', 'handler=Add'), { method: 'POST', credentials: 'same-origin', headers: { 'Accept': 'application/json' }, body: body })
                .then(function (res) { return res.json().catch(function () { return {}; }).then(function (j) { return { ok: res.ok, j: j }; }); })
                .then(function (x) {
                    if (!x.ok || !x.j.value) { note.textContent = (x.j && x.j.error) || 'Not added. Try again, or ask a Workbench administrator.'; return; }
                    choose(byUpn ? x.j.upn : x.j.value, x.j.text);
                    note.textContent = x.j.text + (x.j.added ? ' added to the Workbench and chosen.' : ' chosen.');
                    show([]); q.value = ''; asked = '';
                })
                .catch(function () { note.textContent = 'Not added. Try again.'; });
        }
        q.addEventListener('keydown', function (e) { if (e.key === 'Enter') e.preventDefault(); });
        q.addEventListener('input', function () {
            var t = q.value.replace(/\s+/g, ' ').trim();
            window.clearTimeout(timer);
            if (t.length < 2) { asked = ''; show([]); note.textContent = ''; return; }
            timer = window.setTimeout(function () {
                asked = t;
                fetch(url + '&q=' + encodeURIComponent(t), { credentials: 'same-origin', headers: { 'Accept': 'application/json' } })
                    .then(function (res) { return res.ok ? res.json() : null; })
                    .then(function (rows) {
                        if (asked !== t) return;
                        if (!rows) { show([]); note.textContent = 'The directory cannot be searched now.'; return; }
                        show(rows);
                        note.textContent = rows.length ? '' : 'Nobody in the directory matches.';
                    })
                    .catch(function () { });
            }, 250);
        });
    });
})();
