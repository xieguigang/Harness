---
name: harness-background-jobs
overview: 在 Harness\Task 下新建后台作业引擎（每 Sandbox 一个 JobManager）与四个模型可调用工具：job_start（启动后台 shell 流式作业或后台 SkillAgent 子代理结果型作业）、job_kill（软取消+宽限期后强杀进程树）、job_list、job_output（stream 增量消费 / final-output 幂等），并注册进 Sandbox.ToolRegistry。
todos:
  - id: task-engine
    content: 实现作业引擎：JobModel.vb（JobShape/JobStatus/JobSummary 快照）、BackgroundJob.vb（状态机+有界增量缓冲+消费游标+幂等结果+完成信号）、BackgroundJobManager.vb（id 生成、List/GetSummary/Output(wait,timeout)、Cancel 软取消+5s 宽限期强杀、AttachClient、SkillsDirectory、ShutdownAll）
    status: completed
  - id: extract-launcher
    content: 抽取 Tools/ProcessLaunch.vb（CreateShellStartInfo/ClampTimeout/Limit/AppendStream 及常量），并让 ShellTool.vb 改为复用，保证行为与输出格式零变化
    status: completed
  - id: shell-job-runner
    content: 实现 ShellJobRunner.vb：用 ProcessLaunch 启动后台进程，BeginOutputReadLine/BeginErrorReadLine 逐行入缓冲，记录退出码并暴露进程句柄供整树强杀
    status: completed
    dependencies:
      - task-engine
      - extract-launcher
  - id: job-tools
    content: 在 Harness/Task 实现四个工具：job_start（command/task 二选一+可选 kind 校验+Execute 鉴权与越权重试）、job_kill（job_id+可选 reason）、job_list（零参数）、job_output（job_id/wait/timeout_ms，按 Shape 分派增量输出与幂等结果）
    status: completed
    dependencies:
      - task-engine
      - shell-job-runner
  - id: sandbox-wiring
    content: 接入 Sandbox：新增惰性 Jobs 属性、ToolRegistry 追加 job_start/job_kill/job_list/job_output 四条、RegisterTo 中调用 Jobs.AttachClient(client)；用 [skill:lsp-code-analysis] 核对注册与构造签名的全部引用点无遗漏
    status: completed
    dependencies:
      - job-tools
  - id: agent-job
    content: 给 SkillAgent.RunTask/MatchTaskToSkill 增加可选 CancellationToken 并转发给 LLMClient.Chat；把 job_start 的 task 参数接成结果型作业（懒加载 SkillAgent、skills 目录缺失给出明确失败信息）
    status: completed
    dependencies:
      - job-tools
  - id: verify-build-smoke
    content: dotnet build Harness.vbproj 确认 0 warning；用工作区外临时控制台工程冒烟：启动长跑命令作业→增量读取→无新增提示→wait 超时后仍在运行→job_list→job_kill（软退出与宽限期强杀各一次）→未知/已结束作业等错误路径；结果型作业用委托工作体验证幂等语义，跑完删除临时工程
    status: completed
    dependencies:
      - sandbox-wiring
      - agent-job
---

## Product Overview

为 Harness 增加"后台作业（background job）"能力：模型可以把耗时工作丢到后台执行并立即拿到 job_id，之后随时查看列表、增量读取输出、按需取消。全部实现放在 `Harness\Task` 目录，作为现有 11 个沙箱工具的同族补充。

## Core Features

