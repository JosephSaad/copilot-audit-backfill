// Applies the saved theme before the page paints, so it does not flash.
(function () {
  // The navigation folds its sections only when script runs; without it every section stays open.
  document.documentElement.classList.add('js');
  try {
    var t = localStorage.getItem('insights-theme');
    if (t === 'dark' || t === 'light') document.documentElement.setAttribute('data-theme', t);
  } catch (e) { }
})();
