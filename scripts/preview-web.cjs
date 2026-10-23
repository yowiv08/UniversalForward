// Interactive UI preview with synthetic data. Requests stay in this local page.
const fs = require('node:fs');
const path = require('node:path');
const pageSource = require('../tests/browser/page-source.cjs');
const output = path.resolve(__dirname, '../artifacts/ui-refactor-20261023');

function demoBridge() {
  const now = Date.now();
  let accounts = [
    { id: 'primary', label: 'OpenAI 主力渠道', baseUrl: 'https://api.openai.example/v1', enabled: true, weight: 100,
      networkMode: 'direct', responsesTransport: 'http', keyRevision: 1, keySelectionMode: 'roundRobin',
      models: ['gpt-5.4', 'gpt-5.4-mini', 'gpt-5.3-codex'], endpoints: ['/v1/responses'],
      availableModels: ['gpt-5.4', 'gpt-5.4-mini', 'gpt-5.3-codex', 'gpt-5.2'],
      keys: [{ id: 'key-primary', name: 'a1b2', secret: 'sk-demo-openai-a1b2', enabled: true }, { id: 'key-secondary', name: 'c3d4', secret: 'sk-demo-openai-c3d4', enabled: true }],
      reasoningPolicy: { defaults: { responses: { mode: 'map', mappings: [{ from: 'low', to: 'high' }] } }, models: {} },
      requestPolicy: { maxRetries: 2, headerTimeoutSeconds: 60, totalTimeoutSeconds: 180, streamIdleTimeoutSeconds: 60, rateLimitRetryEnabled: true } },
    { id: 'claude', label: 'Claude 工作区', baseUrl: 'https://api.anthropic.example', enabled: true, weight: 80,
      networkMode: 'proxyPool', proxySubscriptionIds: ['manual'], keyRevision: 1, keySelectionMode: 'priority',
      models: ['claude-opus-4-6', 'claude-sonnet-4-6'], endpoints: ['/v1/messages'],
      keys: [{ id: 'key-claude', name: 'e5f6', secret: 'sk-demo-anthropic-e5f6', enabled: true }],
      headerOverride: { 'anthropic-version': '2023-06-01', 'User-Agent': 'claude-cli/2.1.161' } },
    { id: 'backup', label: '备用转发渠道', baseUrl: 'https://gateway.example/v1', enabled: false, weight: 20,
      networkMode: 'direct', keyRevision: 1, keySelectionMode: 'random',
      models: ['gpt-5.4', 'claude-sonnet-4-6'], endpoints: ['/v1/responses', '/v1/messages'],
      keys: [{ id: 'key-backup', name: 'g7h8', secret: 'sk-demo-backup-g7h8', enabled: false }] }
  ];
  let settings = { enabled: true, maxRecords: 1000, capacityBytes: 67108864, bodyLimitBytes: 4194304 };
  let logs = Array.from({ length: 15 }, (_, index) => {
    const account = accounts[index % 2];
    const model = account.models[index % account.models.length];
    const failed = index === 3 || index === 9;
    return { id: 'request-' + (index + 1), trace: 'trace-20261023-' + String(index + 1).padStart(4, '0'),
      started: now - index * 123000, channel: account.id, channelLabel: account.label, model,
      requestedModel: 'universalforward/' + model, sentModel: model, reportedModel: model,
      endpoint: account.endpoints[0], kind: 'forward', network: account.networkMode,
      state: failed ? 'failed' : 'completed', status: failed ? 429 : 200, durationMs: failed ? 5230 : 1210 + index * 172,
      firstByteMs: 240 + index * 31, retries: failed ? 2 : 0,
      sentReasoning: account.id === 'primary' ? { 'reasoning.effort': 'high' } : {},
      reportedReasoning: account.id === 'primary' ? { 'reasoning.effort': 'high', 'usage.output_tokens_details.reasoning_tokens': 128 + index * 32 } : {},
      ...(account.id === 'primary' ? { receivedReasoning: { 'reasoning.effort': 'low' }, reasoningMapping: { source: 'channel', protocol: 'responses', mode: 'map', reason: 'mapped', received: 'low', sent: 'high' } } : {}),
      ...(account.id === 'claude' ? { proxy: { id: 'proxy-manual', subscriptionId: 'manual', address: 'socks5://127.0.0.1:1080' } } : {}) };
  });
  const jobs = new Map();
  const bodyFor = record => JSON.stringify({ id: record.id, model: record.sentModel, output: [{ type: 'message', content: [{ type: 'output_text', text: 'OK. This is a local UI preview.' }] }], usage: { input_tokens: 24, output_tokens: 32 } }, null, 2);
  window.Router2API = { request: async (method, route, body) => {
    await new Promise(resolve => setTimeout(resolve, 90));
    const [endpoint, queryString] = route.split('?');
    const query = new URLSearchParams(queryString);
    if (endpoint === 'accounts') return { accounts: structuredClone(accounts) };
    if (endpoint === 'accounts/save') {
      const previous = accounts.find(account => account.id === body.id), id = body.id || 'demo-' + Date.now();
      const account = { ...previous, ...body, id, keyRevision: (previous?.keyRevision || 0) + 1,
        keys: body.keys.map((key, index) => {
          const secret = key.secret.trim() || previous?.keys.find(item => item.id === key.id)?.secret || '';
          return { id: key.id || id + '-key-' + index, name: key.name?.trim() || secret.slice(-4), enabled: key.enabled, secret };
        }) };
      delete account.deletedKeyIds;
      accounts = [...accounts.filter(item => item.id !== id), account]; return { account: structuredClone(account) };
    }
    if (endpoint === 'accounts/enabled') {
      const account = accounts.find(item => item.id === body.id); account.enabled = body.enabled;
      return { account: structuredClone(account) };
    }
    if (endpoint === 'accounts/delete') { accounts = accounts.filter(item => item.id !== body.id); return {}; }
    if (endpoint === 'network/proxies') return { subscriptions: [{ id: 'manual', name: '手动节点', enabled: true }, { id: 'subscription-a', name: '工作区订阅', enabled: true }],
      nodes: [{ id: 'proxy-manual', subscriptionId: 'manual', address: 'socks5://127.0.0.1:1080', available: true, state: 'Active' },
        { id: 'proxy-subscribed', subscriptionId: 'subscription-a', address: 'http://127.0.0.1:8080', available: true, state: 'Active' }],
      execution: { setupTimeoutSeconds: 60, totalSetupTimeoutSeconds: 180, responseTimeoutSeconds: 600 } };
    if (endpoint === 'models/discover' || endpoint === 'models/refresh') return { models: ['gpt-5.4', 'gpt-5.4-mini', 'gpt-5.3-codex', 'claude-opus-4-6', 'claude-sonnet-4-6'] };
    if (endpoint === 'logs/status') return { available: true, records: logs.length, bodyBytes: 12480, settings: structuredClone(settings), droppedWrites: 0 };
    if (endpoint === 'logs') {
      const rows = logs.filter(record => (!query.get('channel') || query.get('channel') === record.channel) &&
        (!query.get('model') || record.model.includes(query.get('model'))) && (!query.get('trace') || record.trace.includes(query.get('trace'))) &&
        (!query.get('kind') || record.kind === query.get('kind')) && (!query.get('status') || String(record.status) === query.get('status')) &&
        (!query.get('endpoint') || record.endpoint === query.get('endpoint')) && (!query.get('network') || record.network === query.get('network')) &&
        (!query.get('state') || record.state === query.get('state')) && (!query.get('from') || record.started >= Date.parse(query.get('from'))) &&
        (!query.get('to') || record.started <= Date.parse(query.get('to'))));
      const pageSize = Number(query.get('pageSize') || 10), page = Math.min(Number(query.get('page') || 1), Math.max(1, Math.ceil(rows.length / pageSize)));
      return { rows: structuredClone(rows.slice((page - 1) * pageSize, page * pageSize)), page, pageSize, total: rows.length };
    }
    if (endpoint === 'logs/detail') {
      const record = logs.find(item => item.id === query.get('id'));
      return { ...record, attempts: [{ number: 1, transport: 'http', url: 'https://upstream.example' + record.endpoint, status: record.status, sentModel: record.sentModel, reportedModel: record.reportedModel }],
        parts: [{ name: 'attempt-1-response', savedBytes: bodyFor(record).length, observedBytes: bodyFor(record).length, truncated: false }] };
    }
    if (endpoint === 'logs/body') {
      const bytes = new TextEncoder().encode(bodyFor(logs.find(item => item.id === query.get('id'))));
      return { chunks: [{ sequence: 0, base64: btoa(String.fromCharCode(...bytes)) }], next: 0, done: true };
    }
    if (endpoint === 'logs/delete') { logs = logs.filter(item => item.id !== body.id); return {}; }
    if (endpoint === 'logs/clear') { logs = []; return {}; }
    if (endpoint === 'logs/settings') { settings = structuredClone(body); return {}; }
    if (endpoint === 'tests/start') {
      const account = accounts.find(item => item.id === body.accountId), keys = account.keys.filter(key => key.enabled);
      const targets = body.models.flatMap(model => (body.keyMode === 'all' ? keys : [keys.find(key => key.id === body.keyId) || keys[0]])
        .map(key => ({ model, keyId: key?.id, keyName: key?.name || '按策略', success: true, originalStatus: 200, mappedStatus: 200,
          durationMs: 120 + Math.round(Math.random() * 400), retries: 0, response: '{"output":"OK"}', networkMode: account.networkMode, transport: account.responsesTransport || 'http' })));
      const id = 'demo-job-' + Date.now(); jobs.set(id, { targets, polls: 0, cancelled: false }); return { id, state: 0 };
    }
    if (endpoint === 'tests/status') {
      const job = jobs.get(query.get('id')); job.polls++;
      return job.cancelled ? { state: 4, progress: { rows: [], total: job.targets.length, completed: 0 } }
        : job.polls > 1 ? { state: 2, result: { rows: job.targets }, progress: { total: job.targets.length, completed: job.targets.length } }
        : { state: 1, progress: { rows: [], total: job.targets.length, completed: 0, running: job.targets[0]?.model } };
    }
    if (endpoint === 'tests/cancel') { jobs.get(body.id).cancelled = true; return { accepted: true }; }
    throw Error('演示页面未实现此操作：' + endpoint);
  } };
  window.previewAccounts = () => structuredClone(accounts);
}

fs.mkdirSync(output, { recursive: true });
const bridge = `<script>(${demoBridge.toString()})();</script>`;
const source = pageSource().replace('<head>', '<head>' + bridge)
  .replace('<title>UniversalForward</title>', '<title>UniversalForward · 界面预览</title>')
  .replace('Router2API 插件</span>', '交互预览 · 演示数据</span>');
fs.writeFileSync(path.join(output, 'preview.html'), source);
console.log(path.join(output, 'preview.html'));
if (process.argv.includes('--serve')) {
  const http = require('node:http');
  const server = http.createServer((request, response) => {
    if (request.url !== '/' && request.url !== '/preview.html') { response.writeHead(404); response.end(); return; }
    response.setHeader('Content-Type', 'text/html; charset=utf-8');
    response.setHeader('Cache-Control', 'no-store');
    response.end(fs.readFileSync(path.join(output, 'preview.html')));
  });
  server.listen(0, '127.0.0.1', () => console.log(`http://127.0.0.1:${server.address().port}/`));
}