- **启动（新增入口工具）**：同一个工具既能把一条 shell 命令作为后台流式作业启动，也能把一个自然语言任务作为后台"结果型"作业（子代理，跑完给最终结果）启动；两种输入互斥，必须且只能给一个；可再给一个可选的 kind 做一致性校验；启动后立即返回作业 id、类型与状态。
- **列表**：列出当前会话内的全部后台作业（运行中的与已结束的），每行给出 id、类型（命令 / 子代理）、状态（运行中 / 取消中 / 已取消 / 成功 / 失败）、运行时长与退出码。
- **读取输出**：
- 流式作业：返回"上次读取之后新产生的输出"（读走即消费，再次读取只看到新增内容；没有新增时给出明确提示）。
- 结果型作业：作业完成后每次读取都返回同一份最终结果（幂等）。
- 支持 `wait` 阻塞等待作业结束；`timeout_ms` 到时作业仍在运行则原样返回当前进度（不结束作业）。
- **取消**：请求取消一个运行中的作业，可附带一句原因；原因被记录进该作业日志并传递给作业本身。取消是"请求式"的：先请作业自行退出，超过宽限期仍存活则强制终止（命令作业杀整棵进程树）。
- 所有失败路径都给出可直接读懂的文本提示（未知 id、对已结束作业调用取消、缺失启动参数、未配置子代理能力等），不抛异常、不崩溃。
- 输出沿用现有工具的纯文本标记风格（`<job_id>`、`<kind>`、`<status>`、`<stdout>`、`<stderr>`、`<result>` 等标签块），便于模型解析。

## Tech Stack

- 语言/工程：沿用现有 `Harness.vbproj`（VB.NET，`net10.0`，`RootNamespace=Harness`，`Platforms=AnyCPU;x64`），不引入任何新依赖（已有 Core / JSON / markdown / yaml / Ollama 引用足够）。
- 关键既有设施复用：
- 工具约定：`<Description>` + `<ExportAPI("...")>` + `<Argument(name, Description:=...)>`，`Public Sub New(sandbox As Sandbox)`，`Namespace Tools`。
- 沙箱鉴权：`Sandbox.ResolveAndAuthorize(path, operation, ByRef fullPath)` / `AuthorizeEscalation` / `PermissionWorkspaceWrite` / `PermissionDangerFullAccess` / `Workspace` / `WorkspaceRelative`。
- 进程启动语义：`Harness/Tools/ShellTool.vb` 的 `CreateStartInfo`（Windows `powershell.exe -NoProfile -NonInteractive -EncodedCommand <base64 UTF-16>`，前置 UTF-8 与 `$ProgressPreference='SilentlyContinue'`；其它平台 `/bin/bash -c`）。
- 结果型作业来源：`Harness/SkillSystem/SkillAgent.vb` 的 `RunTask(userTask)`，其内部 `LLMClient.Chat(prompt, Optional cancellationToken)` 已支持取消（`Ollama/LLMClient.vb:330`）。
- 反射绑定约束（`Ollama/ToolCalls/CLRFunction.vb`，决定工具签名怎么写，必须遵守）：只导出带 `<Description>` 的 Public 方法；函数名取 `<ExportAPI>.Name`；`<Argument>` 的 `Name` 必须与 VB 参数名逐字一致（否则字典查找 KeyNotFound）；`required` = 非 Optional 参数；`Double→number`、`Boolean→boolean`、`String→string`；零参数方法可行（`job_list()` 会导出空 `properties/required`）。

## Implementation Approach

