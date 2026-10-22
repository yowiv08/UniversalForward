# UniversalForward

Router2API 上游转发插件，提供渠道管理、多 Key 分配、模型配置和连接测试。

当前源码版本为 **0.3.2**，兼容官方 **Router2API 2.1.0 / Router.Contracts 2.0.0**。直接更新 UniversalForward 插件即可，原渠道配置不需要数据库迁移。

## 渠道管理

- 添加、编辑、删除和启停渠道。
- 配置渠道地址、权重、API Key 和允许端点。
- 按渠道选择直连、全部代理、指定订阅或指定节点；订阅中可单独选择「手动节点」。代理选择统一用于 HTTP、Responses WebSocket、模型获取及连接测试。
- 指定节点仍须属于所选来源且处于可用状态；节点删除、禁用、冷却或订阅停用时明确报错，不切换其他节点或回退直连。一次请求及其重试固定使用同一个节点。
- 代理目录可手动刷新；已失效的选择保留并提示，直到管理员主动调整。获取模型使用表单草稿，刷新模型和连接测试使用已保存配置。
- 代理目录由插件通过宿主现有公开服务生成，只返回节点 ID、来源、状态和无认证地址等白名单字段；代理 HTTP 请求复用宿主已有连接池。
- 请求日志及连接测试详情显示实际代理 ID、来源和地址，不展示代理认证信息。旧渠道仍沿用原来的直连或全部代理池设置。
- 按渠道名称、地址或模型筛选。
- 分页签管理基础设置、Key、模型与协议、请求头、重试与映射。

## 多 Key 管理

- 每个渠道最多100个 Key。
- 支持批量添加、命名、替换、启停、排序和删除。
- Key 掩码展示；编辑时留空保留原密钥。
- 每次请求及其重试使用同一个 Key。

| 分配策略 | 功能 |
| --- | --- |
| 轮询 | 按启用 Key 的顺序依次分配 |
| 随机 | 随机选择一个启用 Key |
| 主备顺序 | 使用排序最前的启用 Key |

## 模型配置

- 手动填写模型，或从上游获取、刷新候选模型。
- 选择渠道允许使用的模型。
- 指定模型发现使用的 Key。
- 使用当前表单的地址和 Key 获取候选模型。
- 为每个模型设置支持协议、首选协议和强制原生模式。

## 请求转发

仅支持 OpenAI Responses `/v1/responses` 和 Anthropic Messages `/v1/messages`，以及普通响应和 SSE 流式响应。
协议路由选择上游路径和认证方式；请求及响应内容按上游格式透传。

客户端使用 `/v1/models` 返回的完整 `platform/model` 模型 ID。渠道模型填写上游实际接受的 ID；上游也是路由服务时，保留它要求的平台前缀。

- 重试次数：0–10次。
- 响应异常重试：可分别开启限流与空回重试，共享每请求 0–10 次额度，默认 3 次、间隔 5 秒，遵守上游更长的等待要求。
- 异常重试仅限首次有效输出前，使用同一 Key 和请求内容，等待计入总时限。
- SSE 实时透传；输出前缓冲及非流式响应上限为 32 MiB。
- 空回仅在没有生成输出时重试，可能重复上游执行和计费；缺少 Token 用量不触发重试。
- 开启空回重试后，完整结束但没有任何输出的 Responses 也会按现有预算重试，耗尽返回 `502 empty_response`；关闭时保留原始完整响应。`usage.output_tokens_details.reasoning_tokens` 缺失、为 0 或为 10 都不决定是否重试。
- 已输出的推理摘要、密文或工具调用同样阻止重放；只有推理而没有正文的响应仍会透传。`incomplete`、失败事件及单独的 `[DONE]` 保留原有处理。
- 输出前检测到上游错误时保留原始状态码、Content-Type 和响应体，不转换为 429/502 JSON；关闭重试不关闭错误识别。
- 已输出的 SSE 字节不改写；缺失结束事件、流错误或超时按失败结束并记录原因，不伪造完成事件、不重放请求。
- 收到完整的 `response.completed`、`response.done`、`message_stop` 或 `[DONE]` 后结束读取，不再等待上游关闭连接；内容块结束或 `finish_reason` 不会提前截掉后续输出与用量。
- 渠道响应头、总请求和流空闲超时可配置 1–3600 秒，同时受宿主执行预算限制；页面展示实际建立响应及响应读取上限。
- 成功 SSE 响应头或 WebSocket 握手后，通过现有执行接口通知宿主切换到响应体预算，再开始首个有效片段探测或非流式汇总；避免已经开始响应的请求继续消耗建立响应预算。
- 建立响应尝试策略最多 300 秒、建立响应总策略最多 180 秒，实际再取宿主配置的较小值。响应体预算默认 10 分钟，和渠道总时限中先到的截止时间生效；连接测试沿用宿主 Job 预算和渠道超时。
- 总时限包含等待、重试和完整流式输出，到期会中断已开始的流；流空闲超时只限制连续无数据的时间。
- 流超时日志区分 `total_timeout`（总时限）和 `stream_idle_timeout`（持续无数据），外部取消记录 `request_cancelled`，并注明取消信号来源、是否已有输出及是否收到结束事件。
- 自定义重试状态码和响应状态码映射。
- 状态码映射提供可视化和 JSON 两种编辑方式，默认可视化。

