# UniversalForward

UniversalForward 是 Router2API 的上游转发插件，支持 Responses、Chat Completions 和 Anthropic Messages。通过渠道管理上游地址、认证、模型与多 Key，配置思考等级、系统提示词、请求头、重试和连接测试。

## 支持的协议

| 协议 | 接口 | 上游连接 |
| --- | --- | --- |
| OpenAI Responses | `/v1/responses` | HTTP / SSE 或 WebSocket |
| Chat Completions | `/v1/chat/completions` | HTTP / SSE |
| Anthropic Messages | `/v1/messages` | HTTP / SSE |

请求与响应使用目标接口的原生格式。协议设置决定上游路径，客户端需要发送与该接口匹配的消息体。

## 开始使用

1. 从 [GitHub Release](https://github.com/yowiv08/UniversalForward/releases/latest) 下载 `universalforward.zip`，在插件管理中安装；也可订阅 `yowiv08/UniversalForward` 仓库。
2. 添加渠道，填写 Base URL，选择认证方式，并在「Key 管理」添加上游密钥。
3. 在「模型与协议」填写模型，或获取候选模型后选择需要使用的项目，保存渠道。
4. 打开渠道的连接测试，选择模型与 Key，检查响应状态和内容。
5. 客户端使用 `/v1/models` 返回的完整 `platform/model` 标识发起请求。

安装包包含 UniversalForward 插件及其依赖。运行环境需提供 Router2API 和 Router.Contracts。

## 渠道管理

渠道保存上游地址、权重、允许端点、认证、Key、模型和转发规则。支持搜索、筛选、启停、复制和删除，可使用卡片或列表查看。

勾选渠道或使用「全选当前结果」，可批量启用、禁用和删除。选择范围限定于当前筛选结果，切换筛选会取消范围外的勾选。渠道启停直接生效，删除需确认；部分操作失败时保留失败项的勾选，并显示各渠道的失败原因。

Base URL 可填写根地址、`/v1` 前缀或完整接口地址。末尾的 `/v1/responses`、`/v1/messages`、`/v1/chat/completions`、`/v1/models` 会按接口段识别，前面的网关路径和大小写保留。

「复制」带入全部可编辑配置和完整 Key，打开独立草稿。副本默认禁用，名称自动避开重名；保存后创建独立渠道和 Key。日志、测试记录、运行状态和候选模型缓存属于各自渠道。

## 认证方式

在「基础设置 → 认证方式」选择 `Authorization: Bearer` 或 `x-api-key`，默认 Bearer。

| 方式 | 发送内容 |
| --- | --- |
| `Authorization: Bearer` | `Authorization: Bearer <当前 Key>` |
| `x-api-key` | `x-api-key: <当前 Key>` |

认证设置用于转发、模型获取、连接测试和 Responses WebSocket。Key 切换时，认证头使用选中的 Key。显式请求头覆盖优先。

编辑器默认选择 Bearer；已配置标准 `x-api-key` 认证的渠道显示该选项。保存时使用所选认证方式。

配置存储在 `extraParams.apiKeyHeader` 和 `extraParams.apiKeyPrefix` 中。Bearer 的前缀为 `Bearer `，`x-api-key` 的前缀为空字符串。

## 多 Key 管理

每个渠道最多 100 个 Key，支持批量添加、命名、替换、启停、排序和删除。默认名称取密钥末尾 4 位，也可自定义。编辑已有 Key 时，密钥留空表示保留已保存值。

通过「全选 Key」或逐行勾选选择操作范围，再批量启用、禁用或删除。删除需要确认，所有改动在保存渠道后生效；取消编辑保留已保存的配置。

| 分配策略 | 行为 |
| --- | --- |
| 轮询 | 按启用 Key 的顺序分配 |
| 随机 | 从启用 Key 中随机选择 |
| 主备顺序 | 优先使用排序靠前的启用 Key |

「自动禁用 Key」按原始 HTTP 状态码和连续失败阈值判断，默认关闭，默认状态码为 `401,403`，默认阈值为 3。状态码支持 400–599 的单值与区间，阈值支持 1–100。

匹配状态码后，该请求可在普通重试额度内切换 Key；达到连续失败阈值后，Key 保持禁用，需在列表中手动启用并保存。同一个客户端请求对同一 Key 最多计一次失败。非匹配 HTTP 响应清零，网络异常、超时和取消不增减计数。

失败计数保存在运行内存，禁用状态持久化保存。详细规则见 [Key 自动禁用与连接测试](docs/key-auto-disable.md)。

## 模型配置

「模型与协议」支持手动填写模型、获取候选模型和刷新模型。模型 ID 使用上游实际接受的值，上游要求的平台前缀也应填写。

- 获取候选模型使用表单中的地址、认证、Key 和请求头。
- 刷新模型和连接测试使用已保存配置。
- 模型获取调用 `/v1/models`，发送渠道认证和 `anthropic-version: 2023-06-01`；通用请求头可覆盖默认值。
- 每个模型可配置支持协议、首选协议和「强制原生」要求。

Chat Completions 的工具调用、思考历史、Token 参数和连接测试规则见 [Chat Completions](docs/chat-completions.md)。

## 思考映射

按渠道默认规则或具体模型配置「不改写」「按值映射」「固定等级」，分别作用于以下字段：

| 协议 | 等级字段 |
| --- | --- |
| Responses | `reasoning.effort` |
| Messages | `output_config.effort` |
| Chat Completions | `reasoning_effort` |

默认不改写。按值映射只替换命中的等级；客户端缺少等级或值为 null 时，可使用单独配置的缺省值。固定等级按填写值发送，常用等级列表也接受自定义值。

规则只处理请求级的等级字段，保留思考开关、Token 预算和对话历史。预览显示输入等级与发送等级，实际请求和上游报告值可在日志中查看。

## 系统提示词

「系统提示词」对渠道中的所有模型生效，支持三种模式：

| 模式 | 行为 |
| --- | --- |
| 不改写 | 使用客户端请求中的指令 |
| 追加 | 保留客户端指令，将渠道文本作为最后一条独立系统指令 |
| 替换 | 移除普通系统指令并写入渠道文本，保留动态工具声明和对话历史 |

提示词保留空格、换行和字面内容，上限 32,000 个 UTF-16 单元。追加和替换需要非空文本。关闭改写时可保留文本以便再次使用。

各协议的角色与系统块处理见 [系统提示词](docs/system-prompt.md)。

## Responses 资源状态

「异常时剥离 Responses 资源状态」默认关闭。开启后，正常请求发送完整思考历史、加密状态、条目 ID 和 `previous_response_id`。

仅在上游明确报告密文校验或解密失败、历史响应或条目不存在，且尚无有效输出时，插件删除相关资源状态并额外重试一次。普通参数错误、限流和网络失败不触发剥离。

剥离范围包括历史 reasoning 条目、资源 ID、加密字段和 `previous_response_id`；消息、工具调用和工具结果保留。包含无法还原的 compaction 或 item_reference 时，插件返回上游错误，需由客户端提供完整历史。

每次调用最多额外重试一次，共用总时限。下一次调用从客户端原始请求开始处理。规则适用于 Responses 的 HTTP、SSE、WebSocket 和连接测试。

## 请求头配置

请求头支持通用配置和按接口配置，可使用可视化或 JSON 编辑器，以及 Codex、Claude Code 模板。配置值可引用当前 Key、客户端请求头和会话标识。

默认透传可用的客户端业务请求头。上游认证使用渠道 Key，本地认证、Cookie 和 CSRF 不透传；显式覆盖优先。模型获取使用通用请求头。

占位符、模板和会话身份规则见 [请求头与会话标识](docs/request-headers.md)。

## 出站连接

渠道可选择直连或代理池，代理池支持全部可用代理、指定订阅、手动节点来源和固定节点。

一次请求及其重试固定使用选中的代理。指定节点被删除、停用、冷却或所属订阅停用时，请求返回错误。转发、模型获取、连接测试和 Responses WebSocket 使用同一出站配置。

Responses 可选择 WebSocket 上游连接，Base URL 使用 HTTP(S) 地址。每次请求独立连接，客户端使用 HTTP/SSE；非流式请求汇总为 Responses JSON。WebSocket 连接失败按渠道规则处理，不回退 HTTP。

## 重试与时限

普通重试支持 0–10 次，不含首次发送。限流与空回重试分别启用，共享每请求 0–10 次额度，默认额度 3 次、间隔 5 秒，并遵守上游更长的等待要求。

响应头、总请求、流空闲时限均可设置为 1–3600 秒，并受运行环境的执行预算约束。总时限覆盖等待、重试和完整输出；空闲时限限制连续无数据的时间。

重试只发生在首次有效输出前。正文、思考、密文、工具调用或音频均可构成有效输出；输出后的错误或断流直接结束该响应。空回重试可能重复上游执行与计费，缺少 Token 用量不构成空回。

「建议总时限」估算等待与重试所需时间，可一键填入。长回答需要额外预留持续输出时间。自定义重试状态码和返回状态码映射均可通过编辑器配置。

完整的次数、预算和流结束规则见 [重试与超时](docs/request-policy.md)。

## 连接测试

使用保存后的渠道配置，测试单个、勾选或全部模型。Key 可按渠道策略分配、手动指定，或遍历全部启用 Key。每批最多 100 个模型与 Key 组合，串行执行。

测试可选择协议、流式模式、输出上限和提示词，支持查看进度与取消。自动选择优先使用模型首选协议；未配置时，Claude 优先 Messages，其他模型优先 Responses，再按渠道允许端点选择。

测试不自动禁用 Key，也不修改正式请求的连续失败计数。测试结束后，点击「禁用 Key」查看每个 Key 的结果，勾选并确认后禁用。已更换或重新启用的 Key 需要重新测试。

测试结果显示模型、Key、协议、状态码、耗时和失败原因，响应原文在详情面板中按需加载。测试仅覆盖所选渠道。

## 请求日志与 Token 用量

日志记录请求模型、实际上游模型、接口、状态、耗时、思考参数、重试、代理、请求／响应头和错误信息。请求与响应正文不采集；连接测试的响应详情独立保存。

Token 明细包含输入、输出、缓存读取和缓存写入。显式零值显示为 0，缺失或无效值显示「未返回」。各次尝试分别记录，摘要使用最后一次尝试。

请求日志和日志设置保存在运行内存，重启或重载插件后清空。队列满或元数据超过解析上限时，记录会标记不完整。请求头可能包含访问密钥，仅供管理员查看。

字段来源与合并规则见 [Token 与缓存用量](docs/cache-usage.md)，资源限制见 [日志与流处理](docs/cpu-performance.md)。

## 管理界面与设置

顶部导航切换渠道与请求日志，并提供外观、插件设置、刷新和添加入口。渠道设置按基础设置、Key 管理、模型与协议、思考映射、系统提示词、请求头、重试与映射分组。日志详情在右侧弹窗中展示，关闭后回到原来的列表位置。连接测试在桌面并排显示列表和详情，窄屏在测试窗口内切换；手机端页签支持横向滚动。

页头提供「跟随宿主」「白天」「黑夜」。外观选择与「插件设置」中的默认尝试次数持久化保存，同一实例的管理员共用。

| 数据 | 保存方式 |
| --- | --- |
| 渠道、Key 和模型配置 | 账号及渠道设置存储 |
| 默认尝试次数 | `data/universalforward/execution-settings.json` |
| 外观选择 | `data/universalforward/appearance-settings.json` |
| 请求日志与日志设置 | 插件运行内存 |
| 连接测试结果 | 插件运行内存，完成任务最多保留一小时 |

设置文件位于运行目录。页面操作说明见 [管理界面](docs/web-design.md)。

## 开发与验证

独立构建使用 NuGet 包 `Router.Contracts 2.0.0`。

```powershell
dotnet test tests/UniversalForward.Tests/UniversalForward.Tests.csproj -c Release
./build.ps1 -Configuration Release -OutputDirectory ./artifacts/plugins
```

构建输出位于 `artifacts/plugins/universalforward`，发行目录需为空。打包与校验：

```powershell
./package-release.ps1 -Tag v4.3.0 -InputDirectory ./artifacts/plugins -OutputDirectory ./artifacts/release
./tests/release-packaging.ps1 -Tag v4.3.0 -ReleaseDirectory ./artifacts/release
```

浏览器测试位于 `tests/browser`，使用 Playwright；可通过 `PLAYWRIGHT_MODULE` 指定模块目录。页面资源嵌入插件程序集，样式与脚本位于 `src/Plugins.UniversalForward/Web`。
