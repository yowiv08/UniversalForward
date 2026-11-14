# Chat Completions

UniversalForward 支持原生 `POST /v1/chat/completions`，模型协议配置键为 `chat`，认证方式使用渠道设置；新建渠道默认 `Authorization: Bearer`，也可选择 `x-api-key`。普通 JSON 和 SSE 直接透传，沿用渠道的代理、时限、重试、状态码映射和 Key 自动禁用规则。

## 渠道设置

1. 在「基础设置 → 允许端点」勾选 Chat Completions。新渠道默认允许三种协议，已有渠道保留已保存的端点选择。
2. 在「模型与协议」填写上游模型。需要限制协议时，勾选「指定支持协议」，选择 Chat Completions，并将其设为首选；「强制原生」要求客户端使用模型支持的接口。
3. Base URL 可填写根地址、`/v1` 前缀或完整 `/v1/chat/completions` 地址。网关前缀会保留，例如 `https://example.test/gateway/v1/chat/completions` 的模型发现请求为 `/gateway/v1/models`。
4. 在「基础设置 → 认证方式」选择 `Authorization: Bearer` 或 `x-api-key`。所选方式同时用于转发、模型获取和连接测试；Key 仍在「Key 管理」填写。
5. 保存后，客户端按宿主返回的完整 `platform/model` 模型 ID 调用 Chat Completions。

协议路由选择路径，认证方式由渠道独立配置，请求与响应继续采用上游原生格式。客户端需发送与目标接口匹配的消息体。Responses 的 WebSocket 选择只作用于 `/v1/responses`，Chat Completions 使用 HTTP/SSE。

旧渠道的自动规则或自定义认证显示为「保留原认证配置」，主动选择其他方式后才改写。选择结果保存到 `extraParams.apiKeyHeader` 和 `extraParams.apiKeyPrefix`；Bearer 的前缀是 `Bearer `，`x-api-key` 的前缀是空字符串。其他高级参数保留，显式请求头覆盖仍优先。完整兼容规则见 [认证方式](../README.md#认证方式)。

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

正常转发保留 `messages`、工具定义、`tool_calls`、`tool_call_id`、工具结果、图片等内容块，以及 `reasoning_content`、`reasoning`、`reasoning_details` 和其他扩展字段。客户端的 `max_tokens` 或 `max_completion_tokens` 保持原值。

「思考映射」中 Chat Completions 只改写顶层 `reasoning_effort`。渠道默认和模型覆盖相互独立；默认不改写，未命中映射时保留客户端值，缺省等级只应用于缺失或 null 的字段。重试复用同一次映射结果，不再次映射历史。

「系统提示词」追加模式保留已有角色，在 `messages` 末尾添加一条独立 system 消息；替换模式先移除普通 system／developer 提示消息，保留动态工具声明，再添加渠道 system 消息。助手思考历史和工具结果保留，重试不会重复追加。

「异常时剥离 Responses 资源状态」仅作用于最终发送到 `/v1/responses` 的请求。原生 Chat 请求即使返回同名的加密状态错误，也保留消息体与错误，不进行 Responses 状态剥离。

## 响应与连接测试

SSE 持续转发到 `[DONE]`，`finish_reason` 后的尾部用量事件仍可被客户端和日志接收。输入、输出和缓存读取统计识别 `prompt_tokens`、`completion_tokens` 和 `prompt_tokens_details.cached_tokens`。正常请求仅返回思考、密文、工具调用或音频时仍算有效输出，不按空回重试；实际输出后发生错误或断流不重新发送请求。

连接测试自动使用已保存的模型首选协议；只启用 Chat 时自动选择 Chat。手动选择同样受渠道端点和模型协议限制。测试生成原生 messages 请求，使用当前 OpenAI 字段 `max_completion_tokens` 表示页面设置的输出上限，并应用已保存的认证方式、请求头、思考映射与系统提示词。只接受旧 `max_tokens` 的上游可通过客户端传入该字段验证，正式转发不替换字段。

测试成功要求存在实际输出及 `stop`、`tool_calls` 或 `function_call` 结束原因；流式测试还要求 `[DONE]`。只有角色、用量或结束标记不算成功，`length`、`content_filter` 和上游错误显示为失败并保留原始响应。输出上限造成 `length` 时可在页面提高上限再测。

验证覆盖原生 JSON/SSE、工具与思考历史、Token 和缓存统计、错误及完成边界、地址拼接、设置重载、Key 切换、宿主模型映射切换，以及管理页面的桌面和 390／320 像素布局。
