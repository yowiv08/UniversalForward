(() => {
  // 在页面解析初期接收宿主通知，避免大型页面尚未初始化时被通用脚本覆盖颜色。
  const appearance = window.UniversalForwardAppearance = { colorMode: null, onChange: null };
  window.addEventListener('message', event => {
    if (event.source !== parent || event.data?.type !== 'router2api-appearance') return;
    const mode = event.data.appearance?.colorMode;
    if (!['light', 'dark'].includes(mode)) return;
    // 2.1.2 没有自定义配色退出开关：其监听器已持有此消息对象，稍后在 rAF 染色。
    // 只把 iframe 收到的副本投影为 light，使通用脚本恢复原样式。真实明暗保留
    // 在本地状态，父窗口设置和其他请求桥消息不受影响；新宿主有开关后替换这里。
    event.data.appearance.colorMode = 'light';
    event.stopImmediatePropagation();
    appearance.colorMode = mode;
    appearance.onChange?.(mode);
  });
})();
