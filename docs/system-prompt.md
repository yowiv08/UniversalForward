# 系统提示词

「系统提示词」为渠道中的所有模型配置统一指令。默认不改写，支持追加和替换。填写的文本按原值发送，包含空格、换行和形如 `{api_key}` 的字面内容。

## 模式

| 模式 | 行为 |
| --- | --- |
| 不改写 | 使用客户端指令；可保存渠道文本供其他模式使用 |
| 追加 | 保留客户端指令，将渠道文本放在最后一条独立系统指令中 |
| 替换 | 移除普通系统指令，使用渠道文本；动态工具声明与对话历史保留 |

文本最多 32,000 个 UTF-16 单元。追加和替换要求非空文本。

## 协议处理

| 协议 | 追加 | 替换 |
| --- | --- | --- |
| Responses | `instructions` 字符串转为首条 `developer` 消息，普通 `system` 消息使用 `developer` 角色，渠道文本作为 `input` 最后一条 `system` 消息 | 移除 `instructions` 和 `input` 中普通 `system`／`developer` 指令，将渠道文本写入最后一条 `system` 消息 |
| Messages | `system` 字符串转为文本块，块数组保留顺序、`cache_control` 与其他属性，渠道文本作为最后一个独立文本块 | 使用渠道文本组成的 `system` 数组，保留 Claude Code 模板要求的客户端身份段 |
| Chat Completions | 保留消息角色，在 `messages` 末尾添加独立 `system` 消息 | 移除普通 `system`／`developer` 指令，在末尾添加渠道 `system` 消息 |

Responses 的顶层 `instructions` 保持为空字符串，渠道文本只出现在 `input` 中。字符串 `input` 使用内容相同的 `user` 消息表示。工具调用、工具结果、动态工具声明、用户消息、助手消息和思考历史保留。

追加模式要求指令字段具有受支持的类型，不能将任意对象或数字当作提示词文本。替换处理范围仅限客户端提供的请求内容，通过 `previous_response_id` 等方式引用的远程历史由上游管理。

## 指令边界

插件设置消息角色与位置，发送填写的文本。上游的额外规则、模型行为和指令遵循程度由上游决定。Messages 的系统块没有独立优先级字段；需要移除普通客户端指令时可使用替换模式。

角色语义可参考 [OpenAI Responses API](https://developers.openai.com/api/reference/resources/responses/methods/create)。

## 配置

`accounts/save` 接受以下可选配置：

```json
{
  "systemPromptPolicy": {
    "mode": "append",
    "text": "请使用中文回答。\n涉及代码时给出可运行示例。"
  }
}
```

`mode` 为 `off`、`append` 或 `replace`，默认 `off`。省略配置或传 null 表示保留已保存值。复制渠道会带入独立的提示词配置。

HTTP、SSE、Responses WebSocket 和连接测试使用同一规则。内部重试复用处理后的请求体，外层尝试从客户端原始请求开始处理。模型获取不使用系统提示词。

日志的 `systemPromptRewrite` 元数据记录模式、字段、文本长度、移除或调整的消息数量、角色、位置和模板身份保留情况，不记录提示词全文。
