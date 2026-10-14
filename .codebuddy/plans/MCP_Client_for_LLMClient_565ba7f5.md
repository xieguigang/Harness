---
name: MCP Client for LLMClient
overview: 在 src\Harness\MCP 中用 VB.NET 实现一个 MCP（Model Context Protocol）客户端库，支持 stdio（本地子进程）与 Streamable HTTP/SSE 两种传输，覆盖 Tools / Resources / Prompts 三类原语；并在 Harness 中提供 McpToolBridge，通过 LLMClient 公开的 AddFunction API 把 MCP 工具/资源/提示暴露给 LLMClient，而不修改 LLMClient.vb。
todos:
  - id: impl-jsonrpc
    content: 实现 JsonRpc.vb：JSON-RPC 2.0 消息模型与 JsonRpcException
    status: completed
  - id: impl-transport
    content: 实现 IMcpTransport 及 stdio（Process+分帧）与 HTTP/SSE 两种传输
    status: completed
    dependencies:
      - impl-jsonrpc
  - id: impl-models
    content: 实现 McpModels.vb 数据模型与 McpServerDescriptor（stdio/http 描述符）
    status: completed
    dependencies:
      - impl-jsonrpc
  - id: impl-client
    content: 实现 McpClient：initialize/list/call/ping/close 及 id 关联
    status: completed
    dependencies:
      - impl-transport
      - impl-models
  - id: impl-bridge
    content: 实现 McpToolBridge：tools/resources/prompts 转 FunctionModel 并注册到 LLMClient.AddFunction
    status: completed
    dependencies:
      - impl-client
  - id: build-verify
    content: 编译 Harness.vbproj 0 错误 0 警告并用内存桩做 tools/list+call 自测
    status: completed
    dependencies:
      - impl-bridge
---

## 产品概述

在 `g:/LLMs/src/Harness/MCP`（当前为空）中用 VB.NET 实现一个 MCP（Model Context Protocol）客户端库，编译进已有的 `Harness.dll`。该客户端支持 stdio（本地子进程，如 npx/uvx/python 启动的 server）与 Streamable HTTP/SSE 两种传输，覆盖 Tools / Resources / Prompts 三类原语，并通过一个桥接类把 MCP 能力挂接到 `Ollama.LLMClient`，使其能像调用本地函数一样发现并调用 MCP server 提供的工具、读取资源、获取提示词。

