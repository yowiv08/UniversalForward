# UniversalForward

Router2API 上游转发插件，提供渠道管理、多 Key 分配、模型配置和连接测试。

**0.3.5 适配官方 Router2API 2.1.2（Router.Contracts 2.0.0）。** 原渠道、多 Key、协议、代理、重试、超时、映射和界面功能保留，无需数据库迁移；本次以最新宿主为验证目标。

管理界面参考 shadcn/ui Dashboard 与 Ant Design 数据展示规范重构，使用黑白灰控件和侧栏导航。渠道管理与请求日志分别展示，渠道默认以卡片展示，可切换为列表；禁用渠道使用灰色背景与「已禁用」标识，文字保持清晰。点击名称或模型数量进入相应设置，启用／禁用按钮直接可用，日志和删除通过更多菜单操作。

设置按六个页签分组，Key 按行编辑，模型协议逐条展开。连接测试提交后收起参数并保留配置摘要。设置保存栏与日志、测试分页栏保持可见；手机端支持遮罩导航、滚动标签和详情抽屉。

日志主要文字使用 15px，日期、Trace、耗时和思考参数使用 13–14px。页头可选「跟随宿主／白天／黑夜」，默认跟随宿主明暗，仅使用插件自己的配色。切换保留当前页面、筛选和未保存表单，白天与黑夜的正文、说明文字保持至少 4.5:1 对比度。

## 插件设置

渠道页「插件设置」中的宿主默认尝试次数为 1–35，默认 1，包含首次。宿主模型映射留空时继承，填写具体次数时覆盖。保存后更新本插件及平台策略，新请求立即使用，执行中请求保留原快照。渠道普通重试不含首次；宿主 3 次、渠道重试 2 次最多普通发送 9 次，另保留原有异常重试额度，仍受时限限制。

渠道内部重试使用同一渠道、Key 和代理；耗尽后的可重放失败交回宿主 `NextAttempt`，由宿主重新选择或切换目标。参数错误、已识别永久错误、取消、总时限耗尽、已有输出或已交付流时停止重放，不额外禁用账号或惩罚代理。状态码映射保留，失败映射到 2xx 可能被宿主按成功处理，界面会提示。连接测试只测试所选渠道。

管理员 `GET/POST execution/settings` 使用 `defaultHostAttempts`；`GET/POST appearance/settings` 使用 `mode: host | light | dark`。宿主请求桥为路由添加 `/api/plugins/universalforward/` 前缀并沿用管理员会话与 CSRF 校验。设置分别原子保存至运行目录的 `data/universalforward/execution-settings.json`、`appearance-settings.json`，同一宿主管理员共用，刷新、重启、升级后保留。读取失败有提示，外观回退白天；保存失败恢复上次已保存外观。

跟随宿主使用 2.1.2 的明暗通知，不复制宿主的蓝色或其他主题配色；尚未收到通知时暂用白天。手动选择白天或黑夜后固定该模式。2.1.2 的通用样式覆盖由插件内部衔接，避免破坏按钮和禁用渠道配色，不改变宿主设置。

## 用量统计

Claude 输入用量计入缓存读取和写入，普通 JSON、SSE／WebSocket 汇总结果共用规则。合法零值和上游明确总量保留，OpenAI 用量明细不重复相加；缺失或无效用量不补零，由宿主解析原文或估算。用量解析异常不影响转发。

宿主 2.1.2 支持在模型映射目标中隐藏 `/v1/models` 的原始模型，映射名及直接调用不受影响，插件无需删除模型配置。宿主持久化总览直接使用其请求日志；下述插件原文日志仍为内存日志。

## 渠道管理