1. **引擎先落地，工具薄封装**。在 `Harness/Task/` 下新建一个"每 Sandbox 一份"的作业注册表 `BackgroundJobManager`（挂到 `Sandbox.Jobs`），内部持有 `Dictionary(Of String, BackgroundJob)`、自增 id 生成器、以及可选的 `LLMClient`（供子代理作业使用）。四个工具只做参数校验、鉴权与文本渲染，业务语义全在引擎里，便于以后给别的宿主复用。
2. **两类作业用统一的作业模型表达**：`Kind`（`"shell"` / `"agent"`，即 job_list 展示的类型）与 `Shape`（`Stream` / `FinalOutput`，决定 job_output 的读取语义）。引擎提供两个内部入口：`StartShellJob(command, workdir)` 与 `StartResultJob(description, work As Func(Of CancellationToken, Threading.Tasks.Task(Of Object)))`——后者是委托式接口，工具层把 `task` 参数包成 `SkillAgent.RunTask`，测试时可直接注入假工作体（不依赖真实 LLM）。
3. **作业状态机**：`running → succeeded / failed / cancelled`，取消时先经 `cancelling`。取消是"命令式请求 + 超时升级"：`Cancel(job_id, reason)` 立即把状态置为 `cancelling`、把 `reason` 追加进作业日志（满足"recorded in the log and forwarded to the job"）并 `CancellationTokenSource.Cancel()`；同时起一个看门人：等待作业自然结束最多 `KillGraceMs`（5000ms），仍未结束则对命令作业 `Process.Kill(entireProcessTree:=True)`（这正是强杀的唯一手段），随后状态落为 `cancelled`。对子代理作业，token 会传进 `LLMClient.Chat`，但没有可杀的句柄，超过宽限期直接落为 `cancelled`。
4. **流式输出的捕获与有界缓冲**：命令作业用 `BeginOutputReadLine` / `BeginErrorReadLine` 逐行回调写入作业缓冲（避免同步 `ReadToEnd` 在大输出下阻塞）；每个作业维护 stdout/stderr 两条滚动缓冲 + 各自"已消费长度"游标，上限 `MaxBufferChars = 256_000`，超限丢弃最旧内容并记 `[output dropped: N characters]`，保证长跑作业不会吃光内存。`ReadNewOutput()` 只返回游标之后的内容并前移游标（消费式）；结果型作业的最终结果单独存放，`ReadResult()` 幂等返回。
5. **等待语义**：`job_output(wait:=True)` 用 `SemaphoreSlim`/`TaskCompletionSource` 等待作业完成信号（不做忙轮询），超时后原样返回当前状态与已产生输出，作业继续运行；`timeout_ms` 缺省 120000、上限 600000，与 `ShellTool` 的默认/上限口径一致（描述里写明"Defaults to and is capped by configuration"）。
6. **鉴权口径按澄清结论**：只有 `job_start` 带 `sandbox_permissions` + `justification`，对 `workdir` 走 `ResolveAndAuthorize(..., SandboxOperation.Execute, cwd)` 并在 `cwd Is Nothing` 时回退原拒绝消息（与 `ShellTool` 完全同构）；`job_kill` / `job_list` / `job_output` 不带这两个参数。
7. **不重复造轮子**：把 `ShellTool` 里的 `CreateStartInfo` / `ClampTimeout` 抽成一个 `Friend Module`（`Harness/Tools/ProcessLaunch.vb`），`ShellTool` 改为调用它——纯抽取、行为逐字不变（PowerShell 的 UTF-8 与进度抑制细节只保留一份实现，避免两处漂移），后台命令作业也复用它。若审阅时更倾向"完全不碰 ShellTool"，退路是把同样几个辅助函数放进 Task 目录（代价是重复实现，计划不推荐）。
8. **性能与复杂度**：启动 O(1)；追加输出 O(行数)（行级回调，无二次遍历）；`ReadNewOutput` 与游标前移 O(新增字符数)，消费后释放已读分块避免累积；`job_list` O(作业数)（作业数量小、无 IO）；无定时轮询线程（取消看门人与 wait 都是事件驱动）。热点是逐行回调，因此它只做"加锁 + 追加 + 超限裁剪"，不做任何格式化/字符串拼接优化之外的额外工作。
9. **已知边界（会在代码注释与文档中明确写出）**：

- 子代理作业取消只能到达 `LLMClient.Chat`；`SkillAgent` 内部经由 `SkillScriptExecutor` 派生的孙进程不在跟踪范围（延迟到其自身超时）。
- 命令作业不设自动超时（后台语义即"跑到结束或被取消"）；由 `job_kill` 与作业列表中的运行时长提供可控性。
- 作业随 `Sandbox` 生命周期存在，进程退出即消失；另提供 `ShutdownAll()` 供宿主在退出前显式终止，不在 `Sandbox` 上加 `IDisposable`（控制改动半径）。
- 子代理作业需要 skills 目录：`Jobs.SkillsDirectory` 可配置，默认 `<workspace>\.harness\skills`；目录不存在时作业以明确错误信息立即失败（而不是抛异常）。

## Implementation Notes

