Imports System.Collections.Generic
Imports System.Text.Json.Serialization

Namespace Logging

    ''' <summary>
    ''' the vocabulary of the tool call log: every value that appears in the
    ''' json files is one of these constants, so a reader never has to guess
    ''' whether a status was spelled "error" or "failed".
    ''' </summary>
    Public Module ToolLogText

        ''' <summary>a tool backed by the sandbox itself (read, write, shell, ...).</summary>
        Public Const SourceSandbox As String = "sandbox"
        ''' <summary>a tool / resource / prompt bridged in from an mcp server.</summary>
        Public Const SourceMcp As String = "mcp"
        ''' <summary>a tool of the skill system (execute_skill_script, read_skill_file).</summary>
        Public Const SourceSkill As String = "skill"

        ''' <summary>the call ran and its result looks like a normal answer.</summary>
        Public Const StatusOk As String = "ok"
        ''' <summary>the call ran but reported a failure through its result text.</summary>
        Public Const StatusError As String = "error"
        ''' <summary>the sandbox or the user refused the operation.</summary>
        Public Const StatusDenied As String = "denied"
        ''' <summary>the call threw; nothing was handed back to the model.</summary>
        Public Const StatusFailed As String = "failed"

        ''' <summary>a tool as the model calls it (an mcp resource or prompt is not one).</summary>
        Public Const KindTool As String = "tool"
        ''' <summary>an mcp resource accessor (list_resources / read_resource).</summary>
        Public Const KindResource As String = "resource"
        ''' <summary>an mcp prompt accessor.</summary>
        Public Const KindPrompt As String = "prompt"

    End Module

    ''' <summary>
    ''' how the argument of one declared parameter was filled for a single
    ''' call: from the model, from the clr default, or not at all.
    ''' </summary>
    Public Class ToolCallParameterRecord

        ''' <summary>the declared name of the parameter.</summary>
        <JsonPropertyName("name")>
        Public Property Name As String

        ''' <summary>the json schema type exported to the model (string / number / integer / boolean / array / object).</summary>
        <JsonPropertyName("type")>
        Public Property Type As String

        ''' <summary>whether the parameter was exported as required.</summary>
        <JsonPropertyName("required")>
        Public Property Required As Boolean

        ''' <summary>the clr default the tool falls back to when the model omits it.</summary>
        <JsonPropertyName("default")>
        Public Property [Default] As String

        ''' <summary>"model" when the call carried it, "default" when the clr default was used.</summary>
        <JsonPropertyName("origin")>
        Public Property Origin As String

        ''' <summary>the raw string the model sent (truncated at <see cref="ToolLogOptions.MaxInlineArgumentChars"/>).</summary>
        <JsonPropertyName("value")>
        Public Property Value As String

        ''' <summary>True when <see cref="Value"/> was cut short.</summary>
        <JsonPropertyName("truncated")>
        Public Property Truncated As Boolean

    End Class

    ''' <summary>
    ''' one tool invocation, from the moment the model asked for it to the
    ''' moment its result (or its exception) was handed back.
    ''' 
    ''' the record is deliberately flat and verbose: it is written for a human
    ''' who debugs a session afterwards, not for a machine that has to parse
    ''' it quickly.
    ''' </summary>
    Public Class ToolCallRecord

        ''' <summary>monotonic sequence number of the call inside its session (1 based).</summary>
        <JsonPropertyName("seq")>
        Public Property Seq As Integer

        ''' <summary>a short unique id of this single call, handy to cross reference a payload file.</summary>
        <JsonPropertyName("id")>
        Public Property Id As String

        ''' <summary>the session (one sandbox instance) this call belongs to.</summary>
        <JsonPropertyName("sessionId")>
        Public Property SessionId As String

        ''' <summary>utc timestamp of the moment the call reached the tool, ISO-8601.</summary>
        <JsonPropertyName("timestampUtc")>
        Public Property TimestampUtc As String

        ''' <summary>local timestamp of the same moment, ISO-8601 with offset.</summary>
        <JsonPropertyName("timestampLocal")>
        Public Property TimestampLocal As String

        ''' <summary>milliseconds between the start of the session and this call.</summary>
        <JsonPropertyName("elapsedSinceStartMs")>
        Public Property ElapsedSinceStartMs As Double

        ''' <summary>the name the model calls the tool by (the registered function name).</summary>
        <JsonPropertyName("tool")>
        Public Property Tool As String

        ''' <summary>where the tool came from: "sandbox", "mcp" or "skill".</summary>
        <JsonPropertyName("source")>
        Public Property Source As String

        ''' <summary>"tool", "resource" or "prompt" (the mcp primitives are not all tools).</summary>
        <JsonPropertyName("kind")>
        Public Property Kind As String

        ''' <summary>the mcp server that owns the primitive, Nothing for the local tools.</summary>
        <JsonPropertyName("server")>
        Public Property Server As String

        ''' <summary>the name the server or the skill gave the primitive, before it was namespaced.</summary>
        <JsonPropertyName("originalName")>
        Public Property OriginalName As String

        ''' <summary>the description exported to the model, kept so a reader knows what the tool claims to do.</summary>
        <JsonPropertyName("description")>
        Public Property Description As String

        ''' <summary>managed thread id of the thread that ran the call (a background job uses its own).</summary>
        <JsonPropertyName("threadId")>
        Public Property ThreadId As Integer

        ''' <summary>the name of that thread when it has one.</summary>
        <JsonPropertyName("threadName")>
        Public Property ThreadName As String

        ''' <summary>the index the provider gave the tool call inside its response message.</summary>
        <JsonPropertyName("callIndex")>
        Public Property CallIndex As Integer

        ''' <summary>whether the provider demanded strict (name based) argument binding.</summary>
        <JsonPropertyName("strict")>
        Public Property Strict As Boolean

        ''' <summary>every argument the model sent, verbatim (values are truncated individually).</summary>
        <JsonPropertyName("arguments")>
        Public Property Arguments As Dictionary(Of String, String)

        ''' <summary>True when at least one argument value was cut short.</summary>
        <JsonPropertyName("argumentsTruncated")>
        Public Property ArgumentsTruncated As Boolean

        ''' <summary>one entry per declared parameter, telling how it was filled.</summary>
        <JsonPropertyName("parameters")>
        Public Property Parameters As List(Of ToolCallParameterRecord)

        ''' <summary>declared required parameters the call did not carry.</summary>
        <JsonPropertyName("missingRequired")>
        Public Property MissingRequired As List(Of String)

        ''' <summary>arguments the model sent that the tool does not declare.</summary>
        <JsonPropertyName("unknownArguments")>
        Public Property UnknownArguments As List(Of String)

        ''' <summary>wall clock milliseconds the tool itself needed.</summary>
        <JsonPropertyName("durationMs")>
        Public Property DurationMs As Double

        ''' <summary>"ok", "error", "denied" or "failed".</summary>
        <JsonPropertyName("status")>
        Public Property Status As String

        ''' <summary>the result text handed back to the model, in full unless it was spilled to a payload file.</summary>
        <JsonPropertyName("result")>
        Public Property Result As String

        ''' <summary>length of the complete result text in characters.</summary>
        <JsonPropertyName("resultLength")>
        Public Property ResultLength As Integer

        ''' <summary>sha256 of the complete result text; only filled when the result was spilled or hashing is on.</summary>
        <JsonPropertyName("resultSha256")>
        Public Property ResultSha256 As String

        ''' <summary>True when the result was too long to inline.</summary>
        <JsonPropertyName("resultTruncated")>
        Public Property ResultTruncated As Boolean

        ''' <summary>the first characters of a spilled result, so the record is readable on its own.</summary>
        <JsonPropertyName("resultPreview")>
        Public Property ResultPreview As String

        ''' <summary>session relative path of the file holding the complete result, when it was spilled.</summary>
        <JsonPropertyName("resultPayloadFile")>
        Public Property ResultPayloadFile As String

        ''' <summary>exception type name, when the call threw.</summary>
        <JsonPropertyName("errorType")>
        Public Property ErrorType As String

        ''' <summary>exception message, when the call threw.</summary>
        <JsonPropertyName("errorMessage")>
        Public Property ErrorMessage As String

        ''' <summary>exception stack trace, when the call threw.</summary>
        <JsonPropertyName("errorStackTrace")>
        Public Property ErrorStackTrace As String

        ''' <summary>what the host was doing when the call happened (task name, round, custom fields).</summary>
        <JsonPropertyName("context")>
        Public Property Context As Dictionary(Of String, String)

        ''' <summary>the workspace root of the sandbox that ran the call.</summary>
        <JsonPropertyName("workspace")>
        Public Property Workspace As String

        ''' <summary>the security policy in force when the call happened.</summary>
        <JsonPropertyName("securityPolicy")>
        Public Property SecurityPolicy As String

    End Class

    ''' <summary>
    ''' the schema snapshot of one registered tool: what the model was told
    ''' the tool is, kept next to the calls so a suspicious call can be
    ''' compared with the declaration that produced it.
    ''' </summary>
    Public Class ToolRegistrationParameter

        <JsonPropertyName("name")>
        Public Property Name As String

        <JsonPropertyName("type")>
        Public Property Type As String

        <JsonPropertyName("required")>
        Public Property Required As Boolean

        <JsonPropertyName("default")>
        Public Property [Default] As String

        <JsonPropertyName("description")>
        Public Property Description As String

    End Class

    Public Class ToolRegistrationRecord

        <JsonPropertyName("tool")>
        Public Property Tool As String

        <JsonPropertyName("source")>
        Public Property Source As String

        <JsonPropertyName("kind")>
        Public Property Kind As String

        <JsonPropertyName("server")>
        Public Property Server As String

        <JsonPropertyName("originalName")>
        Public Property OriginalName As String

        <JsonPropertyName("description")>
        Public Property Description As String

        <JsonPropertyName("parameters")>
        Public Property Parameters As List(Of ToolRegistrationParameter)

        <JsonPropertyName("registeredAtUtc")>
        Public Property RegisteredAtUtc As String

    End Class

    ''' <summary>
    ''' one permission decision: the sandbox asking the host (or the user)
    ''' whether an operation may proceed, and the answer it got.
    ''' </summary>
    Public Class ToolPermissionRecord

        <JsonPropertyName("seq")>
        Public Property Seq As Integer

        <JsonPropertyName("sessionId")>
        Public Property SessionId As String

        <JsonPropertyName("timestampUtc")>
        Public Property TimestampUtc As String

        <JsonPropertyName("operation")>
        Public Property Operation As String

        <JsonPropertyName("target")>
        Public Property Target As String

        <JsonPropertyName("insideWorkspace")>
        Public Property InsideWorkspace As Boolean

        ''' <summary>"workspace-write" / "danger-full-access" for an escalation, Nothing for a policy request.</summary>
        <JsonPropertyName("requestedPermission")>
        Public Property RequestedPermission As String

        ''' <summary>the sentence the model gave to justify the escalation.</summary>
        <JsonPropertyName("justification")>
        Public Property Justification As String

        <JsonPropertyName("granted")>
        Public Property Granted As Boolean

        ''' <summary>why the request happened, or why it was refused without asking.</summary>
        <JsonPropertyName("reason")>
        Public Property Reason As String

    End Class

    ''' <summary>
    ''' what the host was doing at the moment of a call. a host sets
    ''' <see cref="ToolCallLogger.ContextProvider"/> once and every record
    ''' afterwards carries the answer of that delegate.
    ''' </summary>
    Public Class ToolCallContext

        ''' <summary>the task / job the host is working on, e.g. "polish the draft".</summary>
        Public Property Task As String

        ''' <summary>the llm round the call belongs to, when the host counts them.</summary>
        Public Property Round As String

        ''' <summary>anything else the host wants to see in the record.</summary>
        Public Property Extra As Dictionary(Of String, String)

    End Class

    ''' <summary>
    ''' how much of a call the log keeps. the defaults favour completeness:
    ''' a result is inlined in full up to 256 KB and only spilled to its own
    ''' file beyond that.
    ''' </summary>
    Public Class ToolLogOptions

        ''' <summary>False switches every logging off; the tools then run exactly as before.</summary>
        Public Property Enabled As Boolean = True

        ''' <summary>results longer than this are written to a payload file instead of into the json array.</summary>
        Public Property MaxInlineChars As Integer = 262144

        ''' <summary>a single argument value longer than this is cut short (the fact is recorded).</summary>
        Public Property MaxInlineArgumentChars As Integer = 8192

        ''' <summary>characters of a spilled result kept in the record as a preview.</summary>
        Public Property PreviewChars As Integer = 2000

        ''' <summary>write the sha256 of every result, not only of the spilled ones.</summary>
        Public Property HashResults As Boolean = False

        ''' <summary>spill oversized results into the payload folder instead of inlining them anyway.</summary>
        Public Property SpillPayloads As Boolean = True

        ''' <summary>pretty print the json files (a few bytes more, far easier to read).</summary>
        Public Property WriteIndented As Boolean = True

    End Class

    ''' <summary>
    ''' the closing summary of one session: where it ran, what it did and
    ''' everything that went wrong.
    ''' </summary>
    Public Class ToolSessionSummary

        <JsonPropertyName("sessionId")>
        Public Property SessionId As String

        <JsonPropertyName("workspace")>
        Public Property Workspace As String

        <JsonPropertyName("securityPolicy")>
        Public Property SecurityPolicy As String

        <JsonPropertyName("logFolder")>
        Public Property LogFolder As String

        <JsonPropertyName("startedUtc")>
        Public Property StartedUtc As String

        <JsonPropertyName("closedUtc")>
        Public Property ClosedUtc As String

        <JsonPropertyName("durationMs")>
        Public Property DurationMs As Double

        <JsonPropertyName("machine")>
        Public Property Machine As String

        <JsonPropertyName("user")>
        Public Property User As String

        <JsonPropertyName("osVersion")>
        Public Property OsVersion As String

        <JsonPropertyName("processId")>
        Public Property ProcessId As Integer

        <JsonPropertyName("clrVersion")>
        Public Property ClrVersion As String

        <JsonPropertyName("totalCalls")>
        Public Property TotalCalls As Integer

        <JsonPropertyName("totalToolDurationMs")>
        Public Property TotalToolDurationMs As Double

        <JsonPropertyName("callsByStatus")>
        Public Property CallsByStatus As Dictionary(Of String, Integer)

        <JsonPropertyName("callsByTool")>
        Public Property CallsByTool As Dictionary(Of String, Integer)

        <JsonPropertyName("errors")>
        Public Property Errors As List(Of ToolSessionError)

        <JsonPropertyName("registeredTools")>
        Public Property RegisteredTools As Integer

        <JsonPropertyName("permissionEvents")>
        Public Property PermissionEvents As Integer

        <JsonPropertyName("persistent")>
        Public Property Persistent As Boolean

        <JsonPropertyName("lastError")>
        Public Property LastError As String

        <JsonPropertyName("files")>
        Public Property Files As Dictionary(Of String, String)

    End Class

    ''' <summary>one entry of the error list of the session summary.</summary>
    Public Class ToolSessionError

        <JsonPropertyName("seq")>
        Public Property Seq As Integer

        <JsonPropertyName("tool")>
        Public Property Tool As String

        <JsonPropertyName("status")>
        Public Property Status As String

        <JsonPropertyName("message")>
        Public Property Message As String

    End Class

End Namespace
