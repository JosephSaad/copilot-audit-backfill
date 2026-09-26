// Applies the saved theme before the page paints, so it does not flash.
(function () {
  try {
    var t = localStorage.getItem('insights-theme');
    if (t === 'dark' || t === 'light') document.documentElement.setAttribute('data-theme', t);
  } catch (e) { }
})();