- **命名空间与命名**：四个工具类与引擎类统一放在 `Namespace Tools`（与 `Sandbox.ToolRegistry` 的 `GetType(Tools.XxxTool)` 写法一致），文件物理位置在 `Harness/Task/`（VB 里命名空间与目录无强绑定）。**不要新建 `Task` / `Tasks` 命名空间或名为 `Task` 的类型**，否则与 `System.Threading.Tasks.Task` 撞名，需全限定或别名。
- **`<Argument>` 名称必须与参数名逐字相同**（`job_id`、`reason`、`wait`、`timeout_ms`、`command`、`task`、`kind`、`workdir`、`sandbox_permissions`、`justification`），否则反射按名取值会 KeyNotFound。
- **`job_start` 的"二选一"无法用 JSON Schema 表达**（不做 `oneOf`），因此：两个参数都声明为 Optional，靠 `<Description>` 说明"必须且只能给一个"，并在运行时校验（都给 / 都不给 / 与 `kind` 冲突 → 返回 `Error: ...` 文案；文案风格沿用 `Error: xxx` 前缀）。
- **错误文案与输出格式**：沿用现有约定——失败 `Error: ...`；成功/状态信息用 `<tag>` 块；空增量输出回 `No new output since the previous read.`；未知 id 回 `Error: no such background job: <id>`。
- **改动半径控制**：`ShellTool.vb` 只做辅助函数外移（无行为变化）；`SkillAgent.vb` 只加 `Optional cancellationToken As CancellationToken = Nothing` 并转发（既有调用方零改动）；`Sandbox.vb` 只加一个惰性 `Jobs` 属性、注册表 4 条、`RegisterTo` 里一次 `AttachClient`。
- **并发安全**：作业缓冲与状态用细粒度 `SyncLock` 保护；`job_output` 读取时先取快照再渲染，避免持锁格式化长字符串；`Cancel` 幂等（重复取消只追加日志、不重复升级）。
- 日志/诊断：不打印用户命令与 token 之外的内容；`job_list` 只暴露 id/kind/status/时间/退出码，不回显完整命令行敏感环境变量。

## Architecture Design

```mermaid
graph LR
  M[LLM] --> JS[job_start]
  M --> JK[job_kill]
  M --> JL[job_list]
  M --> JO[job_output]
  JS -->|Execute 鉴权 + 越权| MGR[BackgroundJobManager]
  JK --> MGR
  JL --> MGR
  JO --> MGR
  MGR --> J1["BackgroundJob (kind=shell, shape=Stream)"]
  MGR --> J2["BackgroundJob (kind=agent, shape=FinalOutput)"]
  J1 --> P[子进程: powershell / bash]
  J2 --> SA[SkillAgent.RunTask]
  SA --> LLM[LLMClient.Chat(token)]
  MGR -. AttachClient .-> LLM
  SB[Sandbox] --- MGR
```

- 分层：工具层（参数校验/鉴权/渲染）→ 引擎层（作业状态机、缓冲、取消、等待）→ 执行层（进程启动帮助类 `ProcessLaunch`、`SkillAgent`）。
- 关系：`Sandbox` 惰性创建并持有 `BackgroundJobManager`（每 Sandbox 一份），`RegisterTo(client)` 时把 client 交给引擎；工具类沿现有方式由 `Activator.CreateInstance` + `Sandbox` 构造注入创建，因此不需要额外注册机制。

## Directory Structure

