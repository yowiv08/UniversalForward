# Chat Completions

UniversalForward 支持原生 `POST /v1/chat/completions`，模型协议配置键为 `chat`。请求与响应使用 Chat Completions 格式，支持 JSON 和 SSE，应用渠道的认证、代理、时限、重试、状态码映射和 Key 自动禁用规则。

## 渠道设置

1. 在「基础设置 → 允许端点」勾选 Chat Completions。新建渠道默认允许三种协议。
2. 在「模型与协议」填写上游模型。需要限制协议时，勾选「指定支持协议」，选择 Chat Completions，并将其设为首选；「强制原生」要求客户端使用模型支持的接口。
3. Base URL 可填写根地址、`/v1` 前缀或完整 `/v1/chat/completions` 地址。网关前缀会保留，例如 `https://example.test/gateway/v1/chat/completions` 的模型发现请求为 `/gateway/v1/models`。
4. 在「基础设置 → 认证方式」选择 `Authorization: Bearer` 或 `x-api-key`，默认 Bearer。所选方式用于转发、模型获取和连接测试，密钥在「Key 管理」填写。
5. 保存渠道，客户端按 `/v1/models` 返回的完整 `platform/model` 模型 ID 调用 Chat Completions。

协议设置决定上游路径，认证方式由渠道独立配置。客户端需发送与目标接口匹配的消息体；插件不进行 Responses、Messages 与 Chat Completions 之间的消息体转换。Chat Completions 使用 HTTP/SSE，Responses WebSocket 选项仅作用于 `/v1/responses`。

认证字段为 `extraParams.apiKeyHeader` 和 `extraParams.apiKeyPrefix`；Bearer 的前缀是 `Bearer `，`x-api-key` 的前缀是空字符串。显式请求头覆盖优先。详细规则见 [认证方式](../README.md#认证方式)。

渠道 settings 中的相关字段示例：

```json
{
  "endpoints": ["/v1/chat/completions"],
  "extraParams": {
    "apiKeyHeader": "Authorization",
    "apiKeyPrefix": "Bearer "
  },
  "modelProtocols": {
    "your-model-id": {
      "protocols": ["chat"],
      "preferredProtocol": "chat",
      "nativeOnly": true
    }
  },
  "headerOverrideMode": "perEndpoint",
  "endpointHeaderOverrides": {
    "/v1/chat/completions": {
      "useCommon": true,
      "headers": {}
    }
  },
  "reasoningPolicy": {
    "defaults": {
      "chat": { "mode": "map", "mappings": [{ "from": "low", "to": "high" }] }
    }
  }
}
```

## 思考历史与提示词

正常转发保留 `messages`、工具定义、`tool_calls`、`tool_call_id`、工具结果、图片等内容块，以及 `reasoning_content`、`reasoning`、`reasoning_details` 和未知扩展字段。客户端的 `max_tokens` 或 `max_completion_tokens` 按原值透传。

「思考映射」中 Chat Completions 只改写顶层 `reasoning_effort`。渠道默认和模型覆盖相互独立；默认不改写，未命中映射时保留客户端值，缺省等级只应用于缺失或 null 的字段。重试复用同一次映射结果，不再次映射历史。

「系统提示词」的替换模式在 `messages` 首位保留一份渠道指令。首条消息已经是渠道指令，且没有其他普通系统指令时，直接转发。原文在其他普通 `system`／`developer` 消息中时，复用该消息或其匹配的文本块，移到首位并清除多余指令；没有匹配项时写入渠道原文。首条原本是普通系统指令时沿用其角色，否则使用 `system`。

追加模式保留客户端指令，在所有普通系统指令中检查渠道原文；已有则保留原位，缺失时才在首位补上。两种模式都忽略用户、助手和工具消息中的引用，保留动态工具声明、思考历史和工具结果，内部重试复用同一请求体。

「异常时剥离 Responses 资源状态」仅作用于最终发送到 `/v1/responses` 的请求，原生 Chat Completions 请求保留消息体和上游错误。

## 响应与连接测试

SSE 持续转发到 `[DONE]`，保留 `finish_reason` 后的尾部用量事件。输入、输出和缓存读取统计识别 `prompt_tokens`、`completion_tokens` 和 `prompt_tokens_details.cached_tokens`。正文、思考、密文、工具调用和音频均可构成有效输出；有效输出后的错误或断流结束当前响应。

连接测试自动使用已保存的模型首选协议；只启用 Chat Completions 时自动选择该协议。手动选择受渠道端点和模型协议限制。测试生成包含 `messages` 的原生请求，使用 `max_completion_tokens` 表示页面设置的输出上限，并应用已保存的认证、请求头、思考映射与系统提示词。仅接受 `max_tokens` 的上游需要通过客户端传入该字段验证。

测试成功要求存在实际输出及 `stop`、`tool_calls` 或 `function_call` 结束原因；流式测试还要求 `[DONE]`。只有角色、用量或结束标记不算成功，`length`、`content_filter` 和上游错误显示为失败并保留原始响应。输出上限造成 `length` 时可在页面提高上限再测。

[请求头与会话标识](request-headers.md) · [重试与超时](request-policy.md) · [Token 与缓存用量](cache-usage.md)
