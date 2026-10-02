(function () {
  var ids = function () { return Array.prototype.map.call(document.querySelectorAll('.row'), function (row) { return row.dataset.id; }); };
  var active = function () { var row = document.querySelector('.row.active'); return row && row.dataset.id; };
  var before = active();
  var head = document.querySelector('.row.active .row-head');
  head.focus();
  head.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }));
  setTimeout(function () {
    var after = active();
    var list = ids();
    var focused = document.activeElement && document.activeElement.closest('.row');
    window.chrome.webview.postMessage({
      name: 'probe',
      step: list.indexOf(after) - list.indexOf(before),
      focusFollows: !!focused && focused.dataset.id === after,
      visibility: document.visibilityState,
      canvases: document.querySelectorAll('canvas').length,
      font: getComputedStyle(document.querySelector('.status-headline')).fontFamily
    });
  }, 400);
})();
