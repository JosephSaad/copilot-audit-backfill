// Applies the saved theme before the page paints, so it does not flash.
(function () {
  // The navigation folds its sections only when script runs; without it every section stays open.
  document.documentElement.classList.add('js');
  // A phone gets its charts drawn for a narrow screen, so their text stays readable (2.4.10). The server draws them, so it
  // is told with a cookie that holds nothing but narrow or not; the page is fetched again, once, when that changes.
  try {
    var narrow = window.matchMedia('(max-width: 640px)').matches ? '1' : '0';
    var had = /(?:^|; )narrow=([01])/.exec(document.cookie);
    if ((had ? had[1] : '0') !== narrow) {
      document.cookie = 'narrow=' + narrow + '; path=/; max-age=31536000; samesite=lax';
      var again = sessionStorage.getItem('insights-narrow');
      if (again !== narrow && /(?:^|; )narrow=/.test(document.cookie)) { sessionStorage.setItem('insights-narrow', narrow); location.reload(); }
    }
  } catch (e) { }
  try {
    var t = localStorage.getItem('insights-theme');
    if (t === 'dark' || t === 'light') document.documentElement.setAttribute('data-theme', t);
  } catch (e) { }
})();
