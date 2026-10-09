  const emptyReasoningRule = () => ({ mode: 'off', mappings: [], defaultEffort: null, fixedEffort: null });
  let reasoningDraft = { defaults: Object.create(null), models: Object.create(null) };
  let reasoningScope = '', reasoningProtocol = 'responses', reasoningDormant = new Map();
  function reasoningSelection(model = reasoningScope, protocol = reasoningProtocol) {
    const rules = Object.hasOwn(reasoningDraft.models, model) ? reasoningDraft.models[model] : null;
    const custom = !!model && rules && Object.hasOwn(rules, protocol);
    return { source: custom ? 'model' : 'channel', rule: custom ? rules[protocol]
      : reasoningDraft.defaults[protocol] || emptyReasoningRule() };
  }
  function editableReasoningRule() {
    const rules = reasoningScope ? reasoningDraft.models[reasoningScope] : reasoningDraft.defaults;
    return rules[reasoningProtocol] ||= emptyReasoningRule();
  }
  function loadReasoningPolicy(policy) {
    reasoningDraft = {
      defaults: Object.assign(Object.create(null), structuredClone(policy?.defaults || {})),
      models: Object.assign(Object.create(null), structuredClone(policy?.models || {}))
    };
    reasoningScope = ''; reasoningProtocol = 'responses'; reasoningDormant = new Map();
    $('reasoningProtocol').value = reasoningProtocol;
    $('reasoningPreviewInput').value = 'low'; $('reasoningPreviewMissing').checked = false;
    updateReasoningModels();
  }
  function updateReasoningModels() {
    const models = modelIds(), preview = $('reasoningPreviewModel').value;
    if (!models.includes(reasoningScope)) reasoningScope = '';
    for (const id of ['reasoningScope', 'reasoningPreviewModel'])
      $(id).replaceChildren(option('', '渠道默认'), ...models.map(model => option(model, model)));
    $('reasoningScope').value = reasoningScope;
    $('reasoningPreviewModel').value = models.includes(preview) ? preview : '';
    renderReasoning();
  }
  function renderReasoning() {
    const selected = reasoningSelection(), inherited = !!reasoningScope && selected.source === 'channel', rule = selected.rule;
    $('reasoningInheritance').hidden = !reasoningScope;
    $('reasoningUseDefault').checked = inherited;
    $('reasoningFields').disabled = inherited;
    $('reasoningMode').value = rule.mode;
    $('reasoningMapSettings').hidden = rule.mode !== 'map';
    $('reasoningFixedSettings').hidden = rule.mode !== 'fixed';
    $('reasoningDefault').value = rule.defaultEffort || '';
    $('reasoningFixed').value = rule.fixedEffort || '';
    $('reasoningSource').textContent = inherited ? '继承渠道规则 · 取消勾选后可独立配置'
      : reasoningScope ? '模型独立规则 · 整条覆盖渠道规则' : '渠道默认规则 · 应用于继承此接口规则的模型';
    const root = $('reasoningRows'); root.replaceChildren();
    for (const [index, pair] of (rule.mappings || []).entries()) {
      const row = el('div', undefined, 'mapping-row'), from = el('input'), to = el('input');
      from.value = pair.from; to.value = pair.to;
      from.placeholder = '客户端等级'; to.placeholder = '发送等级';
      for (const [input, field, label] of [[from, 'from', '来源等级'], [to, 'to', '目标等级']]) {
        input.setAttribute('aria-label', label + ' ' + (index + 1)); input.setAttribute('list', 'reasoningLevels');
        input.autocomplete = 'off'; input.spellcheck = false;
        input.oninput = () => { pair[field] = input.value; previewReasoning(); };
      }
      const remove = button('×', () => { editableReasoningRule().mappings.splice(index, 1); renderReasoning(); }, 'text-button');
      remove.setAttribute('aria-label', '删除映射 ' + (index + 1));
      row.append(from, el('span', '→', 'arrow'), to, remove); root.append(row);
    }
    previewReasoning();
  }
  function normalizeReasoningRule(rule) {
    const sources = new Set();
    const mappings = (rule.mappings || []).map(pair => {
      const from = pair.from.trim(), to = pair.to.trim();
      if (!from || !to) throw Error('思考映射的来源和目标等级不能为空');
      if (sources.has(from.toLowerCase())) throw Error('思考映射来源等级重复：' + from);
      sources.add(from.toLowerCase()); return { from, to };
    });
    const normalized = { mode: rule.mode, mappings,
      defaultEffort: rule.defaultEffort?.trim() || null, fixedEffort: rule.fixedEffort?.trim() || null };
    if (normalized.mode === 'fixed' && !normalized.fixedEffort) throw Error('固定思考等级不能为空');
    return normalized;
  }
  function previewReasoning() {
    const model = $('reasoningPreviewModel').value, selected = reasoningSelection(model);
    const missing = $('reasoningPreviewMissing').checked, input = $('reasoningPreviewInput').value;
    $('reasoningPreviewInput').disabled = missing;
    const source = selected.source === 'model' ? '模型规则：' + model : '渠道默认规则';
    try {
      const rule = normalizeReasoningRule(selected.rule);
      const target = rule.mode === 'fixed' ? rule.fixedEffort : rule.mode === 'map'
        ? missing ? rule.defaultEffort : rule.mappings.find(pair => pair.from.toLowerCase() === input.trim().toLowerCase())?.to : null;
      feedback('reasoningPreview', `客户端 ${missing ? '未提供' : JSON.stringify(input)} → ${target == null ? '不改写（沿用现有处理）' : '发送 ' + JSON.stringify(target)}\n${source} · ${protocolLabels[reasoningProtocol]}`);
    } catch (error) { feedback('reasoningPreview', source + ' · ' + error.message, true); }
  }
  function readReasoningPolicy() {
    const result = { defaults: Object.create(null), models: Object.create(null) };
    const readScope = (model, rules, target) => {
      for (const [protocol, rule] of Object.entries(rules)) {
        try { target[protocol] = normalizeReasoningRule(rule); }
        catch (error) {
          reasoningScope = model; reasoningProtocol = protocol;
          $('reasoningScope').value = model; $('reasoningProtocol').value = protocol;
          $('reasoningPreviewModel').value = model; renderReasoning();
          throw Error(`${model || '渠道默认'} · ${protocolLabels[protocol]}：${error.message}`);
        }
      }
    };
    readScope('', reasoningDraft.defaults, result.defaults);
    for (const model of modelIds()) {
      if (!Object.hasOwn(reasoningDraft.models, model)) continue;
      const rules = reasoningDraft.models[model];
      if (!Object.keys(rules).length) continue;
      readScope(model, rules, result.models[model] = Object.create(null));
    }
    return result;
  }
  $('reasoningScope').onchange = () => {
    reasoningScope = $('reasoningScope').value; $('reasoningPreviewModel').value = reasoningScope; renderReasoning();
  };
  $('reasoningProtocol').onchange = () => { reasoningProtocol = $('reasoningProtocol').value; renderReasoning(); };
  $('reasoningUseDefault').onchange = () => {
    const key = JSON.stringify([reasoningScope, reasoningProtocol]);
    if ($('reasoningUseDefault').checked) {
      reasoningDormant.set(key, structuredClone(editableReasoningRule()));
      delete reasoningDraft.models[reasoningScope][reasoningProtocol];
    } else {
      const rule = structuredClone(reasoningDormant.get(key) || reasoningSelection().rule);
      if (!Object.hasOwn(reasoningDraft.models, reasoningScope)) reasoningDraft.models[reasoningScope] = Object.create(null);
      reasoningDraft.models[reasoningScope][reasoningProtocol] = rule;
    }
    renderReasoning();
  };
  $('reasoningMode').onchange = () => { editableReasoningRule().mode = $('reasoningMode').value; renderReasoning(); };
  $('reasoningAdd').onclick = () => {
    editableReasoningRule().mappings.push({ from: '', to: '' }); renderReasoning();
    $('reasoningRows').lastElementChild.firstElementChild.focus();
  };
  for (const [id, field] of [['reasoningDefault', 'defaultEffort'], ['reasoningFixed', 'fixedEffort']])
    $(id).oninput = () => { editableReasoningRule()[field] = $(id).value; previewReasoning(); };
  $('reasoningPreviewModel').onchange = previewReasoning;
  $('reasoningPreviewMissing').onchange = previewReasoning;
  $('reasoningPreviewInput').oninput = previewReasoning;