HTTP 200 表示上游已开始正常响应，宿主尝试详情中的 `Healthy` 也不代表整条流已完整转发。后续超时、取消或断流仍会使最终请求失败；完整结束后不会再因等待连接关闭而误报失败或“日志不完整”。

## Responses 上游 WebSocket

在渠道基础设置中，将「Responses 上游连接」选为 WebSocket 并保存，即可通过 WS/WSS 连接上游 `/v1/responses`。Base URL 继续填写 HTTP(S) 地址，默认连接方式为 HTTP / SSE。

- 沿用渠道 Key、请求头、直连或代理池设置；连接测试也使用所选方式。
- 客户端继续使用 HTTP/SSE，非流式请求汇总为 Responses JSON。Messages 和模型获取仍走 HTTP。
- 每次请求独立连接，发送 `response.create`。不支持后台请求或跨请求连接缓存；`previous_response_id` 需要上游持久化支持。
- 连接失败不回退 HTTP；首次有效输出前按渠道规则重试，输出后断流不重放请求。
- 日志记录真实握手状态、响应头和原始 WS 消息；普通 HTTP 200 不算 WS 连接成功。

协议参考：[OpenAI WebSocket mode](https://developers.openai.com/api/docs/guides/websocket-mode)。

## Responses 资源状态剥离

在渠道基础设置中开启「剥离 Responses 资源状态」，可兼容跨资源、账号或中转续聊，避免回传其他来源的历史条目 ID 或加密推理状态。适用于 AnyRouter 等返回标准 `reasoning.encrypted_content` 的 Responses 上游，以及多个 Azure OpenAI 资源轮换的上游。新渠道和旧配置默认关闭，无需额外本地代理或修改客户端地址。

规则作用于当前接收请求的目标渠道，不按密文来源或域名判断。例如 DeepSeek → AnyRouter 时，在 AnyRouter 渠道开启；AnyRouter → 其他中转时，在切换后的目标渠道开启。需要双向切换时，两边都开启。响应中的加密字段仍原样返回，后续发送历史时才剥离。

开启后，仅对最终路由到 `/v1/responses` 的请求，在模板补全和思考映射之后、发送之前删除：

- 顶层 `previous_response_id`。
- `input` 数组中的全部直接 `reasoning` 条目，包括密文、摘要和推理正文；避免留下严格 Responses 上游不接受的无密文推理条目。
- 其他直接对象条目的 `id`，以及名称以 `encrypted` 开头的字段，例如 `encrypted_content`，区分大小写。

不递归删除嵌套字段；消息正文、assistant `phase`、工具调用的 `call_id`、参数、工具结果和 `include` 保留。响应原样返回，历史推理条目仅在下一次发送时移除。HTTP、WebSocket、流式、非流式及连接测试共用处理，重试复用已剥离的请求体；Messages 和模型获取不受影响。

客户端需要携带完整对话历史；仅靠 `previous_response_id` 恢复的历史无法由插件补回。模型每轮重新推理，不再跨轮接续隐藏推理状态或历史推理摘要。若直接历史包含 `compaction` 加密检查点或 `item_reference` 引用，插件会在发送前返回 400，提示恢复完整历史；这些条目无法在删除密文或 ID 后恢复原内容，不能静默丢弃。

渠道查询和 `accounts/save` 使用可选布尔字段 `stripResponsesResourceState`。传 `true` 开启，传 `false` 关闭，省略或传 `null` 保留已有设置；仍保存在渠道 settings JSON 中，无需宿主升级或数据库迁移。诊断事件 `request.responses.state.stripped` 分别记录 `strippedFields`（其他字段删除数）和 `removedReasoningItems`（推理条目移除数），现有请求日志可查看收到与实际发送的请求体。

剥离规则参考 [codex-strip-proxy](https://github.com/AnnaofArendelle/codex-strip-proxy)。

0.3.2 增补的推理条目移除参考 [AnyRouter 无密文 reasoning 报错记录](https://github.com/yetone/magpie/issues/1044)。仅删除密文会留下这种条目；开启资源状态剥离后同时移除，避免跨中转续聊继续被拒。

## 思考等级映射

在渠道编辑的「思考映射」页签中，选择渠道默认或具体模型，并分别配置 Responses、Messages：

- **不改写**：沿用现有处理，新渠道及旧配置默认使用此行为。
- **按值映射**：例如 `low → max`。来源去除首尾空格并忽略大小写；未命中保持原处理。只映射一次，`low → high` 不会继续触发 `high → max`。
- **固定等级**：包含未传等级的请求，统一发送指定等级。
- **客户端未提供时**：映射模式可设置缺省等级，只用于原始字段缺失或为 `null` 的请求，留空则不改写。

模型按接口继承渠道规则；取消继承后整条覆盖，不合并映射表。模型选择「不改写」可独立关闭该接口的渠道映射。等级支持自定义，实际可用值由上游模型决定；空来源、空目标和重复来源禁止保存。预览不会发送请求。

Responses 改写 `reasoning.effort`，Messages 改写 `output_config.effort`。以原始请求中的对应字段为准，不把宿主扩展参数或模板默认值当成客户端输入；规则在模板补全后执行，保留对象内其他参数、思考开关、Token 预算和聊天历史中的动态等级控制。

例如，只有 `low → max` 时，Codex 模板给未传等级请求补入的 `low` 不会触发映射；另设缺省 `high` 后，这类请求最终发送 `high`。现有连接测试生成的请求也按「客户端未提供」处理。HTTP、WebSocket、流式、非流式和重试使用同一规则，重试期间映射结果不变。

日志显示客户端等级、实际发送等级和命中规则来源，上游报告独立显示，不推断内部思考量。

渠道查询与 `accounts/save` 使用可选的 `reasoningPolicy` 字段；省略或传 `null` 保留原配置，传空 `defaults` / `models` 可清空规则。模型键使用渠道允许模型中的 ID，接口键为 `responses` / `messages`。以下为该字段的示例值：

```json
{
  "defaults": {
    "responses": {
      "mode": "map",
      "mappings": [{ "from": "low", "to": "max" }],
      "defaultEffort": "high"
    }
  },
  "models": {
    "your-model-id": {
      "responses": { "mode": "fixed", "fixedEffort": "max" },
      "messages": { "mode": "off" }
    }
  }
}
```

模型或接口条目缺失表示继承；`mode` 为 `off`、`map` 或 `fixed`。配置仍保存在现有渠道 settings JSON 中；思考映射规则本身无需额外 Contracts 变更或数据库迁移。

## 请求头配置

默认透传客户端业务请求头，包括 User-Agent、语言、会话标识及自定义字段。显式覆盖同名字段优先；删除覆盖项并保存后恢复客户端原值，HTTP 与 WebSocket 一致。

上游认证使用渠道 Key；Host、Content-Length、连接专用头和失效的压缩/摘要头由转发链路处理，本地认证、Cookie、CSRF 不透传。连接测试和模型发现不借用管理页面的请求头。旧宿主只能透传它实际提供给插件的字段。

通过 JSON 配置固定值和变量替换；兼容 `*`、正则及旧 `PassThroughHeaders`，默认透传无需配置这些规则。旧 `ReplaceHeaders` 仍参与显式覆盖。
支持所有接口共用配置，或按实际上游 Responses、Messages 接口独立配置；接口可继承通用配置，模板不限接口。模型发现使用通用配置。

| 配置 | 功能 |
| --- | --- |
| `{api_key}` | 使用当前请求选中的 Key |
| `{session_id}` / `{thread_id}` | 会话与线程标识 |
| `{request_id}` | 请求标识，默认每个新请求独立生成 |
| `{turn_id}` | 回合标识，默认每个新请求独立生成 |
| `{window_id}` | Codex 窗口标识 |
| `{codex_turn_metadata}` | Codex 回合元数据 |
| `{client_header:NAME}` | 读取指定客户端请求头 |
| `*` | 匹配允许透传的请求头 |
| `re:` / `regex:` | 按正则表达式匹配请求头名称 |

提供 Codex、Claude Code 请求头模板，以及可视化和 JSON 两种编辑方式。
编辑器支持格式化、复制和清空。显式配置的请求头优先。
Codex 覆盖预设保留固定 User-Agent 和协议头，不重复填写会话、线程、请求、窗口 ID 及回合元数据；后端继续自动补全这些字段。
更新插件后，已有渠道需重新应用 Codex 模板并保存，才能采用新的覆盖预设。

自动补全优先使用显式覆盖（含旧 `ReplaceHeaders`），再使用客户端已有身份。缺少会话标识时，根据 Responses `input` 或 Messages `messages` 的规范化历史识别会话，不按客户端名称、User-Agent 或 IP 判断。含助手或工具历史的请求，只有唯一匹配某个会话最近历史的完整内容或延续前缀时才复用；首次聊天、回到旧历史的分叉、无法匹配或多个候选时新建会话。

推断出的会话复用会话、线程、窗口及生成的安装、上下文标识；新请求的请求 ID 和回合 ID 独立生成，同一次请求的所有重试复用完整身份。HTTP、WebSocket、流式及非流式使用同一规则。自动补全的 `X-Client-Request-Id` 使用 `{request_id}`；客户端或配置已有值仍优先。旧配置若显式填写 `"X-Client-Request-Id": "{session_id}"`，需删除该项或改为 `{request_id}` 才会采用独立请求 ID。

识别缓存只在本次插件运行内存中保存身份和历史摘要，不缓存对话正文；最多 10,000 个会话，24 小时未使用过期，满额淘汰最久未使用记录，卸载或重启清空。记录按渠道及下游认证摘要隔离，上游 Key 轮换不影响会话。已有 `prompt_cache_key` 原样保留，只辅助隔离候选，不单独作为同一聊天的依据。客户端只发最新一句、历史裁剪后无法匹配或多个聊天提供相同可观察信息时，优先新建，不保证识别为原会话。

诊断事件 `request.identity.resolved` 标记来源 `override`（显式覆盖）、`client`（客户端）、`history`（历史匹配）或 `new`（新建）及原因；该事件不记录认证原值或对话正文。

## 连接测试

- 测试单个、勾选或全部模型。
- 按渠道策略、指定 Key 或全部启用 Key 测试。
- 每批最多100个模型与 Key 组合，串行执行。
- 支持端点选择、流式测试、进度查看和取消。
- 展示模型、Key、状态码、耗时、重试次数和错误详情。

连接测试和重试会发送实际模型请求，可能产生费用。

## 请求日志

日志列表显示「本地请求模型」和「上游响应模型」，详情另显示「实际发送模型」，便于比较客户端模型名、路由后的发送模型名和上游响应正文中的 `model`。每次重试单独记录发送与响应模型；当前请求摘要显示最后一次尝试的响应模型。

支持 JSON、Responses / Messages / Chat Completions 的 SSE 和 Responses WebSocket 响应。上游未返回有效模型名时显示「未返回」，不使用发送模型补填；响应模型是上游声明的名称，不能据此验证其内部实际运行的模型。模型字段也包含在日志导出中。

模型和思考参数沿用日志的 1 MiB 字符解析缓冲限制；超大 JSON 或单个 SSE 帧可能无法提取这些字段，原始正文仍按独立的正文保存限额记录。

## 本地构建与验证

独立构建使用已发布的 NuGet 包 `Router.Contracts 2.0.0`，不需要宿主源码。在插件仓库根目录运行：

```powershell
dotnet test tests/UniversalForward.Tests/UniversalForward.Tests.csproj -c Release
./buildall.ps1
```

需要对官方 Router2API 2.1.0 源码进行兼容性验证时，可选用 Contracts 项目联编；无需修改宿主：

```powershell
$contractsProject = 'D:\path\Router2API\src\Router.Contracts\Router.Contracts.csproj'
dotnet test tests/UniversalForward.Tests/UniversalForward.Tests.csproj -c Release "-p:RouterContractsProject=$contractsProject"
./buildall.ps1 -RouterContractsProject $contractsProject
```

插件安装包不包含 `Router.Contracts.dll`，运行时由官方宿主提供。代理选择和响应体预算修复均使用现有公开接口，本次发布只包含 UniversalForward 插件。

可选的宿主集成测试针对官方 Router2API 2.1.0 源码运行，使用真实执行器、连接池及本地 HTTP/WS 上游，验证慢首片段、非流式汇总和取消；无需修改宿主，也不读取生产配置或访问真实模型服务：

```powershell
dotnet test tests/UniversalForward.HostIntegration/UniversalForward.HostIntegration.csproj -c Release '-p:RouterHostRoot=D:\path\Router2API' "-p:RouterContractsProject=$contractsProject"
```

浏览器测试位于 `tests/browser`，使用 Playwright；模块不在默认查找路径时，通过 `PLAYWRIGHT_MODULE` 指向其目录。
