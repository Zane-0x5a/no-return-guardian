// 截图模式里的对齐探针：按 STEP 打开对应界面，一秒后把各部件的位置发回宿主（日志里是 command {"name":"probe",...}），
// 宿主随后截图。scripts\diagnostics\measure-align.py 用这些位置在截图里量文字墨迹偏离中线多少。
// 步骤名带 -old 时换回改动前的按钮标签写法，作同一构建里的对照。
(function () {
  var step = 'STEP';
  var view = step.replace(/-old$/, '');
  var q = function (selector) { return document.querySelector(selector); };
  var rect = function (el) { var r = el.getBoundingClientRect(); return [r.left, r.top, r.width, r.height]; };
  var span = function (el) { var r = el.getBoundingClientRect(); return [r.left, r.right]; };
  var button = function (b) {
    var parts = {};
    var icon = b.querySelector('svg');
    var text = b.querySelector(':scope > span');
    if (icon) parts.icon = span(icon);
    if (text) parts.text = span(text);
    return { name: b.textContent || b.getAttribute('aria-label'), box: rect(b), parts: parts };
  };
  if (/-old$/.test(step)) {
    var old = document.createElement('style');
    old.textContent = '.btn > span, .panel-back span { translate: none; }';
    document.head.appendChild(old);
  }
  if (view === 'main' && !q('.hotkeys div')) {
    // 截图模式不注册快捷键，说明那一行是空的；按 Stage 的结构填一份。
    q('.hotkeys').innerHTML = [['Ctrl', 'Alt', 'F9', '结算页恢复'], ['Ctrl', 'Alt', 'F10', '重开遭遇']].map(function (row) {
      return '<div><dt><span class="keys"><kbd>' + row.slice(0, 3).join('</kbd><kbd>') + '</kbd></span></dt><dd>' + row[3] + '</dd></div>';
    }).join('');
  }
  if (view === 'protect') q('.stage .btn-primary').click();
  if (view === 'remove') q('.row.active [aria-label="删除这份快照"]').click();
  if (view === 'native') {
    q('.row.row-ready .row-head').click();
    setTimeout(function () { q('.row.active .row-actions .btn').click(); }, 300);
  }
  if (view === 'settings') q('[aria-label="设置"]').click();
  if (view === 'toast') {
    // 截图模式不执行命令，不会有真实提示条；按同样的结构放一条。
    var toast = document.createElement('div');
    toast.className = 'toast toast-signal';
    toast.textContent = '战备已保护';
    q('.toasts').appendChild(toast);
  }
  setTimeout(function () {
    var items = [];
    if (view === 'main') {
      document.querySelectorAll('.stage .btn, .row.active .row-actions .btn').forEach(function (b) { items.push(button(b)); });
      document.querySelectorAll('.hotkeys div').forEach(function (d) {
        var keys = d.querySelectorAll('kbd');
        var first = keys[0].getBoundingClientRect();
        var last = keys[keys.length - 1].getBoundingClientRect();
        items.push({
          name: 'hotkey ' + d.querySelector('dd').textContent,
          box: [first.left, first.top, last.right - first.left, first.height],
          parts: { label: span(d.querySelector('dd')), key: [first.left + 3, first.right - 3] },
        });
      });
    }
    if (view === 'protect' || view === 'remove' || view === 'native') {
      document.querySelectorAll('.sheet-actions .btn').forEach(function (b) { items.push(button(b)); });
    }
    if (view === 'settings') {
      document.querySelectorAll('.setting').forEach(function (row) {
        var control = row.querySelector('[role=switch], .setting-buttons .btn:not(.btn-icon), .setting-buttons .btn-icon');
        var name = row.querySelector('.setting-name');
        var single = !row.querySelector('.setting-hint');
        items.push({ name: 'setting ' + name.textContent + (single ? ' (single)' : ''), box: rect(control), parts: { name: span(name) } });
      });
      var back = q('.panel-back');
      items.push({ name: 'back', box: rect(back), parts: { icon: span(back.querySelector('svg')), text: span(back.querySelector('span')) } });
    }
    if (view === 'toast') {
      var t = q('.toast');
      var r = t.getBoundingClientRect();
      items.push({ name: 'toast h' + r.height.toFixed(2), box: rect(t), parts: { text: [r.left + 18, r.right - 18] } });
    }
    window.chrome.webview.postMessage({ name: 'probe', step: step, width: window.innerWidth, items: items });
  }, 1000);
})();
