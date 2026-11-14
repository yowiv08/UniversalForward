  const $ = id => document.getElementById(id);
  const form = $('accountForm'), dialog = $('editor');
  const el = (tag, text, cls) => {
    const n = document.createElement(tag);
    if (text !== undefined) n.textContent = text;
    if (cls) n.className = cls;
    return n;
  };
  const option = (value, title) => {
    const n = el('option', title);
    n.value = value;
    return n;
  };
  const button = (text, work, cls = 'secondary') => {
    const b = el('button', text, cls);
    b.type = 'button';
    b.addEventListener('click', work);
    return b;
  };

  const iconPaths = {
    journal: ['M8 3H5a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-3', 'M8 8h7M8 12h5M8 16h4', 'm16 3 5 5m-6 1 6-6'],
    refresh: ['M20 7a8 8 0 0 0-14-2L3 8', 'M3 3v5h5', 'M4 17a8 8 0 0 0 14 2l3-3', 'M16 16h5v5'],
    plus: ['M12 5v14M5 12h14'],
    channels: ['M4 4h6v6H4zM14 4h6v6h-6zM4 14h6v6H4zM14 14h6v6h-6z'],
    activity: ['M3 12h4l3-8 4 16 3-8h4'],
    key: ['M15 3a6 6 0 1 1 0 12 6 6 0 0 1 0-12Z', 'm10.5 13.5-8 8M3 21h4v-4h4', 'M16 7h.01'],
    models: ['m12 3 9 5-9 5-9-5 9-5ZM3 12l9 5 9-5M3 16l9 5 9-5'],
    search: ['M10.5 3a7.5 7.5 0 1 1 0 15 7.5 7.5 0 0 1 0-15Z', 'm16 16 5 5'],
    list: ['M8 6h13M8 12h13M8 18h13M3 6h.01M3 12h.01M3 18h.01'],
    grid: ['M3 3h7v7H3zM14 3h7v7h-7zM3 14h7v7H3zM14 14h7v7h-7z'],
    settings: ['M4 7h7M15 7h5M4 17h3M11 17h9', 'M11 4v6M15 4v6M7 14v6M11 14v6', 'M11 4h4v6h-4ZM7 14h4v6H7Z'],
    mapping: ['M4 6h5a3 3 0 0 1 3 3v6a3 3 0 0 0 3 3h5', 'm17 15 3 3-3 3', 'M4 18h5a3 3 0 0 0 3-3V9a3 3 0 0 1 3-3h5', 'm17 3 3 3-3 3'],
    code: ['m8 6-6 6 6 6m8-12 6 6-6 6m-3-15-2 18'],
    retry: ['M3 8h12a6 6 0 0 1 0 12h-3', 'm7 4-4 4 4 4'],
    info: ['M12 3a9 9 0 1 1 0 18 9 9 0 0 1 0-18Z', 'M12 11v6M12 7h.01'],
    forward: ['M5 5h7a7 7 0 0 1 7 7v6', 'm15 14 4 4 4-4', 'M5 11h4a3 3 0 0 1 3 3v5', 'm8 15 4 4 4-4'],
    'chevron-right': ['m9 5 7 7-7 7'],
    'chevron-up': ['m5 15 7-7 7 7'],
    'chevron-down': ['m5 9 7 7 7-7'],
    'arrow-left': ['M20 12H4', 'm10 6-6 6 6 6'],
    x: ['m6 6 12 12M6 18 18 6'],
    check: ['m5 12 4 4L19 6'],
    more: ['M5 12h.01M12 12h.01M19 12h.01'],
    test: ['M6 3h12M9 3v7l-5 9a1 1 0 0 0 1 2h14a1 1 0 0 0 1-2l-5-9V3', 'M7 15h10'],
    edit: ['m16 3 5 5-12 12-5 1 1-5 11-13Z', 'm14 5 5 5'],
    copy: ['M9 9h12v12H9z', 'M5 15H3V3h12v2'],
    trash: ['M3 6h18M9 6V3h6v3M5 6l1 15h12l1-15M10 10v7M14 10v7'],
    power: ['M12 3v9', 'M7 5a9 9 0 1 0 10 0'],
    filter: ['M4 5h16l-6 7v6l-4 2v-8L4 5Z'],
    ban: ['M12 3a9 9 0 1 1 0 18 9 9 0 0 1 0-18Z', 'm5.7 5.7 12.6 12.6']
  };

  function icon(name) {
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    for (const [attribute, value] of Object.entries({
      viewBox: '0 0 24 24', fill: 'none', stroke: 'currentColor',
      'stroke-width': '1.7', 'stroke-linecap': 'round', 'stroke-linejoin': 'round',
      class: 'ui-icon', 'aria-hidden': 'true', focusable: 'false'
    })) svg.setAttribute(attribute, value);
    for (const d of iconPaths[name] || iconPaths.channels) {
      const path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
      path.setAttribute('d', d);
      svg.append(path);
    }
    return svg;
  }
  for (const placeholder of document.querySelectorAll('[data-icon]')) {
    placeholder.replaceChildren(icon(placeholder.dataset.icon));
  }

  const feedback = (id, text = '', error = false) => {
    const n = $(id);
    if (!n) return;
    n.textContent = text;
    n.hidden = !text;
    n.classList.add('feedback');
    n.classList.toggle('error', error);
  };
  const message = (text, error = false) => feedback('message', text, error);
  const api = (method, path, body) => {
    if (!window.Router2API?.request) return Promise.reject(Error('页面请求桥未就绪，请重新打开插件'));
    return window.Router2API.request(method, path, body);
  };

  const objectJson = (text, label) => {
    let value;
    try { value = JSON.parse(text || '{}'); } catch { throw Error(label + '格式错误，请检查 JSON'); }
    if (!value || Array.isArray(value) || typeof value !== 'object') throw Error(label + '必须是 JSON 对象');
    return value;
  };

  let confirmResolve = null;
  function ask(text, title = '确认操作') {
    if (confirmResolve) return Promise.resolve(false);
    $('confirmTitle').textContent = title;
    $('confirmText').textContent = text;
    $('confirmDialog').showModal();
    return new Promise(resolve => { confirmResolve = resolve; });
  }
  function resolveConfirm(value) {
    const resolve = confirmResolve;
    confirmResolve = null;
    $('confirmDialog').close();
    resolve?.(value);
  }
  $('confirmYes').onclick = () => resolveConfirm(true);
  $('confirmNo').onclick = () => resolveConfirm(false);
  $('confirmDialog').addEventListener('cancel', e => { e.preventDefault(); resolveConfirm(false); });

  async function copyText(text) {
    const active = document.activeElement, selection = window.getSelection();
    const ranges = selection ? Array.from({ length: selection.rangeCount }, (_, i) => selection.getRangeAt(i).cloneRange()) : [];
    const inputSelection = active && /^(INPUT|TEXTAREA)$/.test(active.tagName)
      ? [active.selectionStart, active.selectionEnd, active.selectionDirection] : null;
    const input = el('textarea');
    input.value = text; input.readOnly = true; input.tabIndex = -1;
    input.style.cssText = 'position:fixed;top:0;left:0;width:1px;height:1px;opacity:0;pointer-events:none;font-size:16px';
    ([...document.querySelectorAll('dialog[open]')].at(-1) || document.body).append(input);
    let copied = false;
    try {
      input.focus({ preventScroll: true }); input.select(); input.setSelectionRange(0, text.length);
      copied = document.execCommand('copy');
    } catch { copied = false; }
    finally {
      input.remove(); active?.focus({ preventScroll: true });
      if (selection) { selection.removeAllRanges(); for (const range of ranges) selection.addRange(range); }
      if (inputSelection?.[0] !== null && inputSelection?.[0] !== undefined)
        active.setSelectionRange(...inputSelection);
    }
    if (copied) return;
    try {
      if (!navigator.clipboard?.writeText) throw Error();
      await navigator.clipboard.writeText(text);
    } catch { throw Error('浏览器未允许复制，请检查剪贴板权限后重试'); }
  }