- 添加、编辑、删除和启停渠道。
- 在渠道列表或卡片上直接启用或禁用，保留渠道配置。
- 配置渠道地址、权重、API Key 和允许端点。
- 出站方式可选择直连、全部代理、指定订阅或指定节点；订阅中包含「手动节点」。`networkMode` 仍为 `direct`（默认）或 `proxyPool`，代理范围通过可选的 `proxySubscriptionIds` 和 `proxyId` 保存。
- 空来源数组表示全部启用订阅及手动节点；`["manual"]` 表示仅手动节点。`proxyId` 为空或 `null` 表示不固定节点。保存接口省略代理字段时保留旧值，传空数组或空字符串可清除相应限制。
- 指定节点必须同时满足来源范围与可用性；节点已删除、禁用、冷却或订阅停用时返回 `503 proxy_pool_unavailable`，不换其他节点，也不回退直连。目录刷新保留已失效的选择，待管理员主动调整。
- 请求日志及连接测试详情记录实际使用的节点 ID、来源和无认证地址。代理目录不返回密码或订阅 URL。
- 代理目录由插件通过现有 `IProxyStore`、`IProxySubscriptionService` 生成白名单投影；代理 HTTP 请求通过 `IProxyHttpClientFactory` 复用宿主已有连接池，沿用宿主的节点状态与订阅管理。
- 出站方式统一用于转发、模型获取和连接测试；单次请求及其重试固定使用同一代理。获取模型使用表单草稿，刷新和连接测试使用已保存配置。
- 按渠道名称、地址或模型筛选。
- 分页签管理基础设置、Key、模型与协议、请求头、重试与映射。

## 多 Key 管理

- 每个渠道最多100个 Key。
- 支持批量添加、命名、替换、启停、排序和删除。
- 密钥名称默认取 Key 末尾 4 位，可自定义；旧的 Key1、Key2 等默认名称改用末尾 4 位。
- Key 管理中完整显示访问密钥，名称与密钥输入框同高对齐；已有密钥留空时保留原值。
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
客户端使用 `/v1/models` 返回的完整 `platform/model` 模型 ID；渠道模型使用上游实际接受的 ID，保留上游要求的平台前缀。
Codex 模板为 Responses 请求补齐协议字段和会话元数据，保留原始对话、指令和工具配置。
Claude 模板为 Messages 请求补齐客户端身份、会话元数据和消息格式，保留自定义系统指令、工具和生成参数。
Claude 请求头模板包含 1m 上下文标识；模型名使用 `[1m]` 后缀时自动转换为对应请求头。
模型名称取自当前请求与渠道配置，不依赖固定模型列表。
Codex Responses 和 Claude Messages 模板使用上游流式传输；客户端使用非流式请求时，汇总为原生 JSON，保留工具调用和用量。
非流式汇总上限为 32 MiB；流中断或缺少结束事件返回明确错误。空错误响应附带原始状态码和请求追踪标识。

