# 渠道系统提示词设计与开源参考

UniversalForward 从 0.3.8 起提供渠道级的系统提示词规则：不改写、追加、替换。规则保存在渠道 settings JSON 中，作用于该渠道的所有模型，默认不改写。本功能沿用现有保存接口和转发链路。当前实现把插件文本放在本次请求的最后一条系统指令中，不再只拼接到原 instructions 字符串。

## 开源实现核查

2026-10-28 核查公开源码，引用固定提交以便复核；以下描述只针对对应提交。

| 项目 | 观察到的行为 | 采用的设计 |
| --- | --- | --- |
| New API | 渠道配置有系统提示词和 override 开关。原提示词缺失时注入；已有提示词且 override 开启时前置拼接，实际没有删除原提示词 | 使用渠道级设置；在 UI 中明确区分追加和替换，避免开关名称掩盖实际行为 |
| LiteLLM | 发送前钩子可改写请求，Responses 转换同时处理 instructions 和 input 中的指令，Claude 转换保留系统块的缓存属性 | 在最终协议、模板已确定后统一改写；替换时同时处理 Responses 两处指令；追加保留 Claude 系统块及缓存属性 |

New API 核查提交 `01da3a55f838cfa21010b05c7451c0a42cf5a173`：

- [渠道字段](https://github.com/QuantumNous/new-api/blob/01da3a55f838cfa21010b05c7451c0a42cf5a173/relaykit/dto/channel_settings.go#L21)。
- [Chat 提示词改写](https://github.com/QuantumNous/new-api/blob/01da3a55f838cfa21010b05c7451c0a42cf5a173/relay/chat_completions_via_responses.go#L22)：只处理遇到的第一个普通系统消息；开启 override 后前置拼接。
- [动态工具消息保护](https://github.com/QuantumNous/new-api/blob/01da3a55f838cfa21010b05c7451c0a42cf5a173/relay/chat_completions_via_responses.go#L32)：跳过带 tools 的 Kimi system 消息，避免丢失工具声明。
- [Claude 提示词改写](https://github.com/QuantumNous/new-api/blob/01da3a55f838cfa21010b05c7451c0a42cf5a173/relay/claude_handler.go#L51)：字符串和块数组分别处理，原块继续保留。

在该提交的原生 [Responses handler](https://github.com/QuantumNous/new-api/blob/01da3a55f838cfa21010b05c7451c0a42cf5a173/relay/responses_handler.go) 和 [WebSocket handler](https://github.com/QuantumNous/new-api/blob/01da3a55f838cfa21010b05c7451c0a42cf5a173/relay/responses_websocket.go) 中未发现渠道系统提示词设置的处理。因此没有把 Chat 转换路径的行为直接视为原生 Responses 支持。

LiteLLM 核查提交 `c7c895db456eda349a7be9661b4b21f578f9b589`：

- [公开请求钩子文档](https://docs.litellm.ai/docs/proxy/call_hooks)及[对应源码示例](https://github.com/BerriAI/litellm/blob/c7c895db456eda349a7be9661b4b21f578f9b589/litellm/proxy/example_config_yaml/custom_callbacks1.py#L18)：发送前修改请求。
- [Responses 指令转换](https://github.com/BerriAI/litellm/blob/c7c895db456eda349a7be9661b4b21f578f9b589/litellm/responses/litellm_completion_transformation/transformation.py#L1246)：instructions 和 input 两处内容都会加入指令处理。
- [system／developer 判断](https://github.com/BerriAI/litellm/blob/c7c895db456eda349a7be9661b4b21f578f9b589/litellm/responses/litellm_completion_transformation/transformation.py#L419)。
- [Claude 系统块转换](https://github.com/BerriAI/litellm/blob/c7c895db456eda349a7be9661b4b21f578f9b589/litellm/llms/anthropic/chat/transformation.py#L2051)：保留原消息或原块的 cache_control。

参考用于行为设计；UniversalForward 使用自己的 C# 实现，没有引入这些项目的运行依赖或复制实现代码。

## 处理规则

| 协议与原值 | 追加 | 替换 |
| --- | --- | --- |
| Responses instructions 字符串 | 原文转为首条 developer 消息；普通旧 system 消息改为 developer；渠道文本作为 input 的最后一条 system 消息 | 删除原 instructions 及 input 中普通 system／developer 指令消息；渠道文本作为最后一条 system 消息 |
| Responses instructions 缺失、null 或空串 | 无原 instructions 消息；渠道文本作为最后一条 system 消息 | 渠道文本作为最后一条 system 消息 |
| Claude system 字符串 | 原文转为第一个 text 块；渠道文本作为最后一个独立 text 块 | 只包含渠道文本的 system 数组 |
| Claude system 块数组 | 末尾新增 text 块；原块及缓存属性保留 | 只包含渠道文本的 system 数组 |
| Claude system 缺失、null 或空串 | 只包含渠道文本的 system 数组 | 只包含渠道文本的 system 数组 |

Responses 对旧指令的删除或角色降级只检查 input 数组的直接对象，类型须缺失或为 message，role 须为 system 或 developer。显式工具项、无法识别的类型、带非空动态 tools 声明的消息和嵌套字段保留。字符串 input 转为原文不变的 user 消息；追加遇到非字符串 instructions，或改写遇到非字符串／数组的 input 时，在修改前返回明确错误。顶层 instructions 保留为空字符串以兼容 Codex，渠道文本只出现一次。用户与助手消息、工具调用、工具结果、顶层工具定义、思考参数及其他协议字段沿用原流程。

Claude Code 请求头模板仍要求客户端身份段。替换后使用既有模板身份补全，身份段在前，插件文本仍在最后；管理员界面明确说明这一例外。追加保留原块，方便上游继续利用原有缓存前缀。

追加遇到错误类型的 instructions／system 时返回明确的请求错误，不把对象或数字转为文本。替换直接写入合法文本。不改写沿用全部已有处理，不因为本功能验证或改写客户端指令。

替换只处理本次请求中的指令；通过 previous_response_id 引用或其他上游机制保存的历史仍由上游管理，插件不能清除未提供的远程上下文。

## 优先级与文本边界

2026-11-01 核对 [OpenAI Docs 的消息角色说明](https://developers.openai.com/api/docs/guides/prompt-engineering)和 [Responses Create API](https://developers.openai.com/api/reference/resources/responses/methods/create)：system／developer 属于指令层级，input 的普通 user 消息层级较低，instructions 表示插入上下文的系统或开发者消息。API 没有可填写的数值优先级。插件使用显式的 system 角色，将普通旧系统消息降为 developer，并把插件消息放到 input 末尾，避免后面的普通客户端指令抢占同一层级。

填写的文本原样发送，包括空格、换行和形如 `{api_key}` 的字面内容。插件不自动写入前缀、后缀或“冲突时以插件规则为准”之类的文字。Claude 的 system 块之间没有单独优先级字段，保留旧块时只能采用最后一个独立系统块；若需要只保留插件普通指令，使用替换模式。动态工具声明保留原角色，不能据此声称所有条目都已降级。插件无法控制上游在接收请求后追加的规则，也不能保证模型完全遵循。

## 设置与执行顺序

管理员 accounts/save 接收可选字段：

```json
{
  "systemPromptPolicy": {
    "mode": "append",
    "text": "请使用中文回答。\n涉及代码时给出可运行示例。"
  }
}
```

mode 支持 off、append、replace；text 最多 32000 个 UTF-16 单元，保留空格和换行。追加／替换拒绝空白文本；关闭后可保留文本。省略或传 null 保留旧设置，新旧渠道缺省为 off。复制使用独立草稿并带入这项配置。

执行顺序为：选择实际上游协议 → 客户端模板补全／生成测试请求 → 思考等级映射 → 系统提示词处理 → Responses 资源状态剥离 → HTTP 或 WebSocket 发送。内部重试复用处理后的请求体，宿主下一次尝试基于原始请求重新处理，避免累积追加。模型获取不经过该处理。

日志元数据 systemPromptRewrite 记录 mode、field、textLength、removedMessages、demotedMessages、priorityRole（system）、placement（last）、templateIdentityPreserved，不重复放入提示词全文。Responses 的 field 为 input，Messages 的 field 为 system。从 v0.3.10 起，收到与实际发送的请求正文均停止采集，改写结果通过这些元数据核对。

## 验证

SystemPromptPolicyTests 检查末尾系统角色、晚出现的旧指令降级、原文与工具历史保留、不添加额外文本、字段边界、Claude 缓存块、默认关闭、文本校验、持久化、内部重试、模板、连接测试及 WebSocket。RequestPreparationTests 检查状态剥离后系统消息仍在末尾。HostAdaptationTests 补充真实宿主执行器的 3×3 次发送及映射切换，检查始终只追加一次并保留原始请求。这些验证检查实际发送结构，未调用收费模型来测量指令遵循概率。

system-prompt.cjs 在不含 allow-same-origin 的 iframe 沙箱中检查模式和页签键盘操作、草稿及失败恢复、复制、长度和空白校验、文本对比度及 320px／390px 布局。使用本地模拟宿主和上游进行验证。
