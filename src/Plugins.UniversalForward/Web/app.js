(() => {
  'use strict';
  const $ = id => document.getElementById(id);
  const form = $('accountForm'), dialog = $('editor');
  const el = (tag, text, cls) => {
    const n = document.createElement(tag);
    if (text !== undefined) n.textContent = text;
    if (cls) n.className = cls;
    return n;
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
    panel: ['M3 4h18v16H3z', 'M9 4v16'],
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
    filter: ['M4 5h16l-6 7v6l-4 2v-8L4 5Z']
  };
  function icon(name) {
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    for (const [attribute, value] of Object.entries({ viewBox: '0 0 24 24', fill: 'none', stroke: 'currentColor',
      'stroke-width': '1.7', 'stroke-linecap': 'round', 'stroke-linejoin': 'round', class: 'ui-icon', 'aria-hidden': 'true', focusable: 'false' }))
      svg.setAttribute(attribute, value);
    for (const d of iconPaths[name] || iconPaths.channels) {
      const path = document.createElementNS('http://www.w3.org/2000/svg', 'path'); path.setAttribute('d', d); svg.append(path);
    }
    return svg;
  }
  for (const placeholder of document.querySelectorAll('[data-icon]')) placeholder.replaceChildren(icon(placeholder.dataset.icon));
  const feedback = (id, text = '', error = false) => {
    const n = $(id); n.textContent = text; n.hidden = !text;
    n.classList.add('feedback'); n.classList.toggle('error', error);
  };
  const message = (text, error = false) => feedback('message', text, error);
  const api = (method, path, body) => {
    if (!window.Router2API?.request) return Promise.reject(Error('页面请求桥未就绪，请重新打开插件'));
    return window.Router2API.request(method, path, body);
  };
  const keyModes = { roundRobin: '轮询', random: '随机', priority: '主备顺序' };
  const defaultKeyName = secret => (secret || '').trim().slice(-4);
  const automaticKeyName = name => !name?.trim() || /^key\s*\d+$/i.test(name.trim()) || name.trim() === '默认 Key';
  const effectiveKeySecret = key => key.secret.trim() || key.originalSecret || '';
  const keySecretChanged = key => !key.id || !!key.secret.trim() && key.secret.trim() !== key.originalSecret;
  function updateResponseRetry() {
    $('responseRetrySettings').hidden = !$('rateLimitRetryEnabled').checked && !$('emptyResponseRetryEnabled').checked;
  }
  for (const name of ['rateLimitRetryEnabled', 'emptyResponseRetryEnabled']) $(name).addEventListener('change', updateResponseRetry);
  const protocolLabels = { responses: 'OpenAI Responses', messages: 'Anthropic Messages' };
  const paths = ['/v1/responses', '/v1/messages'];
  const defaults = { maxRetries: 0, headerTimeoutSeconds: 60, totalTimeoutSeconds: 180, streamIdleTimeoutSeconds: 60,
    responseMaxRetries: 3, responseRetryIntervalSeconds: 5,
    retryStatusCodes: '100-199,300-399,401-407,409-499,500-503,505-523,525-599' };
  const templates = {
    codex: {
      'Content-Type': 'application/json', Accept: 'text/event-stream',
      'User-Agent': 'Codex Desktop/0.162.0-alpha.2 (Windows 10.0.26200; x86_64) unknown (Codex Desktop; 26.1002.52244)',
      Originator: 'codex_exec', 'X-Codex-Beta-Features': 'remote_compaction_v2',
      'X-OpenAI-Internal-Codex-Responses-Lite': 'true'
    },
    claude: {
      'Content-Type': 'application/json', 'User-Agent': 'claude-cli/2.1.161 (external, cli)',
      Accept: 'application/json', 'anthropic-version': '2023-06-01', 'x-app': 'cli',
      'x-claude-code-session-id': '{session_id}', 'anthropic-dangerous-direct-browser-access': 'true',
      'anthropic-beta': 'claude-code-20250219,context-1m-2025-08-07,interleaved-thinking-2025-05-14,thinking-token-count-2026-05-13,context-management-2025-06-27,prompt-caching-scope-2026-01-05,mid-conversation-system-2026-04-07,effort-2025-11-24,fallback-credit-2026-06-01'
    }
  };
  let accounts = [], candidates = [], keyDraft = [], deletedKeyIds = [], keyRevision = 0, keySequence = 0;
  let modelProtocolDraft = {}, extraParams = {}, savedAccount = null, editorEpoch = 0;
  let headerMode = 'visual', mappingMode = 'visual', headerRows = [], mappingRows = [];
  let headerPolicyMode = 'shared', headerScope = 'common', commonHeaderDraft = {};
  let endpointHeaderDraft = Object.create(null);
  let confirmResolve = null, loading = false, saving = false;
  const switchingChannels = new Set();
  const button = (text, work, cls = 'secondary') => {
    const b = el('button', text, cls); b.type = 'button'; b.addEventListener('click', work); return b;
  };
  let workspacePage = 'channels';
  const smallNavigation = matchMedia('(max-width: 900px)');
  function syncNavigation() {
    const expanded = smallNavigation.matches ? document.body.classList.contains('sidebar-open') : !document.body.classList.contains('sidebar-collapsed');
    $('navToggle').setAttribute('aria-expanded', String(expanded));
    $('sidebarOverlay').hidden = !smallNavigation.matches || !expanded;
    $('appSidebar').inert = smallNavigation.matches && !expanded;
    document.querySelector('.app-main').inert = smallNavigation.matches && expanded;
  }
  function closeNavigation() {
    const restoreFocus = smallNavigation.matches && document.body.classList.contains('sidebar-open') && $('appSidebar').contains(document.activeElement);
    document.body.classList.remove('sidebar-open'); syncNavigation();
    if (restoreFocus) $('navToggle').focus();
  }
  $('navToggle').onclick = () => {
    document.body.classList.toggle(smallNavigation.matches ? 'sidebar-open' : 'sidebar-collapsed'); syncNavigation();
    if (smallNavigation.matches && document.body.classList.contains('sidebar-open')) $('appSidebar').querySelector('[aria-current=page]')?.focus();
  };
  $('sidebarOverlay').onclick = closeNavigation;
  smallNavigation.addEventListener('change', closeNavigation);
  document.addEventListener('keydown', event => {
    if (event.key === 'Escape' && document.body.classList.contains('sidebar-open')) { closeNavigation(); $('navToggle').focus(); }
    else if (event.key === 'Tab' && smallNavigation.matches && document.body.classList.contains('sidebar-open')) {
      const items = [...$('appSidebar').querySelectorAll('button:not(:disabled)')];
      const current = items.indexOf(document.activeElement);
      if (event.shiftKey && current <= 0) { event.preventDefault(); items.at(-1)?.focus(); }
      else if (!event.shiftKey && (current < 0 || current === items.length - 1)) { event.preventDefault(); items[0]?.focus(); }
    }
  });
  function setWorkspacePage(page) {
    closeChannelMenu();
    workspacePage = page;
    $('channelsWorkspace').hidden = page !== 'channels';
    $('journal').hidden = page !== 'journal';
    $('workspaceTitle').textContent = page === 'journal' ? '请求日志' : '渠道管理';
    for (const [id, active] of [['showChannels', page === 'channels'], ['openJournal', page === 'journal']]) {
      if (active) $(id).setAttribute('aria-current', 'page'); else $(id).removeAttribute('aria-current');
    }
    closeNavigation();
    window.scrollTo({ top: 0 });
  }
  $('showChannels').onclick = () => { closeJournal(); $('statusFilter').value = 'all'; render(); };
  for (const [id, state] of [['showEnabled', 'enabled'], ['showDisabled', 'disabled']]) $(id).onclick = () => {
    closeJournal(); $('statusFilter').value = state; render();
  };
  syncNavigation();

  // A single viewport-positioned menu stays usable inside card grids and scrolled lists.
  let activeChannelMenu = null;
  function closeChannelMenu(restoreFocus = false) {
    if (!activeChannelMenu) return;
    const { menu, trigger } = activeChannelMenu; activeChannelMenu = null;
    trigger.setAttribute('aria-expanded', 'false'); menu.hidden = true;
    if (restoreFocus && trigger.isConnected) trigger.focus();
  }
  function openChannelMenu(menu, trigger) {
    const wasOpen = activeChannelMenu?.menu === menu; closeChannelMenu();
    if (wasOpen) return;
    menu.hidden = false; trigger.setAttribute('aria-expanded', 'true'); activeChannelMenu = { menu, trigger };
    const bounds = trigger.getBoundingClientRect(), size = menu.getBoundingClientRect();
    menu.style.left = Math.max(8, Math.min(bounds.right - size.width, innerWidth - size.width - 8)) + 'px';
    menu.style.top = (bounds.bottom + size.height + 8 <= innerHeight ? bounds.bottom + 6 : Math.max(8, bounds.top - size.height - 6)) + 'px';
    menu.querySelector('button:not(:disabled)')?.focus();
  }
  document.addEventListener('pointerdown', event => {
    if (activeChannelMenu && !activeChannelMenu.menu.contains(event.target) && !activeChannelMenu.trigger.contains(event.target)) closeChannelMenu();
  });
  document.addEventListener('keydown', event => {
    if (!activeChannelMenu) return;
    if (event.key === 'Escape') { event.preventDefault(); closeChannelMenu(true); }
    else if (event.key === 'Tab') closeChannelMenu();
    else if (['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key)) {
      event.preventDefault();
      const items = [...activeChannelMenu.menu.querySelectorAll('button:not(:disabled)')], current = items.indexOf(document.activeElement);
      const index = event.key === 'Home' ? 0 : event.key === 'End' ? items.length - 1 : (current + (event.key === 'ArrowDown' ? 1 : -1) + items.length) % items.length;
      items[index]?.focus();
    }
  });
  window.addEventListener('resize', () => closeChannelMenu());
  document.addEventListener('scroll', () => closeChannelMenu(), true);
  // Only explicit selections are remembered; the previous release also stored its initial list default.
  const channelViewPreference = 'universalforward.channelViewPreference';
  let channelView = 'grid';
  try { if (localStorage.getItem(channelViewPreference) === 'list') channelView = 'list'; } catch { /* Sandboxed pages may not have storage. */ }
  function selectChannelView(view, remember = true) {
    channelView = view;
    $('cards').dataset.view = view;
    $('channelColumns').hidden = view !== 'list';
    $('viewList').setAttribute('aria-pressed', String(view === 'list'));
    $('viewGrid').setAttribute('aria-pressed', String(view === 'grid'));
    if (remember) try { localStorage.setItem(channelViewPreference, view); } catch { /* The view still works without persistence. */ }
  }
  $('viewList').onclick = () => selectChannelView('list');
  $('viewGrid').onclick = () => selectChannelView('grid');
  selectChannelView(channelView, false);
  const option = (value, title) => { const n = el('option', title); n.value = value; return n; };
  let proxyCatalog = null, proxyCatalogError = '', proxyLoading = false, proxyRequest = 0;
  let proxySourcesDraft = [], proxyIdDraft = '';
  const sameId = (left, right) => String(left || '').toLowerCase() === String(right || '').toLowerCase();
  const uniqueIds = values => [...new Map((values || []).filter(x => typeof x === 'string' && x.trim())
    .map(x => [x.trim().toLowerCase(), x.trim()])).values()];
  function readNetwork(validate = true) {
    const networkMode = form.elements.networkMode.value, scope = $('proxyScope').value;
    const proxySubscriptionIds = scope === 'manual' ? ['manual']
      : scope === 'subscriptions' || scope === 'node' ? uniqueIds(proxySourcesDraft) : [];
    const proxyId = scope === 'node' ? proxyIdDraft.trim() : '';
    if (validate && networkMode === 'proxyPool') {
      if (scope === 'subscriptions' && !proxySubscriptionIds.length) throw Error('请选择至少一个订阅来源，或使用全部可用代理');
      if (scope === 'node' && !proxyId) throw Error('请选择固定代理节点');
    }
    return { networkMode, proxySubscriptionIds, proxyId: proxyId || null };
  }
  function networkChanged(account) {
    const draft = readNetwork(false);
    const sources = values => uniqueIds(values).map(x => x.toLowerCase()).sort();
    return draft.networkMode !== (account.networkMode || 'direct') ||
      !sameId(draft.proxyId, account.proxyId) ||
      JSON.stringify(sources(draft.proxySubscriptionIds)) !== JSON.stringify(sources(account.proxySubscriptionIds));
  }
  function renderProxySettings() {
    const pool = form.elements.networkMode.value === 'proxyPool', scope = $('proxyScope').value;
    $('proxySettings').hidden = !pool;
    $('refreshProxies').disabled = proxyLoading;
    $('proxySubscriptionsField').hidden = !['subscriptions', 'node'].includes(scope);
    $('proxyNodeField').hidden = scope !== 'node';
    $('proxySubscriptionsLabel').textContent = scope === 'node' ? '节点来源（可选，留空表示全部来源）' : '订阅来源';
    const sources = proxyCatalog?.subscriptions || [];
    const root = $('proxySubscriptions'); root.replaceChildren();
    const entries = [...sources];
    if (!entries.some(x => sameId(x.id, 'manual'))) entries.push({ id: 'manual', name: '手动节点', enabled: true });
    for (const id of proxySourcesDraft) if (!entries.some(x => sameId(x.id, id)))
      entries.push({ id, name: id, enabled: false, missing: true });
    for (const source of entries) {
      const label = el('label', undefined, 'proxy-source'), input = el('input'); input.type = 'checkbox'; input.value = source.id;
      input.checked = proxySourcesDraft.some(id => sameId(id, source.id));
      const state = source.missing ? '（目录中不存在）' : source.enabled ? '' : '（已禁用）';
      label.append(input, el('span', (source.name || source.id) + state));
      input.onchange = () => {
        proxySourcesDraft = input.checked ? uniqueIds([...proxySourcesDraft, source.id])
          : proxySourcesDraft.filter(id => !sameId(id, source.id));
        renderProxySettings();
      };
      root.append(label);
    }
    const nodes = proxyCatalog?.nodes || [];
    const filtered = nodes.filter(node => !proxySourcesDraft.length || proxySourcesDraft.some(id => sameId(id, node.subscriptionId)));
    const selectedNode = nodes.find(node => sameId(node.id, proxyIdDraft));
    const select = $('proxyNode'); select.replaceChildren(option('', proxyLoading ? '正在读取代理目录…' : '请选择节点'));
    for (const node of filtered) {
      const source = sources.find(x => sameId(x.id, node.subscriptionId));
      const state = node.available ? '' : ` · 不可用（${node.state || '未知状态'}）`;
      select.append(option(node.id, `${node.address} · ${source?.name || node.subscriptionId}${state}`));
    }
    if (proxyIdDraft && !filtered.some(node => sameId(node.id, proxyIdDraft))) {
      const title = selectedNode ? `${selectedNode.address} · 不在当前来源范围内`
        : `${proxyIdDraft} · ${proxyCatalog ? '节点已删除或不在目录中' : '待核对目录'}`;
      select.append(option(proxyIdDraft, title));
    }
    select.value = [...select.options].find(o => sameId(o.value, proxyIdDraft))?.value || '';
    let hint = scope === 'all' ? '使用全部启用订阅及手动节点中的可用代理。'
      : scope === 'manual' ? '只使用手动添加的可用节点，不混入订阅节点。'
      : scope === 'subscriptions' ? '只使用勾选来源中的可用节点。' : '每次请求固定使用指定节点。';
    let warning = false;
    if (scope === 'node' && proxyIdDraft && proxyCatalog &&
        (!selectedNode || !selectedNode.available || !filtered.some(node => sameId(node.id, proxyIdDraft)))) {
      hint = '已保留当前节点选择，但该节点已失效、不可用或不在所选来源内。请求会报错，请检查选择。'; warning = true;
    } else if (scope === 'subscriptions' && proxySourcesDraft.some(id => !entries.find(x => sameId(x.id, id))?.enabled)) {
      hint = '已保留禁用或失效的订阅来源；这些来源不会提供可用节点。'; warning = true;
    }
    $('proxySelectionHint').textContent = hint;
    $('proxySelectionHint').className = 'proxy-hint' + (warning ? ' is-warning' : '');
    feedback('proxyCatalogStatus', proxyLoading ? '正在读取宿主代理目录…' : proxyCatalogError ||
      (proxyCatalog ? `目录中有 ${nodes.length} 个节点，${nodes.filter(x => x.available).length} 个当前可用。` : ''), !!proxyCatalogError);
  }
  async function loadProxyCatalog() {
    const epoch = editorEpoch, request = ++proxyRequest;
    proxyLoading = true; proxyCatalogError = ''; renderProxySettings();
    try {
      const data = await api('GET', 'network/proxies');
      if (epoch !== editorEpoch || request !== proxyRequest || !dialog.open) return;
      proxyCatalog = { subscriptions: Array.isArray(data.subscriptions) ? data.subscriptions : [], nodes: Array.isArray(data.nodes) ? data.nodes : [] };
    } catch (error) {
      if (epoch !== editorEpoch || request !== proxyRequest || !dialog.open) return;
      proxyCatalogError = '无法读取代理目录：' + error.message + '。已有选择会保留，可重试刷新。';
    } finally {
      if (epoch === editorEpoch && request === proxyRequest && dialog.open) { proxyLoading = false; renderProxySettings(); }
    }
  }
  $('networkMode').onchange = renderProxySettings;
  $('proxyScope').onchange = renderProxySettings;
  $('proxyNode').onchange = () => { proxyIdDraft = $('proxyNode').value; renderProxySettings(); };
  $('refreshProxies').onclick = loadProxyCatalog;
  const modelIds = () => [...new Set(form.elements.models.value.split(/\r?\n/).map(x => x.trim()).filter(Boolean))];
  const objectJson = (text, label) => {
    let value;
    try { value = JSON.parse(text || '{}'); } catch { throw Error(label + '格式错误，请检查 JSON'); }
    if (!value || Array.isArray(value) || typeof value !== 'object') throw Error(label + '必须是 JSON 对象');
    return value;
  };
  function ask(text, title = '确认操作') {
    if (confirmResolve) return Promise.resolve(false);
    $('confirmTitle').textContent = title; $('confirmText').textContent = text;
    $('confirmDialog').showModal();
    return new Promise(resolve => { confirmResolve = resolve; });
  }
  function resolveConfirm(value) {
    const resolve = confirmResolve; confirmResolve = null;
    $('confirmDialog').close(); resolve?.(value);
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

  function selectTab(name, focus = false) {
    closeEffortPicker();
    document.querySelector('.editor-nav').setAttribute('aria-orientation', matchMedia('(max-width: 600px)').matches ? 'horizontal' : 'vertical');
    for (const tab of document.querySelectorAll('[data-tab]')) {
      const selected = tab.dataset.tab === name;
      tab.setAttribute('aria-selected', String(selected)); tab.tabIndex = selected ? 0 : -1;
      $('panel-' + tab.dataset.tab).hidden = !selected;
      if (selected) {
        if (focus) tab.focus();
        tab.scrollIntoView({ block: 'nearest', inline: 'nearest' });
      }
    }
    document.querySelector('.editor-content').scrollTop = 0;
  }
  for (const tab of document.querySelectorAll('[data-tab]')) {
    tab.onclick = () => selectTab(tab.dataset.tab);
    tab.onkeydown = e => {
      const tabs = [...document.querySelectorAll('[data-tab]')], index = tabs.indexOf(tab);
      if (!['ArrowRight', 'ArrowLeft', 'ArrowDown', 'ArrowUp', 'Home', 'End'].includes(e.key)) return;
      e.preventDefault();
      const next = e.key === 'Home' ? 0 : e.key === 'End' ? tabs.length - 1
        : (index + (['ArrowRight', 'ArrowDown'].includes(e.key) ? 1 : -1) + tabs.length) % tabs.length;
      selectTab(tabs[next].dataset.tab, true);
    };
  }
  function fail(text, tab, target) {
    selectTab(tab); if (target) $(target)?.focus(); throw Error(text);
  }
  function summary() {
    $('editorSummary').textContent = `${keyDraft.filter(k => k.enabled).length} 个启用 Key · ${modelIds().length} 个模型`;
  }
  function render() {
    closeChannelMenu();
    $('metricChannels').textContent = accounts.length;
    $('metricEnabled').textContent = accounts.filter(a => a.enabled).length;
    $('metricKeys').textContent = accounts.reduce((sum, a) => sum + (a.keys || []).filter(k => k.enabled).length, 0);
    $('metricModels').textContent = new Set(accounts.flatMap(a => a.models || [])).size;
    $('navChannelCount').textContent = accounts.length;
    $('navEnabledCount').textContent = accounts.filter(a => a.enabled).length;
    $('navDisabledCount').textContent = accounts.filter(a => !a.enabled).length;
    const q = $('filter').value.toLowerCase(), status = $('statusFilter').value;
    $('showEnabled').setAttribute('aria-pressed', String(status === 'enabled'));
    $('showDisabled').setAttribute('aria-pressed', String(status === 'disabled'));
    const visible = accounts.filter(a => [a.label, a.baseUrl, ...(a.models || [])].join(' ').toLowerCase().includes(q)
      && (status === 'all' || !!a.enabled === (status === 'enabled')));
    $('channelCount').textContent = visible.length;
    $('channelResult').textContent = q || status !== 'all' ? `显示 ${visible.length} / ${accounts.length} 个渠道` : `共 ${accounts.length} 个渠道`;
    const root = $('cards'); root.replaceChildren();
    if (!visible.length) {
      const empty = el('div', undefined, 'empty');
      empty.append(icon(accounts.length ? 'search' : 'channels'), el('h3', accounts.length ? '没有匹配的渠道' : '添加你的第一个渠道'),
        el('p', accounts.length ? '尝试其他关键词，或重置筛选条件。' : '填写上游地址和密钥，然后选择需要转发的模型。'));
      empty.append(accounts.length ? button('重置筛选', () => { $('filter').value = ''; $('statusFilter').value = 'all'; render(); })
        : button('添加渠道', () => open(null), 'primary'));
      root.append(empty); return;
    }
    for (const a of visible) {
      const card = el('article', undefined, 'channel-card'), top = el('div', undefined, 'channel-top');
      card.dataset.enabled = String(!!a.enabled);
      card.setAttribute('aria-busy', String(switchingChannels.has(a.id)));
      const identity = el('div', undefined, 'channel-identity');
      const title = el('div', undefined, 'channel-title');
      const name = button(a.label, () => open(a), 'channel-name-button');
      name.setAttribute('aria-label', `编辑渠道「${a.label}」`);
      const heading = el('h3'); heading.append(name); title.append(heading);
      const monogram = el('span', [...(a.label?.trim() || 'U')][0].toUpperCase(), 'channel-monogram'); monogram.setAttribute('aria-hidden', 'true');
      top.append(monogram, title);
      const address = el('p', a.baseUrl, 'channel-url');
      address.title = a.baseUrl;
      const keys = el('div', undefined, 'channel-keys'), keyCount = el('div', undefined, 'key-count');
      keyCount.append(icon('key'), el('strong', String((a.keys || []).filter(k => k.enabled).length)), el('span', `/ ${a.keys?.length || 0} 启用`, 'muted'));
      keys.append(keyCount, el('small', keyModes[a.keySelectionMode] || '轮询'));
      const meta = el('div', undefined, 'channel-meta'), network = el('div', undefined, 'channel-network');
      network.append(el('span', a.networkMode === 'proxyPool' ? '代理池' : '直连', 'route-label'), el('span', a.responsesTransport === 'websocket' ? 'WS' : 'HTTP', 'transport-label'));
      meta.append(network, el('small', `权重 ${a.weight ?? 100}`));
      const tags = el('div', undefined, 'model-tags');
      for (const model of (a.models || []).slice(0, 3)) { const tag = el('span', model, 'model-tag'); tag.title = model; tags.append(tag); }
      if (a.models?.length > 3) tags.append(el('span', `+${a.models.length - 3}`, 'model-tag'));
      const endpoints = el('div', undefined, 'channel-endpoints');
      for (const endpoint of a.endpoints || []) endpoints.append(el('span', endpoint.replace(/^\/v1\//, ''), 'endpoint-tag'));
      const modelHeading = el('div', undefined, 'between channel-model-heading');
      const modelCount = button('', () => { open(a); selectTab('models'); }, 'model-count-button');
      modelCount.append(el('strong', `${a.models?.length || 0} 个模型`), icon('chevron-right')); modelCount.setAttribute('aria-label', `配置「${a.label}」的 ${a.models?.length || 0} 个模型`);
      modelHeading.append(modelCount);
      if (!a.models?.length) tags.append(el('span', '暂无模型', 'muted'));
      const models = el('div', undefined, 'channel-models'); models.append(modelHeading, tags);
      const actions = el('div', undefined, 'channel-actions');
      const toggle = button(switchingChannels.has(a.id) ? '处理中…' : a.enabled ? '禁用' : '启用', () => setChannelEnabled(a), 'channel-toggle-button ' + (a.enabled ? 'secondary' : 'primary'));
      toggle.prepend(icon('power'));
      toggle.setAttribute('aria-label', `${a.enabled ? '禁用' : '启用'}渠道「${a.label}」`);
      toggle.setAttribute('aria-busy', String(switchingChannels.has(a.id)));
      const edit = button('编辑', () => open(a), 'text-button channel-edit-button'), test = button('测试连接', () => openTests(a), 'channel-test-button');
      test.prepend(icon('test'));
      const trigger = button('', () => openChannelMenu(menu, trigger), 'icon-button channel-menu-trigger'); trigger.append(icon(switchingChannels.has(a.id) ? 'refresh' : 'more'));
      trigger.setAttribute('aria-label', `更多操作「${a.label}」`); trigger.setAttribute('aria-haspopup', 'menu'); trigger.setAttribute('aria-expanded', 'false');
      const menu = el('div', undefined, 'channel-menu'); menu.hidden = true; menu.setAttribute('role', 'menu'); menu.setAttribute('aria-label', `渠道「${a.label}」操作`);
      const log = button('日志', () => { closeChannelMenu(); openJournal(a.id); }, 'menu-item'); log.prepend(icon('journal'));
      const copy = button('复制', () => { closeChannelMenu(true); open(a, { duplicate: true }); }, 'menu-item'); copy.prepend(icon('copy'));
      const remove = button('删除', async () => {
          closeChannelMenu();
          if (!await ask(`删除渠道「${a.label}」及其配置？`, '删除渠道')) return;
          try { await api('POST', 'accounts/delete', { id: a.id }); await load(); message('渠道已删除'); }
          catch (e) { message(e.message, true); }
        }, 'menu-item danger'); remove.prepend(icon('trash'));
      for (const item of [log, copy, remove]) item.setAttribute('role', 'menuitem');
      menu.append(log, copy, el('div', undefined, 'menu-separator'), remove);
      actions.append(edit, test, toggle, trigger, menu);
      if (switchingChannels.has(a.id)) for (const control of [...actions.querySelectorAll('button'), modelCount]) control.disabled = true;
      const statusLine = el('div', undefined, 'channel-status-line'); statusLine.append(el('span', a.enabled ? '已启用' : '已禁用', 'badge' + (a.enabled ? '' : ' off')), endpoints);
      title.append(address); identity.append(top, statusLine);
      card.append(identity, models, keys, meta, actions); root.append(card);
    }
  }
  async function setChannelEnabled(account) {
    if (switchingChannels.has(account.id)) return;
    const enabled = !account.enabled;
    switchingChannels.add(account.id); render();
    try {
      const data = await api('POST', 'accounts/enabled', { id: account.id, enabled });
      accounts = accounts.map(a => a.id === account.id ? data.account : a);
      message(`渠道「${account.label}」已${enabled ? '启用' : '禁用'}`);
    } catch (e) { message(e.message, true); }
    finally { switchingChannels.delete(account.id); render(); }
  }
  async function load() {
    if (loading) return;
    loading = true; $('reload').disabled = true; $('cards').setAttribute('aria-busy', 'true'); $('channelResult').textContent = '正在读取渠道…';
    try { const data = await api('GET', 'accounts'); accounts = data.accounts || []; render(); }
    catch (e) { message(e.message, true); $('channelResult').textContent = '读取失败，请刷新重试';
      if (!accounts.length) { const empty = el('div', undefined, 'empty'); empty.append(el('h3', '渠道加载失败'), el('p', '请检查连接后重新加载。'), button('重新加载', load)); $('cards').replaceChildren(empty); }
    }
    finally { loading = false; $('reload').disabled = false; $('cards').setAttribute('aria-busy', 'false'); }
  }
  function duplicateChannelName(label) {
    const base = label?.trim() || '渠道', limit = form.elements.label.maxLength;
    const existing = new Set(accounts.map(account => (account.label || '').trim().toLowerCase()));
    for (let index = 1; ; index++) {
      const suffix = ' - 副本' + (index === 1 ? '' : ` ${index}`);
      const prefix = base.slice(0, limit - suffix.length).replace(/[\uD800-\uDBFF]$/, '').trimEnd();
      const name = prefix + suffix;
      if (!existing.has(name.toLowerCase())) return name;
    }
  }
  function renderSystemPrompt() {
    const mode = form.elements.systemPromptMode.value;
    const hints = {
      off: '当前不改写系统提示词，下面的文字会保留；选择追加或替换后可编辑。',
      append: '保留原系统提示词，在后面补充你填写的内容。',
      replace: '使用下方内容覆盖原系统提示词；Claude Code 模板会保留必要的客户端身份声明。'
    };
    $('systemPromptText').readOnly = mode === 'off';
    $('systemPromptModeHint').textContent = hints[mode];
    $('systemPromptCount').textContent = `${$('systemPromptText').value.length.toLocaleString('en-US')} / 32,000 字符`;
  }
  function loadSystemPrompt(policy) {
    form.elements.systemPromptMode.value = policy?.mode || 'off';
    $('systemPromptText').value = policy?.text || '';
    renderSystemPrompt();
  }
  function readSystemPrompt() {
    const mode = form.elements.systemPromptMode.value, text = $('systemPromptText').value;
    if (!['off', 'append', 'replace'].includes(mode)) fail('请选择系统提示词处理方式', 'prompt');
    if (text.length > 32000) fail('系统提示词不能超过 32,000 字符', 'prompt', 'systemPromptText');
    if (mode !== 'off' && !text.trim()) fail('追加或替换时，请填写系统提示词', 'prompt', 'systemPromptText');
    return { mode, text };
  }
  for (const input of form.querySelectorAll('input[name=systemPromptMode]')) input.addEventListener('change', renderSystemPrompt);
  $('systemPromptText').addEventListener('input', renderSystemPrompt);
  function open(account, { duplicate = false } = {}) {
    const sourceLabel = account?.label || '';
    if (duplicate) {
      account = structuredClone(account);
      account.id = ''; account.label = duplicateChannelName(sourceLabel); account.enabled = false; account.keyRevision = 0;
      for (const key of account.keys || []) key.id = '';
      for (const name of ['state', 'availableModels', 'modelsUpdatedAt']) delete account[name];
    }
    editorEpoch++; savedAccount = duplicate ? null : account; form.reset(); saving = false;
    for (const details of form.querySelectorAll('details')) details.open = false;
    $('discover').disabled = false;
    $('discoveryDetails').hidden = true;
    $('discoveryResponse').textContent = '';
    for (const id of ['formError', 'discoveryStatus', 'protocolStatus', 'headerStatus', 'mappingStatus']) feedback(id);
    $('formTitle').textContent = duplicate ? '复制渠道 · ' + sourceLabel : account ? '编辑渠道 · ' + account.label : '添加渠道';
    $('formDescription').textContent = duplicate ? '副本默认禁用，保存后创建新渠道' : '配置渠道连接与转发规则';
    for (const name of ['id', 'label', 'baseUrl']) form.elements[name].value = account?.[name] || '';
    form.elements.networkMode.value = account?.networkMode || 'direct';
    proxySourcesDraft = uniqueIds(account?.proxySubscriptionIds);
    proxyIdDraft = account?.proxyId || '';
    $('proxyScope').value = proxyIdDraft ? 'node' : proxySourcesDraft.length === 1 && sameId(proxySourcesDraft[0], 'manual')
      ? 'manual' : proxySourcesDraft.length ? 'subscriptions' : 'all';
    proxyCatalog = null; proxyCatalogError = ''; proxyLoading = false;
    renderProxySettings();
    form.elements.responsesTransport.value = account?.responsesTransport || 'http';
    form.elements.stripResponsesResourceState.checked = account?.stripResponsesResourceState ?? false;
    form.elements.weight.value = account?.weight ?? 100;
    form.elements.enabled.checked = account?.enabled ?? true;
    form.elements.models.value = (account?.models || []).join('\n');
    extraParams = typeof account?.extraParams === 'string' ? objectJson(account.extraParams, '高级参数') : account?.extraParams || {};
    keyDraft = (account?.keys || []).map(k => {
      const secret = k.secret || '', autoName = automaticKeyName(k.name) || k.name === defaultKeyName(secret);
      return { ...k, secret, originalSecret: secret, autoName,
        name: autoName && secret ? defaultKeyName(secret) : k.name || '', uiId: 'draft:' + ++keySequence };
    });
    deletedKeyIds = []; keyRevision = account?.keyRevision ?? 0;
    modelProtocolDraft = Object.assign(Object.create(null), structuredClone(account?.modelProtocols || {}));
    loadReasoningPolicy(account?.reasoningPolicy);
    loadSystemPrompt(account?.systemPromptPolicy);
    candidates = account?.availableModels || [];
    $('keySelectionMode').value = account?.keySelectionMode || 'roundRobin';
    for (const [name, fallback] of Object.entries(defaults)) form.elements[name].value = account?.requestPolicy?.[name] ?? fallback;
    for (const name of ['rateLimitRetryEnabled', 'emptyResponseRetryEnabled']) form.elements[name].checked = account?.requestPolicy?.[name] ?? false;
    updateResponseRetry();
    renderExecutionHints();
    headerPolicyMode = account?.headerOverrideMode || 'shared'; headerScope = 'common'; headerMode = 'visual';
    commonHeaderDraft = structuredClone(account?.headerOverride || {});
    endpointHeaderDraft = Object.assign(Object.create(null), structuredClone(account?.endpointHeaderOverrides || {}));
    loadHeaderScope();
    mappingRows = Object.entries(account?.requestPolicy?.statusCodeMapping || {}).map(([from, to]) => ({ from, to: String(to) }));
    mappingMode = 'visual'; $('statusCodeMapping').value = JSON.stringify(account?.requestPolicy?.statusCodeMapping || {}, null, 2);
    const selected = new Set(account?.endpoints || paths), root = $('endpoints'); root.replaceChildren();
    paths.forEach((path, index) => {
      const label = el('label', undefined, 'endpoint-option'), input = el('input'), text = el('span', Object.values(protocolLabels)[index]);
      input.type = 'checkbox'; input.name = 'endpoints'; input.value = path; input.checked = selected.has(path);
      input.onchange = renderProtocols;
      text.append(el('code', path)); label.append(input, text); root.append(label);
    });
    $('refreshModels').disabled = !savedAccount;
    renderKeys(); showCandidates(); renderProtocols(); renderHeaders(); renderMappings(); updateModes(); summary();
    selectTab('basic'); if (!dialog.open) dialog.showModal();
    loadProxyCatalog();
    loadExecutionSettings();
  }
  function closeEditor() { if (saving) return; closeEffortPicker(); editorEpoch++; dialog.close(); }
  $('close').onclick = closeEditor; $('cancel').onclick = closeEditor;
  dialog.addEventListener('cancel', e => { e.preventDefault(); closeEditor(); });
  $('add').onclick = () => open(null); $('reload').onclick = load;
  $('filter').oninput = render; $('statusFilter').onchange = render;

  function fillKeyOptions(select, keys, testing) {
    const previous = select.value; select.replaceChildren();
    if (testing) select.append(option('strategy', '按渠道策略'), option('all', '全部启用 Key'));
    else select.append(option('', '第一个启用 Key'));
    for (const k of keys.filter(k => k.enabled))
      select.append(option(testing ? 'key:' + k.id : k.id || k.uiId, k.name?.trim() || defaultKeyName(k.secret) || '未命名 Key'));
    if ([...select.options].some(o => o.value === previous)) select.value = previous;
  }
  function keyHint() {
    $('keyModeHint').textContent = { roundRobin: '按启用 Key 的顺序依次分配', random: '随机选择一个启用 Key', priority: '使用排序最前的启用 Key' }[$('keySelectionMode').value];
  }
  function renderKeys() {
    const root = $('keyRows'); root.replaceChildren(); $('keyCount').textContent = keyDraft.length;
    if (!keyDraft.length) root.append(el('div', '在下方添加上游 Key', 'empty'));
    else {
      const columns = el('div', undefined, 'key-table-head'); columns.setAttribute('aria-hidden', 'true');
      for (const label of ['密钥名称', '访问密钥', '启用', '操作']) columns.append(el('span', label));
      root.append(columns);
    }
    keyDraft.forEach((key, index) => {
      const row = el('div', undefined, 'key-row card');
      const actions = el('div', undefined, 'actions');
      for (const [title, delta] of [['上移', -1], ['下移', 1]]) {
        const b = button('', () => { [keyDraft[index], keyDraft[index + delta]] = [keyDraft[index + delta], keyDraft[index]]; renderKeys(); }, 'icon-button');
        b.append(icon(delta < 0 ? 'chevron-up' : 'chevron-down')); b.setAttribute('aria-label', title); b.title = title;
        b.disabled = index + delta < 0 || index + delta >= keyDraft.length; actions.append(b);
      }
      const remove = button('', async () => {
        if (!await ask(`删除「${key.name || '未命名 Key'}」？`, '删除 Key')) return;
        if (key.id) deletedKeyIds.push(key.id);
        keyDraft = keyDraft.filter(k => k !== key); renderKeys();
      }, 'icon-button danger');
      remove.append(icon('trash')); remove.setAttribute('aria-label', '删除 Key'); remove.title = '删除 Key'; actions.append(remove);
      const nameCell = el('div', undefined, 'key-name-cell'), secretCell = el('div', undefined, 'key-secret-cell'), name = el('input'), secret = el('input');
      name.value = key.name; name.placeholder = defaultKeyName(effectiveKeySecret(key)) || '默认取密钥末尾 4 位'; name.setAttribute('aria-label', `Key ${index + 1} 名称`);
      name.oninput = () => { key.name = name.value; key.autoName = !name.value.trim(); fillKeyOptions($('discoveryKey'), keyDraft, false); };
      secret.type = 'text'; secret.autocomplete = 'off'; secret.spellcheck = false; secret.value = key.secret;
      secret.placeholder = key.id ? '留空保留原密钥' : '输入完整密钥'; secret.setAttribute('aria-label', `Key ${index + 1} 密钥`);
      secret.oninput = () => {
        key.secret = secret.value;
        if (key.autoName) { key.name = defaultKeyName(effectiveKeySecret(key)); name.value = key.name; name.placeholder = key.name; }
        fillKeyOptions($('discoveryKey'), keyDraft, false);
      };
      nameCell.append(el('span', String(index + 1).padStart(2, '0'), 'key-ordinal'), name);
      secretCell.append(secret);
      const enabled = el('input'); enabled.type = 'checkbox'; enabled.checked = key.enabled;
      enabled.onchange = () => { key.enabled = enabled.checked; fillKeyOptions($('discoveryKey'), keyDraft, false); summary(); };
      const label = el('label', undefined, 'inline-check key-enabled');
      label.append(enabled, el('span', '启用 Key', 'key-enabled-label sr-only')); row.append(nameCell, secretCell, label, actions); root.append(row);
    });
    fillKeyOptions($('discoveryKey'), keyDraft, false); keyHint(); summary();
  }
  $('keySelectionMode').onchange = keyHint;
  $('keyAdd').onclick = () => {
    try {
      const lines = $('keyBatch').value.split(/\r?\n/).map(x => x.trim()).filter(Boolean);
      if (!lines.length) throw Error('请先填写 Key');
      if (keyDraft.length + lines.length > 100) throw Error('每渠道最多100个 Key');
      const seen = new Set(keyDraft.map(effectiveKeySecret).filter(Boolean));
      for (const secret of lines) { if (seen.has(secret)) throw Error('批量内容含重复 Key'); seen.add(secret); }
      for (const secret of lines) keyDraft.push({ id: '', uiId: 'draft:' + ++keySequence,
        name: defaultKeyName(secret), autoName: true, secret, originalSecret: '', enabled: true });
      $('keyBatch').value = ''; feedback('formError'); renderKeys();
    } catch (e) { feedback('formError', e.message, true); }
  };
  function readKeys() {
    if ($('keyBatch').value.trim()) fail('请先将批量 Key 添加到列表', 'keys', 'keyBatch');
    const seen = new Set();
    return keyDraft.map(key => {
      const { id, enabled } = key;
      let secret = key.secret.trim();
      if (!id && !secret) fail('新增 Key 必须填写密钥', 'keys');
      const effective = effectiveKeySecret(key);
      if (effective && seen.has(effective)) fail('Key 列表包含重复密钥', 'keys');
      if (effective) seen.add(effective);
      const name = key.autoName || !key.name.trim() ? defaultKeyName(effective) : key.name.trim();
      if (!keySecretChanged(key)) secret = '';
      return { id, name, secret, enabled };
    });
  }
  function showCandidates() {
    const root = $('candidates'); root.replaceChildren(); const selected = new Set(modelIds());
    for (const model of candidates) {
      const b = button(model, () => {
        const ids = modelIds(); form.elements.models.value = (selected.has(model) ? ids.filter(x => x !== model) : [...ids, model]).join('\n');
        showCandidates(); renderProtocols(); summary();
      }, selected.has(model) ? 'selected' : '');
      b.setAttribute('aria-pressed', String(selected.has(model))); root.append(b);
    }
  }
  async function discover(refresh = false) {
    const epoch = editorEpoch;
    $('discover').disabled = true; $('refreshModels').disabled = true;
    feedback('discoveryStatus', '正在获取候选模型…');
    $('discoveryDetails').hidden = true;
    $('discoveryResponse').textContent = '';
    try {
      commitHeaderDraft();
      const keyId = $('discoveryKey').value;
      const key = keyId ? keyDraft.find(k => (k.id || k.uiId) === keyId && k.enabled) : keyDraft.find(k => k.enabled);
      if (!key) throw Error('请先在 Key 管理中添加并启用 Key');
      const baseUrl = form.elements.baseUrl.value.trim();
      const network = readNetwork();
      if (!/^https?:\/\//i.test(baseUrl)) throw Error('请先填写有效的 Base URL');
      let body, route;
      if (refresh) {
        if (!savedAccount) throw Error('刷新候选需要先保存渠道');
        if (networkChanged(savedAccount))
          throw Error('出站方式或代理选择尚未保存，请使用获取候选模型或先保存渠道');
        if (keySecretChanged(key) || baseUrl.replace(/\/+$/, '') !== savedAccount.baseUrl.replace(/\/+$/, ''))
          throw Error('当前连接配置尚未保存，请使用获取候选模型');
        const canonical = value => JSON.stringify(Object.keys(value || {}).sort().map(k =>
          [k, value[k] && typeof value[k] === 'object' ? canonical(value[k]) : value[k]]));
        const savedExtra = typeof savedAccount.extraParams === 'string'
          ? objectJson(savedAccount.extraParams, '高级参数') : savedAccount.extraParams || {};
        if (canonical(commonHeaderDraft) !== canonical(savedAccount.headerOverride || {}) ||
            canonical(extraParams) !== canonical(savedExtra))
          throw Error('通用请求头或高级参数尚未保存，请使用获取候选模型或先保存渠道');
        route = 'models/refresh'; body = { id: savedAccount.id, keyId: key.id };
      } else {
        route = 'models/discover';
        body = { id: savedAccount?.id || null, baseUrl, ...network, headerOverride: commonHeaderDraft, extraParams };
        if (keySecretChanged(key)) body.apiKey = key.secret.trim(); else body.keyId = key.id;
      }
      const data = await api('POST', route, body);
      if (epoch !== editorEpoch || !dialog.open) return;
      candidates = [...new Set((data.models || []).filter(x => typeof x === 'string' && x.trim()))];
      showCandidates();
      feedback('discoveryStatus', candidates.length ? `已获取 ${candidates.length} 个候选模型，点击模型即可选择` : '上游返回了空模型列表，可直接手动填写');
    } catch (e) {
      if (epoch === editorEpoch && dialog.open) {
        feedback('discoveryStatus', '', true);
        $('discoveryDetails').hidden = false;
        $('discoveryDetails').open = true;
        $('discoveryResponse').textContent = e.message;
      }
    }
    finally { if (epoch === editorEpoch) { $('discover').disabled = false; $('refreshModels').disabled = !savedAccount; } }
  }
  $('discover').onclick = () => discover(false); $('refreshModels').onclick = () => discover(true);
  $('copyDiscoveryResponse').onclick = async () => {
    const epoch = editorEpoch, text = $('discoveryResponse').textContent;
    try {
      await copyText(text);
      if (epoch === editorEpoch && dialog.open) feedback('discoveryStatus', '已复制响应详情');
    } catch (e) {
      if (epoch !== editorEpoch || !dialog.open) return;
      feedback('discoveryStatus', e.message, true);
    }
  };
  $('models').oninput = () => { showCandidates(); renderProtocols(); summary(); };
  $('syncProtocols').onclick = () => {
    renderProtocols();
    feedback('protocolStatus', modelIds().length ? `已同步 ${modelIds().length} 个模型的协议配置` : '请先填写允许模型或选择候选模型', !modelIds().length);
    if (!modelIds().length) $('models').focus();
  };
  function renderProtocols() {
    updateReasoningModels();
    const root = $('modelProtocols'); root.replaceChildren(); $('protocolCount').textContent = modelIds().length;
    if (!modelIds().length) { root.append(el('div', '添加模型后在这里配置协议', 'empty')); return; }
    const endpoints = [...form.querySelectorAll('input[name=endpoints]:checked')].map(x => x.value);
    for (const model of modelIds()) {
      const configured = modelProtocolDraft[model];
      const preferred = /(?:^|[/.])claude(?:$|[-_.:\[])/i.test(model) && endpoints.includes('/v1/messages') ? 'messages'
        : endpoints.includes('/v1/responses') ? 'responses' : 'messages';
      const value = configured || { protocols: [preferred], preferredProtocol: preferred, nativeOnly: false };
      const row = el('div', undefined, 'protocol-row'), header = el('div', undefined, 'protocol-summary'); header.append(el('strong', model));
      const enabled = el('input'); enabled.type = 'checkbox'; enabled.checked = !!configured;
      const label = el('label', undefined, 'inline-check'); label.append(enabled, document.createTextNode('指定支持协议')); header.append(label); row.append(header);
      enabled.onchange = () => { if (enabled.checked) modelProtocolDraft[model] = value; else delete modelProtocolDraft[model]; renderProtocols(); };
      if (configured) {
        const options = el('div', undefined, 'protocol-options');
        for (const [key, title] of Object.entries(protocolLabels)) {
          const box = el('input'); box.type = 'checkbox'; box.checked = value.protocols.includes(key);
          box.onchange = () => {
            value.protocols = box.checked ? [...value.protocols, key] : value.protocols.filter(x => x !== key);
            if (!value.protocols.includes(value.preferredProtocol)) value.preferredProtocol = value.protocols[0] || '';
            renderProtocols();
          };
          const l = el('label', undefined, 'inline-check'); l.append(box, document.createTextNode(title)); options.append(l);
        }
        const bottom = el('div', undefined, 'protocol-bottom'), select = el('select');
        select.setAttribute('aria-label', model + ' 首选协议');
        for (const key of value.protocols) select.append(option(key, protocolLabels[key]));
        select.value = value.preferredProtocol; select.onchange = () => value.preferredProtocol = select.value;
        const native = el('input'); native.type = 'checkbox'; native.checked = value.nativeOnly;
        native.onchange = () => value.nativeOnly = native.checked;
        const l = el('label', undefined, 'inline-check'); l.append(native, document.createTextNode('强制原生'));
        bottom.append(el('span', '首选', 'muted'), select, l); row.append(options, bottom);
      }
      root.append(row);
    }
  }
  function readProtocols() {
    const result = Object.create(null);
    for (const model of modelIds()) {
      const value = modelProtocolDraft[model]; if (!value) continue;
      if (!value.protocols.length || !value.protocols.includes(value.preferredProtocol)) fail(model + ' 需要选择支持协议和首选协议', 'models');
      result[model] = value;
    }
    return result;
  }
  const isHeaderRule = name => name === '*' || /^(re|regex):/i.test(name);
  const headerInherited = () => headerScope !== 'common' && endpointHeaderDraft[headerScope]?.useCommon !== false;
  function commitHeaderDraft() {
    const headers = readHeaders();
    if (headerScope === 'common') commonHeaderDraft = headers;
    else if (!headerInherited()) endpointHeaderDraft[headerScope].headers = headers;
  }
  function updateHeaderControls() {
    const inherited = headerInherited();
    $('headerShared').setAttribute('aria-pressed', String(headerPolicyMode === 'shared'));
    $('headerPerEndpoint').setAttribute('aria-pressed', String(headerPolicyMode === 'perEndpoint'));
    $('headerScopes').hidden = headerPolicyMode !== 'perEndpoint';
    for (const button of document.querySelectorAll('[data-header-scope]'))
      button.setAttribute('aria-pressed', String(button.dataset.headerScope === headerScope));
    $('headerInheritance').hidden = headerScope === 'common';
    $('headerUseCommon').checked = inherited;
    $('headerScopeTitle').textContent = headerScope === 'common' ? '通用配置'
      : protocolLabels[headerScope === '/v1/responses' ? 'responses' : 'messages'];
    $('headerScopeHint').textContent = headerScope === 'common'
      ? headerPolicyMode === 'shared' ? '应用于所有接口及模型发现' : '应用于继承通用配置的接口及模型发现'
      : `${headerScope} · ${inherited ? '使用通用配置，只读' : '独立配置，不叠加通用请求头'} · 按实际上游接口匹配`;
    for (const id of ['templateCodex', 'templateClaude', 'headerClear', 'headerAdd', 'headerFormat'])
      $(id).disabled = inherited;
    $('headerOverride').readOnly = inherited;
  }
  function loadHeaderScope() {
    const headers = headerScope === 'common' || headerInherited()
      ? commonHeaderDraft : endpointHeaderDraft[headerScope].headers;
    headerRows = Object.entries(headers).map(([name, value]) => ({ name, value }));
    $('headerOverride').value = JSON.stringify(headers, null, 2);
    renderHeaders(); updateModes();
  }
  function selectHeaderScope(mode, scope) {
    try {
      commitHeaderDraft();
      headerPolicyMode = mode; headerScope = mode === 'shared' ? 'common' : scope;
      loadHeaderScope(); feedback('headerStatus');
    } catch (e) { feedback('headerStatus', e.message, true); }
  }
  $('headerShared').onclick = () => selectHeaderScope('shared', 'common');
  $('headerPerEndpoint').onclick = () => selectHeaderScope('perEndpoint', 'common');
  for (const button of document.querySelectorAll('[data-header-scope]')) {
    button.onclick = () => selectHeaderScope('perEndpoint', button.dataset.headerScope);
    button.onkeydown = event => {
      if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return;
      event.preventDefault();
      const buttons = [...document.querySelectorAll('[data-header-scope]')], index = buttons.indexOf(button);
      const next = event.key === 'Home' ? 0 : event.key === 'End' ? buttons.length - 1
        : (index + (event.key === 'ArrowRight' ? 1 : -1) + buttons.length) % buttons.length;
      selectHeaderScope('perEndpoint', buttons[next].dataset.headerScope);
      if (headerScope === buttons[next].dataset.headerScope) buttons[next].focus();
    };
  }
  $('headerUseCommon').onchange = () => {
    try {
      commitHeaderDraft();
      endpointHeaderDraft[headerScope] ??= { useCommon: true, headers: structuredClone(commonHeaderDraft) };
      endpointHeaderDraft[headerScope].useCommon = $('headerUseCommon').checked;
      loadHeaderScope(); feedback('headerStatus');
    } catch (e) { updateHeaderControls(); feedback('headerStatus', e.message, true); }
  };
  function readHeaders() {
    const result = headerMode === 'json' ? objectJson($('headerOverride').value, '请求头') : Object.create(null);
    if (headerMode === 'visual') {
      const seen = new Set();
      for (const row of headerRows) {
        const name = row.name.trim();
        if (!name && row.value === '') continue;
        if (!name) throw Error('请填写请求头名称');
        if (seen.has(name.toLowerCase())) throw Error('请求头名称重复：' + name);
        seen.add(name.toLowerCase()); result[name] = row.value;
      }
    }
    for (const [name, value] of Object.entries(result)) {
      if (isHeaderRule(name)) continue;
      if (!/^[!#$%&'*+\-.^_`|~0-9A-Za-z]+$/.test(name) || typeof value !== 'string' || /[\x00-\x1f\x7f]/.test(value))
        throw Error('请求头名称或值无效：' + name);
    }
    return result;
  }
  function renderHeaders() {
    updateHeaderControls();
    const inherited = headerInherited();
    const root = $('headerRows'); root.replaceChildren();
    if (!headerRows.length) root.append(el('div', '使用模板快速配置，或添加自定义请求头', 'empty'));
    headerRows.forEach((row, index) => {
      const line = el('div', undefined, 'header-row'), name = el('input'), value = el('input');
      name.value = row.name; name.placeholder = '请求头名称'; name.setAttribute('aria-label', `请求头 ${index + 1} 名称`);
      value.value = typeof row.value === 'string' ? row.value : JSON.stringify(row.value);
      name.readOnly = inherited; value.readOnly = inherited;
      value.placeholder = '值'; value.setAttribute('aria-label', `请求头 ${index + 1} 值`);
      name.oninput = () => row.name = name.value;
      value.oninput = () => { row.value = value.value; };
      const remove = button('×', () => { headerRows.splice(index, 1); renderHeaders(); }, 'icon-button danger');
      remove.disabled = inherited;
      remove.setAttribute('aria-label', `删除请求头 ${index + 1}`); line.append(name, value, remove); root.append(line);
    });
  }
  function readMapping() {
    const result = mappingMode === 'json' ? objectJson($('statusCodeMapping').value, '状态码映射') : Object.create(null);
    if (mappingMode === 'visual') for (const row of mappingRows) {
      if (!row.from || !row.to) throw Error('请输入原始状态码和目标状态码');
      if (!/^[2-5]\d{2}$/.test(row.from) || !/^[2-5]\d{2}$/.test(row.to))
        throw Error('状态码必须为200–599的三位整数');
      if (Object.hasOwn(result, row.from)) throw Error('原始状态码重复：' + row.from);
      result[row.from] = Number(row.to);
    }
    for (const [from, to] of Object.entries(result)) {
      if (!/^[2-5]\d{2}$/.test(from) || !Number.isInteger(to) || to < 200 || to > 599 || [204, 205, 304].includes(to))
        throw Error('状态码范围为200–599，目标不能为204、205或304');
    }
    return result;
  }
  function updateMappingHint() {
    try {
      $('mappingSuccessHint').hidden = !Object.entries(readMapping()).some(([from, to]) => Number(from) >= 400 && to >= 200 && to < 300);
    } catch { $('mappingSuccessHint').hidden = true; }
  }
  function statusInput(value, target, index) {
    const input = el('input'); input.type = 'text'; input.inputMode = 'numeric';
    input.setAttribute('aria-label', `映射 ${index + 1} ${target ? '目标' : '原始'}状态码`);
    input.placeholder = target ? '目标状态码' : '原始状态码';
    input.value = String(value); return input;
  }
  function renderMappings() {
    updateMappingHint();
    const root = $('mappingRows'); root.replaceChildren();
    if (!mappingRows.length) root.append(el('div', '保持上游状态码，或添加映射规则', 'empty'));
    mappingRows.forEach((row, index) => {
      const line = el('div', undefined, 'mapping-row'), from = statusInput(row.from, false, index), to = statusInput(row.to, true, index);
      from.oninput = () => row.from = from.value; to.oninput = () => row.to = to.value;
      const remove = button('×', () => { mappingRows.splice(index, 1); renderMappings(); }, 'icon-button danger');
      remove.setAttribute('aria-label', `删除映射 ${index + 1}`); line.append(from, el('span', '→', 'arrow'), to, remove); root.append(line);
    });
  }
  function updateModes() {
    for (const [prefix, mode] of [['header', headerMode], ['mapping', mappingMode]]) {
      $(prefix + 'VisualPane').hidden = mode !== 'visual'; $(prefix + 'JsonPane').hidden = mode !== 'json';
      $(prefix + 'Visual').setAttribute('aria-pressed', String(mode === 'visual'));
      $(prefix + 'Json').setAttribute('aria-pressed', String(mode === 'json'));
    }
  }
  function setMode(kind, mode) {
    try {
      if (kind === 'header') {
        const value = readHeaders();
        headerRows = Object.entries(value).map(([name, value]) => ({ name, value }));
        $('headerOverride').value = JSON.stringify(value, null, 2); headerMode = mode; renderHeaders();
      } else {
        const value = readMapping();
        mappingRows = Object.entries(value).map(([from, to]) => ({ from, to: String(to) }));
        $('statusCodeMapping').value = JSON.stringify(value, null, 2); mappingMode = mode; renderMappings();
      }
      updateModes(); feedback(kind + 'Status');
    } catch (e) { feedback(kind + 'Status', e.message, true); }
  }
  $('headerVisual').onclick = () => setMode('header', 'visual'); $('headerJson').onclick = () => setMode('header', 'json');
  $('mappingVisual').onclick = () => setMode('mapping', 'visual'); $('mappingJson').onclick = () => setMode('mapping', 'json');
  $('headerAdd').onclick = () => { if (headerInherited()) return; headerRows.push({ name: '', value: '' }); renderHeaders(); $('headerRows').lastElementChild.querySelector('input').focus(); };
  $('mappingAdd').onclick = () => { mappingRows.push({ from: '', to: '' }); renderMappings(); };
  $('headerFormat').onclick = () => setMode('header', 'json'); $('mappingFormat').onclick = () => setMode('mapping', 'json');
  $('mappingRows').addEventListener('input', updateMappingHint);
  $('statusCodeMapping').addEventListener('input', updateMappingHint);
  async function setTemplate(name) {
    if (headerInherited()) return;
    const epoch = editorEpoch, scope = headerScope, mode = headerPolicyMode;
    if ((headerRows.length || $('headerOverride').value.trim() !== '{}') && !await ask('用所选模板替换当前请求头？', '应用模板')) return;
    if (epoch !== editorEpoch || scope !== headerScope || mode !== headerPolicyMode || headerInherited()) return;
    headerRows = Object.entries(templates[name]).map(([name, value]) => ({ name, value }));
    $('headerOverride').value = JSON.stringify(templates[name], null, 2);
    commitHeaderDraft(); renderHeaders(); feedback('headerStatus', `已应用 ${name === 'codex' ? 'Codex' : 'Claude Code'} 模板`);
  }
  $('templateCodex').onclick = () => setTemplate('codex'); $('templateClaude').onclick = () => setTemplate('claude');
  $('headerClear').onclick = async () => {
    if (headerInherited()) return;
    const epoch = editorEpoch, scope = headerScope, mode = headerPolicyMode;
    if (!await ask('清空当前请求头覆盖配置？', '清空请求头')) return;
    if (epoch !== editorEpoch || scope !== headerScope || mode !== headerPolicyMode || headerInherited()) return;
    headerRows = []; $('headerOverride').value = '{}'; commitHeaderDraft(); renderHeaders(); feedback('headerStatus', '请求头已清空');
  };
  $('headerCopy').onclick = async () => {
    const epoch = editorEpoch;
    try {
      const text = JSON.stringify(readHeaders(), null, 2);
      await copyText(text);
      if (epoch === editorEpoch && dialog.open) feedback('headerStatus', '已复制请求头');
    } catch (e) { if (epoch === editorEpoch && dialog.open) feedback('headerStatus', e.message, true); }
  };
  form.addEventListener('submit', async event => {
    event.preventDefault(); if (saving) return; feedback('formError');
    try {
      for (const input of form.querySelectorAll('input[required],textarea[required]')) {
        if (!input.checkValidity()) fail('请检查' + (input.labels?.[0]?.textContent || '必填字段'), input.closest('[role=tabpanel]').id.slice(6), input.id);
      }
      const endpoints = [...form.querySelectorAll('input[name=endpoints]:checked')].map(x => x.value);
      if (!endpoints.length) fail('至少选择一个端点', 'basic');
      const keys = readKeys(), models = modelIds();
      if (!models.length) fail('请填写或选择允许模型', 'models', 'models');
      const modelProtocols = readProtocols();
      let headerOverride, statusCodeMapping, reasoningPolicy;
      try { reasoningPolicy = readReasoningPolicy(); } catch (e) { fail(e.message, 'reasoning'); }
      const systemPromptPolicy = readSystemPrompt();
      try { commitHeaderDraft(); headerOverride = commonHeaderDraft; } catch (e) { fail(e.message, 'headers'); }
      try { statusCodeMapping = readMapping(); } catch (e) { fail(e.message, 'policy'); }
      const requestPolicy = { statusCodeMapping };
      for (const name of ['rateLimitRetryEnabled', 'emptyResponseRetryEnabled']) requestPolicy[name] = form.elements[name].checked;
      for (const name of Object.keys(defaults)) requestPolicy[name] = name === 'retryStatusCodes' ? form.elements[name].value : Number(form.elements[name].value);
      let network;
      try { network = readNetwork(); } catch (e) { fail(e.message, 'basic', 'proxyScope'); }
      saving = true; $('save').disabled = true; $('save').textContent = '保存中…';
      const saved = await api('POST', 'accounts/save', {
        id: form.elements.id.value || null, label: form.elements.label.value.trim(), baseUrl: form.elements.baseUrl.value.trim(),
        ...network,
        responsesTransport: form.elements.responsesTransport.value,
        stripResponsesResourceState: form.elements.stripResponsesResourceState.checked,
        keys, deletedKeyIds, keyRevision, keySelectionMode: $('keySelectionMode').value, weight: Number(form.elements.weight.value),
        enabled: form.elements.enabled.checked, endpoints, models, modelProtocols, reasoningPolicy, systemPromptPolicy, requestPolicy, headerOverride,
        headerOverrideMode: headerPolicyMode, endpointHeaderOverrides: endpointHeaderDraft, extraParams
      });
      editorEpoch++; dialog.close(); await load(); message(saved.warning || '渠道已保存', !!saved.warning);
    } catch (e) { feedback('formError', e.message, true); }
    finally { saving = false; $('save').disabled = false; $('save').textContent = '保存渠道'; }
  });
  const testState = { account: null, selected: new Set(), rows: new Map(), targets: new Set(), combinations: [],
    jobId: null, active: false, starting: false, cancelling: false, running: null, timer: null, epoch: 0 };
  const testBusy = () => testState.active || testState.starting;
  const resultId = (model, keyId) => JSON.stringify([model, keyId ?? null]);
  function updateTestControls() {
    for (const id of ['testAll', 'testSelected', 'testEndpoint', 'testStream', 'testKey', 'testSelectAll', 'testContent', 'testMaxTokens']) $(id).disabled = testBusy();
    const models = testState.account?.models || [], visible = visibleTestModels().slice((testPage - 1) * +$('testPageSize').value, testPage * +$('testPageSize').value);
    $('testAll').disabled = testBusy() || !models.length;
    $('testSelected').disabled = testBusy() || !testState.selected.size;
    $('testSelected').textContent = testState.selected.size ? `测试勾选 ${testState.selected.size} 个` : '测试勾选';
    $('testSelectAll').disabled = testBusy() || !visible.length;
    $('testSelectAll').checked = !!visible.length && visible.every(model => testState.selected.has(model));
    $('testSelectAll').indeterminate = visible.some(model => testState.selected.has(model)) && !$('testSelectAll').checked;
    $('testCancel').disabled = !testState.active || testState.cancelling;
  }
  let testPage = 1;
  function visibleTestModels() {
    const q = $('testFilter').value.toLowerCase();
    return (testState.account?.models || []).filter(model => model.toLowerCase().includes(q));
  }
  function updateTestConfigSummary() {
    $('testStreamLabel').textContent = $('testStream').checked ? '已启用' : '已禁用';
    $('testConfigSummary').textContent = `${$('testEndpoint').value || '自动选择'} · ${$('testStream').checked ? '流式' : '非流式'} · ${$('testMaxTokens').value || '—'} Token`;
  }
  function renderTests() {
    const root = $('testRows'); root.replaceChildren(); const q = $('testFilter').value.toLowerCase();
    const models = visibleTestModels(), size = +$('testPageSize').value;
    $('testEmpty').hidden = !!models.length;
    const pages = Math.max(1, Math.ceil(models.length / size));
    testPage = Math.min(testPage, pages);
    $('testTotal').textContent = `总计：${models.length}`;
    $('testPageNumber').textContent = String(testPage);
    $('testFirst').disabled = $('testPrevious').disabled = testPage === 1;
    $('testNext').disabled = $('testLast').disabled = testPage === pages;
    $('testAll').textContent = `测试全部 ${(testState.account?.models || []).length} 个模型`;
    updateTestConfigSummary();
    for (const model of models.slice((testPage - 1) * size, testPage * size)) {
      if (!model.toLowerCase().includes(q)) continue;
      const row = el('tr'), cell = el('td'), select = el('input'); select.type = 'checkbox';
      select.checked = testState.selected.has(model); select.disabled = testBusy(); select.setAttribute('aria-label', '选择 ' + model);
      select.onchange = () => { if (select.checked) testState.selected.add(model); else testState.selected.delete(model); updateTestControls(); };
      cell.append(select);
      const results = [...testState.rows.values()].filter(r => r.model === model);
      const pending = testState.active && testState.targets.has(model) && results.length < testState.combinations.filter(c => c.model === model).length;
      const state = pending ? testState.running === model ? '测试中' : '排队中' : results.length
        ? results.every(r => r.cancelled) ? '已取消' : results.every(r => r.success) ? '成功' : '失败' : '未测试';
      const summaryCell = el('td');
      for (const r of results) {
        const result = el('div', '', 'result-line');
        result.append(el('span', r.keyName || r.keyId || '按策略', 'result-key'),
          el('code', `${r.originalStatus ?? '—'} → ${r.mappedStatus ?? '—'}`, 'result-http'),
          el('strong', `${r.durationMs ?? '—'}ms`, 'result-duration'),
          el('span', `重试 ${r.retries ?? 0}次`, 'result-retries'));
        summaryCell.append(result);
      }
      if (!results.length) summaryCell.append(el('span', pending ? '等待响应' : '—', 'result-empty'));
      const ops = el('td'), run = button('测试', () => startTests([model])); run.disabled = testBusy(); ops.append(run);
      if (results.length) ops.append(button('详情', () => {
        $('testDetails').textContent = results.map(r =>
          `${r.keyName || r.keyId || '按策略'} · 测试端点：${r.endpoint || '—'} · 出站方式：${r.networkMode === 'proxyPool' ? '代理池' : '直连'} · 上游连接：${r.transport === 'websocket' ? 'WebSocket' : 'HTTP'}${r.proxy ? `\n实际代理：${r.proxy.address} · ${r.proxy.id}` : ''}\n${r.response || r.error || '未收到上游响应'}${r.responseTruncated ? '\n[原始响应超过 32 MiB，展示已截断]' : ''}`
        ).join('\n\n');
        const overview = $('testDetailOverview'); overview.replaceChildren();
        for (const r of results) {
          const section = el('section', '', 'detail-group'), values = el('dl', '', 'detail-values detail-card');
          section.append(el('h4', r.keyName || r.keyId || '按渠道策略'));
          for (const [label, value] of [['模型', model], ['状态', r.cancelled ? '已取消' : r.success ? '成功' : '失败'],
            ['测试端点', r.endpoint || '—'], ['出站方式', r.networkMode === 'proxyPool' ? '代理池' : '直连'], ['上游连接', r.transport === 'websocket' ? 'WebSocket' : 'HTTP'],
            ...(r.proxy ? [['实际代理', r.proxy.address], ['代理 ID', r.proxy.id], ['代理来源', r.proxy.subscriptionId]] : []),
            ['状态码', `${r.originalStatus ?? '—'} → ${r.mappedStatus ?? '—'}`],
            ['响应时间', `${r.durationMs ?? '—'} ms`], ['重试次数', String(r.retries ?? 0)]]) values.append(el('dt', label), el('dd', value));
          section.append(values); overview.append(section);
        }
        $('testDetailDialog').showModal();
      }, 'text-button'));
      const stateCell = el('td');
      stateCell.append(el('span', state, 'console-badge ' + (state === '成功' ? 'is-success' : state === '失败' ? 'is-error' : pending ? 'is-active' : 'is-neutral')));
      const modelCell = el('td', model, 'console-model');
      for (const endpoint of new Set(results.map(r => r.endpoint).filter(Boolean)))
        modelCell.append(el('code', endpoint, 'result-endpoint'));
      for (const [item, label] of [[cell, '选择'], [modelCell, '模型'], [stateCell, '状态'], [summaryCell, '结果'], [ops, '操作']]) item.dataset.label = label;
      row.append(cell, modelCell, stateCell, summaryCell, ops); root.append(row);
    }
    updateTestControls();
  }
  function openTests(account) {
    if (dialog.open && savedAccount?.id === account.id &&
        (networkChanged(account) ||
         form.elements.responsesTransport.value !== (account.responsesTransport || 'http') ||
         form.elements.stripResponsesResourceState.checked !== (account.stripResponsesResourceState ?? false))) {
      feedback('formError', '连接设置尚未保存，请先保存渠道再测试', true); return;
    }
    if (testBusy() && testState.account?.id !== account.id) { message('请先取消或等待当前渠道测试完成', true); return; }
    $('testTitle').textContent = '连接测试 · ' + account.label;
    if (!testBusy()) {
      fillKeyOptions($('testKey'), account.keys || [], true);
      if (testState.account?.id !== account.id) {
        $('testConfiguration').open = true;
        clearTimeout(testState.timer); testState.epoch++;
        Object.assign(testState, { account, selected: new Set(), rows: new Map(), targets: new Set(), combinations: [], jobId: null, active: false, running: null });
        feedback('testProgress'); feedback('testError'); $('testDetails').textContent = '';
        $('testFilter').value = ''; $('testStream').checked = false; testPage = 1;
      } else testState.account = account;
      $('testEndpoint').replaceChildren(option('', '自动选择'));
      for (const endpoint of account.endpoints || []) $('testEndpoint').append(option(endpoint, endpoint));
    }
    renderTests(); if (!$('testDialog').open) $('testDialog').showModal();
  }
  async function startTests(models) {
    if (testBusy()) return;
    const content = $('testContent').value, maxOutputTokens = Number($('testMaxTokens').value);
    if (content.length > 16000 || !Number.isInteger(maxOutputTokens) || maxOutputTokens < 1 || maxOutputTokens > 32768) {
      feedback('testError', '测试内容最多 16000 字符，输出上限须为 1–32768 Token', true); return;
    }
    const choice = $('testKey').value, keyMode = choice.startsWith('key:') ? 'specified' : choice;
    const keyId = keyMode === 'specified' ? choice.slice(4) : null;
    const keys = keyMode === 'all' ? (testState.account.keys || []).filter(k => k.enabled).map(k => k.id) : [keyId];
    const total = models.length * keys.length;
    if (!total || total > 100) { feedback('testError', '请选择1–100个模型与 Key 组合', true); return; }
    testState.starting = true; updateTestControls();
    if (!await ask(`将发送 ${total} 个模型与 Key 测试请求，可能产生费用。`, '开始连接测试')) {
      testState.starting = false; updateTestControls(); return;
    }
    testState.targets = new Set(models); testState.combinations = models.flatMap(model => keys.map(keyId => ({ model, keyId })));
    for (const [id, row] of testState.rows) if (models.includes(row.model)) testState.rows.delete(id);
    feedback('testError'); $('testResume').hidden = true; renderTests();
    try {
      const job = await api('POST', 'tests/start', { accountId: testState.account.id, models, keyMode, keyId, endpoint: $('testEndpoint').value || null, stream: $('testStream').checked, content, maxOutputTokens });
      testState.jobId = job.id; testState.active = true; testState.epoch++;
      $('testConfiguration').open = false; document.querySelector('.test-content').scrollTop = 0;
      feedback('testProgress', '测试已提交'); await pollTests(testState.epoch);
    } catch (e) { feedback('testError', e.message, true); }
    finally { testState.starting = false; renderTests(); }
  }
  async function pollTests(epoch) {
    if (epoch !== testState.epoch || !testState.active) return;
    clearTimeout(testState.timer);
    try {
      const job = await api('GET', 'tests/status?id=' + encodeURIComponent(testState.jobId));
      if (epoch !== testState.epoch) return;
      const state = typeof job.state === 'number' ? ['Queued', 'Running', 'Completed', 'Failed', 'Cancelled'][job.state] : job.state;
      const progress = job.progress || {}, rows = job.result?.rows || progress.rows || [];
      for (const row of rows) testState.rows.set(resultId(row.model, row.keyId), row);
      testState.running = progress.running || null;
      feedback('testProgress', `${({ Queued: '排队中', Running: '测试中', Completed: '已完成', Failed: '失败', Cancelled: '已取消' })[state] || state} · ${progress.completed ?? rows.length}/${progress.total ?? testState.combinations.length}`);
      $('testResume').hidden = true;
      if (['Completed', 'Failed', 'Cancelled'].includes(state)) {
        testState.active = false; testState.cancelling = false; testState.running = null;
        for (const combo of testState.combinations) {
          const exists = combo.keyId === null ? [...testState.rows.values()].some(r => r.model === combo.model) : testState.rows.has(resultId(combo.model, combo.keyId));
          if (!exists) testState.rows.set(resultId(combo.model, combo.keyId), { ...combo, success: false, cancelled: state === 'Cancelled', error: job.error || '测试未完成' });
        }
        if (job.error) feedback('testError', job.error, true);
      } else if (['Queued', 'Running'].includes(state)) testState.timer = setTimeout(() => pollTests(epoch), 800);
      else throw Error('无法识别任务状态，请重新查询');
      renderTests();
    } catch (e) {
      if (epoch !== testState.epoch) return;
      feedback('testError', '查询失败：' + e.message, true); $('testResume').hidden = false; updateTestControls();
    }
  }
  $('testAll').onclick = () => startTests(testState.account.models || []);
  $('testSelected').onclick = () => startTests([...testState.selected]);
  $('testFilter').oninput = () => { testPage = 1; renderTests(); };
  $('testPageSize').onchange = () => { testPage = 1; renderTests(); };
  $('testFirst').onclick = () => { testPage = 1; renderTests(); };
  $('testPrevious').onclick = () => { testPage--; renderTests(); };
  $('testNext').onclick = () => { testPage++; renderTests(); };
  $('testLast').onclick = () => { testPage = Math.max(1, Math.ceil(visibleTestModels().length / +$('testPageSize').value)); renderTests(); };
  $('testStream').onchange = updateTestConfigSummary;
  $('testEndpoint').onchange = updateTestConfigSummary;
  $('testMaxTokens').oninput = updateTestConfigSummary;
  $('testSelectAll').onchange = () => {
    for (const model of visibleTestModels().slice((testPage - 1) * +$('testPageSize').value, testPage * +$('testPageSize').value)) {
      if ($('testSelectAll').checked) testState.selected.add(model); else testState.selected.delete(model);
    }
    renderTests();
  };
  $('testResume').onclick = () => pollTests(testState.epoch);
  $('testClose').onclick = () => $('testDialog').close();
  $('testDetailClose').onclick = () => $('testDetailDialog').close();
  for (const [buttonId, panelId] of [['journalExpand', 'journalAdvanced']]) {
    $(buttonId).onclick = () => {
      const panel = $(panelId); panel.hidden = !panel.hidden;
      $(buttonId).setAttribute('aria-expanded', String(!panel.hidden));
      $(buttonId).textContent = panel.hidden ? '更多筛选' : '收起筛选';
    };
  }
  $('testCancel').onclick = async () => {
    testState.cancelling = true; updateTestControls();
    try {
      await api('POST', 'tests/cancel', { id: testState.jobId });
      feedback('testProgress', '已申请取消，正在等待任务退出'); await pollTests(testState.epoch);
    } catch (e) { testState.cancelling = false; feedback('testError', e.message, true); updateTestControls(); }
  };
  /*__SETTINGS__*/
  /*__REASONING__*/
  /*__JOURNAL__*/
  for (const modal of document.querySelectorAll('dialog')) {
    let startedOutside = false;
    const outside = event => {
      const bounds = modal.getBoundingClientRect();
      return event.target === modal && (event.clientX < bounds.left || event.clientX > bounds.right ||
        event.clientY < bounds.top || event.clientY > bounds.bottom);
    };
    modal.addEventListener('pointerdown', event => {
      startedOutside = event.isPrimary && event.button === 0 && outside(event);
    });
    modal.addEventListener('pointercancel', () => { startedOutside = false; });
    modal.addEventListener('click', event => {
      const dismiss = startedOutside && outside(event); startedOutside = false;
      if (!dismiss) return;
      event.stopPropagation();
      if (modal.id === 'editor') closeEditor();
      else if (modal.id === 'confirmDialog') resolveConfirm(false);
      else modal.close();
    });
  }
  load();
})();
