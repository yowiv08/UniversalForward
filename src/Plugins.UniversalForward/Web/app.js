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
  const feedback = (id, text = '', error = false) => {
    const n = $(id); n.textContent = text; n.hidden = !text;
    n.className = 'feedback' + (error ? ' error' : '');
  };
  const message = (text, error = false) => feedback('message', text, error);
  const api = (method, path, body) => {
    if (!window.Router2API?.request) return Promise.reject(Error('页面请求桥未就绪，请重新打开插件'));
    return window.Router2API.request(method, path, body);
  };
  const keyModes = { roundRobin: '轮询', random: '随机', priority: '主备顺序' };
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
      'User-Agent': 'Codex Desktop/0.146.0-alpha.9.2 (Windows 10.0.26200; x86_64) unknown (Codex Desktop; 26.727.51351)',
      Originator: 'codex_exec', 'X-Codex-Beta-Features': 'remote_compaction_v2',
      'X-OpenAI-Internal-Codex-Responses-Lite': 'true',
      'Session-Id': '{session_id}', 'Thread-Id': '{thread_id}', 'X-Client-Request-Id': '{session_id}',
      'X-Codex-Window-Id': '{window_id}', 'X-Codex-Turn-Metadata': '{codex_turn_metadata}'
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
  let confirmResolve = null, loading = false, saving = false;
  const switchingChannels = new Set();
  const button = (text, work, cls = 'secondary') => {
    const b = el('button', text, cls); b.type = 'button'; b.addEventListener('click', work); return b;
  };
  const option = (value, title) => { const n = el('option', title); n.value = value; return n; };
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
  $('copyClose').onclick = () => $('copyDialog').close();

  function selectTab(name, focus = false) {
    for (const tab of document.querySelectorAll('[data-tab]')) {
      const selected = tab.dataset.tab === name;
      tab.setAttribute('aria-selected', String(selected)); tab.tabIndex = selected ? 0 : -1;
      $('panel-' + tab.dataset.tab).hidden = !selected;
      if (selected && focus) tab.focus();
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
    $('metricChannels').textContent = accounts.length;
    $('metricEnabled').textContent = accounts.filter(a => a.enabled).length;
    $('metricKeys').textContent = accounts.reduce((sum, a) => sum + (a.keys || []).filter(k => k.enabled).length, 0);
    $('metricModels').textContent = new Set(accounts.flatMap(a => a.models || [])).size;
    const q = $('filter').value.toLowerCase(), status = $('statusFilter').value;
    const visible = accounts.filter(a => [a.label, a.baseUrl, ...(a.models || [])].join(' ').toLowerCase().includes(q)
      && (status === 'all' || !!a.enabled === (status === 'enabled')));
    $('channelCount').textContent = visible.length;
    const root = $('cards'); root.replaceChildren();
    if (!visible.length) { root.append(el('div', accounts.length ? '没有匹配的渠道' : '添加第一个渠道，开始配置上游连接', 'empty')); return; }
    for (const a of visible) {
      const card = el('article', undefined, 'channel-card'), top = el('div', undefined, 'channel-top');
      const title = el('div', undefined, 'channel-title');
      title.append(el('h3', a.label));
      top.append(title,
        el('span', a.enabled ? '已启用' : '已禁用', 'badge' + (a.enabled ? '' : ' off')));
      const address = el('p', a.baseUrl, 'channel-url');
      address.title = a.baseUrl;
      const meta = el('div', undefined, 'channel-meta');
      for (const [label, value] of [['可用 Key', `${(a.keys || []).filter(k => k.enabled).length}/${a.keys?.length || 0}`],
        ['分配', keyModes[a.keySelectionMode] || '轮询'], ['权重', a.weight]]) {
        const item = el('span', label); item.append(el('b', value)); meta.append(item);
      }
      const tags = el('div', undefined, 'model-tags');
      for (const model of (a.models || []).slice(0, 3)) tags.append(el('span', model, 'model-tag'));
      if (a.models?.length > 3) tags.append(el('span', `+${a.models.length - 3}`, 'model-tag'));
      const endpoints = el('div', undefined, 'channel-endpoints');
      for (const endpoint of a.endpoints || []) endpoints.append(el('span', endpoint.replace(/^\/v1\//, ''), 'endpoint-tag'));
      const modelHeading = el('div', undefined, 'between channel-model-heading');
      modelHeading.append(el('h4', '已配置模型'), el('strong', `${a.models?.length || 0} 个`));
      if (!a.models?.length) tags.append(el('span', '暂无模型', 'muted'));
      const actions = el('div', undefined, 'actions');
      const toggle = button(switchingChannels.has(a.id) ? '处理中…' : a.enabled ? '禁用' : '启用', () => setChannelEnabled(a));
      toggle.setAttribute('aria-label', `${a.enabled ? '禁用' : '启用'}渠道「${a.label}」`);
      toggle.setAttribute('aria-busy', String(switchingChannels.has(a.id)));
      actions.append(button('编辑', () => open(a)), button('测试连接', () => openTests(a)),
        button('模型', () => { open(a); selectTab('models'); }),
        toggle,
        button('删除', async () => {
          if (!await ask(`删除渠道「${a.label}」及其配置？`, '删除渠道')) return;
          try { await api('POST', 'accounts/delete', { id: a.id }); await load(); message('渠道已删除'); }
          catch (e) { message(e.message, true); }
        }, 'text-button danger'));
      if (switchingChannels.has(a.id)) for (const control of actions.querySelectorAll('button')) control.disabled = true;
      card.append(top, address, meta, endpoints, modelHeading, tags, actions); root.append(card);
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
    loading = true; $('reload').disabled = true;
    try { const data = await api('GET', 'accounts'); accounts = data.accounts || []; render(); }
    catch (e) { message(e.message, true); }
    finally { loading = false; $('reload').disabled = false; }
  }
  function open(account) {
    editorEpoch++; savedAccount = account; form.reset(); saving = false;
    $('discover').disabled = false;
    for (const id of ['formError', 'discoveryStatus', 'protocolStatus', 'headerStatus', 'mappingStatus']) feedback(id);
    $('formTitle').textContent = account ? '编辑渠道' : '添加渠道';
    for (const name of ['id', 'label', 'baseUrl']) form.elements[name].value = account?.[name] || '';
    form.elements.weight.value = account?.weight ?? 100;
    form.elements.enabled.checked = account?.enabled ?? true;
    form.elements.models.value = (account?.models || []).join('\n');
    extraParams = typeof account?.extraParams === 'string' ? objectJson(account.extraParams, '高级参数') : account?.extraParams || {};
    keyDraft = (account?.keys || []).map(k => ({ ...k, secret: '', uiId: 'draft:' + ++keySequence }));
    deletedKeyIds = []; keyRevision = account?.keyRevision ?? 0;
    modelProtocolDraft = Object.assign(Object.create(null), structuredClone(account?.modelProtocols || {}));
    candidates = account?.availableModels || [];
    $('keySelectionMode').value = account?.keySelectionMode || 'roundRobin';
    for (const [name, fallback] of Object.entries(defaults)) form.elements[name].value = account?.requestPolicy?.[name] ?? fallback;
    for (const name of ['rateLimitRetryEnabled', 'emptyResponseRetryEnabled']) form.elements[name].checked = account?.requestPolicy?.[name] ?? false;
    updateResponseRetry();
    headerRows = Object.entries(account?.headerOverride || {}).map(([name, value]) => ({ name, value }));
    headerMode = 'visual'; $('headerOverride').value = JSON.stringify(account?.headerOverride || {}, null, 2);
    mappingRows = Object.entries(account?.requestPolicy?.statusCodeMapping || {}).map(([from, to]) => ({ from, to: String(to) }));
    mappingMode = 'visual'; $('statusCodeMapping').value = JSON.stringify(account?.requestPolicy?.statusCodeMapping || {}, null, 2);
    const selected = new Set(account?.endpoints || paths), root = $('endpoints'); root.replaceChildren();
    paths.forEach((path, index) => {
      const label = el('label', undefined, 'endpoint-option'), input = el('input'), text = el('span', Object.values(protocolLabels)[index]);
      input.type = 'checkbox'; input.name = 'endpoints'; input.value = path; input.checked = selected.has(path);
      text.append(el('code', path)); label.append(input, text); root.append(label);
    });
    $('refreshModels').disabled = !account;
    renderKeys(); showCandidates(); renderProtocols(); renderHeaders(); renderMappings(); updateModes(); summary();
    selectTab('basic'); if (!dialog.open) dialog.showModal();
  }
  function closeEditor() { if (saving) return; editorEpoch++; dialog.close(); }
  $('close').onclick = closeEditor; $('cancel').onclick = closeEditor;
  dialog.addEventListener('cancel', e => { e.preventDefault(); closeEditor(); });
  $('add').onclick = () => open(null); $('reload').onclick = load;
  $('filter').oninput = render; $('statusFilter').onchange = render;

  function fillKeyOptions(select, keys, testing) {
    const previous = select.value; select.replaceChildren();
    if (testing) select.append(option('strategy', '按渠道策略'), option('all', '全部启用 Key'));
    else select.append(option('', '第一个启用 Key'));
    for (const k of keys.filter(k => k.enabled))
      select.append(option(testing ? 'key:' + k.id : k.id || k.uiId, `${k.name || '未命名 Key'}${k.masked ? ' · ' + k.masked : ''}`));
    if ([...select.options].some(o => o.value === previous)) select.value = previous;
  }
  function keyHint() {
    $('keyModeHint').textContent = { roundRobin: '按启用 Key 的顺序依次分配', random: '随机选择一个启用 Key', priority: '使用排序最前的启用 Key' }[$('keySelectionMode').value];
  }
  function renderKeys() {
    const root = $('keyRows'); root.replaceChildren(); $('keyCount').textContent = keyDraft.length;
    if (!keyDraft.length) root.append(el('div', '在下方添加上游 Key', 'empty'));
    keyDraft.forEach((key, index) => {
      const row = el('div', undefined, 'key-row card'), head = el('div', undefined, 'key-row-head');
      const actions = el('div', undefined, 'actions');
      for (const [title, delta] of [['上移', -1], ['下移', 1]]) {
        const b = button(title, () => { [keyDraft[index], keyDraft[index + delta]] = [keyDraft[index + delta], keyDraft[index]]; renderKeys(); });
        b.disabled = index + delta < 0 || index + delta >= keyDraft.length; actions.append(b);
      }
      actions.append(button('删除 Key', async () => {
        if (!await ask(`删除「${key.name || '未命名 Key'}」？`, '删除 Key')) return;
        if (key.id) deletedKeyIds.push(key.id);
        keyDraft = keyDraft.filter(k => k !== key); renderKeys();
      }, 'text-button danger'));
      head.append(el('strong', `${String(index + 1).padStart(2, '0')} · ${key.masked || '新 Key'}`), actions);
      const fields = el('div', undefined, 'field-row'), name = el('input'), secret = el('input');
      name.value = key.name; name.placeholder = 'Key 名称'; name.setAttribute('aria-label', `Key ${index + 1} 名称`);
      name.oninput = () => { key.name = name.value; fillKeyOptions($('discoveryKey'), keyDraft, false); };
      secret.type = 'password'; secret.autocomplete = 'new-password'; secret.value = key.secret;
      secret.placeholder = key.id ? '留空保留密钥' : '输入密钥'; secret.setAttribute('aria-label', `Key ${index + 1} 密钥`);
      secret.oninput = () => key.secret = secret.value;
      fields.append(name, secret);
      const enabled = el('input'); enabled.type = 'checkbox'; enabled.checked = key.enabled;
      enabled.onchange = () => { key.enabled = enabled.checked; fillKeyOptions($('discoveryKey'), keyDraft, false); summary(); };
      const label = el('label', undefined, 'inline-check'); label.style.marginTop = '10px';
      label.append(enabled, document.createTextNode('启用 Key')); row.append(head, fields, label); root.append(row);
    });
    fillKeyOptions($('discoveryKey'), keyDraft, false); keyHint(); summary();
  }
  $('keySelectionMode').onchange = keyHint;
  $('keyAdd').onclick = () => {
    try {
      const lines = $('keyBatch').value.split(/\r?\n/).map(x => x.trim()).filter(Boolean);
      if (!lines.length) throw Error('请先填写 Key');
      if (keyDraft.length + lines.length > 100) throw Error('每渠道最多100个 Key');
      const seen = new Set(keyDraft.map(k => k.secret.trim()).filter(Boolean));
      for (const secret of lines) { if (seen.has(secret)) throw Error('批量内容含重复 Key'); seen.add(secret); }
      for (const secret of lines) keyDraft.push({ id: '', uiId: 'draft:' + ++keySequence, name: 'Key ' + (keyDraft.length + 1), secret, enabled: true });
      $('keyBatch').value = ''; feedback('formError'); renderKeys();
    } catch (e) { feedback('formError', e.message, true); }
  };
  function readKeys() {
    if ($('keyBatch').value.trim()) fail('请先将批量 Key 添加到列表', 'keys', 'keyBatch');
    const seen = new Set();
    return keyDraft.map(({ id, name, secret, enabled }) => {
      secret = secret.trim();
      if (!id && !secret) fail('新增 Key 必须填写密钥', 'keys');
      if (secret && seen.has(secret)) fail('Key 列表包含重复密钥', 'keys');
      if (secret) seen.add(secret);
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
    try {
      const keyId = $('discoveryKey').value;
      const key = keyId ? keyDraft.find(k => (k.id || k.uiId) === keyId && k.enabled) : keyDraft.find(k => k.enabled);
      if (!key) throw Error('请先在 Key 管理中添加并启用 Key');
      const baseUrl = form.elements.baseUrl.value.trim();
      if (!/^https?:\/\//i.test(baseUrl)) throw Error('请先填写有效的 Base URL');
      let body, route;
      if (refresh) {
        if (!savedAccount) throw Error('刷新候选需要先保存渠道');
        if (!key.id || key.secret.trim() || baseUrl.replace(/\/+$/, '') !== savedAccount.baseUrl.replace(/\/+$/, ''))
          throw Error('当前连接配置尚未保存，请使用获取候选模型');
        route = 'models/refresh'; body = { id: savedAccount.id, keyId: key.id };
      } else {
        route = 'models/discover';
        body = { id: savedAccount?.id || null, baseUrl, headerOverride: readHeaders(), extraParams };
        if (key.secret.trim()) body.apiKey = key.secret.trim(); else body.keyId = key.id;
      }
      const data = await api('POST', route, body);
      if (epoch !== editorEpoch || !dialog.open) return;
      candidates = [...new Set((data.models || []).filter(x => typeof x === 'string' && x.trim()))];
      showCandidates();
      feedback('discoveryStatus', candidates.length ? `已获取 ${candidates.length} 个候选模型，点击模型即可选择` : '上游返回了空模型列表，可直接手动填写');
    } catch (e) { if (epoch === editorEpoch) feedback('discoveryStatus', e.message, true); }
    finally { if (epoch === editorEpoch) { $('discover').disabled = false; $('refreshModels').disabled = !savedAccount; } }
  }
  $('discover').onclick = () => discover(false); $('refreshModels').onclick = () => discover(true);
  $('models').oninput = () => { showCandidates(); renderProtocols(); summary(); };
  $('syncProtocols').onclick = () => {
    renderProtocols();
    feedback('protocolStatus', modelIds().length ? `已同步 ${modelIds().length} 个模型的协议配置` : '请先填写允许模型或选择候选模型', !modelIds().length);
    if (!modelIds().length) $('models').focus();
  };
  function renderProtocols() {
    const root = $('modelProtocols'); root.replaceChildren(); $('protocolCount').textContent = modelIds().length;
    if (!modelIds().length) { root.append(el('div', '添加模型后在这里配置协议', 'empty')); return; }
    for (const model of modelIds()) {
      const configured = modelProtocolDraft[model], value = configured || { protocols: ['responses'], preferredProtocol: 'responses', nativeOnly: false };
      const row = el('div', undefined, 'protocol-row'); row.append(el('strong', model));
      const enabled = el('input'); enabled.type = 'checkbox'; enabled.checked = !!configured;
      const label = el('label', undefined, 'inline-check'); label.append(enabled, document.createTextNode('指定支持协议')); row.append(label);
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
    const root = $('headerRows'); root.replaceChildren();
    if (!headerRows.length) root.append(el('div', '使用模板快速配置，或添加自定义请求头', 'empty'));
    headerRows.forEach((row, index) => {
      const line = el('div', undefined, 'header-row'), name = el('input'), value = el('input');
      name.value = row.name; name.placeholder = '请求头名称'; name.setAttribute('aria-label', `请求头 ${index + 1} 名称`);
      value.value = typeof row.value === 'string' ? row.value : JSON.stringify(row.value);
      value.placeholder = '值'; value.setAttribute('aria-label', `请求头 ${index + 1} 值`);
      name.oninput = () => row.name = name.value;
      value.oninput = () => { row.value = value.value; };
      const remove = button('×', () => { headerRows.splice(index, 1); renderHeaders(); }, 'icon-button danger');
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
  function statusInput(value, target, index) {
    const input = el('input'); input.type = 'text'; input.inputMode = 'numeric';
    input.setAttribute('aria-label', `映射 ${index + 1} ${target ? '目标' : '原始'}状态码`);
    input.placeholder = target ? '目标状态码' : '原始状态码';
    input.value = String(value); return input;
  }
  function renderMappings() {
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
  $('headerAdd').onclick = () => { headerRows.push({ name: '', value: '' }); renderHeaders(); $('headerRows').lastElementChild.querySelector('input').focus(); };
  $('mappingAdd').onclick = () => { mappingRows.push({ from: '', to: '' }); renderMappings(); };
  $('headerFormat').onclick = () => setMode('header', 'json'); $('mappingFormat').onclick = () => setMode('mapping', 'json');
  async function setTemplate(name) {
    if ((headerRows.length || $('headerOverride').value.trim() !== '{}') && !await ask('用所选模板替换当前请求头？', '应用模板')) return;
    headerRows = Object.entries(templates[name]).map(([name, value]) => ({ name, value }));
    $('headerOverride').value = JSON.stringify(templates[name], null, 2);
    renderHeaders(); feedback('headerStatus', `已应用 ${name === 'codex' ? 'Codex' : 'Claude Code'} 模板`);
  }
  $('templateCodex').onclick = () => setTemplate('codex'); $('templateClaude').onclick = () => setTemplate('claude');
  $('headerClear').onclick = async () => {
    if (!await ask('清空当前请求头覆盖配置？', '清空请求头')) return;
    headerRows = []; $('headerOverride').value = '{}'; renderHeaders(); feedback('headerStatus', '请求头已清空');
  };
  $('headerCopy').onclick = async () => {
    try {
      const text = JSON.stringify(readHeaders(), null, 2);
      try { await navigator.clipboard.writeText(text); feedback('headerStatus', '已复制请求头'); }
      catch { $('copyContent').value = text; $('copyDialog').showModal(); $('copyContent').focus(); $('copyContent').select(); }
    } catch (e) { feedback('headerStatus', e.message, true); }
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
      let headerOverride, statusCodeMapping;
      try { headerOverride = readHeaders(); } catch (e) { fail(e.message, 'headers'); }
      try { statusCodeMapping = readMapping(); } catch (e) { fail(e.message, 'policy'); }
      const requestPolicy = { statusCodeMapping };
      for (const name of ['rateLimitRetryEnabled', 'emptyResponseRetryEnabled']) requestPolicy[name] = form.elements[name].checked;
      for (const name of Object.keys(defaults)) requestPolicy[name] = name === 'retryStatusCodes' ? form.elements[name].value : Number(form.elements[name].value);
      saving = true; $('save').disabled = true; $('save').textContent = '保存中…';
      const saved = await api('POST', 'accounts/save', {
        id: form.elements.id.value || null, label: form.elements.label.value.trim(), baseUrl: form.elements.baseUrl.value.trim(),
        keys, deletedKeyIds, keyRevision, keySelectionMode: $('keySelectionMode').value, weight: Number(form.elements.weight.value),
        enabled: form.elements.enabled.checked, endpoints, models, modelProtocols, requestPolicy, headerOverride, extraParams
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
    for (const id of ['testAll', 'testSelected', 'testEndpoint', 'testStream', 'testKey', 'testSelectAll']) $(id).disabled = testBusy();
    $('testCancel').disabled = !testState.active || testState.cancelling;
  }
  function renderTests() {
    const root = $('testRows'); root.replaceChildren(); const q = $('testFilter').value.toLowerCase();
    for (const model of testState.account?.models || []) {
      if (!model.toLowerCase().includes(q)) continue;
      const row = el('tr'), cell = el('td'), select = el('input'); select.type = 'checkbox';
      select.checked = testState.selected.has(model); select.disabled = testBusy(); select.setAttribute('aria-label', '选择 ' + model);
      select.onchange = () => { if (select.checked) testState.selected.add(model); else testState.selected.delete(model); };
      cell.append(select);
      const results = [...testState.rows.values()].filter(r => r.model === model);
      const pending = testState.active && testState.targets.has(model) && results.length < testState.combinations.filter(c => c.model === model).length;
      const state = pending ? testState.running === model ? '测试中' : '排队中' : results.length
        ? results.every(r => r.cancelled) ? '已取消' : results.every(r => r.success) ? '成功' : '失败' : '未测试';
      const summaryCell = el('td');
      for (const r of results) summaryCell.append(el('div',
        `${r.keyName || r.keyId || '按策略'} · ${r.cancelled ? '已取消' : r.success ? '成功' : '失败'} · ${r.originalStatus ?? '—'} → ${r.mappedStatus ?? '—'} · ${r.durationMs ?? '—'}ms · 重试 ${r.retries ?? 0}次`, 'result-line'));
      const ops = el('td'), run = button('测试', () => startTests([model])); run.disabled = testBusy(); ops.append(run);
      if (results.length) ops.append(button('详情', () => {
        $('testDetails').textContent = results.map(r =>
          `${r.keyName || r.keyId || '按策略'}\n${r.response ?? r.error ?? '未收到上游响应'}${r.responseTruncated ? '\n[原始响应超过 32 MiB，展示已截断]' : ''}`
        ).join('\n\n'); $('testDetails').parentElement.open = true;
      }, 'text-button'));
      row.append(cell, el('td', model), el('td', state), summaryCell, ops); root.append(row);
    }
    updateTestControls();
  }
  function openTests(account) {
    if (testBusy() && testState.account?.id !== account.id) { message('请先取消或等待当前渠道测试完成', true); return; }
    $('testTitle').textContent = '测试连接 · ' + account.label;
    if (!testBusy()) {
      fillKeyOptions($('testKey'), account.keys || [], true);
      if (testState.account?.id !== account.id) {
        clearTimeout(testState.timer); testState.epoch++;
        Object.assign(testState, { account, selected: new Set(), rows: new Map(), targets: new Set(), combinations: [], jobId: null, active: false, running: null });
        feedback('testProgress'); feedback('testError'); $('testDetails').textContent = '';
        $('testFilter').value = ''; $('testStream').checked = false;
      } else testState.account = account;
      $('testEndpoint').replaceChildren(option('', '自动选择'));
      for (const endpoint of account.endpoints || []) $('testEndpoint').append(option(endpoint, endpoint));
    }
    renderTests(); if (!$('testDialog').open) $('testDialog').showModal();
  }
  async function startTests(models) {
    if (testBusy()) return;
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
      const job = await api('POST', 'tests/start', { accountId: testState.account.id, models, keyMode, keyId, endpoint: $('testEndpoint').value || null, stream: $('testStream').checked });
      testState.jobId = job.id; testState.active = true; testState.epoch++;
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
  $('testSelected').onclick = () => startTests([...testState.selected]); $('testFilter').oninput = renderTests;
  $('testSelectAll').onchange = () => {
    for (const model of testState.account.models || []) if (model.toLowerCase().includes($('testFilter').value.toLowerCase())) {
      if ($('testSelectAll').checked) testState.selected.add(model); else testState.selected.delete(model);
    }
    renderTests();
  };
  $('testResume').onclick = () => pollTests(testState.epoch);
  $('testClose').onclick = () => $('testDialog').close();
  $('testCancel').onclick = async () => {
    testState.cancelling = true; updateTestControls();
    try {
      await api('POST', 'tests/cancel', { id: testState.jobId });
      feedback('testProgress', '已申请取消，正在等待任务退出'); await pollTests(testState.epoch);
    } catch (e) { testState.cancelling = false; feedback('testError', e.message, true); updateTestControls(); }
  };
  load();
})();
