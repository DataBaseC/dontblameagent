/* ══════════════════════════════════════════════════════════════
   console-kit · 主题应用器
   ──────────────────────────────────────────────────────────────
   职责只有一件：把「本机存的偏好」变成 html 上的 data 属性，
   让 theme.css 里的选择器生效。

   偏好存 localStorage（键前缀 ck.）—— 存本机、不进会话日志：
   界面喜好是这台机器的事，换台电脑本来就该重来一遍。
   ══════════════════════════════════════════════════════════════ */
(function () {
  'use strict';

  var KEY_THEME = 'ck.theme';
  var KEY_READING = 'ck.reading';
  var THEMES = ['default', 'light', 'ink', 'forest'];

  function read() {
    try {
      var theme = localStorage.getItem(KEY_THEME) || 'default';
      return {
        theme: THEMES.indexOf(theme) >= 0 ? theme : 'default',
        reading: localStorage.getItem(KEY_READING) === 'on'
      };
    } catch (e) {
      // 隐私模式等场景下 localStorage 可能不可用 —— 那就退回默认外观，别把界面搞挂
      return { theme: 'default', reading: false };
    }
  }

  function apply(state) {
    var root = document.documentElement;

    if (state.theme && state.theme !== 'default') {
      root.setAttribute('data-ck-theme', state.theme);
    } else {
      root.removeAttribute('data-ck-theme');
    }

    if (state.reading) {
      root.setAttribute('data-ck-reading', 'on');
    } else {
      root.removeAttribute('data-ck-reading');
    }
  }

  function save(partial) {
    var next = Object.assign(read(), partial || {});
    if (THEMES.indexOf(next.theme) < 0) { next.theme = 'default'; }

    try {
      localStorage.setItem(KEY_THEME, next.theme);
      localStorage.setItem(KEY_READING, next.reading ? 'on' : 'off');
    } catch (e) { /* 存不下就只在本次生效 */ }

    apply(next);
    return next;
  }

  // 面板（同源 iframe）通过它读写父窗口的状态 —— 只暴露这两件事，不递 DOM 出去
  window.ConsoleKit = {
    themes: THEMES.slice(),
    read: read,
    apply: apply,
    save: save
  };

  apply(read());
})();
