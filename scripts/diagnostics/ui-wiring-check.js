(function () {
  var q = function (selector) { return document.querySelector(selector); };
  var steps = [
    function () { q('[aria-label="设置"]').click(); },
    function () { q('[role=switch][aria-label="开机启动"]').click(); },
    function () { q('.panel-back').click(); },
    function () { q('.stage .btn-primary').click(); },
    function () { q('.sheet .btn-primary').click(); },
    function () { q('.row:not(.active) .row-head').click(); },
    function () { q('.row.active [aria-label="删除这份快照"]').click(); },
    function () { q('.sheet .btn-danger-solid').click(); },
    function () { q('[aria-label="最小化"]').click(); }
  ];
  steps.forEach(function (step, index) { setTimeout(step, 150 * index); });
})();
