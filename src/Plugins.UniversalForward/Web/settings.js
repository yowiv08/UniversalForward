  // 所有外观入口共用插件配色；宿主只提供明暗状态。
  function applyAppearance(mode) {
    if (document.documentElement.dataset.mode === mode) return;
    document.documentElement.classList.add('appearance-changing');
    document.documentElement.dataset.mode = mode;
    // 两套配色同时生效，避免背景过渡期间出现浅字配浅底。
    void document.documentElement.offsetWidth;
    requestAnimationFrame(() => document.documentElement.classList.remove('appearance-changing'));
  }
  let savedAppearance = 'host', selectedAppearance = 'host', hostColorMode = window.UniversalForwardAppearance.colorMode;
  let appearanceRevision = 0, appearanceQueue = Promise.resolve();
  function renderAppearance() {
    applyAppearance(selectedAppearance === 'host' ? hostColorMode || 'light' : selectedAppearance);
    for (const control of document.querySelectorAll('[data-appearance-mode]'))
      control.setAttribute('aria-pressed', String(control.dataset.appearanceMode === selectedAppearance));
    $('appearanceSource').textContent = selectedAppearance === 'host'
      ? hostColorMode ? `正在跟随宿主${hostColorMode === 'dark' ? '黑夜' : '白天'}模式，仅同步明暗。`
        : '等待宿主明暗通知，暂用白天配色。'
      : '使用手动选择，宿主切换明暗时保持当前模式。';
  }
  window.UniversalForwardAppearance.onChange = mode => {
    hostColorMode = mode;
    renderAppearance();
  };
  function appearanceFeedback(text = '', error = false) {
    feedback('appearanceStatus', text, error);
    feedback('appearanceDialogStatus', text, error);
  }
  renderAppearance();
  if (parent !== window) parent.postMessage({ type: 'router2api-appearance-ready' }, '*');
  async function loadAppearance() {
    const revision = appearanceRevision;
    try {
      const value = await api('GET', 'appearance/settings');
      if (revision !== appearanceRevision) return;
      if (!['host', 'light', 'dark'].includes(value?.mode)) throw Error('外观设置无效');
      savedAppearance = selectedAppearance = value.mode; renderAppearance();
      appearanceFeedback();
    } catch (error) {
      if (revision !== appearanceRevision) return;
      savedAppearance = selectedAppearance = 'light'; renderAppearance();
      appearanceFeedback('外观读取失败，当前使用白天模式。' + error.message, true);
    }
  }
  function selectAppearance(mode) {
    const revision = ++appearanceRevision;
    selectedAppearance = mode; renderAppearance();
    appearanceFeedback('正在保存外观…');
    appearanceQueue = appearanceQueue.then(async () => {
      try {
        const value = await api('POST', 'appearance/settings', { mode });
        if (value?.mode !== mode) throw Error('宿主未确认外观已保存');
        savedAppearance = mode;
        if (revision === appearanceRevision) appearanceFeedback();
      } catch (error) {
        if (revision !== appearanceRevision) return;
        selectedAppearance = savedAppearance; renderAppearance();
        appearanceFeedback('外观保存失败，已恢复此前模式。' + error.message, true);
      }
    });
  }
  for (const control of document.querySelectorAll('[data-appearance-mode]'))
    control.onclick = () => selectAppearance(control.dataset.appearanceMode);

  let executionSettings = null, executionLoading = false, executionSaving = false, executionError = '';
  const timeoutSeconds = value => new Intl.NumberFormat('zh-CN', { maximumFractionDigits: 2, useGrouping: false }).format(value);
  function timeoutInput(name) {
    const input = form.elements[name], value = Number(input.value);
    return input.value.trim() && Number.isInteger(value) && value >= Number(input.min) && value <= Number(input.max) ? value : null;
  }
  function retryIncludes520() {
    const text = form.elements.retryStatusCodes.value.trim();
    if (!text) return false;
    let includes = false;
    for (const item of text.split(',')) {
      const parts = item.split('-').map(value => value.trim());
      if (parts.length > 2 || parts.some(value => !/^\d+$/.test(value))) return null;
      const first = Number(parts[0]), last = Number(parts[1] ?? parts[0]);
      if (first < 100 || last > 599 || last < first) return null;
      includes ||= first <= 520 && last >= 520;
    }
    return includes;
  }
  function calculateTimeoutSuggestion(execution) {
    const retries = timeoutInput('maxRetries'), header = timeoutInput('headerTimeoutSeconds');
    const anomalyEnabled = $('rateLimitRetryEnabled').checked || $('emptyResponseRetryEnabled').checked;
    const anomalyRetries = anomalyEnabled ? timeoutInput('responseMaxRetries') : 0;
    const interval = anomalyRetries > 0 ? timeoutInput('responseRetryIntervalSeconds') : 0;
    if ([retries, header, anomalyRetries, interval].includes(null))
      return { error: '请补全有效的重试次数和响应头超时；启用异常重试时还需填写有效的次数与间隔。' };
    const retries520 = retryIncludes520();
    if (retries520 === null) return { error: '请检查自动重试状态码，例如 429,500-503。' };
    // 对应内部循环：两类重试额度相加；只有 HTTP 520 的普通重试带退避。
    let backoff = 0;
    if (retries520) for (let index = 0; index < retries; index++) backoff += Math.min(2, .25 * 2 ** Math.min(index, 3));
    const sends = 1 + retries + anomalyRetries, waits = anomalyRetries * interval;
    const headerBudget = sends * header, requestedSetup = headerBudget + waits + backoff;
    const setupLimit = Math.min(execution.setupTimeoutSeconds, execution.totalSetupTimeoutSeconds);
    const setup = Math.min(requestedSetup, setupLimit), reading = execution.responseTimeoutSeconds;
    // 只给当前插件调用建议；宿主尝试次数不能乘到这份总时限里。
    return { seconds: Math.min(3600, Math.ceil(setup + reading)), retries, anomalyRetries, sends, header,
      headerBudget, waits, backoff, requestedSetup, setupLimit, setup, reading };
  }
  function renderTimeoutSuggestion(execution) {
    const suggestion = execution ? calculateTimeoutSuggestion(execution) : null;
    const available = suggestion && !suggestion.error;
    $('timeoutReload').hidden = !executionError;
    $('timeoutReload').disabled = executionLoading || executionSaving;
    $('timeoutRecommendation').textContent = available ? String(suggestion.seconds) : '—';
    $('timeoutRecommendationUnit').hidden = !available;
    $('timeoutDuration').textContent = '';
    $('timeoutApply').disabled = !available;
    $('timeoutApply').textContent = '填入建议';
    $('timeoutSendDetail').textContent = '';
    $('timeoutWaitDetail').textContent = '';
    $('timeoutBudgetWarning').hidden = true;
    const idle = timeoutInput('streamIdleTimeoutSeconds');
    $('timeoutIdleDetail').textContent = `流空闲超时${idle === null ? '' : ` ${idle} 秒`}只限制连续无数据的时间，不作为整个回答的输出时长加入计算。总时限到期仍会中断正在输出的流。`;
    if (!available) {
      $('timeoutCalculation').textContent = suggestion?.error || (executionError
        ? '宿主执行限制读取失败，暂时无法给出建议。' : '正在读取宿主执行限制…');
      $('timeoutCurrent').textContent = '仍可手动填写总时限（1–3600 秒），原有设置保持不变。';
      return;
    }
    const { seconds, sends, retries, anomalyRetries, header, headerBudget, waits, backoff, requestedSetup, setupLimit, setup, reading } = suggestion;
    const minutes = Math.floor(seconds / 60), remainder = seconds % 60;
    $('timeoutDuration').textContent = minutes ? `${minutes} 分钟${remainder ? ` ${remainder} 秒` : ''}` : '';
    $('timeoutCalculation').textContent = `建立响应预留 ${timeoutSeconds(setup)} 秒 + 响应读取预留 ${timeoutSeconds(reading)} 秒`;
    $('timeoutSendDetail').textContent = `最多 ${sends} 次内部发送：首次 1 次 + 普通重试 ${retries} 次${anomalyRetries ? ` + 限流／空回共享重试 ${anomalyRetries} 次` : ''}。两类异常同时开启时共享次数，不重复计算。`;
    $('timeoutWaitDetail').textContent = `按每次响应头等满 ${header} 秒估算：${sends} × ${header} = ${timeoutSeconds(headerBudget)} 秒` +
      (waits ? `，加上异常重试间隔 ${timeoutSeconds(waits)} 秒` : '') +
      (backoff ? `，以及 HTTP 520 退避预留 ${timeoutSeconds(backoff)} 秒` : '') +
      `。建立响应部分取该估算与宿主 ${timeoutSeconds(setupLimit)} 秒限制中较小的值，总和向上取整秒。`;
    const warnings = [];
    if (requestedSetup > setupLimit) warnings.push(`等待与重试预估 ${timeoutSeconds(requestedSetup)} 秒，超过宿主 ${timeoutSeconds(setupLimit)} 秒的建立响应限制。慢响应时可能用不完重试次数，提高总时限也不能延长这个阶段。`);
    if (setup + reading > 3600) warnings.push(`两段预留合计 ${timeoutSeconds(setup + reading)} 秒，建议值已取渠道上限 3600 秒。`);
    $('timeoutBudgetWarning').textContent = warnings.join(' ');
    $('timeoutBudgetWarning').hidden = !warnings.length;
    const current = timeoutInput('totalTimeoutSeconds');
    $('timeoutCurrent').textContent = current === null ? '可直接填入建议值，也可手动填写 1–3600 的整数。'
      : current < seconds ? `当前 ${current} 秒，比建议少 ${seconds - current} 秒，可能提前中断较长输出。`
      : current > seconds ? `当前 ${current} 秒，高于建议值；宿主各阶段限制仍然生效。`
      : '当前与建议一致。保存渠道后生效，也可继续手动修改。';
    $('timeoutApply').disabled = current === seconds;
    if (current === seconds) $('timeoutApply').textContent = '与建议一致';
  }
  function renderExecutionHints() {
    const retries = timeoutInput('maxRetries');
    $('internalRetryHint').textContent = retries !== null
      ? `不含首次；一次宿主尝试最多普通发送 ${retries + 1} 次。` : '0–10 次，不含首次发送。';
    const currentSettings = !executionLoading && !executionError ? executionSettings : null;
    $('hostAttemptsHint').textContent = currentSettings
      ? `宿主默认最多尝试 ${currentSettings.defaultHostAttempts} 次（含首次），映射填写的次数优先。建议总时限只计算一次插件调用，不乘宿主次数；内部重试继续使用同一渠道、Key 和代理。`
      : executionError ? '宿主默认尝试次数读取失败，可在「插件设置」中重试。' : '正在读取宿主默认尝试次数…';
    const execution = currentSettings?.execution;
    $('hostTimeoutHint').textContent = execution
      ? `宿主当前限制：单次建立响应 ${execution.setupTimeoutSeconds} 秒，建立响应总预算 ${execution.totalSetupTimeoutSeconds} 秒，进入响应后的读取预算 ${execution.responseTimeoutSeconds} 秒。渠道设置只能进一步缩短这些预算；填写 3600 秒不会覆盖宿主限制。`
      : '渠道超时还受宿主的建立响应和响应读取预算限制。' + (executionError ? '当前限制读取失败，可在「插件设置」中重试。' : '正在读取当前限制…');
    renderTimeoutSuggestion(execution);
  }
  async function loadExecutionSettings() {
    if (executionLoading || executionSaving) return;
    executionLoading = true; executionError = '';
    renderExecutionHints();
    $('executionSave').disabled = true; $('executionReload').disabled = true; $('defaultHostAttempts').disabled = true;
    feedback('executionStatus', '正在读取插件设置…');
    try {
      const value = await api('GET', 'execution/settings');
      if (!Number.isInteger(value?.defaultHostAttempts) || value.defaultHostAttempts < 1 || value.defaultHostAttempts > 35)
        throw Error('宿主尝试次数无效');
      if (!value.execution || !['setupTimeoutSeconds', 'totalSetupTimeoutSeconds', 'responseTimeoutSeconds']
        .every(name => Number.isFinite(value.execution[name]) && value.execution[name] > 0)) throw Error('宿主执行限制无效');
      executionSettings = value;
      $('defaultHostAttempts').value = String(value.defaultHostAttempts);
      feedback('executionStatus');
    } catch (error) {
      executionError = error.message;
      feedback('executionStatus', '读取失败：' + error.message + '。可重试读取，或填写次数重新保存。', true);
    } finally {
      executionLoading = false;
      $('executionSave').disabled = false; $('executionReload').disabled = false; $('defaultHostAttempts').disabled = false;
      renderExecutionHints();
    }
  }
  $('openPluginSettings').onclick = () => { $('pluginSettingsDialog').showModal(); loadExecutionSettings(); };
  $('pluginSettingsClose').onclick = () => $('pluginSettingsDialog').close();
  $('executionReload').onclick = loadExecutionSettings;
  $('timeoutReload').onclick = loadExecutionSettings;
  for (const name of ['maxRetries', 'headerTimeoutSeconds', 'totalTimeoutSeconds', 'streamIdleTimeoutSeconds',
    'responseMaxRetries', 'responseRetryIntervalSeconds', 'retryStatusCodes']) $(name).addEventListener('input', renderExecutionHints);
  for (const name of ['rateLimitRetryEnabled', 'emptyResponseRetryEnabled']) $(name).addEventListener('change', renderExecutionHints);
  $('timeoutApply').onclick = () => {
    if (executionLoading || executionError || !executionSettings) return;
    const suggestion = calculateTimeoutSuggestion(executionSettings.execution);
    if (suggestion.error) return;
    $('totalTimeoutSeconds').value = String(suggestion.seconds);
    $('totalTimeoutSeconds').dispatchEvent(new Event('input', { bubbles: true }));
  };
  $('executionForm').onsubmit = async event => {
    event.preventDefault();
    if (executionLoading || executionSaving) return;
    const defaultHostAttempts = Number($('defaultHostAttempts').value);
    if (!Number.isInteger(defaultHostAttempts) || defaultHostAttempts < 1 || defaultHostAttempts > 35) {
      feedback('executionStatus', '请输入 1–35 的整数，包含首次尝试。', true); return;
    }
    executionSaving = true;
    $('executionSave').disabled = true; $('executionReload').disabled = true; $('defaultHostAttempts').disabled = true;
    feedback('executionStatus', '正在保存…');
    try {
      executionSettings = await api('POST', 'execution/settings', { defaultHostAttempts });
      executionError = '';
      feedback('executionStatus', '已保存。新请求使用新次数，执行中的请求继续使用原有次数。');
      renderExecutionHints();
    } catch (error) { feedback('executionStatus', '保存失败：' + error.message, true); }
    finally {
      executionSaving = false;
      $('executionSave').disabled = false; $('executionReload').disabled = false; $('defaultHostAttempts').disabled = false;
    }
  };
  loadAppearance();
  loadExecutionSettings();
