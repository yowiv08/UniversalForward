  // Header overrides (shared / per-endpoint, templates, visual & JSON) and status code mapping.
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

  let headerMode = 'visual', mappingMode = 'visual', headerRows = [], mappingRows = [];
  let headerPolicyMode = 'shared', headerScope = 'common', commonHeaderDraft = {};
  let endpointHeaderDraft = Object.create(null);

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
    for (const btn of document.querySelectorAll('[data-header-scope]'))
      btn.setAttribute('aria-pressed', String(btn.dataset.headerScope === headerScope));
    $('headerInheritance').hidden = headerScope === 'common';
    $('headerUseCommon').checked = inherited;
    $('headerScopeTitle').textContent = headerScope === 'common' ? '通用配置'
      : protocolLabels[endpointProtocols[headerScope]];
    $('headerScopeHint').textContent = headerScope === 'common'
      ? headerPolicyMode === 'shared' ? '应用于所有接口及模型获取' : '应用于继承通用配置的接口及模型获取'
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
    renderHeaders();
    updateModes();
  }

  function selectHeaderScope(mode, scope) {
    try {
      commitHeaderDraft();
      headerPolicyMode = mode;
      headerScope = mode === 'shared' ? 'common' : scope;
      loadHeaderScope();
      feedback('headerStatus');
    } catch (e) { feedback('headerStatus', e.message, true); }
  }

  $('headerShared').onclick = () => selectHeaderScope('shared', 'common');
  $('headerPerEndpoint').onclick = () => selectHeaderScope('perEndpoint', 'common');
  for (const btn of document.querySelectorAll('[data-header-scope]')) {
    btn.onclick = () => selectHeaderScope('perEndpoint', btn.dataset.headerScope);
    btn.onkeydown = event => {
      if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return;
      event.preventDefault();
      const buttons = [...document.querySelectorAll('[data-header-scope]')], index = buttons.indexOf(btn);
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
      loadHeaderScope();
      feedback('headerStatus');
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
        seen.add(name.toLowerCase());
        result[name] = row.value;
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
      name.oninput = () => { row.name = name.value; };
      value.oninput = () => { row.value = value.value; };
      const remove = button('×', () => { headerRows.splice(index, 1); renderHeaders(); }, 'icon-button danger');
      remove.disabled = inherited;
      remove.setAttribute('aria-label', `删除请求头 ${index + 1}`);
      line.append(name, value, remove);
      root.append(line);
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
    input.value = String(value);
    return input;
  }

  function renderMappings() {
    updateMappingHint();
    const root = $('mappingRows'); root.replaceChildren();
    if (!mappingRows.length) root.append(el('div', '保持上游状态码，或添加映射规则', 'empty'));
    mappingRows.forEach((row, index) => {
      const line = el('div', undefined, 'mapping-row'), from = statusInput(row.from, false, index), to = statusInput(row.to, true, index);
      from.oninput = () => { row.from = from.value; };
      to.oninput = () => { row.to = to.value; };
      const remove = button('×', () => { mappingRows.splice(index, 1); renderMappings(); }, 'icon-button danger');
      remove.setAttribute('aria-label', `删除映射 ${index + 1}`);
      line.append(from, el('span', '→', 'arrow'), to, remove);
      root.append(line);
    });
  }

  function updateModes() {
    for (const [prefix, mode] of [['header', headerMode], ['mapping', mappingMode]]) {
      $(prefix + 'VisualPane').hidden = mode !== 'visual';
      $(prefix + 'JsonPane').hidden = mode !== 'json';
      $(prefix + 'Visual').setAttribute('aria-pressed', String(mode === 'visual'));
      $(prefix + 'Json').setAttribute('aria-pressed', String(mode === 'json'));
    }
  }

  function setMode(kind, mode) {
    try {
      if (kind === 'header') {
        const value = readHeaders();
        headerRows = Object.entries(value).map(([name, val]) => ({ name, value: val }));
        $('headerOverride').value = JSON.stringify(value, null, 2);
        headerMode = mode;
        renderHeaders();
      } else {
        const value = readMapping();
        mappingRows = Object.entries(value).map(([from, to]) => ({ from, to: String(to) }));
        $('statusCodeMapping').value = JSON.stringify(value, null, 2);
        mappingMode = mode;
        renderMappings();
      }
      updateModes();
      feedback(kind + 'Status');
    } catch (e) { feedback(kind + 'Status', e.message, true); }
  }

  $('headerVisual').onclick = () => setMode('header', 'visual');
  $('headerJson').onclick = () => setMode('header', 'json');
  $('mappingVisual').onclick = () => setMode('mapping', 'visual');
  $('mappingJson').onclick = () => setMode('mapping', 'json');
  $('headerAdd').onclick = () => {
    if (headerInherited()) return;
    headerRows.push({ name: '', value: '' });
    renderHeaders();
    $('headerRows').lastElementChild.querySelector('input').focus();
  };
  $('mappingAdd').onclick = () => { mappingRows.push({ from: '', to: '' }); renderMappings(); };
  $('headerFormat').onclick = () => setMode('header', 'json');
  $('mappingFormat').onclick = () => setMode('mapping', 'json');
  $('mappingRows').addEventListener('input', updateMappingHint);
  $('statusCodeMapping').addEventListener('input', updateMappingHint);

  async function setTemplate(name) {
    if (headerInherited()) return;
    const epoch = editorEpoch, scope = headerScope, mode = headerPolicyMode;
    if ((headerRows.length || $('headerOverride').value.trim() !== '{}') && !await ask('用所选模板替换当前请求头？', '应用模板')) return;
    if (epoch !== editorEpoch || scope !== headerScope || mode !== headerPolicyMode || headerInherited()) return;
    headerRows = Object.entries(templates[name]).map(([k, v]) => ({ name: k, value: v }));
    $('headerOverride').value = JSON.stringify(templates[name], null, 2);
    commitHeaderDraft();
    renderHeaders();
    feedback('headerStatus', `已应用 ${name === 'codex' ? 'Codex' : 'Claude Code'} 模板`);
  }
  $('templateCodex').onclick = () => setTemplate('codex');
  $('templateClaude').onclick = () => setTemplate('claude');

  $('headerClear').onclick = async () => {
    if (headerInherited()) return;
    const epoch = editorEpoch, scope = headerScope, mode = headerPolicyMode;
    if (!await ask('清空当前请求头覆盖配置？', '清空请求头')) return;
    if (epoch !== editorEpoch || scope !== headerScope || mode !== headerPolicyMode || headerInherited()) return;
    headerRows = [];
    $('headerOverride').value = '{}';
    commitHeaderDraft();
    renderHeaders();
    feedback('headerStatus', '请求头已清空');
  };

  $('headerCopy').onclick = async () => {
    const epoch = editorEpoch;
    try {
      const text = JSON.stringify(readHeaders(), null, 2);
      await copyText(text);
      if (epoch === editorEpoch && dialog.open) feedback('headerStatus', '已复制请求头');
    } catch (e) { if (epoch === editorEpoch && dialog.open) feedback('headerStatus', e.message, true); }
  };
