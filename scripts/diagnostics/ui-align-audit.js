// 界面对齐检查：在页面里运行，返回所有不在同一水平线上的并排元素。
// 按字形墨迹（不是行框）比较：横排 flex/grid 里垂直方向重叠的相邻项，墨迹中线相差 1px 以上就报出来；
// 有底色或边框的按钮、键帽、提示条，文字墨迹偏离框中线 1px 以上也报出来。
// 用法：在预览页执行本文件内容，再调用 window.__alignAudit()。
window.__alignAudit = function () {
  var issues = [];
  var ctx = document.createElement('canvas').getContext('2d');

  var shown = function (el) {
    for (var e = el; e && e !== document.documentElement; e = e.parentElement) {
      var s = getComputedStyle(e);
      if (s.display === 'none' || s.visibility === 'hidden' || parseFloat(s.opacity) < 0.05) return false;
      if (e.classList.contains('preview-badge')) return false;
    }
    var r = el.getBoundingClientRect();
    return r.width > 0.5 && r.height > 0.5;
  };

  var label = function (el) {
    return el.tagName.toLowerCase() + (el.classList.length ? '.' + Array.prototype.join.call(el.classList, '.') : '');
  };

  var path = function (el) {
    var parts = [];
    for (var e = el, i = 0; e && e !== document.body && i < 3; e = e.parentElement, i++) parts.unshift(label(e));
    return parts.join(' > ');
  };

  // 文字第一行的墨迹：行框顶部加主字体的 ascent 得到基线，再用实际字形的上下伸展求墨迹。
  var ink = function (node) {
    var text = node.textContent.replace(/\s+/g, ' ').trim();
    if (!text || !node.parentElement || !shown(node.parentElement)) return null;
    var range = document.createRange();
    range.selectNodeContents(node);
    var rects = Array.prototype.filter.call(range.getClientRects(), function (r) { return r.width > 0.5; });
    if (!rects.length) return null;
    var r = rects[0];
    var s = getComputedStyle(node.parentElement);
    ctx.font = s.fontStyle + ' ' + s.fontWeight + ' ' + s.fontSize + ' ' + s.fontFamily;
    var m = ctx.measureText(text);
    var base = r.top + m.fontBoundingBoxAscent;
    return {
      text: text.slice(0, 24),
      base: base,
      center: base + (m.actualBoundingBoxDescent - m.actualBoundingBoxAscent) / 2,
      top: r.top,
      bottom: r.bottom,
    };
  };

  var firstInk = function (el) {
    var walker = document.createTreeWalker(el, NodeFilter.SHOW_TEXT);
    for (var n = walker.nextNode(); n; n = walker.nextNode()) {
      var k = ink(n);
      if (k) return k;
    }
    return null;
  };

  var item = function (node) {
    if (node.nodeType === 3) {
      var t = ink(node);
      return t && { name: JSON.stringify(t.text), center: t.center, base: t.base, top: t.top, bottom: t.bottom };
    }
    if (node.nodeType !== 1 || !shown(node)) return null;
    var s = getComputedStyle(node);
    if (s.position === 'absolute' || s.position === 'fixed') return null;
    var r = node.getBoundingClientRect();
    var k = firstInk(node);
    return {
      name: label(node) + (k ? ' ' + JSON.stringify(k.text) : ''),
      center: k ? k.center : (r.top + r.bottom) / 2,
      base: k ? k.base : null,
      top: r.top,
      bottom: r.bottom,
    };
  };

  Array.prototype.forEach.call(document.querySelectorAll('body *'), function (el) {
    if (!shown(el)) return;
    var s = getComputedStyle(el);
    var row = s.display.indexOf('flex') >= 0 && s.flexDirection.indexOf('column') !== 0;
    if (!row && s.display.indexOf('grid') < 0) return;
    var items = Array.prototype.map.call(el.childNodes, item).filter(Boolean);
    for (var i = 0; i < items.length; i++) {
      for (var j = i + 1; j < items.length; j++) {
        var a = items[i];
        var b = items[j];
        var overlap = Math.min(a.bottom, b.bottom) - Math.max(a.top, b.top);
        if (overlap < 0.5 * Math.min(a.bottom - a.top, b.bottom - b.top)) continue;
        var d = b.center - a.center;
        if (Math.abs(d) >= 1) {
          issues.push({
            where: path(el),
            a: a.name,
            b: b.name,
            ink: +d.toFixed(2),
            baseline: a.base != null && b.base != null ? +(b.base - a.base).toFixed(2) : null,
          });
        }
      }
    }
  });

  Array.prototype.forEach.call(document.querySelectorAll('button, kbd, .toast, .panel-count'), function (el) {
    if (!shown(el)) return;
    var s = getComputedStyle(el);
    var boxed = (parseFloat(s.borderTopWidth) > 0 && s.borderTopColor !== 'rgba(0, 0, 0, 0)')
      || s.backgroundColor !== 'rgba(0, 0, 0, 0)' || s.backgroundImage !== 'none';
    if (!boxed) return;
    var k = firstInk(el);
    if (!k) return;
    var r = el.getBoundingClientRect();
    var d = k.center - (r.top + r.bottom) / 2;
    if (Math.abs(d) >= 1) issues.push({ where: path(el), inBox: k.text, ink: +d.toFixed(2), height: +r.height.toFixed(1) });
  });

  return issues;
};