```
g:/LLMs/src/Harness/
├── Task/
│   ├── JobModel.vb              # [NEW] 引擎的值类型与枚举：`JobShape`(Stream|FinalOutput)、`JobStatus`(Running|Cancelling|Cancelled|Succeeded|Failed)、kind 常量("shell"/"agent")、`JobSummary`(id/kind/status/started/finished/exitCode/cancelReason/elapsed) 快照类型。均为不可变快照，供 job_list/job_output 渲染，避免把可变作业对象暴露给工具层。
│   ├── BackgroundJob.vb         # [NEW] 单个作业的状态机与有界输出缓冲。承担：id/kind/shape 元数据；`CancellationTokenSource`；stdout/stderr 两条滚动缓冲 + 消费游标（上限 256000 字符，超限丢弃最旧并记 `[output dropped: N]`）；`AppendStdout/AppendStderr(line)`、`AppendNote(text)`、`ReadNewOutput()`（消费式）、`Result`/`ReadResult()`（幂等）、`MarkFinished(status, exitCode)`；`Snapshot()`；完成信号（TaskCompletionSource/SemaphoreSlim）供 wait 使用。线程安全，内部 SyncLock；只允许引擎写入、工具层读快照。
│   ├── BackgroundJobManager.vb  # [NEW] 作业注册表与会话级 API。承担：id 生成（`job-1` 自增）；`StartShellJob(command, workdir)`；`StartResultJob(description, work As Func(Of CancellationToken, Task(Of Object)))`；`Cancel(jobId, reason)`（置 cancelling、记日志、Cancel token、看门人 KillGraceMs=5000ms 后强杀进程树/落 cancelled，幂等）；`GetSummary(jobId)`/`List()`；`Output(jobId, wait, timeoutMs)`（默认 120000、上限 600000，超时保持运行）；`AttachClient(client)`；`SkillsDirectory` 与 `ShutdownAll()`；错误以文本消息形式返回（未知 id、未附加 client、skills 目录缺失等），不抛异常给工具层。
│   ├── ShellJobRunner.vb        # [NEW] 命令作业的执行细节。承担：用 ProcessLaunch 构建 ProcessStartInfo 启动后台进程、`BeginOutputReadLine/BeginErrorReadLine` 逐行写入 BackgroundJob 缓冲、`StandardInput.Close()`、记录退出码、把启动异常转成作业失败信息、暴露 Process 句柄供强杀（`Kill(entireProcessTree:=True)`），并在进程退出时触发作业完成信号。
│   ├── JobStartTool.vb          # [NEW] 工具 job_start。承担：`command`/`task` 二选一 + 可选 `kind` 一致性校验；`workdir` 走 `SandboxOperation.Execute` 鉴权与越权重试（与 ShellTool 同构，含 `cwd Is Nothing` 回退）；把 `task` 包装成 SkillAgent 结果型作业（懒加载 SkillAgent、注入 client 与 SkillsDirectory）；返回 `<job_id>/<kind>/<status>` 文本块并提示用 job_output 读取。
│   ├── JobKillTool.vb           # [NEW] 工具 job_kill。承担：`job_id` 必填 + 可选 `reason`；调用引擎 Cancel 并把取消结果（受理/已结束/未知）渲染为文本；不带越权参数。
│   ├── JobListTool.vb           # [NEW] 工具 job_list。承担：零参数（JSON schema 为 `{}`）；渲染每行 id、kind、status、运行时长或结束时间、退出码（最终结果型作业额外标出 succeeded/failed）；无作业时给出明确空结果提示。
│   └── JobOutputTool.vb         # [NEW] 工具 job_output。承担：`job_id` 必填、`wait`、`timeout_ms`；按 Shape 分派：Stream 输出 `<stdout>/<stderr>` 增量块（无新增时 `No new output since the previous read.`）+ `<status>`、FinalOutput 输出 `<result>`（幂等）或运行中提示；等待超时明确写出"作业仍在运行"；不带越权参数。
├── Tools/
│   ├── ProcessLaunch.vb         # [NEW] 从 ShellTool 抽出的共享帮助模块（Friend Module）：`CreateShellStartInfo(command, workDir)`、`ClampTimeout(value, defaultMs, maxMs)`、`Limit(text, ByRef truncated)`、`AppendStream(sb, tag, text, truncated)` 与相关常量。语义与抽取前逐字一致，作为"命令如何被平台 shell 执行"的唯一实现。
│   └── ShellTool.vb             # [MODIFY] 删除私有 StartInfo/超时/限幅实现，改为委托 ProcessLaunch 中的同名逻辑。纯重构，对外行为与输出格式零变化（不新增后台参数）。
├── SkillSystem/
│   └── SkillAgent.vb            # [MODIFY] `RunTask` 与 `MatchTaskToSkill` 增加 `Optional cancellationToken As CancellationToken = Nothing` 并转发给 `_ollama.Chat(prompt, cancellationToken)`；既有调用方不受影响，用于让后台子代理作业可被软取消。
└── Sandbox.vb                   # [MODIFY] 新增 `Public ReadOnly Property Jobs As BackgroundJobManager`（惰性创建，绑定 Workspace 与默认 SkillsDirectory）；`ToolRegistry` 追加 4 条（job_start/job_kill/job_list/job_output）；`RegisterTo(client)` 中在注册工具前调用 `Jobs.AttachClient(client)`。
```

