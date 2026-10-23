  const effortLevels = [['none', '无'], ['minimal', '极低'], ['low', '低'], ['medium', '中等'],
    ['high', '高'], ['xhigh', '更高'], ['max', '最大'], ['ultra', '极高']];
  const effortScrollContainer = document.querySelector('.editor-content');
  let activeEffortPicker = null, choosingEffort = false;
  const effortPopover = el('div', undefined, 'effort-popover');
  effortPopover.hidden = true; effortPopover.setAttribute('popover', 'manual');
  const effortHeading = el('div', undefined, 'effort-popover-heading'), effortHeadingTitle = el('span', '常用等级');
  const effortSearchHint = el('span', '输入筛选'); effortSearchHint.prepend(icon('search'));
  effortHeading.append(effortHeadingTitle, effortSearchHint);
  const effortOptions = el('div', undefined, 'effort-options');
  effortOptions.id = 'reasoningEffortOptions'; effortOptions.setAttribute('role', 'listbox');
  effortOptions.setAttribute('aria-label', '思考等级建议');
  effortPopover.append(effortHeading, effortOptions, el('div', '↑↓ 选择 · Enter 确认 · 支持自定义值', 'effort-popover-footer'));
  dialog.append(effortPopover);

  function closeEffortPicker() {
    if (!activeEffortPicker) return;
    activeEffortPicker.input.setAttribute('aria-expanded', 'false');
    activeEffortPicker.input.removeAttribute('aria-activedescendant');
    activeEffortPicker.control.classList.remove('is-open');
    if (typeof effortPopover.hidePopover === 'function' && effortPopover.matches(':popover-open')) effortPopover.hidePopover();
    effortPopover.hidden = true; activeEffortPicker = null;
  }
  function positionEffortPicker() {
    if (!activeEffortPicker) return;
    const bounds = document.querySelector('.editor-content').getBoundingClientRect();
    const field = activeEffortPicker.control.getBoundingClientRect();
    const left = Math.max(8, bounds.left + 8), right = Math.min(innerWidth - 8, bounds.right - 8);
    const top = Math.max(8, bounds.top + 8), bottom = Math.min(innerHeight - 8, bounds.bottom - 8);
    if (field.bottom < top || field.top > bottom || right <= left) { closeEffortPicker(); return; }
    const width = Math.min(Math.max(field.width, 260), right - left);
    effortPopover.style.width = width + 'px';
    effortPopover.style.left = Math.max(left, Math.min(field.left, right - width)) + 'px';
    effortPopover.classList.remove('is-compact'); effortPopover.style.maxHeight = '352px';
    const naturalHeight = effortPopover.getBoundingClientRect().height;
    const below = bottom - field.bottom - 6, above = field.top - top - 6;
    const opensBelow = below >= Math.min(naturalHeight, 200) || below >= above;
    const available = opensBelow ? below : above;
    if (available < 36) { closeEffortPicker(); return; }
    effortPopover.classList.toggle('is-compact', available < 160);
    effortPopover.style.maxHeight = Math.min(352, available) + 'px';
    const height = effortPopover.getBoundingClientRect().height;
    effortPopover.style.top = (opensBelow ? field.bottom + 6 : field.top - height - 6) + 'px';
  }
  function highlightEffort(index, scroll = false) {
    if (!activeEffortPicker) return;
    activeEffortPicker.index = index;
    const rows = [...effortOptions.querySelectorAll('[role=option]')];
    rows.forEach((row, i) => row.classList.toggle('is-active', i === index));
    const row = rows[index];
    if (!row) { activeEffortPicker.input.removeAttribute('aria-activedescendant'); return; }
    activeEffortPicker.input.setAttribute('aria-activedescendant', row.id);
    if (scroll) row.scrollIntoView({ block: 'nearest' });
  }
  function chooseEffort(value) {
    const input = activeEffortPicker?.input;
    if (!input || input.matches(':disabled')) { closeEffortPicker(); return; }
    input.value = value; closeEffortPicker(); input.focus({ preventScroll: true });
    choosingEffort = true;
    try { input.dispatchEvent(new Event('input', { bubbles: true })); }
    finally { choosingEffort = false; }
  }
  function openEffortPicker(input, filter = false) {
    if (input.matches(':disabled') || !dialog.open) return;
    if (activeEffortPicker?.input !== input) closeEffortPicker();
    const control = input.closest('.effort-combobox'), value = input.value.trim();
    const query = filter ? value.toLowerCase() : '';
    const items = effortLevels.filter(([level]) => level.includes(query)).map(([value, hint]) => ({ value, hint }));
    if (value && !effortLevels.some(([level]) => level === value.toLowerCase())) items.push({ value, custom: true });
    activeEffortPicker = { input, control, items, index: -1,
      scrollTop: effortScrollContainer.scrollTop, scrollLeft: effortScrollContainer.scrollLeft };
    effortOptions.replaceChildren(); effortOptions.scrollTop = 0;
    effortHeadingTitle.textContent = query ? '匹配等级' : '常用等级';
    if (filter && !items.some(item => !item.custom)) effortOptions.append(el('div', '没有匹配的常用等级', 'effort-empty'));
    items.forEach((item, index) => {
      const row = el('div', undefined, 'effort-option' + (item.custom ? ' effort-custom' : ''));
      row.id = 'reasoningEffortOption-' + index; row.setAttribute('role', 'option'); row.dataset.effortValue = item.value;
      row.setAttribute('aria-selected', String(item.value.toLowerCase() === value.toLowerCase()));
      row.append(icon('check'));
      if (item.custom) {
        const copy = el('div', undefined, 'effort-custom-copy');
        copy.append(el('span', '使用自定义等级', 'effort-custom-label'), el('span', item.value, 'effort-option-value')); row.append(copy);
      } else row.append(el('span', item.value, 'effort-option-value'), el('span', item.hint, 'effort-option-hint'));
      row.onpointermove = () => highlightEffort(index);
      row.onpointerdown = event => { if (event.button === 0) event.preventDefault(); };
      row.onclick = () => chooseEffort(item.value); effortOptions.append(row);
    });
    input.setAttribute('aria-expanded', 'true'); control.classList.add('is-open'); effortPopover.hidden = false;
    if (typeof effortPopover.showPopover === 'function' && !effortPopover.matches(':popover-open')) effortPopover.showPopover();
    positionEffortPicker();
    highlightEffort(filter ? -1 : Math.max(0, items.findIndex(item => item.value.toLowerCase() === value.toLowerCase())), true);
  }
  function effortControl(input) {
    const control = el('div', undefined, 'effort-combobox'); input.before(control);
    input.removeAttribute('list'); input.autocomplete = 'off'; input.spellcheck = false;
    input.setAttribute('role', 'combobox'); input.setAttribute('aria-autocomplete', 'list');
    input.setAttribute('aria-haspopup', 'listbox'); input.setAttribute('aria-expanded', 'false');
    input.setAttribute('aria-controls', effortOptions.id);
    const trigger = button('', () => {
      const open = activeEffortPicker?.input === input;
      input.focus({ preventScroll: true }); if (open) closeEffortPicker(); else openEffortPicker(input);
    }, 'effort-trigger');
    trigger.append(icon('chevron-down')); trigger.tabIndex = -1;
    const label = input.getAttribute('aria-label') || input.labels?.[0]?.textContent || '思考等级';
    trigger.setAttribute('aria-label', '展开' + label + '选项'); trigger.setAttribute('aria-controls', effortOptions.id);
    control.append(input, trigger);
    input.addEventListener('click', () => { if (activeEffortPicker?.input !== input) openEffortPicker(input); });
    input.addEventListener('input', () => { if (!choosingEffort) openEffortPicker(input, true); });
    input.addEventListener('keydown', event => {
      if (event.isComposing || event.ctrlKey || event.metaKey || event.shiftKey) return;
      if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
        event.preventDefault();
        if (activeEffortPicker?.input !== input) { openEffortPicker(input); return; }
        const { index, items } = activeEffortPicker;
        highlightEffort(index < 0 ? event.key === 'ArrowDown' ? 0 : items.length - 1
          : (index + (event.key === 'ArrowDown' ? 1 : items.length - 1)) % items.length, true);
      } else if (event.key === 'Enter' && activeEffortPicker?.input === input) {
        event.preventDefault();
        const item = activeEffortPicker.items[activeEffortPicker.index];
        if (item || input.value.trim()) chooseEffort(item?.value || input.value.trim()); else closeEffortPicker();
      } else if (event.key === 'Tab') closeEffortPicker();
    });
    control.addEventListener('focusout', event => {
      if (activeEffortPicker?.input === input && !control.contains(event.relatedTarget) && !effortPopover.contains(event.relatedTarget)) closeEffortPicker();
    });
    return control;
  }
  function syncEffortDisabled() {
    for (const control of dialog.querySelectorAll('.effort-combobox'))
      control.querySelector('.effort-trigger').disabled = control.querySelector('input').matches(':disabled');
    if (activeEffortPicker?.input.matches(':disabled')) closeEffortPicker();
  }
  document.addEventListener('pointerdown', event => {
    if (activeEffortPicker && !activeEffortPicker.control.contains(event.target) && !effortPopover.contains(event.target)) closeEffortPicker();
  });
  document.addEventListener('keydown', event => {
    if (event.key !== 'Escape' || !activeEffortPicker) return;
    event.preventDefault(); event.stopPropagation(); closeEffortPicker();
  }, true);
  document.addEventListener('scroll', event => {
    if (!activeEffortPicker || effortPopover.contains(event.target)) return;
    if (activeEffortPicker.control.contains(event.target)) return;
    // Focusing an offscreen input may deliver its earlier scroll event after the popup opens.
    if (event.target === effortScrollContainer && effortScrollContainer.scrollTop === activeEffortPicker.scrollTop &&
        effortScrollContainer.scrollLeft === activeEffortPicker.scrollLeft) return;
    closeEffortPicker();
  }, true);
  window.addEventListener('resize', closeEffortPicker);
  window.visualViewport?.addEventListener('resize', closeEffortPicker);
  dialog.addEventListener('close', closeEffortPicker);

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
    closeEffortPicker();
    const selected = reasoningSelection(), inherited = !!reasoningScope && selected.source === 'channel', rule = selected.rule;
    $('reasoningInheritance').hidden = !reasoningScope;
    $('reasoningUseDefault').checked = inherited;
    $('reasoningFields').disabled = inherited;
    $('reasoningMode').setAttribute('aria-disabled', String(inherited));
    for (const input of $('reasoningMode').querySelectorAll('input')) input.checked = input.value === rule.mode;
    $('reasoningOffSettings').hidden = rule.mode !== 'off';
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
        input.setAttribute('aria-label', label + ' ' + (index + 1));
        input.autocomplete = 'off'; input.spellcheck = false;
        input.oninput = () => { pair[field] = input.value; previewReasoning(); };
      }
      const remove = button('', () => { editableReasoningRule().mappings.splice(index, 1); renderReasoning(); }, 'icon-button danger');
      remove.append(icon('x'));
      remove.setAttribute('aria-label', '删除映射 ' + (index + 1));
      row.append(effortControl(from), el('span', '→', 'arrow'), effortControl(to), remove); root.append(row);
    }
    $('reasoningMapEmpty').hidden = !!root.children.length;
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
    syncEffortDisabled();
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
  $('reasoningMode').onchange = event => {
    if (event.target.matches('input[type=radio]:not(:disabled)')) { editableReasoningRule().mode = event.target.value; renderReasoning(); }
  };
  $('reasoningAdd').onclick = () => {
    editableReasoningRule().mappings.push({ from: '', to: '' }); renderReasoning();
    $('reasoningRows').lastElementChild.querySelector('input').focus();
  };
  for (const [id, field] of [['reasoningDefault', 'defaultEffort'], ['reasoningFixed', 'fixedEffort']])
    $(id).oninput = () => { editableReasoningRule()[field] = $(id).value; previewReasoning(); };
  $('reasoningPreviewModel').onchange = previewReasoning;
  $('reasoningPreviewMissing').onchange = previewReasoning;
  $('reasoningPreviewInput').oninput = previewReasoning;
  for (const id of ['reasoningDefault', 'reasoningFixed', 'reasoningPreviewInput']) effortControl($(id));
