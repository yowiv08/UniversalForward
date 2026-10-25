const assert = require('node:assert/strict');
const bridge = `<script>(()=>{let n=0;const pending=new Map();addEventListener('message',e=>{if(e.source!==parent||e.data?.type!=='router2api-response')return;const p=pending.get(e.data.id);if(!p)return;pending.delete(e.data.id);e.data.ok?p.resolve(e.data.body):p.reject(Error(e.data.error))});window.Router2API={request:(method,route,body)=>new Promise((resolve,reject)=>{const id=String(++n);pending.set(id,{resolve,reject});parent.postMessage({type:'router2api-request',id,method,route,body},'*')})}})();</script>`;

function hostMock() {
  window.hostState = { mode: 'light', defaultHostAttempts: 1, failAppearanceRead: false, failAppearanceWrite: false,
    failExecution: false, failProxies: true, pending: [], holdAppearance: false, calls: [],
    execution: { setupTimeoutSeconds: 300, totalSetupTimeoutSeconds: 180, responseTimeoutSeconds: 600 },
    holdExecution: false, pendingExecution: [] };
  const accounts = [
    { id: 'primary', label: '主力推理渠道', baseUrl: 'https://primary.example/v1', enabled: true, weight: 100,
      models: ['gpt-model', 'claude-model'], endpoints: ['/v1/responses', '/v1/messages'], keyRevision: 1,
      keys: [{ id: 'key-a', name: 'a1b2', secret: 'sk-demo-a1b2', enabled: true }], requestPolicy: { maxRetries: 2 } },
    { id: 'backup', label: '备用渠道', baseUrl: 'https://backup.example', enabled: false, weight: 50,
      models: ['claude-model'], endpoints: ['/v1/messages'], keyRevision: 1,
      keys: [{ id: 'key-b', name: 'c3d4', secret: 'sk-demo-c3d4', enabled: true }] }
  ];
  window.hostState.accounts = accounts;
  const row = { id: 'log-1', started: 1792800000000, channel: 'primary', channelLabel: '主力推理渠道',
    model: 'gpt-model', endpoint: '/v1/responses', kind: 'forward', network: 'direct', state: 'failed', status: 503,
    trace: 'test-trace-3-attempts', durationMs: 1000, retries: 2, internalSends: 3,
    hostContinuationAllowed: true, hostContinuationReason: 'retryable_status', sentReasoning: { 'reasoning.effort': 'high' } };
  addEventListener('message', event => {
    if (event.source !== document.querySelector('iframe').contentWindow || event.data?.type !== 'router2api-request') return;
    const { id, method, route, body } = event.data, state = window.hostState;
    state.calls.push({ method, route, body });
    const execute = () => {
      let result, error;
      if (route === 'appearance/settings') {
        if (method === 'GET' && state.failAppearanceRead || method === 'POST' && state.failAppearanceWrite) error = '模拟外观服务失败';
        else { if (method === 'POST') state.mode = body.mode; result = { mode: state.mode }; }
      } else if (route === 'execution/settings') {
        if (state.failExecution) error = '模拟保存失败';
        else {
          if (method === 'POST') state.defaultHostAttempts = body.defaultHostAttempts;
          result = { defaultHostAttempts: state.defaultHostAttempts, execution: state.execution };
        }
      } else if (route === 'accounts') result = { accounts };
      else if (route === 'accounts/save') {
        const previous = accounts.find(account => account.id === body.id), id = body.id || 'new-channel';
        const account = { ...previous, ...body, id, keyRevision: (previous?.keyRevision || 0) + 1,
          keys: body.keys.map((key, index) => ({ ...key, id: key.id || id + '-key-' + index,
            secret: key.secret || previous?.keys.find(item => item.id === key.id)?.secret || '' })) };
        const index = accounts.findIndex(item => item.id === id);
        if (index === -1) accounts.push(account); else accounts[index] = account;
        result = { account };
      }
      else if (route === 'network/proxies') {
        if (state.failProxies) error = '代理目录不可用'; else result = { subscriptions: [], nodes: [] };
      } else if (route === 'logs/status') result = { available: true, records: 1, settings: { enabled: true } };
      else if (route.startsWith('logs?')) result = { rows: [row], total: 1, page: 1, pageSize: 10 };
      else if (route.startsWith('logs/detail?')) result = { ...row, parts: [], attempts: [] };
      else error = 'Unexpected route: ' + route;
      event.source.postMessage({ type: 'router2api-response', id, ok: !error, body: result, error }, '*');
    };
    if (state.holdAppearance && method === 'POST' && route === 'appearance/settings') state.pending.push(execute);
    else if (state.holdExecution && route === 'execution/settings') state.pendingExecution.push(execute);
    else execute();
  });
}

// Checks actual computed colors, including inherited backgrounds and hover/selection states.
async function assertContrast(frame, area = 'body') {
  const violations = await frame.locator(area).evaluate(root => {
    const rgba = value => (value.match(/[\d.]+/g) || []).map(Number);
    const blend = (top, below) => {
      const alpha = top[3] ?? 1;
      return [0, 1, 2].map(index => top[index] * alpha + below[index] * (1 - alpha)).concat(1);
    };
    const background = node => {
      const chain = []; for (let current = node; current; current = current.parentElement) chain.unshift(current);
      return chain.reduce((color, current) => blend(rgba(getComputedStyle(current).backgroundColor), color), [255, 255, 255, 1]);
    };
    const luminance = rgb => rgb.slice(0, 3).map(value => { value /= 255; return value <= .04045 ? value / 12.92 : ((value + .055) / 1.055) ** 2.4; })
      .reduce((total, value, index) => total + value * [.2126, .7152, .0722][index], 0);
    const failures = [];
    for (const node of [root, ...root.querySelectorAll('*')]) {
      if (!node.checkVisibility({ checkOpacity: true, checkVisibilityCSS: true }) || node.closest('.sr-only') || node.matches('svg, path, [aria-hidden=true], option')) continue;
      const bounds = node.getBoundingClientRect();
      if (bounds.width < 3 || bounds.height < 3) continue;
      const text = [...node.childNodes].filter(child => child.nodeType === Node.TEXT_NODE).map(child => child.textContent.trim()).join('');
      const isInput = node.matches('input:not([type=checkbox]):not([type=radio]), textarea, select');
      if (!text && !isInput) continue;
      const style = getComputedStyle(node), bg = background(node), fg = blend(rgba(style.color), bg);
      const values = [luminance(bg), luminance(fg)].sort((a, b) => b - a), ratio = (values[0] + .05) / (values[1] + .05);
      if (ratio < 4.5) failures.push({ selector: node.id || node.className || node.tagName, text: text.slice(0, 45), ratio: +ratio.toFixed(2), color: style.color, bg });
      if (node.matches('input[placeholder], textarea[placeholder]')) {
        const placeholder = getComputedStyle(node, '::placeholder');
        const p = luminance(blend(rgba(placeholder.color), bg));
        const r = (Math.max(p, luminance(bg)) + .05) / (Math.min(p, luminance(bg)) + .05);
        if (r < 4.5) failures.push({ selector: node.id, text: 'placeholder', ratio: +r.toFixed(2) });
      }
    }
    return failures;
  });
  assert.deepEqual(violations, [], 'Visible text must meet 4.5:1 contrast');
}

module.exports = { bridge, hostMock, assertContrast };
