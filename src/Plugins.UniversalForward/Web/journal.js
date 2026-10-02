  let journalEpoch = 0, journalPage = 1, journalDetail = null, journalRawBody = '', journalPartEpoch = 0;
  const journalStates = { running: '执行中', completed: '已完成', failed: '失败', cancelled: '已取消', interrupted: '中断' };
  const journalKinds = { forward: '转发', 'connection-test': '连接测试', 'models-discover': '获取模型', 'models-refresh': '刷新模型' };
  const journalPartLabel = name => {
    const labels = { incoming: '插件收到的请求体', extensions: '宿主传入的扩展参数', 'incoming-headers': '宿主提供的请求头' };
    if (labels[name]) return labels[name];
    const match = /^attempt-(\d+)-(request|response)(-headers)?$/.exec(name);
    return match ? `尝试 ${match[1]} · ${match[2] === 'request' ? '实际发送' : '上游响应'}${match[3] ? '头' : '正文'}` : name;
  };
  const journalEffort = value => value && Object.keys(value).length
    ? Object.entries(value).map(([key, v]) => `${key}=${JSON.stringify(v)}`).join('；') : '未提供';
  function journalError(error) { feedback('journalStatus', error.message || String(error), true); }
  async function openJournal(channel = '') {
    $('journalFilters').reset(); $('journalFilters').elements.channel.value = channel;
    journalPage = 1; journalDetail = null; journalRawBody = ''; journalPartEpoch++;
    $('journalDetail').hidden = true; $('journalBody').textContent = ''; $('journalMetadata').textContent = '';
    if (!$('journal').open) $('journal').showModal();
    await loadJournal();
  }
  async function loadJournal() {
    const epoch = ++journalEpoch;
    feedback('journalStatus', '正在读取日志…');
    try {
      const state = await api('GET', 'logs/status');
      if (epoch !== journalEpoch || !$('journal').open) return;
      $('journalStorage').textContent = `目录：${state.directory || '未初始化'}\n状态：${state.available ? '可用' : '不可用'}\n未写入操作：${state.droppedWrites || 0}${state.error ? '\n错误：' + state.error : ''}`;
      if (state.settings) {
        const fields = $('journalSettings').elements;
        fields.enabled.checked = state.settings.enabled;
        fields.retentionDays.value = state.settings.retentionDays;
        fields.capacityMiB.value = state.settings.capacityBytes / 1048576;
        fields.bodyMiB.value = state.settings.bodyLimitBytes / 1048576;
      }
      if (!state.available) throw Error(state.error || '日志存储不可用');
      const query = new URLSearchParams({ page: journalPage, pageSize: 25 });
      for (const [key, value] of new FormData($('journalFilters'))) {
        if (!value) continue;
        query.set(key, key === 'from' || key === 'to' ? new Date(value).toISOString() : value);
      }
      const data = await api('GET', 'logs?' + query);
      if (epoch !== journalEpoch || !$('journal').open) return;
      const root = $('journalRows'); root.replaceChildren();
      for (const row of data.rows || []) {
        const tr = el('tr'), identity = el('td'), model = el('td'), stateCell = el('td'), effort = el('td'), actions = el('td');
        identity.append(el('div', new Date(row.started).toLocaleString()), el('div', row.channelLabel || row.channel || '未保存渠道'), el('small', row.trace || row.id));
        model.append(el('div', row.model || '—'), el('div', row.endpoint || '—'), el('small', `${journalKinds[row.kind] || row.kind} · ${row.network === 'proxyPool' ? '代理池' : '直连'}`));
        stateCell.append(el('div', `${journalStates[row.state] || row.state} · HTTP ${row.status ?? '—'}${row.incomplete ? ' · 日志不完整' : ''}`),
          el('small', `首包 ${row.firstByteMs ?? '—'} ms · 总计 ${row.durationMs ?? '—'} ms · 重试 ${row.retries || 0}`));
        effort.append(el('div', '收到：' + journalEffort(row.receivedReasoning)),
          el('div', '扩展：' + journalEffort(row.extensionReasoning)),
          el('div', '发送：' + journalEffort(row.sentReasoning)),
          el('div', '上游报告：' + (row.reportedReasoning ? journalEffort(row.reportedReasoning) : '未返回')));
        if (row.reasoningChanged) effort.append(el('strong', '插件收到与发送的参数不同'));
        actions.append(button('详情', () => showJournalDetail(row.id)));
        tr.append(identity, model, stateCell, effort, actions); root.append(tr);
      }
      $('journalPage').textContent = `第 ${data.page} 页 · 共 ${data.total} 条`;
      $('journalPrevious').disabled = journalPage <= 1;
      $('journalNext').disabled = journalPage * data.pageSize >= data.total;
      feedback('journalStatus', state.error || '', !!state.error);
    } catch (error) { if (epoch === journalEpoch && $('journal').open) journalError(error); }
  }
  async function showJournalDetail(id) {
    const epoch = ++journalPartEpoch;
    journalDetail = null; journalRawBody = ''; $('journalBody').textContent = '';
    try {
      const detail = await api('GET', 'logs/detail?id=' + encodeURIComponent(id));
      if (epoch !== journalPartEpoch || !$('journal').open) return;
      journalDetail = detail; $('journalDetail').hidden = false;
      $('journalMetadata').textContent = JSON.stringify(detail, null, 2);
      $('journalDelete').disabled = detail.state === 'running';
      $('journalPart').replaceChildren(...(detail.parts || []).map(part => option(part.name, journalPartLabel(part.name))));
      const response = (detail.parts || []).filter(p => /^attempt-\d+-response$/.test(p.name)).at(-1);
      if (response) $('journalPart').value = response.name;
      await loadJournalPart();
    } catch (error) { if (epoch === journalPartEpoch) journalError(error); }
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
    if (!detail || !part) return;
    journalRawBody = ''; $('journalBody').textContent = '正在读取正文…'; $('journalCopy').disabled = true;
    const stat = detail.parts.find(p => p.name === part);
    $('journalPartStatus').textContent = `已保存 ${stat.savedBytes} 字节 · 已观察 ${stat.observedBytes} 字节${stat.truncated ? ' · 已截断或读取不完整' : ''}`;
    const decoder = new TextDecoder(), segments = [];
    const current = () => epoch === journalPartEpoch && $('journal').open;
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
  $('journalClose').onclick = () => $('journal').close();
  $('journal').addEventListener('close', () => { journalEpoch++; journalPartEpoch++; journalDetail = null; journalRawBody = ''; $('journalBody').textContent = ''; $('journalMetadata').textContent = ''; });
  $('journalFilters').onsubmit = event => { event.preventDefault(); journalPage = 1; loadJournal(); };
  $('journalPrevious').onclick = () => { journalPage--; loadJournal(); };
  $('journalNext').onclick = () => { journalPage++; loadJournal(); };
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
    try { await api('POST', 'logs/clear', {}); $('journalDetail').hidden = true; journalPartEpoch++; journalDetail = null; journalPage = 1; await loadJournal(); }
    catch (error) { journalError(error); }
  };
  $('journalDelete').onclick = async () => {
    const detail = journalDetail;
    if (!detail || !await ask('删除这条请求日志及正文？', '删除请求日志')) return;
    try { await api('POST', 'logs/delete', { id: detail.id }); $('journalDetail').hidden = true; journalPartEpoch++; journalDetail = null; await loadJournal(); }
    catch (error) { journalError(error); }
  };
  $('journalSettings').onsubmit = async event => {
    event.preventDefault(); const fields = $('journalSettings').elements;
    try {
      await api('POST', 'logs/settings', { enabled: fields.enabled.checked, retentionDays: +fields.retentionDays.value,
        capacityBytes: +fields.capacityMiB.value * 1048576, bodyLimitBytes: +fields.bodyMiB.value * 1048576 });
      await loadJournal(); feedback('journalStatus', '日志设置已保存');
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
    const current = () => epoch === journalPartEpoch && $('journal').open;
    try {
      const pieces = [JSON.stringify({ ...detail, format: 'universalforward-request-log-v1' }).slice(0, -1), ',"bodyChunks":['];
      let first = true;
      for (const part of detail.parts || [])
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
