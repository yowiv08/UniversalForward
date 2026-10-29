# 缓存写入计数与 New API 核查

2026-10-29 对照公开的 [QuantumNous/new-api](https://github.com/QuantumNous/new-api/tree/e449cc700eb569cdfc2199cb73e97de46944f43c) 源码，固定核查提交为 `e449cc700eb569cdfc2199cb73e97de46944f43c`。本次只改 UniversalForward 的日志元数据，不调整宿主计量或转发正文。

## 上游字段

| 响应格式 | 缓存读取 | 缓存写入总量 | 明确报告的缓存写入分项 |
| --- | --- | --- | --- |
| OpenAI Responses | `usage.input_tokens_details.cached_tokens` | 同一明细中的 `cache_write_tokens`、`cached_creation_tokens`、`cache_creation_input_tokens` | `cache_write_tokens_details.ttl_30m_tokens`、`ttl_12h_tokens` |
| OpenAI Chat 格式 | `usage.prompt_tokens_details.cached_tokens` | 同一明细中的上述三个字段 | 同上 |
| Claude Messages | `usage.cache_read_input_tokens` | `usage.cache_creation_input_tokens` | `usage.cache_creation.ephemeral_5m_input_tokens`、`ephemeral_1h_input_tokens` |

New API 的 [InputTokenDetails 与总量选择](https://github.com/QuantumNous/new-api/blob/e449cc700eb569cdfc2199cb73e97de46944f43c/relaykit/dto/openai_response.go#L262) 定义 OpenAI 明细字段，同义总量取最大值，避免相加导致重复计数。[ClaudeUsage](https://github.com/QuantumNous/new-api/blob/e449cc700eb569cdfc2199cb73e97de46944f43c/relaykit/dto/claude.go#L680) 则读取总量及 5 分钟／1 小时分项。

Responses 的 HTTP、SSE 与 WebSocket 共用 [ResponsesUsageAccumulator](https://github.com/QuantumNous/new-api/blob/e449cc700eb569cdfc2199cb73e97de46944f43c/service/responses_usage.go#L13)，在完整结束、失败或不完整等终态事件里提取 `response.usage`，再进入统一计量。输入和输出缺失时的估算不会产生缓存写入计数。

## UniversalForward v0.3.10

v0.3.9 日志只读取顶层 `cache_creation_input_tokens`，因此 OpenAI 明细中的缓存写入可能显示「未返回」。v0.3.10 补充上述字段，继续支持非流式 JSON、SSE 和转换为 SSE 的 Responses WebSocket。

同一次事件中表示同一总量的多个别名取最大值，后续快照覆盖该来源的总量，不累加重复事件。总量缺失时，使用已明确报告的 TTL 分项进行检查溢出的相加；已报告的总量优先，不再加上分项。OpenAI 的输入总量已包含缓存，不把缓存读写再次加入输入；Claude 输入按非缓存输入加缓存读写计算。

字段缺失时保留同次尝试的先前观察值；明确的零值显示 0。负数、非整数、字符串、null、错误容器及溢出保持未知。缓存写入不会通过「输入减缓存读取」或价格计算补值，各次尝试独立记录，摘要采用最后一次尝试。

New API 的缓存写入字段使用 Go `int`，部分界面或导出会把缺失值折算为 0，不能把这种 0 当作上游明确报告。它还存在针对 OpenRouter Claude 计费、依赖价格和 Cost 的 [专用反推](https://github.com/QuantumNous/new-api/blob/e449cc700eb569cdfc2199cb73e97de46944f43c/service/text_quota.go#L269)；UniversalForward 的日志不采用这类反推。

响应日志解析仍有 1 MiB 字符上限。超限 SSE 事件本身跳过元数据提取，在真正空行后恢复后续事件；若完整用量只存在于超限事件或超大普通 JSON 中，缓存明细仍可能显示「未返回」。网络响应继续按既有规则完整转发，不保留正文日志。
