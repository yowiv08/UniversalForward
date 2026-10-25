# 宿主生命周期集成测试

此项目按需引用未修改的官方 Router2API **2.1.2** 源码（提交 `7c93d223bcd1f2b3efabb6655f1efe2e46811572`，Contracts `2.0.0`），不加入默认解决方案。使用独立临时 SQLite 和本地模拟上游，不启动生产宿主、不读取生产数据库或配置，不依赖真实 Redis 和业务上游。

```powershell
$hostSource = 'D:\path\Router2API' # 未修改的官方公开源码目录
dotnet test tests/UniversalForward.HostIntegration/UniversalForward.HostIntegration.csproj `
  "-p:RouterHostRoot=$hostSource" `
  "-p:RouterContractsProject=$hostSource\src\Router.Contracts\Router.Contracts.csproj" `
  --disable-build-servers -m:1 -p:ConcurrentBuild=false -p:UseSharedCompilation=false
```

使用真实 `PluginAttemptExecutor`、`ProxyPoolHttpClientFactory`、`ProxyTransportFactory`、账号租约、本地 HTTP/CONNECT 代理和 Kestrel HTTP/WebSocket 上游；仅账号/代理持久化存储、日志接收器及无关服务使用内存替代。

固定节点通过官方 `IPluginHost.GetServiceAsync<T>()` 提供的公开 `IProxyStore`、`IProxySubscriptionService` 和 `IProxyHttpClientFactory` 选择并创建客户端，与执行器共享同一物理连接池。测试还验证加载的契约程序集版本为 `2.0.0.0`，不依赖新增的代理目录或固定节点 SDK 成员。

覆盖直连、固定手动代理、HTTP/SSE、WebSocket、流式透传和非流式汇总。上游在发送成功响应头后延迟首个有效事件，使延迟超过 setup 预算、同时小于 response 预算；测试要求完整内容及用量正确返回，且上游只收到一次请求、账号租约全部归还。

取消测试等待插件真正调用宿主的响应体阶段通知后再取消调用方令牌，要求保留取消类型及日志。另一个测试缩短宿主 response 预算，验证响应头阶段切换没有关闭整体响应截止时间。

`HostAdaptationTests` 使用真实 `RouterPipeline`、`PluginAttemptExecutor`、`PluginPolicyRegistry`、`ModelMappingStore` 和 `RequestUsageTracker`，验证 429/502/503 内部重试后交回宿主、3×3 发送、映射次数覆盖、运行中更新的快照隔离、永久错误与输入错误停止、既有冷却、失败映射为成功、Claude 缓存用量和不完整用量回退、流交付后不重放。

这些测试不经过完整 HTTP 下游写出管道；最终写出失败与结束标记的判定由宿主仓库的 `StreamLifecycleTests`、`ProtocolResponseWriterTests` 等覆盖。