- 重试次数：0–10次。
- 渠道响应头、总请求和流空闲超时可设 1–3600 秒，同时受宿主执行预算限制；页面展示实际预算。
- 成功 SSE 响应头或 WebSocket 握手后，通过 Contracts 2.0.0 已有执行接口通知宿主切换到响应体预算，再进行首个有效片段探测、流式转发或非流式汇总。
- 建立响应尝试策略最多 300 秒、建立响应总策略最多 180 秒，且不能超过宿主配置。响应体预算默认 10 分钟，与渠道总时限中先到的截止时间生效。连接测试另受宿主 Job 时限约束。
- 自定义重试状态码和响应状态码映射。
- 响应异常重试：首次有效输出前支持限流与空回重试，SSE 实时透传。
- 开启空回重试后，完整结束但没有任何输出的 Responses 也会按现有预算重试，耗尽返回 `502 empty_response`；关闭时保留原始完整响应。`usage.output_tokens_details.reasoning_tokens` 缺失、为 0 或为 10 都不决定是否重试。
- 已输出的推理摘要、密文或工具调用同样阻止重放；只有推理而没有正文的响应仍会透传。`incomplete`、失败事件及单独的 `[DONE]` 保留原有处理。
- 输出前检测到上游错误时保留原始状态码、Content-Type 和响应体，不转换为 429/502 JSON；关闭重试不关闭错误识别。
- 已输出的 SSE 字节不改写；缺失结束事件、流错误或超时按失败结束并记录原因，不伪造完成事件、不重放请求。
- 收到完整的 `response.completed`、`response.done`、`message_stop` 或 `[DONE]` 后结束读取，不再等待上游关闭连接；内容块结束或 `finish_reason` 不会提前截掉后续输出与用量。
- 总时限包含等待、重试和完整流式输出，到期会中断已开始的流；流空闲超时只限制连续无数据的时间。
- 「建议总时限」默认收起为一行入口，点击后展示建议与操作，展开／收起保留草稿。实时估算内部发送的响应头等待、异常重试间隔和适用的 520 退避，将建立响应预留约束在宿主单次／总预算内，再加宿主响应读取预算，向上取整且最多 3600 秒。一键填入后保存渠道生效，原有手动填写保留；共享异常次数不重复计算，不乘宿主尝试次数，不把流空闲当作完整输出时长。
- 例如内部重试 10 次、响应头 20 秒、宿主建立响应 180 秒与读取 600 秒时，建议 780 秒（13 分钟），并提示慢响应下可能用不完次数。读取预算用于成功 SSE／WebSocket 进入读取阶段后，普通 JSON 仍可能受建立响应预算约束；建议不保证输出完成或执行全部重试，上游更长的 Retry-After、错误响应读取、映射剩余预算和测试任务时限仍可能提前结束。读取宿主限制失败时可重试读取或手动填写。
- 流超时日志区分 `total_timeout`（总时限）和 `stream_idle_timeout`（持续无数据），外部取消记录 `request_cancelled`，并注明取消信号来源、是否已有输出及是否收到结束事件。
- 状态码映射提供可视化和 JSON 两种编辑方式，默认可视化。

HTTP 200 表示上游已开始正常响应，宿主尝试详情中的 `Healthy` 也不代表整条流已完整转发。后续超时、取消或断流仍会使最终请求失败；完整结束后不会再因等待连接关闭而误报失败或“日志不完整”。

## Responses 上游 WebSocket

渠道的「Responses 上游连接」可选择 HTTP / SSE 或 WebSocket，默认 HTTP。WebSocket 使用相同的渠道地址、Key、请求头和出站方式；仅作用于最终路由到 `/v1/responses` 的请求。Messages 和模型获取仍使用 HTTP。

每次请求独立建立 WS 连接，发送 `response.create`，不发送 `stream` / `background` 字段。后台请求不受支持。客户端仍使用 HTTP / SSE，非流式请求汇总为 Responses JSON。不提供跨请求连接复用或连接内续接缓存；`previous_response_id` 是否可用取决于上游持久化能力。

连接失败不回退 HTTP；首次有效输出前沿用渠道重试设置，输出后不重放。HTTP 4xx/5xx 握手拒绝保留上游响应；普通 HTTP 2xx 不算握手成功，按连接失败处理。WS 事件按顺序转换为 SSE。连接测试详情显示上游连接方式，日志保留真实握手状态、响应头和原始 WS 消息。

