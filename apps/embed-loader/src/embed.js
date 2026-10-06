// smart-agri embed loader. Contract: apps/embed-loader/README.md
(function (w, d) {
  'use strict';
  const flags = /** @type {{ __smartagriEmbed?: boolean }} */ (w);
  if (flags.__smartagriEmbed) return;

  let script = d.currentScript;
  if (!(script instanceof HTMLScriptElement && script.dataset.assistant)) {
    script = null;
    const all = d.querySelectorAll('script[data-assistant]');
    for (let i = 0; i < all.length; i++) {
      const s = /** @type {HTMLScriptElement} */ (all[i]);
      if (/\/embed\.js$/.test(s.src.split(/[?#]/)[0])) {
        script = s;
        break;
      }
    }
  }
  const id = script && script.dataset.assistant;
  if (!script || !id) return;
  flags.__smartagriEmbed = true;

  const url = new URL(script.src, location.href);
  const base = url.origin + url.pathname.replace(/\/embed\.js$/, '');
  const left = script.dataset.position === 'left';

  const css =
    '.smartagri-root,.smartagri-launcher,.smartagri-frame{all:initial;box-sizing:border-box}' +
    '.smartagri-root{position:fixed;bottom:20px;' + (left ? 'left' : 'right') + ':20px;z-index:2147483000;' +
    'font-family:system-ui,sans-serif}' +
    '.smartagri-launcher{display:flex;align-items:center;justify-content:center;width:56px;height:56px;' +
    'border-radius:50%;background:#1f6f5c;color:#fff;cursor:pointer;box-shadow:0 4px 14px rgba(0,0,0,.3)}' +
    '.smartagri-launcher:focus-visible{outline:3px solid #fff;box-shadow:0 0 0 6px #1f6f5c}' +
    '.smartagri-launcher svg{display:block;width:26px;height:26px;fill:none;stroke:currentColor;stroke-width:2;' +
    'stroke-linecap:round;stroke-linejoin:round}' +
    '.smartagri-launcher .smartagri-x{display:none}' +
    '.smartagri-open .smartagri-launcher .smartagri-x{display:block}' +
    '.smartagri-open .smartagri-launcher .smartagri-chat{display:none}' +
    '.smartagri-frame{display:none;position:absolute;bottom:68px;' + (left ? 'left' : 'right') + ':0;' +
    'width:380px;height:600px;max-height:calc(100vh - 108px);border:0;border-radius:12px;' +
    'background:#fff;box-shadow:0 8px 30px rgba(0,0,0,.3);color-scheme:light}' +
    '.smartagri-open .smartagri-frame{display:block}' +
    '@media (max-width:480px){' +
    '.smartagri-open .smartagri-frame{position:fixed;inset:0;width:100%;height:100%;max-height:none;border-radius:0}' +
    '.smartagri-open .smartagri-launcher{display:none}}';

  function init() {
    const style = d.createElement('style');
    style.id = 'smartagri-style';
    style.textContent = css;
    d.head.appendChild(style);

    const root = d.createElement('div');
    root.id = 'smartagri-embed';
    root.className = 'smartagri-root';

    const btn = d.createElement('button');
    btn.id = 'smartagri-launcher';
    btn.className = 'smartagri-launcher';
    btn.type = 'button';
    btn.setAttribute('aria-label', '開啟客服對話');
    btn.setAttribute('aria-expanded', 'false');
    btn.setAttribute('aria-controls', 'smartagri-frame');
    btn.innerHTML =
      '<svg aria-hidden="true" viewBox="0 0 24 24"><path class="smartagri-chat" d="M21 12a8 8 0 0 1-11.6 7.1L4 20l1-4.6A8 8 0 1 1 21 12z"/>' +
      '<path class="smartagri-x" d="M6 6l12 12M18 6L6 18"/></svg>';
    root.appendChild(btn);
    d.body.appendChild(root);

    /** @type {HTMLIFrameElement | null} */
    let frame = null;
    let open = false;

    /** @param {boolean} next @param {boolean} [focusButton] */
    function setOpen(next, focusButton) {
      open = next;
      if (open && !frame) {
        frame = d.createElement('iframe');
        frame.id = 'smartagri-frame';
        frame.className = 'smartagri-frame';
        frame.title = '客服對話';
        frame.src = base + '/use/' + encodeURIComponent(id || '') + '?host=' + encodeURIComponent(location.origin);
        root.insertBefore(frame, btn);
      }
      root.classList.toggle('smartagri-open', open);
      btn.setAttribute('aria-expanded', String(open));
      if (open && frame) frame.focus();
      if (!open && focusButton) btn.focus();
    }

    btn.addEventListener('click', function () {
      setOpen(!open, true);
    });
    d.addEventListener('keydown', function (e) {
      if (open && e.key === 'Escape') setOpen(false, true);
    });
    w.addEventListener('message', function (e) {
      const data = e.data;
      if (e.origin !== url.origin || !frame || e.source !== frame.contentWindow) return;
      if (data && data.type === 'smartagri:close') setOpen(false, true);
    });
  }

  if (d.body) init();
  else d.addEventListener('DOMContentLoaded', init);
})(window, document);
