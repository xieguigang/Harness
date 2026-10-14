---
name: nature-polishing-skill-test
overview: 在 Harness 自测工程中新增一个真实的 nature-skills「论文润色（nature-polishing）」技能端到端测试入口：用用户已有的 DeepSeek LLMClient 驱动 SkillAgent 完成 Layer1 路由 → Layer2 注入 → Layer3 工具调用全链路，并打印诊断信息。
todos:
  - id: add-polish-entry
    content: 新增 NaturePolishingSelfTest.vb：解析 --file/--skills/--model，用 GetLLMs() 构造 SkillAgent 并打印 Layer1/Layer2/Layer3 诊断
    status: completed
  - id: wire-program-branch
    content: 在 Program.vb Main 增加 --polish 分支（Async Main）并更新 --help 文案
    status: completed
    dependencies:
      - add-polish-entry
  - id: build-and-run-polish
    content: 编译 test.vbproj 并实跑 --polish 真实端到端测试，据报错必要时切换模型名重试
    status: completed
    dependencies:
      - wire-program-branch
---

## 用户需求

用户已在 `g:/LLMs/src/Harness/test/Program.vb`（第 15-23 行）中创建了 LLM 客户端工厂（从本地 INI 读 DeepSeek apiKey，`GetLLMs()` 返回 `LLMClient(OpenAIProvider("https://api.deepseek.com", apikey), "deepseek-flash")`）。现在要求：**通过这个 LLM 客户端对象，对 `C:\Users\Administrator\Downloads\nature-skills` 中的「论文润色」技能做一次真实端到端测试**。

## 功能内容

- 在自测工程中新增一个 `--polish` 测试入口，用 `GetLLMs()` 构造的 LLMClient 驱动 `Harness.SkillSystem.SkillAgent`（skills 根指向 nature-skills/skills）。
- 分层打印诊断，验证改造后的 SkillSystem 三层机制：
- **Layer1**：扫描到的技能数；路由清单中包含 `nature-polishing`，且内部共享包 `nature-shared` 被排除。
- **Layer2**：`LoadFullSkill("nature-polishing")` 后 manifest 的 `always_load`（7 项：4 个 `../nature-shared/core/*` + 3 个 `static/core/*`）被预载或明确列入 deferred；提示片段包含正文兜底（`## Skill Instructions`）、manifest 轴与按需文件表（`## Skill Manifest: Optional Axes`）、预载内容（`## Preloaded skill file:`）。
- **Layer3**：本地抽检 `read_skill_file` 后端能读 `static/fragments/section/results.md` 与共享包相对路径 `../nature-shared/core/reader-workflow.md`，并拒绝越界路径。
- 真实 LLM 全链路：把一段待润色英文 Results 文本连同任务说明交给 `RunTask`，由 LLMClient 的工具调用循环驱动模型按需调用 `read_skill_file`，最终打印 `MatchedSkillName`、`Success` 与完整 `Answer`（润色结果）。

## 边界与约束

- 待润色文本：默认内置一段英文 Results 样例，可用 `--file <路径>` 覆盖；skills 根可用 `--skills` 覆盖；模型名可用 `--model` 覆盖（默认沿用 `deepseek-flash`，若服务端模型名无效可切换）。
- 不改 Core / Ollama / Harness 库代码；只新增测试入口文件并在 `Program.vb` 增加分支，保持既有离线自测（MCP/Skill）行为不变。
- API key 继续从 INI 读取，不写入代码、不打印完整 key。
- 真实调用可能耗时数十秒，失败时输出可读诊断（key 缺失/模型名错误/网络问题）并给出切换 `--model` 的提示。

## 技术栈

