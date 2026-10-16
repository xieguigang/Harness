Imports System.Collections.Generic
Imports System.Text.Json.Serialization

Namespace Logging

    ''' <summary>
    ''' the vocabulary of the chat session log: every status, source and event
    ''' kind that shows up in the json files is one of these constants.
    ''' </summary>
    Public Module ChatLogText

        ''' <summary>the turn answered with a final response.</summary>
        Public Const StatusOk As String = "ok"
        ''' <summary>the turn ended with an exception that no retry could recover from.</summary>
        Public Const StatusFailed As String = "failed"
        ''' <summary>the turn used up every round without ever producing a final answer.</summary>
        Public Const StatusMaxRounds As String = "max_rounds_exceeded"

        ''' <summary>the tool was executed by the function registry of the client.</summary>
        Public Const SourceRegistry As String = "registry"
        ''' <summary>the tool was executed by the external <c>tool_invoke</c> engine.</summary>
        Public Const SourceExternal As String = "tool_invoke"
        ''' <summary>neither a registered function nor an external engine was available.</summary>
        Public Const SourceMissing As String = "missing"

        ''' <summary>the context window dropped messages.</summary>
        Public Const EventEvict As String = "evict"
        ''' <summary>a request failed; the client retries it in the next round.</summary>
        Public Const EventRetry As String = "retry"
        ''' <summary>a turn ran out of rounds.</summary>
        Public Const EventMaxRounds As String = "max_rounds"
        ''' <summary>anything else worth seeing in the session summary.</summary>
        Public Const EventNote As String = "note"

    End Module

    ''' <summary>
    ''' how much of a session the log keeps. the defaults favour completeness:
    ''' every message, the whole reasoning and the whole answer are written
    ''' out, and only a text beyond <see cref="MaxInlineChars"/> is moved into
    ''' its own payload file.
    ''' </summary>
    Public Class ChatSessionLogOptions

        ''' <summary>False freezes the log: everything is still measured but nothing is written.</summary>
        Public Property Enabled As Boolean = True

        ''' <summary>a text longer than this is written to a payload file instead of into the json array.</summary>
        Public Property MaxInlineChars As Integer = 262144

        ''' <summary>a single message of a request longer than this is cut short and spilled.</summary>
        Public Property MaxInlineMessageChars As Integer = 32768

        ''' <summary>characters of a spilled text kept in the record as a preview.</summary>
        Public Property PreviewChars As Integer = 2000

        ''' <summary>write out the complete message list of every request, not only its size.</summary>
        Public Property CaptureRequestMessages As Boolean = True

        ''' <summary>write out every single stream chunk; off by default, it is a debugging aid only.</summary>
        Public Property CaptureChunks As Boolean = False

        ''' <summary>move oversized texts into the payload folder instead of inlining them anyway.</summary>
        Public Property SpillPayloads As Boolean = True

        ''' <summary>pretty print the json files: a few bytes more, far easier to read.</summary>
        Public Property WriteIndented As Boolean = True

    End Class

    ''' <summary>
    ''' one text of a session: a prompt, a reasoning, an answer, a tool
    ''' result. it is inlined while it is short enough and spilled into the
    ''' payload folder beyond that; either way its length, its hash and - when
    ''' it was spilled - a preview stay in the record.
    ''' </summary>
    Public Class LoggedText

        ''' <summary>the complete text, or Nothing when it was spilled.</summary>
        <JsonPropertyName("text")>
        Public Property Text As String

        ''' <summary>length of the complete text in characters.</summary>
        <JsonPropertyName("length")>
        Public Property Length As Integer

        ''' <summary>True when the text was moved into a payload file.</summary>
        <JsonPropertyName("truncated")>
        Public Property Truncated As Boolean

        ''' <summary>the first characters of a spilled text.</summary>
        <JsonPropertyName("preview")>
        Public Property Preview As String

        ''' <summary>sha256 of the complete text; filled when it was spilled or when hashing is on.</summary>
        <JsonPropertyName("sha256")>
        Public Property Sha256 As String

        ''' <summary>session relative path of the payload file holding the complete text.</summary>
        <JsonPropertyName("payloadFile")>
        Public Property PayloadFile As String

    End Class

    ''' <summary>
    ''' a tool call the model asked for, exactly as it came out of the
    ''' response: its id, its name and the arguments it carried.
    ''' </summary>
    Public Class LoggedToolCallRequest

        ''' <summary>the id the provider gave the call; Nothing for a DSML call.</summary>
        <JsonPropertyName("id")>
        Public Property Id As String

        ''' <summary>the name of the function the model wants to run.</summary>
        <JsonPropertyName("name")>
        Public Property Name As String

        ''' <summary>the arguments of the call, verbatim.</summary>
        <JsonPropertyName("arguments")>
        Public Property Arguments As Dictionary(Of String, String)

        ''' <summary>True when the call was parsed out of a DSML text block instead of a tool call frame.</summary>
        <JsonPropertyName("dsmlLeak")>
        Public Property DsmlLeak As Boolean

    End Class

    ''' <summary>
    ''' one message of the conversation, in the order it entered the session.
    ''' the complete list is the dialogue ledger a reviewer reads top to
    ''' bottom.
    ''' </summary>
    Public Class ChatMessageRecord

        ''' <summary>monotonic sequence number inside the session.</summary>
        <JsonPropertyName("seq")>
        Public Property Seq As Integer

        ''' <summary>the turn the message belongs to.</summary>
        <JsonPropertyName("turnSeq")>
        Public Property TurnSeq As Integer

        ''' <summary>the round the message belongs to.</summary>
        <JsonPropertyName("roundSeq")>
        Public Property RoundSeq As Integer

        ''' <summary>system, user, assistant or tool.</summary>
        <JsonPropertyName("role")>
        Public Property Role As String

        ''' <summary>the body of the message.</summary>
        <JsonPropertyName("content")>
        Public Property Content As LoggedText

        ''' <summary>the tool calls an assistant message asked for.</summary>
        <JsonPropertyName("toolCalls")>
        Public Property ToolCalls As List(Of LoggedToolCallRequest)

        ''' <summary>the id of the call a tool message answers.</summary>
        <JsonPropertyName("toolCallId")>
        Public Property ToolCallId As String

        ''' <summary>when the message was recorded, ISO-8601 utc.</summary>
        <JsonPropertyName("timestampUtc")>
        Public Property TimestampUtc As String

    End Class

    ''' <summary>
    ''' the token usage of one request, with the totals of the session next to
    ''' it so a single line already shows how a session spends its budget.
    ''' </summary>
    Public Class ChatUsageSnapshot

        ''' <summary>monotonic sequence number inside the session.</summary>
        <JsonPropertyName("seq")>
        Public Property Seq As Integer

        ''' <summary>the turn the request belongs to.</summary>
        <JsonPropertyName("turnSeq")>
        Public Property TurnSeq As Integer

        ''' <summary>the round the request belongs to.</summary>
        <JsonPropertyName("roundSeq")>
        Public Property RoundSeq As Integer

        ''' <summary>when the usage was reported, ISO-8601 utc.</summary>
        <JsonPropertyName("timestampUtc")>
        Public Property TimestampUtc As String

        ''' <summary>the model the request was sent to.</summary>
        <JsonPropertyName("model")>
        Public Property Model As String

        ''' <summary>input tokens of this request.</summary>
        <JsonPropertyName("promptTokens")>
        Public Property PromptTokens As Long

        ''' <summary>output tokens of this request.</summary>
        <JsonPropertyName("completionTokens")>
        Public Property CompletionTokens As Long

        ''' <summary>input tokens served from the kv cache; 0 when the backend does not report it.</summary>
        <JsonPropertyName("cacheHitTokens")>
        Public Property CacheHitTokens As Long

        ''' <summary>input tokens that missed the kv cache; 0 when the backend does not report it.</summary>
        <JsonPropertyName("cacheMissTokens")>
        Public Property CacheMissTokens As Long

        ''' <summary>hit / (hit + miss) of this request.</summary>
        <JsonPropertyName("hitRate")>
        Public Property HitRate As Double

        ''' <summary>input tokens of the whole session so far.</summary>
        <JsonPropertyName("cumulativePromptTokens")>
        Public Property CumulativePromptTokens As Long

        ''' <summary>output tokens of the whole session so far.</summary>
        <JsonPropertyName("cumulativeCompletionTokens")>
        Public Property CumulativeCompletionTokens As Long

    End Class

    ''' <summary>
    ''' one executed tool call: what the model asked for, how long it took and
    ''' what it handed back.
    ''' </summary>
    Public Class ChatToolCallRecord

        ''' <summary>monotonic sequence number inside the session.</summary>
        <JsonPropertyName("seq")>
        Public Property Seq As Integer

        ''' <summary>the turn the call belongs to.</summary>
        <JsonPropertyName("turnSeq")>
        Public Property TurnSeq As Integer

        ''' <summary>the round the call belongs to.</summary>
        <JsonPropertyName("roundSeq")>
        Public Property RoundSeq As Integer

        ''' <summary>the name of the tool.</summary>
        <JsonPropertyName("name")>
        Public Property Name As String

        ''' <summary>the id the provider gave the call.</summary>
        <JsonPropertyName("callId")>
        Public Property CallId As String

        ''' <summary>the arguments of the call.</summary>
        <JsonPropertyName("arguments")>
        Public Property Arguments As Dictionary(Of String, String)

        ''' <summary>what the tool handed back to the model.</summary>
        <JsonPropertyName("result")>
        Public Property Result As LoggedText

        ''' <summary>how long the tool itself needed, in milliseconds.</summary>
        <JsonPropertyName("durationMs")>
        Public Property DurationMs As Double

        ''' <summary>"registry", "tool_invoke" or "missing".</summary>
        <JsonPropertyName("source")>
        Public Property Source As String

        ''' <summary>True when the result was wrapped into a &lt;tool_result&gt; block for a DSML backend.</summary>
        <JsonPropertyName("dsmlWrapped")>
        Public Property DsmlWrapped As Boolean

        ''' <summary>"ok" or "failed".</summary>
        <JsonPropertyName("status")>
        Public Property Status As String

        ''' <summary>exception type, when the tool threw.</summary>
        <JsonPropertyName("errorType")>
        Public Property ErrorType As String

        ''' <summary>exception message, when the tool threw.</summary>
        <JsonPropertyName("errorMessage")>
        Public Property ErrorMessage As String

        ''' <summary>exception stack trace, when the tool threw.</summary>
        <JsonPropertyName("errorStackTrace")>
        Public Property ErrorStackTrace As String

        ''' <summary>when the call started, ISO-8601 utc.</summary>
        <JsonPropertyName("timestampUtc")>
        Public Property TimestampUtc As String

    End Class

    ''' <summary>
    ''' one request to the model: what was sent, what came back, how fast the
    ''' first token arrived and what the request cost.
    ''' </summary>
    Public Class ChatRoundRecord

        ''' <summary>sequence number of the round inside its turn.</summary>
        <JsonPropertyName("seq")>
        Public Property Seq As Integer

        ''' <summary>the turn this round belongs to.</summary>
        <JsonPropertyName("turnSeq")>
        Public Property TurnSeq As Integer

        ''' <summary>when the request was sent, ISO-8601 utc.</summary>
        <JsonPropertyName("timestampUtc")>
        Public Property TimestampUtc As String

        ''' <summary>the complete message list that was sent to the model.</summary>
        <JsonPropertyName("requestMessages")>
        Public Property RequestMessages As List(Of ChatMessageRecord)

        ''' <summary>how many messages the request carried.</summary>
        <JsonPropertyName("requestMessageCount")>
        Public Property RequestMessageCount As Integer

        ''' <summary>characters of the request body, roughly.</summary>
        <JsonPropertyName("requestChars")>
        Public Property RequestChars As Long

        ''' <summary>milliseconds until the first chunk arrived.</summary>
        <JsonPropertyName("firstTokenMs")>
        Public Property FirstTokenMs As Double

        ''' <summary>milliseconds the stream itself needed.</summary>
        <JsonPropertyName("streamMs")>
        Public Property StreamMs As Double

        ''' <summary>how many chunks the stream delivered.</summary>
        <JsonPropertyName("chunkCount")>
        Public Property ChunkCount As Integer

        ''' <summary>the reasoning of this round.</summary>
        <JsonPropertyName("think")>
        Public Property Think As LoggedText

        ''' <summary>the answer text of this round.</summary>
        <JsonPropertyName("output")>
        Public Property Output As LoggedText

        ''' <summary>the tool calls the model asked for in this round.</summary>
        <JsonPropertyName("requestedToolCalls")>
        Public Property RequestedToolCalls As List(Of LoggedToolCallRequest)

        ''' <summary>True when the tool calls were parsed out of a DSML text block.</summary>
        <JsonPropertyName("dsmlFallback")>
        Public Property DsmlFallback As Boolean

        ''' <summary>the usage the backend reported for this request.</summary>
        <JsonPropertyName("usage")>
        Public Property Usage As ChatUsageSnapshot

        ''' <summary>"ok" or "failed".</summary>
        <JsonPropertyName("status")>
        Public Property Status As String

        ''' <summary>why the round failed, when it did.</summary>
        <JsonPropertyName("errorMessage")>
        Public Property ErrorMessage As String

        ''' <summary>total milliseconds of the round.</summary>
        <JsonPropertyName("durationMs")>
        Public Property DurationMs As Double

        ''' <summary>the single chunks of the stream; only filled with <see cref="ChatSessionLogOptions.CaptureChunks"/>.</summary>
        <JsonPropertyName("chunks")>
        Public Property Chunks As List(Of String)

    End Class

    ''' <summary>
    ''' one <c>Chat</c> call: the question of the user and everything the
    ''' client did to answer it.
    ''' </summary>
    Public Class ChatTurnRecord

        ''' <summary>monotonic sequence number inside the session.</summary>
        <JsonPropertyName("seq")>
        Public Property Seq As Integer

        ''' <summary>the session this turn belongs to.</summary>
        <JsonPropertyName("sessionId")>
        Public Property SessionId As String

        ''' <summary>when the turn started, ISO-8601 utc.</summary>
        <JsonPropertyName("timestampUtc")>
        Public Property TimestampUtc As String

        ''' <summary>the question of the user.</summary>
        <JsonPropertyName("prompt")>
        Public Property Prompt As LoggedText

        ''' <summary>the model the turn was sent to.</summary>
        <JsonPropertyName("model")>
        Public Property Model As String

        ''' <summary>the sampling temperature of the request.</summary>
        <JsonPropertyName("temperature")>
        Public Property Temperature As Double?

        ''' <summary>how many rounds the turn was allowed to use.</summary>
        <JsonPropertyName("maxRounds")>
        Public Property MaxRounds As Integer

        ''' <summary>the tools that were exported to the model for this turn.</summary>
        <JsonPropertyName("tools")>
        Public Property Tools As String()

        ''' <summary>how many messages the context held when the turn started.</summary>
        <JsonPropertyName("contextMessages")>
        Public Property ContextMessages As Integer

        ''' <summary>the rounds of the turn, in order.</summary>
        <JsonPropertyName("rounds")>
        Public Property Rounds As List(Of ChatRoundRecord)

        ''' <summary>how many rounds the turn needed.</summary>
        <JsonPropertyName("roundCount")>
        Public Property RoundCount As Integer

        ''' <summary>how many tool calls the turn executed.</summary>
        <JsonPropertyName("toolCallCount")>
        Public Property ToolCallCount As Integer

        ''' <summary>the complete reasoning of the answer.</summary>
        <JsonPropertyName("think")>
        Public Property Think As LoggedText

        ''' <summary>the answer the model produced.</summary>
        <JsonPropertyName("output")>
        Public Property Output As LoggedText

        ''' <summary>"ok", "failed" or "max_rounds_exceeded".</summary>
        <JsonPropertyName("status")>
        Public Property Status As String

        ''' <summary>input tokens the turn spent.</summary>
        <JsonPropertyName("promptTokens")>
        Public Property PromptTokens As Long

        ''' <summary>output tokens the turn spent.</summary>
        <JsonPropertyName("completionTokens")>
        Public Property CompletionTokens As Long

        ''' <summary>total milliseconds of the turn.</summary>
        <JsonPropertyName("durationMs")>
        Public Property DurationMs As Double

        ''' <summary>the failures the turn had to retry.</summary>
        <JsonPropertyName("errors")>
        Public Property Errors As List(Of ChatSessionEvent)

        ''' <summary>the messages the turn added to the conversation.</summary>
        <JsonPropertyName("messages")>
        Public Property Messages As List(Of ChatMessageRecord)

    End Class

    ''' <summary>
    ''' something worth reporting in the session summary: a failure the client
    ''' retried, a context window that dropped messages, a turn that ran out
    ''' of rounds.
    ''' </summary>
    Public Class ChatSessionEvent

        ''' <summary>monotonic sequence number inside the session.</summary>
        <JsonPropertyName("seq")>
        Public Property Seq As Integer

        ''' <summary>when it happened, ISO-8601 utc.</summary>
        <JsonPropertyName("timestampUtc")>
        Public Property TimestampUtc As String

        ''' <summary>"retry", "evict", "max_rounds" or "note".</summary>
        <JsonPropertyName("kind")>
        Public Property Kind As String

        ''' <summary>one line describing it.</summary>
        <JsonPropertyName("summary")>
        Public Property Summary As String

        ''' <summary>the longer text: an exception message, the dropped messages.</summary>
        <JsonPropertyName("detail")>
        Public Property Detail As LoggedText

    End Class

    ''' <summary>
    ''' the closing summary of a session: what was configured, what ran, what
    ''' it cost and everything that went wrong.
    ''' </summary>
    Public Class ChatSessionRecord

        ''' <summary>the id of the session; also the name of its log folder.</summary>
        <JsonPropertyName("sessionId")>
        Public Property SessionId As String

        ''' <summary>the model the client talks to.</summary>
        <JsonPropertyName("model")>
        Public Property Model As String

        ''' <summary>the endpoint of the provider.</summary>
        <JsonPropertyName("endpoint")>
        Public Property Endpoint As String

        ''' <summary>the system prompt in force.</summary>
        <JsonPropertyName("systemMessage")>
        Public Property SystemMessage As LoggedText

        ''' <summary>the sampling temperature.</summary>
        <JsonPropertyName("temperature")>
        Public Property Temperature As Double?

        ''' <summary>how many rounds a single turn may use.</summary>
        <JsonPropertyName("maxRounds")>
        Public Property MaxRounds As Integer

        ''' <summary>whether the client keeps the dialogue in its context memory.</summary>
        <JsonPropertyName("preserveMemory")>
        Public Property PreserveMemory As Boolean

        ''' <summary>when the session started, ISO-8601 utc.</summary>
        <JsonPropertyName("startedUtc")>
        Public Property StartedUtc As String

        ''' <summary>when the session was closed, ISO-8601 utc.</summary>
        <JsonPropertyName("closedUtc")>
        Public Property ClosedUtc As String

        ''' <summary>milliseconds between start and close.</summary>
        <JsonPropertyName("durationMs")>
        Public Property DurationMs As Double

        ''' <summary>the machine the session ran on.</summary>
        <JsonPropertyName("machine")>
        Public Property Machine As String

        ''' <summary>the user the session ran as.</summary>
        <JsonPropertyName("user")>
        Public Property User As String

        ''' <summary>the operating system.</summary>
        <JsonPropertyName("osVersion")>
        Public Property OsVersion As String

        ''' <summary>the process that hosted the session.</summary>
        <JsonPropertyName("processId")>
        Public Property ProcessId As Integer

        ''' <summary>the runtime version.</summary>
        <JsonPropertyName("clrVersion")>
        Public Property ClrVersion As String

        ''' <summary>how many turns the session served.</summary>
        <JsonPropertyName("turns")>
        Public Property Turns As Integer

        ''' <summary>how many requests to the model the session made.</summary>
        <JsonPropertyName("rounds")>
        Public Property Rounds As Integer

        ''' <summary>how many tool calls the session executed.</summary>
        <JsonPropertyName("toolCalls")>
        Public Property ToolCalls As Integer

        ''' <summary>input tokens of the whole session.</summary>
        <JsonPropertyName("promptTokens")>
        Public Property PromptTokens As Long

        ''' <summary>output tokens of the whole session.</summary>
        <JsonPropertyName("completionTokens")>
        Public Property CompletionTokens As Long

        ''' <summary>input tokens served from the kv cache.</summary>
        <JsonPropertyName("cacheHitTokens")>
        Public Property CacheHitTokens As Long

        ''' <summary>input tokens that missed the kv cache.</summary>
        <JsonPropertyName("cacheMissTokens")>
        Public Property CacheMissTokens As Long

        ''' <summary>the tool log folder of the harness, when the two logs share a session id.</summary>
        <JsonPropertyName("harnessLogFolder")>
        Public Property HarnessLogFolder As String

        ''' <summary>the events of the session, in order.</summary>
        <JsonPropertyName("events")>
        Public Property Events As List(Of ChatSessionEvent)

        ''' <summary>the log files of the session.</summary>
        <JsonPropertyName("files")>
        Public Property Files As Dictionary(Of String, String)

        ''' <summary>False when the log could not reach the disk.</summary>
        <JsonPropertyName("persistent")>
        Public Property Persistent As Boolean

        ''' <summary>why the log stopped reaching the disk, when it did.</summary>
        <JsonPropertyName("lastError")>
        Public Property LastError As String

    End Class

End Namespace
