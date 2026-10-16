# A Large-Language-Model Harness Toolkit Written in VisualBasic.NET

A collection of VisualBasic.NET projects that together provide everything you
need to build, host, and interact with large language models: a unified LLM
client with tool-calling, an agent **harness** with a skill system and a
security sandbox, an OpenAI-compatible **RAG proxy**, a WebView2 desktop chat
UI, R# scripting bindings, an educational LLM internals demo.

Everything is written against **.NET 10** and builds on top of the
[sciBASIC#](https://github.com/xieguigang/GCModeller) runtime
(`Microsoft.VisualBasic.Core`, JSON/YAML/Markdown MIME layers, NLP, etc.) from
the GCModeller repository.

---

## Table of Contents

- [Overview](#overview)
- [Repository Layout](#repository-layout)
- [Project Guide](#project-guide)
  - [Ollama — Unified LLM Client](#ollama--unified-llm-client)
  - [Harness — Agent Skill System & Sandbox](#harness--agent-skill-system--sandbox)
  - [OllamaProxy — RAG-Augmented Proxy](#ollamaproxy--rag-augmented-proxy)
  - [Agent — R# Scripting Bindings](#agent--r-scripting-bindings)
  - [Talk — Llama2 & Text Generation](#talk--llama2--text-generation)
  - [TalkBuddy — Educational LLM Internals Demo](#talkbuddy--educational-llm-internals-demo)
  - [WebView2UI — Desktop Chat UI](#webview2ui--desktop-chat-ui)
  - [HMM — Hidden Markov Model](#hmm--hidden-markov-model)
- [Building](#building)
- [Getting Started](#getting-started)
- [License & Acknowledgements](#license--acknowledgements)

---

## Overview

The toolkit is organised as a set of cooperating libraries rather than a single
application:

- **Talk to any backend.** `Ollama` provides one `LLMClient` abstraction that
  talks to local [Ollama](https://ollama.com) servers and any
  OpenAI-compatible endpoint (OpenAI, DeepSeek, …) through pluggable
  `ILLMProvider` implementations, with streaming, multi-turn memory,
  function/tool calling, and token-usage/KV-cache statistics.
- **Build autonomous agents safely.** `Harness` adds a three-layer
  *progressive-disclosure* skill system (`SkillAgent` / `SkillManager`),
  a security `Sandbox` with pluggable `SecurityPolicy`, a rich set of file /
  shell tools, and an MCP (Model Context Protocol) client that bridges external
  MCP servers into the unified client as callable tools.
- **Serve a RAG endpoint.** `OllamaProxy` is an HTTP server that presents an
  OpenAI-compatible `/v1/chat/completions` interface and augments requests
  with retrieval-augmented context before forwarding them to an Ollama backend.
- **Embed / script / teach.** `WebView2UI` is a WinForms + WebView2 desktop
  chat host; `Agent` exposes the client to the R# scripting language;
  `TalkBuddy` is a from-scratch teaching demo of tokenizer → chat template →
  MoE / KV-cache / function-calling.

---

## Repository Layout

```
LLMs/
├── LLMs.slnx            # main solution (Agent, HMM, Ollama, OllamaProxy, Talk)
├── Ollama.slnx          # solution for the Ollama provider library
├── TalkBuddy.slnx       # solution for the TalkBuddy demo
├── ollama_rag.py        # helper script for the RAG proxy
└── src/
    ├── Ollama/          # unified LLM client + providers + tool calling
    ├── Harness/         # agent skill system, sandbox, tools, MCP client
    ├── OllamaProxy/     # OpenAI-compatible RAG proxy server
    ├── Agent/           # R# (Rsharp) scripting package for Ollama / ChatGLM
    ├── Talk/            # Llama2 training/generation + HMM text generation
    ├── TalkBuddy/       # educational LLM internals demo + tutorial
    ├── WebView2UI/      # WinForms + WebView2 desktop chat UI
    └── HMM/             # Hidden Markov Model library + tests
```

> The projects reference the **GCModeller** source tree (sciBASIC#, R#, Flute
> HTTP server, etc.) via relative `ProjectReference` paths such as
> `..\..\..\GCModeller\src\runtime\…`. Clone GCModeller alongside `LLMs` so the
> references resolve.

---

## Project Guide

### Ollama — Unified LLM Client

The core library (`RootNamespace = Ollama`, `net10.0`). It defines a single
`LLMClient` that owns the conversation memory, a function/tool registry, and a
usage logger, and drives any backend through the `ILLMProvider` interface.

**Providers**

- `OllamaProvider` — talks to a local Ollama server (`/api/chat`). Local
  inference does not report KV-cache hit statistics (`SupportsCacheStats =
  False`).
- `OpenAIProvider` — speaks the OpenAI `/v1/chat/completions` protocol and
  additionally requests `stream_options.include_usage`, so backends such as
  DeepSeek can report KV-cache hit/miss token counts.

**Key capabilities**

- Streaming chat (`StreamChatAsync`) with optional `think`/`output` separation.
- Tool / function calling (`Ollama.ToolCalls`): `FunctionTool`, `FunctionCall`,
  `FunctionCaller`, `CLRFunction`, argument parsing (`ToolArgument`), and JSON
  extraction (`LlmJsonExtractor`).
- Conversation memory (`Ollama.ContextMemory`) with estimated token accounting.
- Per-request and cumulative token usage (`ChatUsage`, `ChatUsageRecord`),
  capped by `MaxUsageLogSize` to keep long agent sessions bounded.
- Configurable `maxRounds`, `temperature`, and `enable_usage_stats`.

### Harness — Agent Skill System & Sandbox

A higher-level agent framework (`RootNamespace = Harness`) layered on top of
`Ollama`. It contains four cooperating subsystems:

- **SkillSystem** — a three-layer *progressive disclosure* mechanism
  implemented by `SkillAgent` and `SkillManager`:
  1. **Layer 1 (metadata)** — every installed skill's lightweight metadata is
     scanned and cached at construction so intent classification is cheap.
  2. **Layer 2 (SKILL.md)** — only the matched skill's full `SKILL.md` is
     injected into a fresh prompt to drive execution.
  3. **Layer 3 (on-demand files / scripts)** — during execution the LLM can
     pull individual skill files (`read_skill_file`-style tool) or run a skill
     script whose stdout is fed back as the tool result.
  Supporting types: `SkillDefinition`, `SkillManifest`, `SkillMetadata`,
  `SkillResource`, `SkillExecutionResult`.
- **Sandbox** — the security-gated filesystem backend shared by every tool.
  It owns the workspace root and a `SecurityPolicy`
  (`Readonly` / `FullControls` / `AskPermission`) and raises a
  `RequestPermission` event so a host can prompt the user. Path resolution
  (`ResolveAndAuthorize`, `ResolvePath`, `IsInsideWorkspace`) enforces the
  workspace boundary. Also hosts a `BackgroundJobManager` for long-running tool
  jobs and the MCP bridge.
- **Tools** — one file per capability: `ReadTool`, `WriteTool`, `EditTool`,
  `MultiEditTool`, `ApplyPatchTool`, `GlobTool`, `GrepTool`, `LsTool`,
  `MoveTool`, `DeleteTool`, `ShellTool`, `ProcessLaunch`, `TextFileUtils`.
- **MCP** — a Model Context Protocol client (`McpClient`) with stdio
  (`McpStdioTransport`) and Streamable-HTTP (`McpHttpTransport`) transports,
  JSON-RPC request/response correlation, and `McpToolBridge` which turns MCP
  `tools`/`resources`/`prompts` into Ollama function tools registered on the
  unified client — without modifying `LLMClient` itself.

Other harness pieces: `SecurityPolicy`, `PermissionEventArg`, `SandboxOperation`,
and a `Task/` folder for orchestration.

### OllamaProxy — RAG-Augmented Proxy

A small HTTP server (`OllamaProxyServer`, built on the Flute HTTP framework)
that exposes an **OpenAI-compatible** `/v1/chat/completions` endpoint. It
forwards chat requests to a local Ollama backend (`http://localhost:11434`) and
can augment the prompt with retrieval-augmented context. A `/health` route is
provided for liveness checks. The companion `ollama_rag.py` script implements
the retrieval side of the pipeline.

### Agent — R# Scripting Bindings

An R# (Rsharp) package (`RootNamespace = Agent`) that exposes the Ollama client
to the R# scripting language. It ships two packages:

- `ollama` (`OLlama`) — `new(...)` creates a configured `LLMClient`, plus
  `setup_global_hook(...)` to register a process-wide LLM instance.
- `chatglm` (`ChatGLM`) — chat-history construction helpers
  (`input_and_response`, `ChatHistory`) for GLM-family models, plus
  `GLMBatchTask` for batched runs.

It depends on the R# runtime and the `Ollama` / `Talk` libraries.

### Talk — Llama2 & Text Generation

A text-generation project (`TalkGenerator`) that includes:

- `Llama2` — a from-scratch Llama2 inference/training demo. This is a
  VB.NET port (MIT licensed) of Rahul TR's `llama2.c`, with `Llama2Models` for
  weight loading and generation.
- `HMM`-based text generation utilities living alongside the Llama2 code.

It builds against the sciBASIC# data-science, math, and NLP layers.

### TalkBuddy — Educational LLM Internals Demo

A teaching-oriented project that *demonstrates* how modern LLMs work rather
than wrapping a hosted API. `Pipeline` (`DemoPipeline`) assembles a tokenizer
(`DeepSeekTokenizerAdapter`), a chat template (`ChatTemplate`), a tool registry
(`ToolRegistry`), and a toy `LLMModel` + `EmbeddedCorpus`, then walks through
the three training stages (pretraining → instruction SFT → tool SFT) and makes
the three headline mechanisms observable:

- **MoE** (Mixture-of-Experts),
- **KV Cache**, and
- **Function Calling**.

The bundled `readme.md` is a long-form tutorial (in Chinese) covering the
Transformer decoder, attention, the training pipeline, inference/sampling,
DeepSeek-MoE, and the implementation principle of function calling.

### WebView2UI — Desktop Chat UI

A WinForms desktop chat application (`net10.0-windows`) that embeds a WebView2
control. `LLMHost` is a COM-visible bridge (`ClassInterfaceType.AutoDual`) that
exposes the configured `LLMClient` (model name, context-token usage, max
context) to the HTML/JavaScript front-end (`index.html`), and renders model
responses as Markdown (`MarkdownRender`). It ships a `WebView2LLMUI` host form.

---

## Building

Prerequisites:

1. **.NET 10 SDK** (the projects target `net10.0` / `net10.0-windows`).
2. A local clone of the **GCModeller** repository placed so that the relative
   `..\..\..\GCModeller\src\…` project references resolve (i.e. `LLMs` and
   `GCModeller` should share a common parent, e.g. `g:\`).

Pick a solution depending on what you need:

```powershell
# The main toolkit solution
dotnet build src\LLMs.slnx

# Just the Ollama provider library (+ NuGet package on build)
dotnet build src\Ollama\Ollama.vbproj -c Release

# The educational TalkBuddy demo
dotnet build src\TalkBuddy.slnx
```

Platforms `AnyCPU` and `x64` are both supported; the desktop UI
(`WebView2UI`) additionally requires the `net10.0-windows` target.

---

## Getting Started

A minimal chat session against a local Ollama model:

```vbnet
Imports Ollama
Imports Ollama.JSON.FunctionCall

Dim provider As New OllamaProvider("127.0.0.1:11434")
Dim client As New LLMClient(provider, model:="llama3")

For Each chunk In client.Chat("Hello, who are you?")
    Console.Write(chunk.Message.Content)
Next
```

Bridging an external MCP server into the same client as callable tools:

```vbnet
Dim bridge As New McpToolBridge(New McpStdioServer("name", "cmd", args))
Await bridge.ConnectAllAsync()
bridge.Attach(client)
```

Running an agent task through the progressive-disclosure skill system:

```vbnet
Dim agent As New SkillAgent(client, "C:\path\to\skills")
Dim result As SkillExecutionResult = Await agent.RunTask("polish this draft")
Console.WriteLine(result.Answer)
```

(See `Harness/test/` for runnable self-tests, including the real end-to-end
`--polish` skill test.)

---

## License & Acknowledgements

- The **HMM** implementation is adapted from
  [AhmedHani/Hidden-Markov-Model](https://github.com/AhmedHani/Hidden-Markov-Model)
  (see that project for its license terms).
- The **Talk / Llama2** code is a VB.NET port (MIT licensed) of
  [Rahul TR's `llama2.c`](https://github.com/karpathy/llama2.c).
- The rest of the toolkit is part of this repository and builds on the
  [sciBASIC#](https://github.com/xieguigang/GCModeller) runtime from the
  GCModeller project.