## Key Code Structures

```
' Harness/Task/JobModel.vb —— 工具层与引擎层共享的只读契约
Namespace Tools

    ''' <summary>作业的输出形态，决定 job_output 的读取语义。</summary>
    Friend Enum JobShape
        ''' <summary>持续产生输出的作业（命令）：读走即消费。</summary>
        Stream
        ''' <summary>运行结束才有结果、结果幂等可重复读取的作业（子代理）。</summary>
        FinalOutput
    End Enum

    Friend Enum JobStatus
        Running
        Cancelling
        Cancelled
        Succeeded
        Failed
    End Enum

    ''' <summary>job_list / job_output 渲染用的作业快照（不可变）。</summary>
    Friend Class JobSummary
        Public ReadOnly Property Id As String
        Public ReadOnly Property Kind As String          ' "shell" | "agent"
        Public ReadOnly Property Shape As JobShape
        Public ReadOnly Property Status As JobStatus
        Public ReadOnly Property Description As String    ' 命令首行或任务摘要（截断）
        Public ReadOnly Property StartedAt As DateTime
        Public ReadOnly Property FinishedAt As DateTime?
        Public ReadOnly Property ExitCode As Integer?
        Public ReadOnly Property CancelReason As String
    End Class

End Namespace
```

```
' Harness/Task/BackgroundJobManager.vb —— 引擎对工具层暴露的会话级 API（不含实现）
Namespace Tools

    Friend Class BackgroundJobManager

        ''' <summary>可配置的 skills 根目录，子代理（结果型）作业据此构造 SkillAgent。</summary>
        Public Property SkillsDirectory As String

        ''' <summary>宽限期：取消请求后等待作业自行退出的毫秒数，超时则强杀进程树。</summary>
        Public Const KillGraceMs As Integer = 5000

        ''' <summary>由 Sandbox.RegisterTo 注入；子代理作业需要它，未注入时请求 agent 作业返回明确错误。</summary>
        Public Sub AttachClient(client As Ollama.LLMClient)

        ''' <summary>启动后台命令作业（Stream，kind="shell"），返回新作业 id；workdir 由调用方负责鉴权。</summary>
        Public Function StartShellJob(command As String, workDir As String) As String

        ''' <summary>启动后台结果型作业（FinalOutput，kind="agent"），work 为工作体（token 可取消）。</summary>
        Public Function StartResultJob(description As String, work As Func(Of Threading.CancellationToken, Threading.Tasks.Task(Of Object))) As String

        ''' <summary>请求取消：置 cancelling、把 reason 记入作业日志并传给作业；返回 Nothing 表示已受理，否则返回错误文案。</summary>
        Public Function Cancel(jobId As String, reason As String) As String

        ''' <summary>全部作业快照（运行中的与已结束的）。</summary>
        Public Function List() As IReadOnlyList(Of JobSummary)

        ''' <summary>按 id 取快照；未知 id 返回 Nothing。</summary>
        Public Function GetSummary(jobId As String) As JobSummary

        ''' <summary>读取输出：wait=True 时最多等待 timeoutMs（缺省/上限由配置决定，超时后作业继续运行）。</summary>
        Public Function Output(jobId As String, wait As Boolean, timeoutMs As Integer) As String

        ''' <summary>宿主退出前的可选清理：取消全部运行中的作业。</summary>
        Public Sub ShutdownAll()

    End Class

End Namespace
```

## Agent Extensions

### Skill

- **lsp-code-analysis**
- Purpose: 在接入 `Sandbox`（新增 `Jobs` 属性与 4 条 `ToolRegistry` 注册）时，用语义级导航确认 `RegisterTo` / `ToolRegistry` / `Tools.*Tool` 构造签名的全部引用点与实现关系，避免漏改或与既有工具模式不一致。
- Expected outcome: 得到一份完整的引用清单（哪些文件/位置依赖工具注册与 `New(sandbox)` 构造约定），据此确认新增 4 个工具后没有任何调用方需要同步调整。