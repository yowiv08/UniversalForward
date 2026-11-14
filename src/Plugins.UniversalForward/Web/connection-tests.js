  // Connection tests, inline detail inspector, and post-test manual Key disable selector.
  const testState = {
    account: null, selected: new Set(), rows: new Map(), targets: new Set(), combinations: [],
    jobId: null, active: false, starting: false, cancelling: false, disabling: false,
    disableOpen: false, disableSelected: new Set(), running: null, timer: null, epoch: 0, resultsVersion: -1
  };
  const testBusy = () => testState.active || testState.starting || testState.disabling;
  const resultId = (model, keyId) => JSON.stringify([model, keyId ?? null]);
  let testPage = 1;

  const testRowState = r => ({
    queued: '排队中', running: '测试中', succeeded: '成功', failed: '失败',
    skipped: '已跳过', cancelled: '已取消', notExecuted: '未执行', unavailable: '结果不可用'
  })[r.state] || (r.cancelled ? '已取消' : r.success ? '成功' : '失败');

  function testedKeySummaries() {
    const accountKeys = new Map((testState.account?.keys || []).map(k => [k.id, k]));
    const byKey = new Map();
    for (const r of testState.rows.values()) {
      if (!r.keyId || !['succeeded', 'failed'].includes(r.state)) continue;
      const key = accountKeys.get(r.keyId);
      if (!key?.enabled || !Number.isSafeInteger(r.keyHealthVersion)
          || r.keyHealthVersion !== key.healthVersion) continue;
      let summary = byKey.get(r.keyId);
      if (!summary) {
        summary = {
          id: r.keyId,
          name: key.name || r.keyName || defaultKeyName(key.secret) || r.keyId,
          masked: key.masked || '',
          succeeded: 0,
          failed: 0,
          other: 0,
          statuses: new Set()
        };
        byKey.set(r.keyId, summary);
      }
      if (r.state === 'succeeded' || r.success) summary.succeeded++;
      else if (r.state === 'failed' || (!r.cancelled && !r.skipped && !['notExecuted', 'unavailable'].includes(r.state))) summary.failed++;
      else summary.other++;
      if (r.originalStatus != null) summary.statuses.add(r.originalStatus);
    }
    return [...byKey.values()];
  }

  function renderTestDisablePanel() {
    const candidates = testedKeySummaries();
    const failedIds = candidates.filter(item => item.failed > 0).map(item => item.id);
    const openBtn = $('testDisableKeysOpen');
    openBtn.hidden = !candidates.length;
    openBtn.disabled = testBusy();
    openBtn.textContent = failedIds.length ? `禁用 Key（${failedIds.length} 个异常）` : '禁用 Key';
    openBtn.setAttribute('aria-expanded', String(testState.disableOpen && !!candidates.length));

    for (const id of [...testState.disableSelected]) {
      if (!candidates.some(item => item.id === id)) testState.disableSelected.delete(id);
    }
    if (!candidates.length) testState.disableOpen = false;
    $('testDisablePanel').hidden = !testState.disableOpen;
    if (!testState.disableOpen) return;

    const list = $('testDisableKeyList');
    list.replaceChildren();
    for (const item of candidates) {
      const row = el('label', undefined, 'disable-key-item');
      const box = el('input');
      box.type = 'checkbox';
      box.value = item.id;
      box.checked = testState.disableSelected.has(item.id);
      box.disabled = testState.disabling;
      box.onchange = () => {
        if (box.checked) testState.disableSelected.add(item.id);
        else testState.disableSelected.delete(item.id);
        updateDisablePanelButtons();
      };
      const identity = el('span', undefined, 'disable-key-identity');
      identity.append(el('strong', item.name));
      if (item.masked) identity.append(el('code', item.masked));
      const codes = [...item.statuses].slice(0, 3).map(c => 'HTTP ' + c).join(', ');
      const metrics = el('span', `成功 ${item.succeeded} · 失败 ${item.failed}${codes ? ' · ' + codes : ''}`,
        'console-badge ' + (item.failed ? 'is-error' : 'is-success'));
      row.append(box, identity, metrics);
      list.append(row);
    }
    $('testDisableSelectFailed').disabled = testState.disabling || !failedIds.length;
    $('testDisableSelectAll').disabled = testState.disabling;
    $('testDisableSelectNone').disabled = testState.disabling;
    $('testDisableCancel').disabled = testState.disabling;
    updateDisablePanelButtons();
  }

  function updateDisablePanelButtons() {
    const count = testState.disableSelected.size;
    const submit = $('testDisableConfirm');
    submit.disabled = testState.disabling || !count;
    submit.textContent = testState.disabling ? '正在禁用…' : count ? `禁用所选 Key (${count})` : '禁用所选 Key';
  }

  function openTestDisablePanel() {
    if (testBusy()) return;
    if (testState.disableOpen) {
      testState.disableOpen = false;
      renderTestDisablePanel();
      return;
    }
    const candidates = testedKeySummaries();
    if (!candidates.length) return;
    testState.disableSelected = new Set(candidates.filter(item => item.failed > 0).map(item => item.id));
    testState.disableOpen = true;
    renderTestDisablePanel();
  }

  $('testDisableKeysOpen').onclick = openTestDisablePanel;
  $('testDisableCancel').onclick = () => { testState.disableOpen = false; renderTestDisablePanel(); };
  $('testDisableSelectFailed').onclick = () => {
    testState.disableSelected = new Set(testedKeySummaries().filter(item => item.failed > 0).map(item => item.id));
    renderTestDisablePanel();
  };
  $('testDisableSelectAll').onclick = () => {
    testState.disableSelected = new Set(testedKeySummaries().map(item => item.id));
    renderTestDisablePanel();
  };
  $('testDisableSelectNone').onclick = () => {
    testState.disableSelected.clear();
    renderTestDisablePanel();
  };

  $('testDisableConfirm').onclick = async () => {
    if (testBusy() || !testState.disableSelected.size || !testState.account) return;
    const ids = [...testState.disableSelected];
    const accountId = testState.account.id, revision = testState.account.keyRevision;
    const names = ids.map(id => testState.account.keys?.find(k => k.id === id)?.name || id);
    testState.disabling = true;
    updateTestControls();
    renderTestDisablePanel();
    if (!await ask(`确定禁用所选的 ${ids.length} 个 Key（${names.join('、')}）？禁用后可在渠道「Key 管理」中重新启用。`, '禁用所选 Key')) {
      testState.disabling = false;
      updateTestControls();
      renderTestDisablePanel();
      return;
    }
    feedback('testError');
    try {
      const res = await api('POST', 'accounts/keys/disable', { id: accountId, keyIds: ids, keyRevision: revision });
      testState.account = res.account;
      accounts = accounts.map(account => account.id === accountId ? res.account : account);
      render();
      fillKeyOptions($('testKey'), testState.account.keys || [], true);
      testState.disableOpen = false;
      testState.disableSelected.clear();
      feedback('testProgress', `已禁用 ${res.disabledCount ?? ids.length} 个 Key：${names.join('、')}`);
    } catch (e) {
      feedback('testError', e.message, true);
      await load();
      testState.account = accounts.find(account => account.id === accountId) || testState.account;
    } finally {
      testState.disabling = false;
      renderTests();
    }
  };

  function closeTestDetail() {
    const restoreFocus = $('testDetailDialog').contains(document.activeElement);
    testDetailEpoch++;
    testDetailRow = null;
    testDetailOffset = 0;
    $('testDetailDialog').hidden = true;
    $('testDetails').textContent = '';
    $('testDetailOverview').replaceChildren();
    if (restoreFocus && testDetailTrigger?.isConnected) testDetailTrigger.focus({ preventScroll: true });
    testDetailTrigger = null;
  }

  function updateTestControls() {
    for (const id of ['testAll', 'testSelected', 'testEndpoint', 'testStream', 'testKey', 'testSelectAll', 'testContent', 'testMaxTokens'])
      $(id).disabled = testBusy();
    const models = testState.account?.models || [];
    const visible = visibleTestModels().slice((testPage - 1) * +$('testPageSize').value, testPage * +$('testPageSize').value);
    $('testAll').disabled = testBusy() || !models.length;
    $('testSelected').disabled = testBusy() || !testState.selected.size;
    $('testSelected').textContent = testState.selected.size ? `测试勾选 ${testState.selected.size} 个` : '测试勾选';
    $('testSelectAll').disabled = testBusy() || !visible.length;
    $('testSelectAll').checked = !!visible.length && visible.every(model => testState.selected.has(model));
    $('testSelectAll').indeterminate = visible.some(model => testState.selected.has(model)) && !$('testSelectAll').checked;
    $('testCancel').disabled = !testState.active || testState.cancelling;
    $('testClose').disabled = testState.disabling;
    renderTestDisablePanel();
  }

  let testDetailEpoch = 0, testDetailRow = null, testDetailOffset = 0, testDetailTrigger = null;
  async function loadTestDetail(row, append = false) {
    const epoch = append ? testDetailEpoch : ++testDetailEpoch;
    if (!append) { testDetailRow = row; testDetailOffset = 0; $('testDetails').textContent = '正在读取响应…'; }
    $('testDetailMore').hidden = true;
    if (!row.jobId || row.rowId == null) { $('testDetails').textContent = row.response || row.error || '未收到上游响应'; return; }
    try {
      const detail = await api('GET', `tests/detail?id=${encodeURIComponent(row.jobId)}&rowId=${row.rowId}&offset=${testDetailOffset}`);
      if (epoch !== testDetailEpoch) return;
      const text = detail.evicted ? '[响应原文已因容量限制清理，测试摘要仍保留]' : detail.text || (!append ? row.error || '未收到上游响应' : '');
      $('testDetails').textContent = (append ? $('testDetails').textContent : `${row.keyName || row.keyId || '按策略'}\n`) + text
        + (detail.done && detail.truncated ? '\n[原始响应已截断]' : '');
      testDetailOffset = detail.nextOffset;
      $('testDetailMore').hidden = detail.done;
    } catch (e) { if (epoch === testDetailEpoch) $('testDetails').textContent = '读取失败：' + e.message; }
  }
  $('testDetailMore').onclick = () => loadTestDetail(testDetailRow, true);

  function visibleTestModels() {
    const q = $('testFilter').value.toLowerCase();
    return (testState.account?.models || []).filter(model => model.toLowerCase().includes(q));
  }

  function updateTestConfigSummary() {
    $('testStreamLabel').textContent = $('testStream').checked ? '已启用' : '已禁用';
    $('testConfigSummary').textContent = `${$('testEndpoint').value || '自动选择'} · ${$('testStream').checked ? '流式' : '非流式'} · ${$('testMaxTokens').value || '—'} Token`;
  }

  function renderTests() {
    const root = $('testRows'); root.replaceChildren();
    const q = $('testFilter').value.toLowerCase();
    const models = visibleTestModels(), size = +$('testPageSize').value;
    const disabledKeyIds = new Set((testState.account?.keys || []).filter(k => !k.enabled).map(k => k.id));
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
      const row = el('tr'), cell = el('td'), select = el('input');
      select.type = 'checkbox';
      select.checked = testState.selected.has(model);
      select.disabled = testBusy();
      select.setAttribute('aria-label', '选择 ' + model);
      select.onchange = () => {
        if (select.checked) testState.selected.add(model);
        else testState.selected.delete(model);
        updateTestControls();
      };
      cell.append(select);

      const results = [...testState.rows.values()].filter(r => r.model === model);
      const pending = testState.active && testState.targets.has(model) &&
        (results.some(r => ['queued', 'running'].includes(r.state)) || results.length < testState.combinations.filter(c => c.model === model).length);
      const state = pending ? testState.running === model ? '测试中' : '排队中' : results.length
        ? results.every(r => r.cancelled) ? '已取消' : results.every(r => r.state === 'skipped') ? '已跳过'
          : results.every(r => r.state === 'notExecuted') ? '未执行' : results.every(r => r.state === 'unavailable') ? '结果不可用'
          : results.every(r => r.success) ? '成功' : results.some(r => r.success) ? '部分成功' : '失败' : '未测试';

      const summaryCell = el('td');
      for (const r of results) {
        const result = el('div', '', 'result-line');
        const keyDisabled = r.keyId && disabledKeyIds.has(r.keyId);
        result.append(
          el('span', (r.keyName || r.keyId || '按策略') + (keyDisabled ? ' · 已禁用' : ''), 'result-key' + (keyDisabled ? ' is-disabled' : '')),
          el('code', `${r.originalStatus ?? '—'} → ${r.mappedStatus ?? '—'}`, 'result-http'),
          el('strong', `${r.durationMs ?? '—'}ms`, 'result-duration'),
          el('span', `重试 ${r.retries ?? 0}次 · ${testRowState(r)}`, 'result-retries')
        );
        summaryCell.append(result);
      }
      if (!results.length) summaryCell.append(el('span', pending ? '等待响应' : '—', 'result-empty'));

      const ops = el('td'), run = button('测试', () => startTests([model]));
      run.disabled = testBusy();
      ops.append(run);
      if (results.length) {
        ops.append(button('详情', () => {
          testDetailTrigger = document.activeElement;
          loadTestDetail(results[0]);
          const overview = $('testDetailOverview'); overview.replaceChildren();
          for (const r of results) {
            const section = el('section', '', 'detail-group'), values = el('dl', '', 'detail-values detail-card');
            section.append(el('h4', r.keyName || r.keyId || '按渠道策略'));
            for (const [label, value] of [
              ['模型', model], ['状态', testRowState(r)],
              ['测试端点', r.endpoint || '—'],
              ['出站方式', r.networkMode === 'proxyPool' ? '代理池' : '直连'],
              ['上游连接', r.transport === 'websocket' ? 'WebSocket' : 'HTTP'],
              ...(r.proxy ? [['实际代理', r.proxy.address], ['代理 ID', r.proxy.id], ['代理来源', r.proxy.subscriptionId]] : []),
              ['状态码', `${r.originalStatus ?? '—'} → ${r.mappedStatus ?? '—'}`],
              ['响应时间', `${r.durationMs ?? '—'} ms`], ['重试次数', String(r.retries ?? 0)]
            ]) values.append(el('dt', label), el('dd', value));
            section.append(values, button('查看此 Key 响应', () => loadTestDetail(r), 'text-button'));
            overview.append(section);
          }
          $('testDetailDialog').hidden = false;
          $('testDetailClose').focus({ preventScroll: true });
        }, 'text-button'));
      }

      const stateCell = el('td');
      stateCell.append(el('span', state, 'console-badge ' + (state === '成功' ? 'is-success' : state === '失败' ? 'is-error' : pending ? 'is-active' : 'is-neutral')));
      if (results.length) stateCell.append(el('small', `成功 ${results.filter(r => r.success).length} / 失败 ${results.filter(r => testRowState(r) === '失败').length}`));
      const modelCell = el('td', model, 'console-model');
      for (const endpoint of new Set(results.map(r => r.endpoint).filter(Boolean)))
        modelCell.append(el('code', endpoint, 'result-endpoint'));
      for (const [item, label] of [[cell, '选择'], [modelCell, '模型'], [stateCell, '状态'], [summaryCell, '结果'], [ops, '操作']])
        item.dataset.label = label;
      row.append(cell, modelCell, stateCell, summaryCell, ops);
      root.append(row);
    }
    updateTestControls();
  }

  function openTests(account) {
    if (dialog.open && savedAccount?.id === account.id &&
        (networkChanged(account) ||
         form.elements.responsesTransport.value !== (account.responsesTransport || 'http') ||
         form.elements.stripResponsesResourceState.checked !== (account.stripResponsesResourceState ?? false))) {
      feedback('formError', '连接设置尚未保存，请先保存渠道再测试', true);
      return;
    }
    if (testBusy() && testState.account?.id !== account.id) { message('请先取消或等待当前渠道测试完成', true); return; }
    $('testTitle').textContent = '连接测试 · ' + account.label;
    if (!testBusy()) {
      fillKeyOptions($('testKey'), account.keys || [], true);
      if (testState.account?.id !== account.id) {
        $('testConfiguration').open = true;
        clearTimeout(testState.timer); testState.epoch++;
        Object.assign(testState, {
          account, selected: new Set(), rows: new Map(), targets: new Set(), combinations: [],
          jobId: null, active: false, disableOpen: false, disableSelected: new Set(), running: null
        });
        closeTestDetail();
        feedback('testProgress'); feedback('testError');
        $('testFilter').value = ''; $('testStream').checked = false; testPage = 1;
      } else {
        testState.account = account;
      }
      $('testEndpoint').replaceChildren(option('', '自动选择'));
      for (const endpoint of account.endpoints || []) $('testEndpoint').append(option(endpoint, endpoint));
    }
    renderTests();
    if (!$('testDialog').open) $('testDialog').showModal();
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
    testState.disableOpen = false;
    testState.targets = new Set(models);
    testState.combinations = models.flatMap(model => keys.map(id => ({ model, keyId: id })));
    for (const [id, row] of testState.rows) if (models.includes(row.model)) testState.rows.delete(id);
    feedback('testError'); $('testResume').hidden = true; renderTests();
    try {
      const job = await api('POST', 'tests/start', {
        accountId: testState.account.id, models, keyMode, keyId,
        endpoint: $('testEndpoint').value || null, stream: $('testStream').checked, content, maxOutputTokens
      });
      testState.resultsVersion = -1;
      testState.jobId = job.id; testState.active = true; testState.epoch++;
      $('testConfiguration').open = false;
      document.querySelector('.test-content').scrollTop = 0;
      feedback('testProgress', '测试已提交');
      await pollTests(testState.epoch);
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
      const progress = job.progress || {};
      let rows = job.result?.rows || progress.rows || [];
      let resultsError = null;
      const version = job.result?.resultVersion ?? progress.resultVersion;
      if (version != null && version !== testState.resultsVersion) {
        rows = []; let offset = 0, page;
        try {
          do {
            page = await api('GET', `tests/results?id=${encodeURIComponent(testState.jobId)}&offset=${offset}`);
            if (epoch !== testState.epoch) return;
            rows.push(...page.rows); offset = page.nextOffset;
          } while (offset < page.total && page.rows.length);
        } catch (e) {
          if (epoch !== testState.epoch) return;
          if (!['Completed', 'Failed', 'Cancelled'].includes(state)) throw e;
          resultsError = '测试已结束，结果读取失败或已清理：' + e.message;
          feedback('testError', resultsError, true);
        }
        testState.resultsVersion = version;
      }
      for (const row of rows) testState.rows.set(row.jobId ? `${row.jobId}:${row.rowId}` : resultId(row.model, row.keyId), row);
      testState.running = [...testState.rows.values()].find(r => r.jobId === testState.jobId && r.state === 'running')?.model || progress.running || null;
      feedback('testProgress', `${({ Queued: '排队中', Running: '测试中', Completed: '已完成', Failed: '失败', Cancelled: '已取消' })[state] || state} · ${progress.completed ?? rows.length}/${progress.total ?? testState.combinations.length}`);
      $('testResume').hidden = true;
      if (['Completed', 'Failed', 'Cancelled'].includes(state)) {
        testState.active = false; testState.cancelling = false; testState.running = null;
        for (const r of testState.rows.values()) if (r.jobId === testState.jobId && ['queued', 'running'].includes(r.state))
          Object.assign(r, { state: resultsError ? 'unavailable' : state === 'Cancelled' ? 'cancelled' : 'notExecuted', cancelled: state === 'Cancelled', error: resultsError || job.error });
        for (const combo of testState.combinations) {
          const exists = [...testState.rows.values()].some(r => r.model === combo.model && (combo.keyId === null || r.keyId === combo.keyId));
          if (!exists) testState.rows.set(resultId(combo.model, combo.keyId), {
            ...combo, keyName: testState.account.keys?.find(k => k.id === combo.keyId)?.name,
            state: resultsError ? 'unavailable' : state === 'Cancelled' ? 'cancelled' : 'notExecuted',
            success: false, cancelled: state === 'Cancelled', error: resultsError || job.error || '测试未完成'
          });
        }
        if (job.error) feedback('testError', job.error, true);
        await load();
        if (epoch !== testState.epoch) return;
        testState.account = accounts.find(a => a.id === testState.account.id) || testState.account;
        fillKeyOptions($('testKey'), testState.account.keys || [], true);
      } else if (['Queued', 'Running'].includes(state)) {
        testState.timer = setTimeout(() => pollTests(epoch), 800);
      } else {
        throw Error('无法识别任务状态，请重新查询');
      }
      renderTests();
    } catch (e) {
      if (epoch !== testState.epoch) return;
      feedback('testError', '查询失败：' + e.message, true);
      $('testResume').hidden = false;
      updateTestControls();
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
  $('testClose').onclick = () => { closeTestDetail(); $('testDialog').close(); };
  $('testDetailClose').onclick = closeTestDetail;
  $('testDialog').addEventListener('cancel', e => {
    if (testState.disabling) { e.preventDefault(); return; }
    if (!$('testDetailDialog').hidden) { e.preventDefault(); closeTestDetail(); return; }
    if (testState.disableOpen) { e.preventDefault(); testState.disableOpen = false; renderTestDisablePanel(); }
  });
  $('testCancel').onclick = async () => {
    testState.cancelling = true; updateTestControls();
    try {
      await api('POST', 'tests/cancel', { id: testState.jobId });
      feedback('testProgress', '已申请取消，正在等待任务退出');
      await pollTests(testState.epoch);
    } catch (e) { testState.cancelling = false; feedback('testError', e.message, true); updateTestControls(); }
  };