**JSON-RPC 层不再自建**：复用底层库 `G:\GCModeller\src\runtime\sciBASIC#\mime\application%json\JsonRpc\` 中的 `JsonRpc` 模块（分帧读写 + 消息构造器）与 `Microsoft.VisualBasic.MIME.application.json.Javascript` 的 JSON DOM（`JsonObject`/`JsonValue`/`JsonArray`/`JsonElement`）。仅对该底层库做最小化改造以适配 MCP 客户端（stdio 走 Stream、补客户端请求构造器）。

## 核心特性

- **复用并改造底层 JsonRpc 库**：把分帧的 `NetworkStream` 参数泛化为 `Stream`（stdio 子进程可直接复用）；新增 `MakeRequest` 与 `JsonRpcException`。`JsonRpcSession`（服务端骨架）继续可用、向后兼容。
- 两种传输：stdio（子进程 + LSP 式 Content-Length 分帧，复用底层 `JsonRpc.ReadMessageAsync/SendMessageAsync`）、Streamable HTTP/SSE（POST + `Accept: application/json, text/event-stream` + SSE 解析 + `Mcp-Session-Id` 会话保持）。
- `McpClient`：connect（initialize + notifications/initialized 能力协商）、tools/list、tools/call、resources/list、resources/read、prompts/list、prompts/get、ping、close；请求/响应以 `TaskCompletionSource` 按 id 关联。
- 三种服务器描述符编程 API：`McpStdioServer(command, args, env, cwd)` 与 `McpHttpServer(url, headers, bearer)`。
- `McpToolBridge`：连接多个 MCP server，把其 tools/resources/prompts 转成 `Ollama.JSON.FunctionCall.FunctionModel` 并通过 `LLMClient.AddFunction` 注册执行委托；工具调用失败时返回错误文本而非抛异常，保证对话继续。
- 不修改 `LLMClient.vb`，不引入外部 NuGet 包（复用 net10.0 内置能力与项目已有底层库）。

## 底层库改造（G:\GCModeller\src\runtime\sciBASIC#\mime\application%json\JsonRpc\JsonRpc.vb）

1. `ReadMessageAsync` / `SendMessageAsync` 及内部 `ReadHeaderAsync` / `ReadBodyAsync` 的 `NetworkStream` 参数改为 `Stream`（向后兼容：`NetworkStream : Stream`）。
2. 新增 `MakeRequest(id As JsonElement, method As String, params As JsonElement) As jsobj`，并提供 `MakeRequest(id As Integer, method, params)` 便捷重载——客户端据此构造带 id 的请求。
3. 新增 `JsonRpcException`（code As Integer, message As String, data As JsonElement?）用于客户端把响应中的 `error` 对象转成异常。
（注：`MakeResponse/MakeError/MakeNotification/Str/Int` 与 `JsonObject/JsonValue/JsonArray/JsonElement` 原样复用，无需改动。）

## 技术栈 / 约束

- VB.NET + .NET 10（net10.0），与 `Harness.vbproj` 一致；SDK 项目 glob 编译 `src/Harness/MCP/*.vb`，无需改 vbproj。
- JSON：复用底层 `Microsoft.VisualBasic.MIME.application.json.Javascript`（`JsonObject`/`JsonValue`/`JsonElement`）；MCP 报文用 `JsonElement.ParseJSON` 解析、`BuildJsonString` 序列化。
- 子进程：`System.Diagnostics.Process`（仅 stdio）；HTTP：`System.Net.Http.HttpClient`（仅 HTTP）。
- 集成代码放 `Harness`（已引用 `Ollama`），避免循环依赖；桥接只用 `LLMClient` 公开 API（`AddFunction`、`FunctionModel`、`FunctionCall`）。

## 目录结构（src\Harness\MCP\，命名空间 Harness.MCP）

```
src/Harness/MCP/
├── IMcpTransport.vb        # [NEW] 传输抽象：ConnectAsync/SendAsync/CloseAsync、接收循环与通知回调
├── McpStdioTransport.vb    # [NEW] stdio：Process 启动子进程，复用底层 JsonRpc 分帧读写 stdin/stdout，
│                           #        后台接收线程按 id 投递
├── McpHttpTransport.vb     # [NEW] Streamable HTTP：HttpClient POST + SSE 解析 + Mcp-Session-Id + 回传通知
├── McpModels.vb            # [NEW] MCP 数据模型：McpTool/McpToolInputSchema、McpResource、McpPrompt、
│                           #        Content(text/resource/image)、CallToolResult、ServerCapabilities、InitializeResult
├── McpServerDescriptor.vb  # [NEW] 描述符基类 + McpStdioServer(command,args,env,cwd) + McpHttpServer(url,headers,bearer)，
│                           #        CreateTransport() 工厂
├── McpClient.vb            # [NEW] 协议客户端：ConnectAsync/ListTools/CallTool/ListResources/ReadResource/
│                           #        ListPrompts/GetPrompt/Ping/CloseAsync；id↔TCS 关联；错误→JsonRpcException
└── McpToolBridge.vb        # [NEW] 桥接 LLMClient：Attach(client, servers()) 连接各 server、聚合原语，
                            #        转 FunctionModel 并 AddFunction 注册；含名称前缀、超时、失败回退；IDisposable
```

（原计划的 `src/Harness/MCP/JsonRpc.vb` 已取消，改为复用并改造底层 `JsonRpc` 库。）

## 实现要点（防回归）

- **复用底层分帧**：`McpStdioTransport` 调用 `JsonRpc.ReadMessageAsync(process.StandardOutput.BaseStream)` 与 `JsonRpc.SendMessageAsync(process.StandardInput.BaseStream)`；请求用 `JsonRpc.MakeRequest`，通知用 `JsonRpc.MakeNotification`。
- **同步阻塞调用**：`LLMClient.ExecuteTool` 本轮同步，桥接委托内部 `.GetAwaiter().GetResult()` 阻塞等待 MCP 异步调用，与现有 `FunctionCaller` 同步派发一致。
- **参数重建**：`FunctionCall.arguments` 为 `Dictionary(Of String, String)`；桥接对每个值尝试 `JsonElement.ParseJSON`：成功（数字/布尔/对象/数组）则用解析节点，失败则作为字符串，正确还原 MCP `tools/call` 的 arguments 对象。
- **名称空间前缀**：多 server 时生成带 server 名前缀的合法函数名（仅 `[a-zA-Z0-9_]`）；resources 暴露为 `read_<prefix>_resource(uri)`，prompts 暴露为 `get_<prefix>_prompt(name, args?)`。
- **失败回退**：`tools/call` 抛异常或 `isError` 时，把错误信息拼成文本返回给 LLM，而非让执行委托抛异常中断对话。
- **HTTP 会话**：保存首包 `Mcp-Session-Id` 并回传；SSE 行解析支持 `event:`/`data:`/`：ping`，通知经回调忽略或记录。
- **日志与资源**：错误用 `App.LogException(ex)` 与 `ex.Message.warning`（与 LLMClient.vb 一致）；`McpStdioTransport.CloseAsync` 先关 stdin 再优雅 Kill/Dispose 子进程；`McpHttpTransport` 复用单 `HttpClient` 并在桥接释放时 Dispose。
- **能力协商**：`initialize` 的 `protocolVersion` 取 `"2025-06-18"`（或客户端支持最新值），响应中读取 server `capabilities` 决定 resources/prompts 分支（缺失则列表返回空）。
- **作用域控制**：仅新增 `src/Harness/MCP/*.vb`，并最小化改造底层 `JsonRpc.vb`（仅泛化参数 + 新增两个构造/异常），不触碰 `LLMClient.vb` 及其它模块。

## 验证

- 改造后 `dotnet build` GCModeller 的 JSON 工程 + `dotnet build src/Harness/Harness.vbproj` 均 0 错误 0 警告（确认底层 `NetworkStream→Stream` 改动未破坏 `JsonRpcSession`）。
- 内存内 `IMcpTransport` 桩做 `tools/list` + `tools/call` 往返自测；条件允许时连一个真实 stdio MCP server（如 filesystem/weather 示例）验证端到端。

## 目录结构（当前待办）

1. 改造底层 JsonRpc 库（NetworkStream→Stream + MakeRequest + JsonRpcException）
2. 实现 IMcpTransport 及 stdio / HTTP/SSE 两种传输
3. 实现 McpModels.vb 数据模型与 McpServerDescriptor
4. 实现 McpClient（initialize/list/call/ping/close + id 关联）
5. 实现 McpToolBridge（转 FunctionModel 并注册到 LLMClient.AddFunction）
6. 编译 Harness.vbproj 0 错误 0 警告 + 内存桩自测