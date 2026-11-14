(() => {
  'use strict';
  /*__CORE__*/
  const keyModes = { roundRobin: '轮询', random: '随机', priority: '主备顺序' };
  const defaultKeyName = secret => (secret || '').trim().slice(-4);
  const automaticKeyName = name => !name?.trim() || /^key\s*\d+$/i.test(name.trim()) || name.trim() === '默认 Key';
  const effectiveKeySecret = key => key.secret.trim() || key.originalSecret || '';
  const keySecretChanged = key => !key.id || !!key.secret.trim() && key.secret.trim() !== key.originalSecret;
  function updateResponseRetry() {
    $('responseRetrySettings').hidden = !$('rateLimitRetryEnabled').checked && !$('emptyResponseRetryEnabled').checked;
  }
  for (const name of ['rateLimitRetryEnabled', 'emptyResponseRetryEnabled']) $(name).addEventListener('change', updateResponseRetry);
  const protocolLabels = { responses: 'OpenAI Responses', messages: 'Anthropic Messages', chat: 'Chat Completions' };
  const protocolPaths = { responses: '/v1/responses', messages: '/v1/messages', chat: '/v1/chat/completions' };
  const endpointProtocols = Object.fromEntries(Object.entries(protocolPaths).map(([key, path]) => [path, key]));
  const paths = Object.values(protocolPaths);
  const defaults = { maxRetries: 0, headerTimeoutSeconds: 60, totalTimeoutSeconds: 180, streamIdleTimeoutSeconds: 60,
    responseMaxRetries: 3, responseRetryIntervalSeconds: 5,
    retryStatusCodes: '100-199,300-399,401-407,409-499,500-503,505-523,525-599' };
  let accounts = [], candidates = [], keyDraft = [], deletedKeyIds = [], keyRevision = 0, keySequence = 0;
  const selectedKeyIds = new Set();
  let modelProtocolDraft = {}, extraParams = {}, savedAccount = null, editorEpoch = 0;
  let accountLoadRequest = 0, accountLoad, saving = false;
  const switchingChannels = new Set();
  let workspacePage = 'channels';
  function setWorkspacePage(page) {
    closeChannelMenu();
    workspacePage = page;
    $('channelsWorkspace').hidden = page !== 'channels';
    $('journal').hidden = page !== 'journal';
    for (const [id, active] of [['showChannels', page === 'channels'], ['openJournal', page === 'journal']]) {
      if (active) $(id).setAttribute('aria-current', 'page'); else $(id).removeAttribute('aria-current');
    }
    const content = page === 'journal' ? $('journal').querySelector('.journal-content') : $('channelsWorkspace');
    content.scrollTo({ top: 0, left: 0 });
  }
  $('showChannels').onclick = () => { closeJournal(); $('statusFilter').value = 'all'; render(); };
  for (const [id, state] of [['showEnabled', 'enabled'], ['showDisabled', 'disabled']]) $(id).onclick = () => {
    closeJournal(); $('statusFilter').value = state; render();
  };

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
  function load() {
    const request = ++accountLoadRequest;
    $('reload').disabled = true; $('cards').setAttribute('aria-busy', 'true'); $('channelResult').textContent = '正在读取渠道…';
    return accountLoad = (async () => {
      try {
        const data = await api('GET', 'accounts');
        if (request !== accountLoadRequest) return accountLoad;
        accounts = data.accounts || []; render(); return true;
      }
      catch (e) {
        if (request !== accountLoadRequest) return accountLoad;
        message(e.message, true); $('channelResult').textContent = '读取失败，请刷新重试';
        if (!accounts.length) { const empty = el('div', undefined, 'empty'); empty.append(el('h3', '渠道加载失败'), el('p', '请检查连接后重新加载。'), button('重新加载', load)); $('cards').replaceChildren(empty); }
        return false;
      }
      finally { if (request === accountLoadRequest) { $('reload').disabled = false; $('cards').setAttribute('aria-busy', 'false'); } }
    })();
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
      off: '使用客户端系统指令；下方文本保留，选择追加或替换后可编辑。',
      append: '保留客户端指令，将下方内容作为最后一条系统指令。',
      replace: '以下方内容覆盖普通系统指令，保留动态工具声明与对话历史。'
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
  function applyAuthenticationMode() {
    const mode = $('authenticationMode').value;
    extraParams.apiKeyHeader = mode === 'x-api-key' ? 'x-api-key' : 'Authorization';
    extraParams.apiKeyPrefix = mode === 'x-api-key' ? '' : 'Bearer ';
  }
  function loadAuthentication() {
    const header = typeof extraParams.apiKeyHeader === 'string' ? extraParams.apiKeyHeader.toLowerCase() : '';
    const prefix = extraParams.apiKeyPrefix;
    const apiKey = header === 'x-api-key' && (prefix === undefined || prefix === '');
    const bearer = header === 'authorization' && (prefix === undefined || prefix === 'Bearer ');
    const select = $('authenticationMode');
    select.replaceChildren(option('bearer', 'Authorization: Bearer'), option('x-api-key', 'x-api-key'));
    select.value = apiKey ? 'x-api-key' : 'bearer';
    if (!apiKey && !bearer) applyAuthenticationMode();
  }
  $('authenticationMode').onchange = applyAuthenticationMode;
  function open(account, { duplicate = false } = {}) {
    if (saving) return;
    const sourceLabel = account?.label || '';
    if (duplicate) {
      account = structuredClone(account);
      account.id = ''; account.label = duplicateChannelName(sourceLabel); account.enabled = false; account.keyRevision = 0;
      for (const key of account.keys || []) key.id = '';
      for (const name of ['state', 'availableModels', 'modelsUpdatedAt']) delete account[name];
    }
    editorEpoch++; savedAccount = duplicate ? null : account; form.reset();
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
    extraParams = structuredClone(typeof account?.extraParams === 'string' ? objectJson(account.extraParams, '高级参数') : account?.extraParams || {});
    loadAuthentication();
    keyDraft = (account?.keys || []).map(k => {
      const secret = k.secret || '', autoName = automaticKeyName(k.name) || k.name === defaultKeyName(secret);
      return { ...k, secret, originalSecret: secret, autoName,
        name: autoName && secret ? defaultKeyName(secret) : k.name || '', uiId: 'draft:' + ++keySequence };
    });
    deletedKeyIds = []; keyRevision = account?.keyRevision ?? 0; selectedKeyIds.clear();
    modelProtocolDraft = Object.assign(Object.create(null), structuredClone(account?.modelProtocols || {}));
    loadReasoningPolicy(account?.reasoningPolicy);
    loadSystemPrompt(account?.systemPromptPolicy);
    candidates = account?.availableModels || [];
    $('keySelectionMode').value = account?.keySelectionMode || 'roundRobin';
    $('keyAutoDisableEnabled').checked = account?.keyAutoDisable?.enabled ?? false;
    $('keyAutoDisableCodes').value = account?.keyAutoDisable?.statusCodes ?? '401,403';
    $('keyAutoDisableThreshold').value = account?.keyAutoDisable?.failureThreshold ?? 3;
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
    paths.forEach(path => {
      const label = el('label', undefined, 'endpoint-option'), input = el('input'), text = el('span', protocolLabels[endpointProtocols[path]]);
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
  function setEditorSaving(value) {
    saving = value;
    $('editorFields').disabled = value;
    form.setAttribute('aria-busy', String(value));
    for (const id of ['save', 'cancel', 'close']) $(id).disabled = value;
    $('save').textContent = value ? '保存中…' : '保存渠道';
    syncKeySelection();
  }
  function syncKeySelection() {
    const selected = keyDraft.filter(key => selectedKeyIds.has(key.uiId));
    $('keySelectAll').checked = !!keyDraft.length && selected.length === keyDraft.length;
    $('keySelectAll').indeterminate = selected.length > 0 && selected.length < keyDraft.length;
    $('keySelectAll').disabled = saving || !keyDraft.length;
    $('keySelectionSummary').textContent = `已选 ${selected.length} / ${keyDraft.length} 个`;
    $('keyEnableSelected').disabled = saving || !selected.some(key => !key.enabled);
    $('keyDisableSelected').disabled = saving || !selected.some(key => key.enabled);
    $('keyDeleteSelected').disabled = saving || !selected.length;
    for (const input of $('keyRows').querySelectorAll('.key-selection input')) input.disabled = saving;
  }
  $('keySelectAll').onchange = () => {
    selectedKeyIds.clear();
    if ($('keySelectAll').checked) for (const key of keyDraft) selectedKeyIds.add(key.uiId);
    renderKeys();
  };
  function setSelectedKeysEnabled(enabled) {
    if (saving) return;
    for (const key of keyDraft) if (selectedKeyIds.has(key.uiId)) key.enabled = enabled;
    renderKeys();
  }
  $('keyEnableSelected').onclick = () => setSelectedKeysEnabled(true);
  $('keyDisableSelected').onclick = () => setSelectedKeysEnabled(false);
  async function removeKeys(keys, text, title) {
    if (saving) return;
    const targets = new Set(keys), epoch = editorEpoch;
    if (!targets.size || !await ask(text, title)) return;
    if (epoch !== editorEpoch || !dialog.open || saving) return;
    for (const key of keyDraft) if (targets.has(key) && key.id) deletedKeyIds.push(key.id);
    keyDraft = keyDraft.filter(key => !targets.has(key));
    renderKeys();
  }
  $('keyDeleteSelected').onclick = () => {
    const keys = keyDraft.filter(key => selectedKeyIds.has(key.uiId));
    return removeKeys(keys, `删除所选的 ${keys.length} 个 Key？保存渠道后生效。`, '批量删除 Key');
  };
  function renderKeys() {
    const currentIds = new Set(keyDraft.map(key => key.uiId));
    for (const id of selectedKeyIds) if (!currentIds.has(id)) selectedKeyIds.delete(id);
    const root = $('keyRows'); root.replaceChildren(); $('keyCount').textContent = keyDraft.length;
    if (!keyDraft.length) root.append(el('div', '在下方添加上游 Key', 'empty'));
    else {
      const columns = el('div', undefined, 'key-table-head'); columns.setAttribute('aria-hidden', 'true');
      for (const label of ['选择', '密钥名称', '访问密钥', '启用', '操作']) columns.append(el('span', label));
      root.append(columns);
    }
    keyDraft.forEach((key, index) => {
      const row = el('div', undefined, 'key-row card');
      row.classList.toggle('is-selected', selectedKeyIds.has(key.uiId));
      const selection = el('input'), selectionCell = el('div', undefined, 'key-selection');
      selection.type = 'checkbox'; selection.checked = selectedKeyIds.has(key.uiId);
      selection.setAttribute('aria-label', `选择 Key ${index + 1}`);
      selection.onchange = () => {
        if (selection.checked) selectedKeyIds.add(key.uiId); else selectedKeyIds.delete(key.uiId);
        row.classList.toggle('is-selected', selection.checked); syncKeySelection();
      };
      selectionCell.append(selection);
      const actions = el('div', undefined, 'actions');
      for (const [title, delta] of [['上移', -1], ['下移', 1]]) {
        const b = button('', () => { [keyDraft[index], keyDraft[index + delta]] = [keyDraft[index + delta], keyDraft[index]]; renderKeys(); }, 'icon-button');
        b.append(icon(delta < 0 ? 'chevron-up' : 'chevron-down')); b.setAttribute('aria-label', title); b.title = title;
        b.disabled = index + delta < 0 || index + delta >= keyDraft.length; actions.append(b);
      }
      const remove = button('', () => removeKeys([key], `删除「${key.name || '未命名 Key'}」？保存渠道后生效。`, '删除 Key'), 'icon-button danger');
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
      if (key.autoDisabledAt) secretCell.append(el('small', `自动禁用 · HTTP ${key.autoDisabledStatusCode} · 连续 ${key.autoDisabledFailures} 次 · ${key.autoDisabledSource === 'connection-test' ? '连接测试' : '正式请求'} · ${new Date(key.autoDisabledAt).toLocaleString()}`));
      else if (key.health?.consecutiveFailures) secretCell.append(el('small', `连续失败 ${key.health.consecutiveFailures} 次`));
      if (key.health?.saveError) secretCell.append(el('small', key.health.saveError, 'feedback error'));
      const enabled = el('input'); enabled.type = 'checkbox'; enabled.checked = key.enabled;
      enabled.onchange = () => { key.enabled = enabled.checked; fillKeyOptions($('discoveryKey'), keyDraft, false); syncKeySelection(); summary(); };
      const label = el('label', undefined, 'inline-check key-enabled');
      label.append(enabled, el('span', '启用 Key', 'key-enabled-label sr-only')); row.append(selectionCell, nameCell, secretCell, label, actions); root.append(row);
    });
    fillKeyOptions($('discoveryKey'), keyDraft, false); keyHint(); syncKeySelection(); summary();
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
          throw Error('认证方式、通用请求头或高级参数尚未保存，请使用获取候选模型或先保存渠道');
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
        : endpoints.includes('/v1/responses') ? 'responses' : endpointProtocols[endpoints[0]] || 'responses';
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
  /*__HEADERS_MAPPING__*/
  form.addEventListener('submit', async event => {
    event.preventDefault(); if (saving) return; feedback('formError');
    try {
      for (const input of form.querySelectorAll('input[required],textarea[required]')) {
        if (!input.checkValidity()) fail('请检查' + (input.labels?.[0]?.textContent || '必填字段'), input.closest('[role=tabpanel]').id.slice(6), input.id);
      }
      const endpoints = [...form.querySelectorAll('input[name=endpoints]:checked')].map(x => x.value);
      if (!endpoints.length) fail('至少选择一个端点', 'basic');
      const keys = readKeys(), models = modelIds();
      const keyAutoDisable = { enabled: $('keyAutoDisableEnabled').checked,
        statusCodes: $('keyAutoDisableCodes').value.trim(), failureThreshold: Number($('keyAutoDisableThreshold').value) };
      if (!Number.isInteger(keyAutoDisable.failureThreshold) || keyAutoDisable.failureThreshold < 1 || keyAutoDisable.failureThreshold > 100)
        fail('自动禁用阈值须为 1–100', 'keys', 'keyAutoDisableThreshold');
      if (!keyAutoDisable.statusCodes || keyAutoDisable.statusCodes.split(',').some(value => {
        const parts = value.trim().split('-').map(x => x.trim());
        return parts.length > 2 || parts.some(x => !/^[45]\d{2}$/.test(x)) || Number(parts.at(-1)) < Number(parts[0]);
      })) fail('自动禁用状态码须为 400–599 的单值或区间', 'keys', 'keyAutoDisableCodes');
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
      const payload = {
        id: form.elements.id.value || null, label: form.elements.label.value.trim(), baseUrl: form.elements.baseUrl.value.trim(),
        ...network,
        responsesTransport: form.elements.responsesTransport.value,
        stripResponsesResourceState: form.elements.stripResponsesResourceState.checked,
        keys, deletedKeyIds, keyRevision, keySelectionMode: $('keySelectionMode').value, keyAutoDisable, weight: Number(form.elements.weight.value),
        enabled: form.elements.enabled.checked, endpoints, models, modelProtocols, reasoningPolicy, systemPromptPolicy, requestPolicy, headerOverride,
        headerOverrideMode: headerPolicyMode, endpointHeaderOverrides: endpointHeaderDraft, extraParams
      };
      setEditorSaving(true);
      const saved = await api('POST', 'accounts/save', payload);
      if (saved.account?.id) {
        const index = accounts.findIndex(account => account.id === saved.account.id);
        if (index < 0) accounts.push(saved.account); else accounts[index] = saved.account;
        render();
      }
      const refreshed = await load();
      editorEpoch++; dialog.close();
      message(saved.warning || (refreshed ? '渠道已保存' : '渠道已保存，列表刷新失败，请刷新重试'), !!saved.warning || !refreshed);
    } catch (e) { feedback('formError', e.message, true); }
    finally { setEditorSaving(false); }
  });
  /*__CONNECTION_TESTS__*/
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
