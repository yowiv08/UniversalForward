  let journalEpoch = 0, journalPage = 1, journalPageSize = 10, journalPages = 1, journalLoading = false;
  let journalDetail = null, journalRawBody = '', journalPartEpoch = 0;
  const journalStates = { running: '执行中', completed: '已完成', failed: '失败', cancelled: '已取消', interrupted: '中断' };
  const journalKinds = { forward: '转发', 'connection-test': '连接测试', 'models-discover': '获取模型', 'models-refresh': '刷新模型' };
  const journalRetainedPart = name => !/^attempt-\d+-(request|response|websocket-events)$/.test(name);
  const journalParts = detail => (detail.parts || []).filter(part => journalRetainedPart(part.name));
  const journalPartLabel = name => {
    const labels = { incoming: '插件收到的请求体', extensions: '宿主传入的扩展参数', 'incoming-headers': '宿主提供的请求头' };
    if (labels[name]) return labels[name];
    const match = /^attempt-(\d+)-(request|response)-headers$/.exec(name);
    return match ? `尝试 ${match[1]} · ${match[2] === 'request' ? '实际发送' : '上游响应'}头` : name;
  };
  const journalEffort = value => value && Object.keys(value).length
    ? Object.entries(value).map(([key, v]) => `${key}=${JSON.stringify(v)}`).join('；') : '未提供';
  function journalReasoningSummary(values, empty) {
    const fields = values || {}, parts = [];
    const level = fields['reasoning.effort'] ?? fields['output_config.effort'] ?? fields.reasoning_effort ?? fields['thinking.effort'] ?? fields.effort;
    if (level != null) parts.push(String(level));
    if (fields['thinking.type'] != null) parts.push('模式 ' + fields['thinking.type']);
    if (fields['thinking.budget_tokens'] != null) parts.push(`预算 ${fields['thinking.budget_tokens']} tokens`);
    const tokens = fields['usage.output_tokens_details.reasoning_tokens'] ?? fields['usage.completion_tokens_details.reasoning_tokens'];
    if (tokens != null) parts.push(`${tokens} tokens`);
    return parts.join(' · ') || (Object.keys(fields).length ? '已记录参数' : empty);
  }
  const journalModel = (value, fallback = '未提供') => typeof value === 'string' && value.trim() ? value : fallback;
  const journalRequestedModel = value => journalModel(value.requestedModel, journalModel(value.model));
  const journalTokenFormat = new Intl.NumberFormat('en-US');
  const journalToken = value => typeof value === 'number' && Number.isSafeInteger(value) && value >= 0
    ? journalTokenFormat.format(value) : '未返回';
  const journalTokenValues = usage => [
    ['输入 Token', journalToken(usage?.inputTokens)], ['输出 Token', journalToken(usage?.outputTokens)],
    ['缓存读取', journalToken(usage?.cacheReadTokens)], ['缓存写入', journalToken(usage?.cacheWriteTokens)]
  ];
  const journalMappingSource = mapping => (mapping.source === 'model' ? '模型规则：' + mapping.model : '渠道默认规则')
    + ' · ' + (mapping.reason === 'default' ? '未提供时使用缺省值' : mapping.mode === 'fixed' ? '固定等级' : '按值映射');
  function journalError(error) { feedback('journalStatus', error.message || String(error), true); }
  const journalActive = () => workspacePage === 'journal' && !$('journal').hidden;
  function updateJournalScope() {
    const channel = $('journalFilters').elements.channel.value;
    $('journalScope').hidden = !channel;
    $('journalScopeLabel').textContent = '渠道：' + (accounts.find(account => account.id === channel)?.label || channel);
    $('journalTitle').textContent = channel ? '请求日志 · ' + (accounts.find(account => account.id === channel)?.label || channel) : '请求日志';
  }
  function closeJournal() {
    journalEpoch++; journalPartEpoch++; journalDetail = null; journalRawBody = '';
    $('journalDetail').close(); $('journalBody').textContent = ''; $('journalMetadata').textContent = '';
    setWorkspacePage('channels');
  }
  function updateJournalPagination() {
    $('journalPrevious').disabled = journalLoading || journalPage <= 1;
    $('journalNext').disabled = journalLoading || journalPage >= journalPages;
    $('journalPageSize').disabled = journalLoading;
    $('journalReload').disabled = journalLoading;
  }
  async function openJournal(channel = '') {
    $('journalFilters').reset(); $('journalFilters').elements.channel.value = channel;
    updateJournalScope();
    $('journalAdvanced').hidden = !channel;
    $('journalExpand').setAttribute('aria-expanded', String(!!channel));
    $('journalExpand').textContent = channel ? '收起筛选' : '更多筛选';
    $('journalEmpty').hidden = true;
    journalPage = 1; journalPages = 1; journalDetail = null; journalRawBody = ''; journalPartEpoch++;
    $('journalDetail').close(); $('journalBody').textContent = ''; $('journalMetadata').textContent = '';
    setWorkspacePage('journal');
    await loadJournal();
  }
  async function loadJournal(page = journalPage) {
    const epoch = ++journalEpoch;
    journalLoading = true; updateJournalPagination();
    updateJournalScope();
    feedback('journalStatus', '正在读取日志…');
    try {
      const query = new URLSearchParams({ page, pageSize: $('journalPageSize').value });
      for (const [key, value] of new FormData($('journalFilters'))) {
        if (!value) continue;
        query.set(key, key === 'from' || key === 'to' ? new Date(value).toISOString() : value);
      }
      const state = await api('GET', 'logs/status');
      if (epoch !== journalEpoch || !journalActive()) return;
      $('journalStorage').textContent = `存储：本次运行内存\n状态：${state.available ? '可用' : '不可用'}\n正文 ${state.bodyBytes || 0} 字节 · ${state.records || 0} 条\n未保留操作：${state.droppedWrites || 0}${state.error ? '\n错误：' + state.error : ''}`;
      if (state.settings) {
        const fields = $('journalSettings').elements;
        fields.enabled.checked = state.settings.enabled;
        fields.maxRecords.value = state.settings.maxRecords;
        fields.capacityMiB.value = state.settings.capacityBytes / 1048576;
        fields.bodyMiB.value = state.settings.bodyLimitBytes / 1048576;
      }
      if (!state.available) throw Error(state.error || '日志存储不可用');
      const data = await api('GET', 'logs?' + query);
      if (epoch !== journalEpoch || !journalActive()) return;
      journalPage = data.page; journalPageSize = data.pageSize;
      journalPages = Math.max(1, Math.ceil(data.total / data.pageSize));
      $('journalRecordCount').textContent = data.total + ' 条';
      const root = $('journalRows'); root.replaceChildren();
      for (const row of data.rows || []) {
        const tr = el('tr'), identity = el('td'), model = el('td'), stateCell = el('td'), effort = el('td'), actions = el('td');
        const stamp = new Date(row.started), trace = el('small', row.trace || row.id, 'record-trace');
        trace.title = row.trace || row.id;
        identity.append(el('strong', stamp.toLocaleTimeString('zh-CN', { hour12: false }), 'record-time'),
          el('span', stamp.toLocaleDateString(), 'record-date'),
          el('div', row.channelLabel || row.channel || '未保存渠道', 'record-channel'), trace);
        const requestedModel = el('strong', journalRequestedModel(row), 'record-model');
        requestedModel.title = '本地请求模型：' + journalRequestedModel(row);
        const route = el('div', undefined, 'record-route');
        route.append(el('code', row.endpoint || '—', 'record-endpoint'), el('span', journalKinds[row.kind] || row.kind, 'record-kind'));
        const network = el('span', row.network === 'proxyPool' ? '代理池' : '直连', 'record-network');
        if (row.proxy) network.title = `实际代理：${row.proxy.address} · ${row.proxy.id}`;
        route.append(network); model.append(requestedModel, route);
        const reported = el('small', '响应：' + journalModel(row.reportedModel, '未返回'), 'record-reported-model');
        reported.title = '上游响应模型：' + journalModel(row.reportedModel, '未返回'); model.append(reported);
        stateCell.append(el('div', `${journalStates[row.state] || row.state} · HTTP ${row.status ?? '—'}${row.incomplete ? ' · 日志不完整' : ''}`, 'console-badge ' + (row.state === 'completed' ? 'is-success' : row.state === 'failed' ? 'is-error' : row.state === 'running' ? 'is-active' : 'is-neutral')),
          el('div', '', 'record-metrics'));
        for (const [label, value] of [['首字', `${row.firstByteMs ?? '—'} ms`], ['耗时', `${row.durationMs ?? '—'} ms`], ['重试', String(row.retries || 0)],
          ['输入 Token', journalToken(row.tokenUsage?.inputTokens)], ['输出 Token', journalToken(row.tokenUsage?.outputTokens)]]) {
          const metric = el('span');
          metric.append(el('small', label), el('strong', value));
          stateCell.lastChild.append(metric);
        }
        if (row.reasoningMapping) {
          const mapping = row.reasoningMapping;
          const mappingLine = el('div', `客户端 ${mapping.received == null ? '未提供' : JSON.stringify(mapping.received)} → 发送 ${JSON.stringify(mapping.sent)}`, 'record-effort');
          mappingLine.title = journalMappingSource(mapping); effort.append(mappingLine);
        } else {
          const sent = row.sentReasoning || {};
          const sentLine = el('div', '请求：' + journalReasoningSummary(sent, '未提供'), 'record-effort');
          sentLine.title = journalEffort(sent); effort.append(sentLine);
        }
        const reportedValues = row.reportedReasoning || {};
        const reportedLine = el('small', '上游：' + journalReasoningSummary(reportedValues, '未返回'), 'record-effort-reported');
        reportedLine.title = journalEffort(reportedValues); effort.append(reportedLine);
        actions.append(button('详情', () => {
          for (const item of root.children) item.classList.remove('is-selected');
          tr.classList.add('is-selected'); showJournalDetail(row.id);
        }, 'text-button'));
        for (const [cell, label] of [[identity, '时间 / 渠道'], [model, '模型 / 接口'], [stateCell, '结果'], [effort, '思考参数'], [actions, '操作']]) cell.dataset.label = label;
        tr.append(identity, model, stateCell, effort, actions); root.append(tr);
      }
      $('journalEmpty').hidden = !!root.childElementCount;
      root.closest('.journal-table').scrollTop = 0;
      $('journalPage').textContent = `第 ${journalPage} / ${journalPages} 页 · 共 ${data.total} 条`;
      feedback('journalStatus', state.error || '', !!state.error);
    } catch (error) { if (epoch === journalEpoch && journalActive()) journalError(error); }
    finally {
      if (epoch === journalEpoch) {
        journalLoading = false; $('journalPageSize').value = String(journalPageSize); updateJournalPagination();
      }
    }
  }
  async function showJournalDetail(id) {
    const epoch = ++journalPartEpoch;
    journalDetail = null; journalRawBody = ''; $('journalBody').textContent = '';
    try {
      const received = await api('GET', 'logs/detail?id=' + encodeURIComponent(id));
      if (epoch !== journalPartEpoch || !journalActive()) return;
      const detail = { ...received, parts: journalParts(received) };
      journalDetail = detail;
      renderJournalOverview(detail);
      if (!$('journalDetail').open) $('journalDetail').showModal();
      $('journalMetadata').textContent = JSON.stringify(detail, null, 2);
      $('journalDelete').disabled = detail.state === 'running';
      $('journalPart').replaceChildren(...(detail.parts || []).map(part => option(part.name, journalPartLabel(part.name))));
      const response = detail.parts.filter(p => /^attempt-\d+-response-headers$/.test(p.name)).at(-1);
      if (response) $('journalPart').value = response.name;
      await loadJournalPart();
    } catch (error) { if (epoch === journalPartEpoch) journalError(error); }
  }
  function renderJournalOverview(detail) {
    const root = $('journalOverview'); root.replaceChildren();
    const group = (title, values) => {
      const section = el('section', '', 'detail-group');
      if (title) section.append(el('h4', title));
      const list = el('dl', '', title ? 'detail-values detail-card' : 'detail-values');
      for (const [label, value] of values) {
        list.append(el('dt', label), el('dd', value == null || value === '' ? '—' : String(value)));
      }
      section.append(list); root.append(section);
    };
    group('', [['请求 ID', detail.id], ['Trace ID', detail.trace], ['渠道', detail.channelLabel || detail.channel],
      ['执行状态', journalStates[detail.state] || detail.state],
      ['出站方式', detail.network === 'proxyPool' ? '代理池' : '直连'],
      ['耗时', `${detail.durationMs ?? '—'} ms · 首字 ${detail.firstByteMs ?? '—'} ms`]]);
    group('请求信息', [['接口', detail.endpoint], ['请求类型', journalKinds[detail.kind] || detail.kind],
      ['状态码', detail.status], ['重试次数', detail.retries || 0]]);
    const hostReasons = { success: '响应成功', input_error: '请求参数错误', permanent_error: '上游永久错误',
      total_timeout: '总时限已耗尽', request_cancelled: '请求已取消', stream_transferred: '流已交付，禁止重放',
      output_observed: '已有有效输出，禁止重放', connection_test: '连接测试仅使用选定渠道',
      retryable_status: '可重试的上游状态', header_timeout: '等待响应头超时', transport_error: '连接失败',
      rate_limit: '限流重试已结束', empty_response: '空响应重试已结束', stream_interrupted: '输出前流中断',
      retry_not_configured: '未满足继续尝试条件', invalid_upstream_response: '上游响应无效',
      unclassified_response: '无法确认响应可重放', plugin_error: '插件执行失败' };
    if (detail.internalSends != null) group('宿主尝试衔接', [['本次内部发送', `${detail.internalSends} 次`],
      ['允许宿主继续', detail.hostContinuationAllowed == null ? '尚未决定' : detail.hostContinuationAllowed ? '是，由宿主决定下一步' : '否'],
      ['原因', hostReasons[detail.hostContinuationReason] || detail.hostContinuationReason]]);
    group('模型信息', [['本地请求模型', journalRequestedModel(detail)],
      ['实际发送模型', journalModel(detail.sentModel)], ['上游响应模型', journalModel(detail.reportedModel, '未返回')]]);
    group('Token 明细', journalTokenValues(detail.tokenUsage));
    if (detail.proxy) group('实际代理', [['节点地址', detail.proxy.address], ['代理 ID', detail.proxy.id], ['来源 ID', detail.proxy.subscriptionId]]);
    group('思考参数', [...(detail.reasoningMapping ? [
      ['客户端', journalEffort(detail.receivedReasoning)], ['命中规则', journalMappingSource(detail.reasoningMapping)]] : []),
      [detail.reasoningMapping ? '实际发送' : '请求', journalEffort(detail.sentReasoning)],
      ['上游', detail.reportedReasoning && Object.keys(detail.reportedReasoning).length ? journalEffort(detail.reportedReasoning) : '未返回']]);
    for (const attempt of detail.attempts || []) group(`上游尝试 ${attempt.number}`, [
      ['连接方式', attempt.transport === 'websocket' ? 'WebSocket' : 'HTTP'],
      ['实际发送模型', journalModel(attempt.sentModel)], ['上游响应模型', journalModel(attempt.reportedModel, '未返回')],
      ['请求地址', attempt.url], ['最终地址', attempt.finalUrl], ['状态码', attempt.status],
      ['重试原因', attempt.retryReason || '无'], ...journalTokenValues(attempt.tokenUsage)]);
  }
  async function readJournalChunks(id, part, consume, current) {
    let after = -1;
    while (current()) {
      const data = await api('GET', 'logs/body?' + new URLSearchParams({ id, part, after }));
      if (!current()) return;
      for (const chunk of data.chunks || []) await consume(chunk);
      if (data.done) return;
      if (data.next <= after) throw Error('日志分段游标无进展');
      after = data.next;
    }
  }
  async function loadJournalPart() {
    const detail = journalDetail, part = $('journalPart').value, epoch = ++journalPartEpoch;
    const stat = detail && journalParts(detail).find(p => p.name === part);
    $('journalPart').disabled = !detail?.parts.length;
    $('journalRaw').disabled = !stat; $('journalJson').disabled = !stat; $('journalCopy').disabled = true;
    if (!stat) {
      journalRawBody = ''; $('journalBody').textContent = '（没有可查看的原始内容）'; $('journalPartStatus').textContent = '';
      return;
    }
    journalRawBody = ''; $('journalBody').textContent = '正在读取正文…'; $('journalCopy').disabled = true;
    $('journalPartStatus').textContent = `已保存 ${stat.savedBytes} 字节 · 已观察 ${stat.observedBytes} 字节${stat.truncated ? ' · 已截断或读取不完整' : ''}`;
    const decoder = new TextDecoder(), segments = [];
    const current = () => epoch === journalPartEpoch && journalActive();
    try {
      await readJournalChunks(detail.id, part, chunk => {
        const bytes = Uint8Array.from(atob(chunk.base64), c => c.charCodeAt(0));
        segments.push(decoder.decode(bytes, { stream: true }));
      }, current);
      if (!current()) return;
      segments.push(decoder.decode()); journalRawBody = segments.join('');
      $('journalBody').textContent = journalRawBody || '（正文为空）';
      $('journalCopy').disabled = false;
    } catch (error) { if (current()) { $('journalBody').textContent = ''; journalError(error); } }
  }
  $('openJournal').onclick = () => openJournal();
  $('journalDetailClose').onclick = () => $('journalDetail').close();
  $('journalDetail').addEventListener('close', () => {
    journalPartEpoch++; journalRawBody = ''; journalDetail = null;
    $('journalBody').textContent = ''; $('journalMetadata').textContent = ''; $('journalOverview').replaceChildren();
  });
  $('journalClose').onclick = closeJournal;
  $('journalReload').onclick = () => loadJournal();
  $('journalFilters').onsubmit = event => { event.preventDefault(); loadJournal(1); };
  $('journalReset').onclick = () => {
    $('journalFilters').reset(); loadJournal(1);
  };
  $('journalScopeClear').onclick = () => { $('journalFilters').elements.channel.value = ''; loadJournal(1); };
  $('journalPageSize').onchange = () => loadJournal(1);
  $('journalPrevious').onclick = () => { if (!journalLoading && journalPage > 1) loadJournal(journalPage - 1); };
  $('journalNext').onclick = () => { if (!journalLoading && journalPage < journalPages) loadJournal(journalPage + 1); };
  $('journalPart').onchange = loadJournalPart;
  $('journalRaw').onclick = () => { $('journalBody').textContent = journalRawBody || '（正文为空）'; };
  $('journalJson').onclick = () => {
    try { $('journalBody').textContent = JSON.stringify(JSON.parse(journalRawBody), null, 2); }
    catch { feedback('journalStatus', '正文不是单个 JSON 对象，可使用原文查看 SSE 或其他内容', true); }
  };
  $('journalCopy').onclick = async () => {
    const epoch = journalPartEpoch;
    try { await copyText(journalRawBody); if (epoch === journalPartEpoch) feedback('journalStatus', '已复制原文'); }
    catch (error) { if (epoch === journalPartEpoch) journalError(error); }
  };
  $('journalClear').onclick = async () => {
    if (!await ask('清空所有已结束的请求日志？', '清空请求日志')) return;
    try { await api('POST', 'logs/clear', {}); $('journalDetail').close(); journalPartEpoch++; journalDetail = null; journalRawBody = ''; $('journalBody').textContent = ''; $('journalMetadata').textContent = ''; await loadJournal(1); }
    catch (error) { journalError(error); }
  };
  $('journalDelete').onclick = async () => {
    const detail = journalDetail;
    if (!detail || !await ask('删除这条请求日志及正文？', '删除请求日志')) return;
    try { await api('POST', 'logs/delete', { id: detail.id }); $('journalDetail').close(); journalPartEpoch++; journalDetail = null; journalRawBody = ''; $('journalBody').textContent = ''; $('journalMetadata').textContent = ''; await loadJournal(); }
    catch (error) { journalError(error); }
  };
  $('journalSettings').onsubmit = async event => {
    event.preventDefault(); const fields = $('journalSettings').elements;
    try {
      await api('POST', 'logs/settings', { enabled: fields.enabled.checked, maxRecords: +fields.maxRecords.value,
        capacityBytes: +fields.capacityMiB.value * 1048576, bodyLimitBytes: +fields.bodyMiB.value * 1048576 });
      await loadJournal(); feedback('journalStatus', '日志设置已生效，仅限本次运行');
    } catch (error) { journalError(error); }
  };
  $('journalExport').onclick = async () => {
    const detail = journalDetail, epoch = journalPartEpoch;
    if (!detail) return;
    const exportWindow = window.open('', '_blank');
    if (!exportWindow) { journalError(Error('浏览器阻止导出窗口，请允许弹出窗口')); return; }
    exportWindow.document.title = '导出请求日志';
    exportWindow.document.body.textContent = '正在准备日志文件…';
    $('journalExport').disabled = true;
    const current = () => epoch === journalPartEpoch && journalActive();
    try {
      const parts = journalParts(detail);
      const pieces = [JSON.stringify({ ...detail, parts, format: 'universalforward-request-log-v1' }).slice(0, -1), ',"bodyChunks":['];
      let first = true;
      for (const part of parts)
        await readJournalChunks(detail.id, part.name, chunk => {
          pieces.push((first ? '' : ',') + JSON.stringify({ part: part.name, ...chunk })); first = false;
        }, current);
      if (!current()) { exportWindow.close(); return; }
      pieces.push(']}');
      const url = URL.createObjectURL(new Blob(pieces, { type: 'application/json' }));
      const link = exportWindow.document.createElement('a'); link.href = url; link.download = `request-${detail.id}.json`;
      link.textContent = '下载日志文件'; exportWindow.document.body.replaceChildren(link);
      link.click(); setTimeout(() => URL.revokeObjectURL(url), 60000);
      feedback('journalStatus', '已导出日志，正文以 Base64 分段保留');
    } catch (error) { exportWindow.close(); if (current()) journalError(error); }
    finally { $('journalExport').disabled = false; }
  };
