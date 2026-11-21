# 系统提示词

「系统提示词」为渠道中的所有模型配置统一指令。默认不改写，支持追加和替换。填写的文本按原值发送，包含空格、换行和形如 `{api_key}` 的字面内容。

## 模式

| 模式 | 行为 |
| --- | --- |
| 不改写 | 使用客户端指令；可保存渠道文本供其他模式使用 |
| 追加 | 保留客户端指令。本轮系统指令已包含完整的渠道提示词时原样保留，缺失时才补上 |
| 替换 | 本轮普通系统指令已是渠道提示词时保持原样；缺失或被改写时恢复原文，并清除其他普通系统指令 |

插件不限制自定义系统提示词的字符数。追加和替换时不能留空或只填空白。上游模型的上下文限制仍然适用。

## 协议处理

| 协议 | 追加时检查和补写的位置 | 替换 |
| --- | --- | --- |
| Responses | 检查顶层 `instructions` 和 `input` 中的普通 `system`／`developer` 指令。缺失时补到 `instructions`；有原指令时用一个空行分隔。`input` 保留原值 | 顶层 `instructions` 使用渠道文本，移除本次 `input` 数组中的普通 `system`／`developer` 指令 |
| Messages | 检查顶层 `system` 字符串或 `text` 块。缺失时在末尾补一个文本块，保留原文、块顺序和 `cache_control` | 顶层 `system` 使用渠道文本，保留 Claude Code 模板要求的客户端身份段 |
| Chat Completions | 检查 `messages` 中所有普通 `system`／`developer` 指令。缺失时补到首条普通系统指令的字符串或文本块中；若首条消息不是普通系统指令，则在首位创建 `system` 消息 | 移除其他普通 `system`／`developer` 指令，在首位使用渠道指令。首条原本是普通系统指令时沿用其角色，否则使用 `system` |

例如，渠道提示词为 `先给结论。`。在替换模式下，如果本轮 Responses 的 `instructions` 就是这句话，且没有其他普通系统指令，插件直接转发。如果客户端改成 `请用中文回答。`，或只带 `previous_response_id` 而没带指令，插件会将 `先给结论。` 写回 `instructions`。如果原文仍在，只是 `input` 里多了其他普通系统指令，插件只移除多余指令。引用的远端历史由上游管理。

在 Messages 或 Chat 中，如果已有文本块与渠道原文完全一致，替换模式会沿用该块及其 `cache_control`，移除重复和其他普通系统指令。Chat 的渠道指令位于首条消息，Claude Code 模板保留必要的身份段。用户消息、工具结果或思考历史里出现同样的文字，不会阻止本轮恢复系统指令。

追加按原文匹配：渠道提示词须在同一条系统指令的字符串或同一文本块中完整出现，起止均在行边界。`不要回答中文` 不会命中 `回答中文`。用户消息、助手消息、工具调用与结果、动态工具声明和思考历史均不参与匹配，也不会被修改。Chat 首条系统指令使用中转自定义格式时，原消息保留，缺失的渠道指令写在它前面。

## 指令边界

插件设置消息角色与位置，发送填写的文本。上游的额外规则、模型行为和指令遵循程度由上游决定。Messages 的系统块没有独立优先级字段；需要移除普通客户端指令时可使用替换模式。

直接填写指令正文即可。消息角色由协议字段指定，写入文本框的 `<|im_start|>system:` 或 XML 标签只属于正文。参见 [OpenAI Responses API](https://developers.openai.com/api/reference/resources/responses/methods/create)、[Chat Completions API](https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create) 和 [Anthropic Messages API](https://platform.claude.com/docs/en/api/messages/create)。

## 配置

`accounts/save` 接受以下可选配置：

```json
{
  "systemPromptPolicy": {
    "mode": "replace",
    "text": "请使用中文回答。\n涉及代码时给出可运行示例。"
  }
}
```

`mode` 为 `off`、`append` 或 `replace`，默认 `off`。省略配置或传 null 表示保留已保存值。复制渠道会带入独立的提示词配置。

HTTP、SSE、Responses WebSocket 和连接测试使用同一规则。内部重试复用处理后的请求体，外层尝试从客户端原始请求开始处理。模型获取不使用系统提示词。

只有实际改写系统指令时，日志才包含 `systemPromptRewrite` 元数据，其中记录模式、字段、文本长度、移除或调整的消息数量、角色、位置和模板身份保留情况，不记录提示词全文。