配置地址继续填写 `http://` 或 `https://`，连接时自动使用对应的 `ws://` 或 `wss://`。协议参考：[OpenAI WebSocket mode](https://developers.openai.com/api/docs/guides/websocket-mode)。

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

处理方式使用三组选项卡。等级输入使用统一的搜索下拉控件，常用值带中文提示与选中标记，可输入自定义值；支持方向键、Enter 选择，Escape 仅收起选项。映射行、固定等级与预览使用相同控件，等级文字为 14px。

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

默认透传客户端的业务请求头，包括 User-Agent、Accept-Language、会话标识及自定义字段；无需逐项配置或添加 `*`。请求头覆盖只修改同名字段，删除覆盖项并保存后恢复客户端原值，HTTP 和 WebSocket 使用同一规则。

认证使用渠道 Key。Host、Content-Length、连接专用头及重新编码请求体后失效的压缩/摘要头不沿用；本地认证、Cookie 和 CSRF 头不向上游透传。连接测试和模型发现没有原始业务客户端，不借用管理页面的请求头。旧宿主若不提供完整头快照，只能透传其交给插件的字段。

通过 JSON 配置固定请求头和变量替换；兼容原有 `*`、正则规则及 `PassThroughHeaders`，无需再靠它们开启默认透传。旧 `ReplaceHeaders` 仍作为显式覆盖参与优先级。
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
- 自动选择优先使用已保存的模型首选协议；未配置时，Claude 优先使用 `/v1/messages`，其他模型优先使用 `/v1/responses`，仅选择渠道已启用的端点。手动选择仍受模型支持协议限制。
- Codex Responses 和 Claude Messages 测试复用普通请求的协议补全。
- 展示模型、Key、测试端点、状态码、耗时、重试次数和错误详情。

连接测试和重试会发送实际模型请求，可能产生费用。

## 请求日志

- 「请求日志」提供筛选、原始请求与响应、重试记录、一键复制和分段导出。
- 列表默认每页 10 条，可切换为 25、50 或 100 条，并显示当前页和总页数；删除或清理日志后自动回到有效页。
- 列表显示「本地请求模型」和「上游响应模型」，详情另显示「实际发送模型」。各次重试保留自己的发送与响应模型，摘要使用最后一次尝试。支持 JSON、Responses / Messages / Chat Completions SSE 和 Responses WebSocket，导出包含这些模型字段。
- 上游响应模型取自响应正文中的有效 `model`；未返回时显示「未返回」，不使用请求模型补填。这是上游声明的名称，不能据此验证其内部实际运行的模型。
- 思考参数分别显示插件收到的请求体、宿主扩展参数、实际发送内容和上游明确报告的值；未返回的值不作推断。
- 模型和思考参数沿用 1 MiB 字符解析缓冲限制；超大 JSON 或单个 SSE 帧可能无法提取这些字段，正文保存受独立的正文限额控制。
- 原文包含密钥、Cookie 和对话内容，不脱敏；仅通过管理员插件端点访问，导出文件应妥善保管。
- 日志及日志设置仅保存在本次运行内存，不写入磁盘，重启或重新加载插件后清空。
- 默认最多保留 1000 条，正文容量 64 MiB，每份正文最多 4 MiB；超限优先清理最旧的已结束日志。活动日志占满容量时丢弃新采集内容并提示，不影响转发。
- 截断、采集缺失和异常中断均标记为不完整。日志故障不触发模型请求重试。

## 本地构建与验证

独立构建使用已发布的 NuGet 包 `Router.Contracts 2.0.0`。在插件仓库根目录运行：

```powershell
dotnet test tests/UniversalForward.Tests/UniversalForward.Tests.csproj -c Release
./buildall.ps1
```

使用官方 Router2API 2.1.2 源码联编和运行宿主集成测试，验证公开接口与实际转发行为，无需修改或发布宿主：

```powershell
$contractsProject = 'D:\path\Router2API\src\Router.Contracts\Router.Contracts.csproj'
dotnet test tests/UniversalForward.Tests/UniversalForward.Tests.csproj -c Release "-p:RouterContractsProject=$contractsProject"
dotnet test tests/UniversalForward.HostIntegration/UniversalForward.HostIntegration.csproj -c Release '-p:RouterHostRoot=D:\path\Router2API' "-p:RouterContractsProject=$contractsProject"
./buildall.ps1 -RouterContractsProject $contractsProject
```

插件安装包不包含 `Router.Contracts.dll`，运行时由官方宿主提供。本次发布只包含 UniversalForward 插件；代理选择和响应体预算修复使用宿主现有公开接口。