- 语言/框架：VB.NET，`net10.0`，现有自测工程 `g:/LLMs/src/Harness/test/test.vbproj`（AssemblyName `HarnessSelfTest`，OutputType Exe；已引用 Harness、Ollama、Core、JSON、yaml、markdown）
- LLM：`Ollama.LLMClient` + `Ollama.OpenAIProvider`（DeepSeek 兼容端点），复用 `Program.GetLLMs()`
- 技能栈：`Harness.SkillSystem.SkillAgent` / `SkillManager` / `SkillDefinition` / `SkillManifest`
- 配置读取：`Microsoft.VisualBasic.ComponentModel.Settings.Inf.INIProfile`（沿用现有写法）

## 实现方式

1. **新增测试入口文件** `g:/LLMs/src/Harness/test/NaturePolishingSelfTest.vb`（Namespace `HarnessSelfTest`，Friend Module）：

- `RunAsync(args As String()) As Task(Of Integer)`，解析 `--file` / `--skills` / `--model`；
- `GetLLMs()` 取客户端（按 `--model` 覆盖时通过反射或重新构造 `New LLMClient(New OpenAIProvider("https://api.deepseek.com", apikey), model)` 保持一致，避免改动 `GetLLMs` 签名）；
- `Dim agent As New SkillAgent(client, skillsRoot)`，随后打印 Layer1/Layer2/Layer3 诊断；
- 组装润色任务文本（明确：目标期刊 generic、章节 Results、语言 en、保留事实与术语、不要新增内容），`Await agent.RunTask(taskText)`；
- 统一 `Try/Catch` 把 api key 缺失、模型名无效、网络异常转成可读中文提示。

2. **`Program.vb` 增加 `--polish` 分支**：`Main` 改为 `Await NaturePolishingSelfTest.RunAsync(...)`（`Async Function Main`），并更新 `--help` 文案；保留 `--stub-mcp-server` 与默认离线自测路径。
3. **编译与实跑**：`dotnet build g:/LLMs/src/Harness/test/test.vbproj` 后执行 `dotnet HarnessSelfTest.dll --polish`；若模型名不被接受，按报错换 `--model deepseek-chat` 重试。

## 关键决策与理由

- **不修改 `GetLLMs()`**：用户明确“通过这个 LLM 客户端对象”，测试入口直接复用它；仅在 `--model` 覆盖时按同样参数重建，保持端点与 key 一致。
- **用 `RunTask` 而不是手动拼 prompt**：`LLMClient.Chat` 已内置工具调用循环（`For round 1 To _maxRounds`，返回 Tool Calls 即执行并带结果续请求），这样能真实验证 `read_skill_file` 工具被模型调用、Layer3 按需加载 fragments 的完整链路，而不是只验证“一次性 prompt”。
- **诊断与真实调用分离**：Layer1/Layer2/Layer3 的本机断言先跑（不依赖网络），再跑真实 LLM；任一环节失败都能定位是技能栈问题还是网络/模型问题。
- **默认内置样例文本而非依赖外部文件**：保证零配置即可复现；`--file` 提供覆盖能力。

## 性能与可靠性

- Layer1 扫描为一次性目录遍历（约 20 个技能），只读 front matter，开销可忽略。
- Layer2 预载受 `PreloadSingleFileLimit=30000` / `PreloadTotalBudget=120000` 约束；超预算文件降级为 deferred 清单，不会把上下文打爆。
- 真实 API 调用耗时取决于模型与工具轮数（预计 1-3 轮）；`RunTask` 支持 `CancellationToken`，入口可选超时保护（建议 180 秒）。

## 验收标准

- 控制台输出：路由命中 `nature-polishing`；`nature-shared` 不在路由清单。
- Layer2 诊断显示 7 个 `always_load` 文件被预载（或超预算时全部列出为 deferred），片段含 `## Skill Instructions` 与 `## Skill Manifest: Optional Axes`。
- Layer3 抽检：`static/fragments/section/results.md` 与 `../nature-shared/core/reader-workflow.md` 读取成功；`../../../etc/passwd` 之类越界路径被拒（返回 `[error]`）。
- 最终 `Answer` 为非空润色文本，且模型行为体现 SKILL.md 路由协议（声明轴值、引用已加载片段、保留事实不新增）。