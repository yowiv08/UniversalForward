# Token 与缓存用量

请求日志展示输入、输出、缓存读取和缓存写入四项计数。数据来自上游 JSON、SSE 或 Responses WebSocket 事件。各次尝试独立记录，摘要使用最后一次尝试。

## 字段来源

| 协议 | 缓存读取 | 缓存写入总量 | 缓存写入分项 |
| --- | --- | --- | --- |
| Responses | `usage.input_tokens_details.cached_tokens` | 同一明细中的 `cache_write_tokens`、`cached_creation_tokens`、`cache_creation_input_tokens` | `cache_write_tokens_details.ttl_30m_tokens`、`ttl_12h_tokens` |
| Chat Completions | `usage.prompt_tokens_details.cached_tokens` | 同一明细中的上述三个字段 | 同上 |
| Messages | `usage.cache_read_input_tokens` | `usage.cache_creation_input_tokens` | `usage.cache_creation.ephemeral_5m_input_tokens`、`ephemeral_1h_input_tokens` |

同一事件中的缓存写入总量别名取最大值，避免重复计算。总量缺失时，使用明确报告的 TTL 分项之和；总量存在时使用总量。

OpenAI 格式的输入总量已包含缓存，缓存明细单独展示。Messages 的输入总量按非缓存输入、缓存读取与缓存写入相加。

## 数值含义

- 显式零值显示为 0。
- 字段缺失时保留同一次尝试已经读取的值。
- 负数、非整数、字符串、null、错误容器和溢出标记为未知。
- 后续快照覆盖对应来源的值，重复事件不累加。
- 缓存写入以明确报告的字段为准，不通过输入差值、价格或费用推算。

「未返回」表示未取得有效计数，不能解释为实际用量为零。显示结果用于观察上游报告，不构成计费依据。

## 解析范围

元数据解析上限为 1,048,576 个字符。超限 SSE 事件跳过元数据提取，在事件边界后处理后续事件；超大普通 JSON 或只在超限事件中出现的用量可能显示为「未返回」。

日志元数据处理与正文转发分别执行。请求与响应正文不保存在请求日志中。
