Imports System.Diagnostics
Imports System.IO
Imports System.Runtime.ExceptionServices
Imports System.Text
Imports System.Text.Encodings.Web
Imports System.Text.Json
Imports System.Text.Json.Serialization
Imports System.Threading

Namespace Logging

    ''' <summary>
    ''' the structured chat log of one <see cref="LLMClient"/> instance.
    ''' 
    ''' the logger never talks to a model itself: the client hands it a
    ''' question when one arrives (<see cref="BeginTurn"/>), a request when it
    ''' is sent (<see cref="BeginRound"/>), every tool call around its
    ''' execution (<see cref="LogToolCall"/>) and every usage report
    ''' (<see cref="LogUsage"/>), and the logger writes what it saw into
    ''' 
    ''' <c>&lt;logFolder&gt;\&lt;session&gt;\</c>:
    ''' <list>
    '''   <item><c>session.json</c> - what the session was and what it did (rewritten as it closes)</item>
    '''   <item><c>turns.json</c> - one json object per <c>Chat</c> call, with its rounds nested</item>
    '''   <item><c>tool-calls.json</c> - one json object per executed tool call</item>
    '''   <item><c>usage.json</c> - one json object per token usage report</item>
    '''   <item><c>messages.json</c> - every message that entered the conversation, in order</item>
    '''   <item><c>payloads\*.txt</c> - texts too long to inline</item>
    ''' </list>
    ''' 
    ''' every array file is appended in constant time and is a valid json array
    ''' at any moment, so a killed process leaves a readable log behind. a
    ''' failure to create the folder or to write a record only degrades the log
    ''' to an in memory one (<see cref="Persistent"/> turns False) - it never
    ''' fails a chat.
    ''' </summary>
    Public Class ChatSessionLogger

        Private ReadOnly _root As String
        Private ReadOnly _options As ChatSessionLogOptions
        Private ReadOnly _model As String
        Private ReadOnly _endpoint As String
        Private ReadOnly _startedUtc As DateTimeOffset
        Private ReadOnly _stopwatch As Stopwatch
        Private ReadOnly _lock As New Object

        Private ReadOnly _turns As New List(Of ChatTurnRecord)
        Private ReadOnly _toolCalls As New List(Of ChatToolCallRecord)
        Private ReadOnly _usages As New List(Of ChatUsageSnapshot)
        Private ReadOnly _messages As New List(Of ChatMessageRecord)
        Private ReadOnly _events As New List(Of ChatSessionEvent)

        Private _sessionId As String
        Private _folder As String
        Private _systemMessage As String
        Private _temperature As Double?
        Private _maxRounds As Integer
        Private _preserveMemory As Boolean = True

        Private _turnFile As String
        Private _toolFile As String
        Private _usageFile As String
        Private _messageFile As String
        Private _sessionFile As String
        Private _payloadFolder As String

        Private _turnWriter As JsonArrayAppender
        Private _toolWriter As JsonArrayAppender
        Private _usageWriter As JsonArrayAppender
        Private _messageWriter As JsonArrayAppender

        Private _folderReady As Boolean
        Private _lastError As String
        Private _closed As Boolean
        Private _payloadSeq As Integer
        Private _turnSeq As Integer
        Private _toolSeq As Integer
        Private _usageSeq As Integer
        Private _messageSeq As Integer
        Private _eventSeq As Integer
        Private _roundCount As Integer
        Private _promptTokens As Long
        Private _completionTokens As Long
        Private _cacheHitTokens As Long
        Private _cacheMissTokens As Long

        ''' <param name="logFolder">the folder the session folder is created in.</param>
        ''' <param name="model">the model the client talks to.</param>
        ''' <param name="endpoint">the endpoint of the provider.</param>
        ''' <param name="options">how much detail to keep; the defaults keep everything.</param>
        ''' <param name="sessionId">the id of the session; generated when omitted. a host may hand in the id of its own log so the two can be read side by side.</param>
        Public Sub New(logFolder As String,
                       model As String,
                       Optional endpoint As String = Nothing,
                       Optional options As ChatSessionLogOptions = Nothing,
                       Optional sessionId As String = Nothing)

            If String.IsNullOrWhiteSpace(logFolder) Then
                Throw New ArgumentNullException(NameOf(logFolder))
            End If

            _root = IO.Path.GetFullPath(logFolder)
            _model = model
            _endpoint = endpoint
            _options = If(options, New ChatSessionLogOptions())
            _sessionId = If(String.IsNullOrWhiteSpace(sessionId), MakeSessionId(), sessionId)
            _startedUtc = DateTimeOffset.UtcNow
            _stopwatch = Stopwatch.StartNew()
        End Sub

        ' ====================================================================
        ' the surface a host uses
        ' ====================================================================

        ''' <summary>
        ''' the id of the session; also the name of its folder. a host may set
        ''' it once, before the first record is written, to make this log share
        ''' the id of its own - the harness does that so its tool log and this
        ''' chat log can be read side by side.
        ''' </summary>
        Public Property SessionId As String
            Get
                Return _sessionId
            End Get
            Set(value As String)
                If String.IsNullOrWhiteSpace(value) OrElse _folderReady Then
                    Return
                End If

                _sessionId = value
                _folder = Nothing
            End Set
        End Property

        ''' <summary>the folder the files of this session live in.</summary>
        Public ReadOnly Property Folder As String
            Get
                Call EnsurePaths()

                Return _folder
            End Get
        End Property

        ''' <summary>
        ''' derive the file names of the session once. the id of the session
        ''' may still change before the first record is written, so the paths
        ''' are recomputed whenever the folder is asked for.
        ''' </summary>
        Private Sub EnsurePaths()
            If _folder Is Nothing Then
                _folder = IO.Path.Combine(_root, _sessionId)
                _turnFile = IO.Path.Combine(_folder, "turns.json")
                _toolFile = IO.Path.Combine(_folder, "tool-calls.json")
                _usageFile = IO.Path.Combine(_folder, "usage.json")
                _messageFile = IO.Path.Combine(_folder, "messages.json")
                _sessionFile = IO.Path.Combine(_folder, "session.json")
                _payloadFolder = IO.Path.Combine(_folder, "payloads")
            End If
        End Sub

        ''' <summary>the json array file holding one object per chat call.</summary>
        Public ReadOnly Property TurnsFile As String
            Get
                Return IO.Path.Combine(Folder, "turns.json")
            End Get
        End Property

        ''' <summary>the json array file holding one object per tool call.</summary>
        Public ReadOnly Property ToolCallsFile As String
            Get
                Return IO.Path.Combine(Folder, "tool-calls.json")
            End Get
        End Property

        ''' <summary>the json array file holding one object per usage report.</summary>
        Public ReadOnly Property UsageFile As String
            Get
                Return IO.Path.Combine(Folder, "usage.json")
            End Get
        End Property

        ''' <summary>the json array file holding the messages of the conversation.</summary>
        Public ReadOnly Property MessagesFile As String
            Get
                Return IO.Path.Combine(Folder, "messages.json")
            End Get
        End Property

        ''' <summary>the summary file of the session.</summary>
        Public ReadOnly Property SessionFile As String
            Get
                Return IO.Path.Combine(Folder, "session.json")
            End Get
        End Property

        ''' <summary>the tuning of this logger; mutable so a host can widen it before the first call.</summary>
        Public ReadOnly Property Options As ChatSessionLogOptions
            Get
                Return _options
            End Get
        End Property

        ''' <summary>
        ''' True once the session folder exists and records reach the disk.
        ''' False means the log is memory only.
        ''' </summary>
        Public ReadOnly Property Persistent As Boolean
            Get
                Return _folderReady AndAlso _turnWriter IsNot Nothing AndAlso Not _turnWriter.Broken
            End Get
        End Property

        ''' <summary>the reason the log stopped reaching the disk, when it did.</summary>
        Public ReadOnly Property LastError As String
            Get
                Return _lastError
            End Get
        End Property

        ''' <summary>
        ''' the folder of the tool call log that shares this session id, so a
        ''' reader of one log finds the other.
        ''' </summary>
        Public Property HarnessLogFolder As String

        ''' <summary>every turn recorded so far, in order.</summary>
        Public ReadOnly Property Turns As IReadOnlyList(Of ChatTurnRecord)
            Get
                SyncLock _lock
                    Return _turns.ToArray()
                End SyncLock
            End Get
        End Property

        ''' <summary>every tool call recorded so far, in order.</summary>
        Public ReadOnly Property ToolCalls As IReadOnlyList(Of ChatToolCallRecord)
            Get
                SyncLock _lock
                    Return _toolCalls.ToArray()
                End SyncLock
            End Get
        End Property

        ''' <summary>every usage report recorded so far, in order.</summary>
        Public ReadOnly Property Usages As IReadOnlyList(Of ChatUsageSnapshot)
            Get
                SyncLock _lock
                    Return _usages.ToArray()
                End SyncLock
            End Get
        End Property

        ''' <summary>every message recorded so far, in order.</summary>
        Public ReadOnly Property Messages As IReadOnlyList(Of ChatMessageRecord)
            Get
                SyncLock _lock
                    Return _messages.ToArray()
                End SyncLock
            End Get
        End Property

        ''' <summary>every event recorded so far, in order.</summary>
        Public ReadOnly Property Events As IReadOnlyList(Of ChatSessionEvent)
            Get
                SyncLock _lock
                    Return _events.ToArray()
                End SyncLock
            End Get
        End Property

        ''' <summary>
        ''' hand the configuration of the client to the log, so session.json
        ''' says under which settings the conversation ran.
        ''' </summary>
        ''' <param name="systemMessage">the system prompt in force.</param>
        ''' <param name="temperature">the sampling temperature.</param>
        ''' <param name="maxRounds">how many rounds one turn may use.</param>
        ''' <param name="preserveMemory">whether the client keeps the dialogue in its context memory.</param>
        ''' <returns>this logger, so the call can be chained.</returns>
        Public Function Configure(Optional systemMessage As String = Nothing,
                                  Optional temperature As Double? = Nothing,
                                  Optional maxRounds As Integer = -1,
                                  Optional preserveMemory As Boolean = True) As ChatSessionLogger

            _systemMessage = systemMessage
            _temperature = temperature
            _maxRounds = maxRounds
            _preserveMemory = preserveMemory

            Return Me
        End Function

        ' ====================================================================
        ' turns and rounds
        ' ====================================================================

        ''' <summary>
        ''' open the record of one <c>Chat</c> call. the returned object is the
        ''' handle the caller keeps filling until <see cref="EndTurn"/>.
        ''' </summary>
        ''' <param name="prompt">the question of the user.</param>
        ''' <param name="req">the request that was built for it.</param>
        ''' <param name="maxRounds">how many rounds the turn is allowed to use.</param>
        Public Function BeginTurn(prompt As String,
                                  Optional req As ChatRequestOptions = Nothing,
                                  Optional maxRounds As Integer = -1) As ChatTurnRecord

            Dim turn As New ChatTurnRecord With {
                .Seq = Interlocked.Increment(_turnSeq),
                .SessionId = _sessionId,
                .TimestampUtc = DateTimeOffset.UtcNow.ToString("o"),
                .Prompt = Capture(prompt),
                .Model = If(req Is Nothing, _model, req.Model),
                .Temperature = If(req Is Nothing, _temperature, req.Temperature),
                .MaxRounds = If(maxRounds >= 0, maxRounds, _maxRounds),
                .Tools = ToolNames(req),
                .ContextMessages = If(req Is Nothing OrElse req.Messages Is Nothing, 0, req.Messages.Count),
                .Rounds = New List(Of ChatRoundRecord)(),
                .Errors = New List(Of ChatSessionEvent)(),
                .Messages = New List(Of ChatMessageRecord)(),
                .Status = ChatLogText.StatusOk
            }

            SyncLock _lock
                _turns.Add(turn)
            End SyncLock

            Return turn
        End Function

        ''' <summary>
        ''' open the record of one request to the model. the caller fills in
        ''' what it measured - first token delay, chunk count, stream duration
        ''' - and closes it with <see cref="EndRound"/>.
        ''' </summary>
        ''' <param name="turn">the turn the request belongs to.</param>
        ''' <param name="req">the request that is about to be sent.</param>
        Public Function BeginRound(turn As ChatTurnRecord, Optional req As ChatRequestOptions = Nothing) As ChatRoundRecord
            Dim round As New ChatRoundRecord With {
                .Seq = If(turn Is Nothing, 0, turn.Rounds.Count + 1),
                .TurnSeq = If(turn Is Nothing, 0, turn.Seq),
                .TimestampUtc = DateTimeOffset.UtcNow.ToString("o"),
                .Status = ChatLogText.StatusOk
            }

            If req IsNot Nothing AndAlso req.Messages IsNot Nothing Then
                round.RequestMessageCount = req.Messages.Count

                If _options.CaptureRequestMessages Then
                    Dim list As New List(Of ChatMessageRecord)()
                    Dim chars As Long = 0

                    For Each msg As ChatMessage In req.Messages
                        If msg Is Nothing Then
                            Continue For
                        End If

                        Dim record As ChatMessageRecord = MessageRecord(msg, turn, round)
                        list.Add(record)
                        chars += If(record.Content Is Nothing, 0, CLng(record.Content.Length))
                    Next

                    round.RequestMessages = list
                    round.RequestChars = chars
                End If
            End If

            SyncLock _lock
                _roundCount += 1

                If turn IsNot Nothing AndAlso turn.Rounds IsNot Nothing Then
                    turn.Rounds.Add(round)
                End If
            End SyncLock

            Return round
        End Function

        ''' <summary>
        ''' close the record of one request: store what the model said, what it
        ''' cost and whether the round failed.
        ''' </summary>
        ''' <param name="round">the record <see cref="BeginRound"/> returned.</param>
        ''' <param name="think">the reasoning of this round.</param>
        ''' <param name="output">the answer text of this round.</param>
        ''' <param name="errorMessage">why the round failed, when it did.</param>
        Public Sub EndRound(round As ChatRoundRecord,
                            think As String,
                            output As String,
                            Optional errorMessage As String = Nothing)

            If round Is Nothing Then
                Return
            End If

            round.Think = Capture(think)
            round.Output = Capture(output)

            ' the duration of the whole round: the request, the stream and
            ' whatever tools ran in between, measured from the moment the
            ' record was opened
            Dim started As DateTimeOffset

            If DateTimeOffset.TryParse(round.TimestampUtc, Nothing, Globalization.DateTimeStyles.RoundtripKind, started) Then
                round.DurationMs = Math.Round((DateTimeOffset.UtcNow - started).TotalMilliseconds, 3)
            End If

            If errorMessage IsNot Nothing Then
                round.Status = ChatLogText.StatusFailed
                round.ErrorMessage = errorMessage
            End If

            If _options.Enabled AndAlso _options.CaptureChunks AndAlso round.Chunks IsNot Nothing Then
                ' the chunks are already on the record; nothing else to do
            End If
        End Sub

        ''' <summary>
        ''' close the record of one <c>Chat</c> call and write it to
        ''' turns.json.
        ''' </summary>
        ''' <param name="turn">the record <see cref="BeginTurn"/> returned.</param>
        ''' <param name="think">the complete reasoning of the answer.</param>
        ''' <param name="output">the answer the model produced.</param>
        ''' <param name="status">"ok", "failed" or "max_rounds_exceeded".</param>
        Public Sub EndTurn(turn As ChatTurnRecord, think As String, output As String, status As String)
            If turn Is Nothing Then
                Return
            End If

            turn.Think = Capture(think)
            turn.Output = Capture(output)
            turn.Status = If(status, ChatLogText.StatusOk)
            turn.RoundCount = If(turn.Rounds Is Nothing, 0, turn.Rounds.Count)

            Dim prompt As Long = 0
            Dim completion As Long = 0
            Dim calls As Integer = 0

            If turn.Rounds IsNot Nothing Then
                For Each round In turn.Rounds
                    If round.Usage IsNot Nothing Then
                        prompt += round.Usage.PromptTokens
                        completion += round.Usage.CompletionTokens
                    End If
                Next
            End If

            For Each callRecord In _toolCalls
                If callRecord.TurnSeq = turn.Seq Then
                    calls += 1
                End If
            Next

            turn.PromptTokens = prompt
            turn.CompletionTokens = completion
            turn.ToolCallCount = calls

            If Not _options.Enabled Then
                Return
            End If

            Call Append(_turnWriter, _turnFile, turn, "turns")
        End Sub

        ' ====================================================================
        ' tool calls
        ' ====================================================================

        ''' <summary>
        ''' run one tool call and record it: what was asked for, how long it
        ''' took, what it handed back and - when it threw - the exception. the
        ''' result is passed through untouched and a thrown exception is
        ''' rethrown with its original stack, so the log cannot change what the
        ''' model sees.
        ''' </summary>
        ''' <param name="turn">the turn the call belongs to.</param>
        ''' <param name="round">the round the call belongs to.</param>
        ''' <param name="name">the name of the tool.</param>
        ''' <param name="callId">the id the provider gave the call.</param>
        ''' <param name="arguments">the arguments of the call.</param>
        ''' <param name="source">"registry", "tool_invoke" or "missing".</param>
        ''' <param name="dsmlWrapped">True when the result is wrapped into a &lt;tool_result&gt; block.</param>
        ''' <param name="invoke">the real execution: the registered function, the external engine, the missing-tool message.</param>
        ''' <returns>whatever <paramref name="invoke"/> returned.</returns>
        Public Function LogToolCall(turn As ChatTurnRecord,
                                    round As ChatRoundRecord,
                                    name As String,
                                    callId As String,
                                    arguments As Dictionary(Of String, String),
                                    source As String,
                                    dsmlWrapped As Boolean,
                                    invoke As Func(Of String)) As String

            If invoke Is Nothing Then
                Return Nothing
            End If

            Dim record As New ChatToolCallRecord With {
                .Seq = Interlocked.Increment(_toolSeq),
                .TurnSeq = If(turn Is Nothing, 0, turn.Seq),
                .RoundSeq = If(round Is Nothing, 0, round.Seq),
                .Name = name,
                .CallId = callId,
                .Arguments = CopyArguments(arguments),
                .Source = source,
                .DsmlWrapped = dsmlWrapped,
                .TimestampUtc = DateTimeOffset.UtcNow.ToString("o"),
                .Status = ChatLogText.StatusOk
            }

            Dim watch As Stopwatch = Stopwatch.StartNew()
            Dim result As String = Nothing
            Dim thrown As Exception = Nothing

            Try
                result = invoke()
            Catch ex As Exception
                thrown = ex
            End Try

            watch.Stop()

            record.DurationMs = Math.Round(watch.Elapsed.TotalMilliseconds, 3)
            record.Result = Capture(result)

            If thrown IsNot Nothing Then
                Dim real As Exception = thrown

                If TypeOf thrown Is Reflection.TargetInvocationException AndAlso thrown.InnerException IsNot Nothing Then
                    real = thrown.InnerException
                End If

                record.Status = ChatLogText.StatusFailed
                record.ErrorType = real.GetType().FullName
                record.ErrorMessage = real.Message
                record.ErrorStackTrace = real.StackTrace
            End If

            SyncLock _lock
                _toolCalls.Add(record)
            End SyncLock

            If _options.Enabled Then
                Call Append(_toolWriter, _toolFile, record, "tool-calls")
            End If

            If thrown IsNot Nothing Then
                Call ExceptionDispatchInfo.Capture(thrown).Throw()
            End If

            Return result
        End Function

        ' ====================================================================
        ' usage, messages and events
        ' ====================================================================

        ''' <summary>
        ''' record the token usage a backend reported for one request, next to
        ''' the totals of the session so far.
        ''' </summary>
        ''' <param name="usage">the usage the provider reported.</param>
        ''' <param name="turn">the turn the request belongs to.</param>
        ''' <param name="round">the round the request belongs to; the snapshot is stored on it as well.</param>
        Public Sub LogUsage(usage As ChatUsage, Optional turn As ChatTurnRecord = Nothing, Optional round As ChatRoundRecord = Nothing)
            If usage Is Nothing Then
                Return
            End If

            Dim hit As Long = If(usage.CacheHitTokens, 0L)
            Dim miss As Long = If(usage.CacheMissTokens, 0L)

            SyncLock _lock
                _promptTokens += usage.PromptTokens
                _completionTokens += usage.CompletionTokens
                _cacheHitTokens += hit
                _cacheMissTokens += miss
            End SyncLock

            Dim snapshot As New ChatUsageSnapshot With {
                .Seq = Interlocked.Increment(_usageSeq),
                .TurnSeq = If(turn Is Nothing, 0, turn.Seq),
                .RoundSeq = If(round Is Nothing, 0, round.Seq),
                .TimestampUtc = DateTimeOffset.UtcNow.ToString("o"),
                .Model = _model,
                .PromptTokens = usage.PromptTokens,
                .CompletionTokens = usage.CompletionTokens,
                .CacheHitTokens = hit,
                .CacheMissTokens = miss,
                .HitRate = If(hit + miss > 0, hit / (hit + miss), 0.0R),
                .CumulativePromptTokens = _promptTokens,
                .CumulativeCompletionTokens = _completionTokens
            }

            SyncLock _lock
                _usages.Add(snapshot)
            End SyncLock

            If round IsNot Nothing Then
                round.Usage = snapshot
            End If

            If _options.Enabled Then
                Call Append(_usageWriter, _usageFile, snapshot, "usage")
            End If
        End Sub

        ''' <summary>
        ''' record one message that entered the conversation.
        ''' </summary>
        ''' <param name="msg">the message.</param>
        ''' <param name="turn">the turn it belongs to.</param>
        ''' <param name="round">the round it belongs to.</param>
        ''' <param name="write">False keeps the message in the memory log only: a request snapshot uses that, the conversation ledger does not.</param>
        Public Sub LogMessage(msg As ChatMessage,
                              Optional turn As ChatTurnRecord = Nothing,
                              Optional round As ChatRoundRecord = Nothing,
                              Optional write As Boolean = True)

            If msg Is Nothing Then
                Return
            End If

            Dim record As ChatMessageRecord = MessageRecord(msg, turn, round)

            If record Is Nothing Then
                Return
            End If

            record.Seq = Interlocked.Increment(_messageSeq)

            SyncLock _lock
                _messages.Add(record)
            End SyncLock

            If turn IsNot Nothing AndAlso turn.Messages IsNot Nothing Then
                turn.Messages.Add(record)
            End If

            If _options.Enabled AndAlso write Then
                Call Append(_messageWriter, _messageFile, record, "messages")
            End If
        End Sub

        ''' <summary>
        ''' record something worth seeing in the session summary: a request the
        ''' client had to retry, a context window that dropped messages, a turn
        ''' that ran out of rounds.
        ''' </summary>
        ''' <param name="kind">"retry", "evict", "max_rounds" or "note".</param>
        ''' <param name="summary">one line describing it.</param>
        ''' <param name="detail">the longer text: an exception message, the dropped messages.</param>
        ''' <param name="turn">the turn it belongs to; a retry is listed on the turn as well.</param>
        Public Sub LogEvent(kind As String,
                            summary As String,
                            Optional detail As String = Nothing,
                            Optional turn As ChatTurnRecord = Nothing)

            Dim record As New ChatSessionEvent With {
                .Seq = Interlocked.Increment(_eventSeq),
                .TimestampUtc = DateTimeOffset.UtcNow.ToString("o"),
                .Kind = kind,
                .Summary = summary,
                .Detail = Capture(detail)
            }

            SyncLock _lock
                _events.Add(record)
            End SyncLock

            If turn IsNot Nothing AndAlso turn.Errors IsNot Nothing Then
                turn.Errors.Add(record)
            End If
        End Sub

        ' ====================================================================
        ' writing
        ' ====================================================================

        ''' <summary>
        ''' create the session folder and open the appenders, once. a failure
        ''' is remembered in <see cref="LastError"/> and degrades the log to
        ''' memory only - it is never thrown at the caller.
        ''' </summary>
        Private Function EnsureFolder() As Boolean
            If _folderReady Then
                Return True
            End If

            SyncLock _lock
                If _folderReady Then
                    Return True
                End If

                Try
                    Call EnsurePaths()

                    If Not Directory.Exists(_folder) Then
                        Call Directory.CreateDirectory(_folder)
                    End If

                    _turnWriter = OpenAppender(_turnFile)
                    _toolWriter = OpenAppender(_toolFile)
                    _usageWriter = OpenAppender(_usageFile)
                    _messageWriter = OpenAppender(_messageFile)

                    If _turnWriter Is Nothing Then
                        _lastError = "cannot open " & _turnFile
                        Return False
                    End If

                    _folderReady = True

                    Call SaveSession()
                Catch ex As Exception
                    _lastError = "cannot create the chat log folder '" & Folder & "': " & ex.Message
                    Return False
                End Try
            End SyncLock

            Return True
        End Function

        Private Function OpenAppender(path As String) As JsonArrayAppender
            Dim writer As New JsonArrayAppender(path)

            If Not writer.Open() Then
                _lastError = "cannot open " & path
                Return Nothing
            End If

            Return writer
        End Function

        ''' <summary>append one record to one of the array files.</summary>
        Private Sub Append(Of T)(ByRef writer As JsonArrayAppender, path As String, record As T, name As String)
            If Not EnsureFolder() Then
                Return
            End If

            If writer Is Nothing Then
                Return
            End If

            Try
                If Not writer.Append(JsonSerializer.Serialize(record, SerializerOptions())) Then
                    _lastError = "cannot append to " & path
                End If
            Catch ex As Exception
                _lastError = "the " & name & " log failed: " & ex.Message
            End Try
        End Sub

        Private Function SerializerOptions() As JsonSerializerOptions
            Return New JsonSerializerOptions With {
                .WriteIndented = _options.WriteIndented,
                .DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                .Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }
        End Function

        ''' <summary>
        ''' write session.json. the client does not have to call it:
        ''' <see cref="Close"/> does it as well.
        ''' </summary>
        Public Sub SaveSession()
            If Not _folderReady Then
                Return
            End If

            Try
                Dim record As ChatSessionRecord

                SyncLock _lock
                    record = New ChatSessionRecord With {
                        .SessionId = _sessionId,
                        .Model = _model,
                        .Endpoint = _endpoint,
                        .SystemMessage = Capture(_systemMessage),
                        .Temperature = _temperature,
                        .MaxRounds = _maxRounds,
                        .PreserveMemory = _preserveMemory,
                        .StartedUtc = _startedUtc.ToString("o"),
                        .ClosedUtc = DateTimeOffset.UtcNow.ToString("o"),
                        .DurationMs = Math.Round(_stopwatch.Elapsed.TotalMilliseconds, 3),
                        .Machine = Environment.MachineName,
                        .User = Environment.UserName,
                        .OsVersion = Environment.OSVersion.ToString(),
                        .ProcessId = Process.GetCurrentProcess().Id,
                        .ClrVersion = Environment.Version.ToString(),
                        .Turns = _turns.Count,
                        .Rounds = _roundCount,
                        .ToolCalls = _toolCalls.Count,
                        .PromptTokens = _promptTokens,
                        .CompletionTokens = _completionTokens,
                        .CacheHitTokens = _cacheHitTokens,
                        .CacheMissTokens = _cacheMissTokens,
                        .HarnessLogFolder = HarnessLogFolder,
                        .Events = _events.ToArray().ToList(),
                        .Persistent = Persistent,
                        .LastError = _lastError,
                        .Files = New Dictionary(Of String, String)(StringComparer.Ordinal) From {
                            {"turns", _turnFile},
                            {"toolCalls", _toolFile},
                            {"usage", _usageFile},
                            {"messages", _messageFile},
                            {"payloads", _payloadFolder}
                        }
                    }
                End SyncLock

                Call File.WriteAllText(_sessionFile, JsonSerializer.Serialize(record, SerializerOptions()), New UTF8Encoding(False))
            Catch ex As Exception
                _lastError = "cannot write " & _sessionFile & ": " & ex.Message
            End Try
        End Sub

        ''' <summary>
        ''' bring the log up to date on disk: every record is flushed the
        ''' moment it is appended, so this only rewrites session.json.
        ''' </summary>
        Public Sub Flush()
            Call SaveSession()
        End Sub

        ''' <summary>
        ''' finish the session: write session.json and release the file
        ''' handles. calling it twice is harmless.
        ''' </summary>
        Public Sub Close()
            If _closed Then
                Return
            End If

            _closed = True

            Call SaveSession()

            If _turnWriter IsNot Nothing Then
                _turnWriter.Close()
            End If
            If _toolWriter IsNot Nothing Then
                _toolWriter.Close()
            End If
            If _usageWriter IsNot Nothing Then
                _usageWriter.Close()
            End If
            If _messageWriter IsNot Nothing Then
                _messageWriter.Close()
            End If
        End Sub

        ' ====================================================================
        ' helpers
        ' ====================================================================

        Private Shared Function MakeSessionId() As String
            Return DateTime.Now.ToString("yyyyMMdd-HHmmss") & "-" & Guid.NewGuid().ToString("N").Substring(0, 6)
        End Function

        Private Shared Function ToolNames(req As ChatRequestOptions) As String()
            If req Is Nothing OrElse req.Tools Is Nothing Then
                Return Nothing
            End If

            Dim names As New List(Of String)()

            For Each tool In req.Tools
                If tool IsNot Nothing AndAlso tool.function IsNot Nothing Then
                    names.Add(tool.function.name)
                End If
            Next

            Return names.ToArray()
        End Function

        Private Shared Function CopyArguments(arguments As Dictionary(Of String, String)) As Dictionary(Of String, String)
            If arguments Is Nothing Then
                Return Nothing
            End If

            Dim copy As New Dictionary(Of String, String)(StringComparer.Ordinal)

            For Each pair In arguments
                copy(pair.Key) = pair.Value
            Next

            Return copy
        End Function

        ''' <summary>
        ''' store a text: inlined in full while it is short enough, spilled
        ''' into its own file beyond that.
        ''' </summary>
        Private Function Capture(text As String, Optional cap As Integer = -1) As LoggedText
            Dim record As New LoggedText With {.Length = If(text Is Nothing, 0, text.Length)}

            If text Is Nothing Then
                Return record
            End If

            Dim limit As Integer = If(cap > 0, cap, _options.MaxInlineChars)

            If _options.SpillPayloads AndAlso text.Length > limit Then
                Dim relative As String = TrySpill(text)

                If relative IsNot Nothing Then
                    record.Truncated = True
                    record.PayloadFile = relative
                    record.Preview = text.Substring(0, Math.Min(_options.PreviewChars, text.Length))
                    record.Sha256 = Sha256Of(text)
                    Return record
                End If
            End If

            record.Text = text

            Return record
        End Function

        Private Function TrySpill(text As String) As String
            Try
                If Not EnsureFolder() Then
                    Return Nothing
                End If

                If Not Directory.Exists(_payloadFolder) Then
                    Call Directory.CreateDirectory(_payloadFolder)
                End If

                Dim seq As Integer

                SyncLock _lock
                    _payloadSeq += 1
                    seq = _payloadSeq
                End SyncLock

                Dim name As String = seq.ToString("0000") & ".txt"

                Call File.WriteAllText(IO.Path.Combine(_payloadFolder, name), text, New UTF8Encoding(False))

                Return "payloads/" & name
            Catch ex As Exception
                _lastError = "payload spill failed: " & ex.Message
                Return Nothing
            End Try
        End Function

        ''' <summary>
        ''' sha256 of a text. the type is fully qualified because a member of
        ''' this class is called Sha256Of and vb resolves names without case
        ''' sensitivity.
        ''' </summary>
        Private Shared Function Sha256Of(text As String) As String
            Using sha As System.Security.Cryptography.SHA256 = System.Security.Cryptography.SHA256.Create()
                Dim hash As Byte() = sha.ComputeHash(Encoding.UTF8.GetBytes(text))
                Dim sb As New StringBuilder(hash.Length * 2)

                For Each b In hash
                    sb.Append(b.ToString("x2"))
                Next

                Return sb.ToString()
            End Using
        End Function

        Private Function MessageRecord(msg As ChatMessage,
                                       turn As ChatTurnRecord,
                                       round As ChatRoundRecord,
                                       Optional cap As Integer = -1) As ChatMessageRecord

            If msg Is Nothing Then
                Return Nothing
            End If

            Dim record As New ChatMessageRecord With {
                .TurnSeq = If(turn Is Nothing, 0, turn.Seq),
                .RoundSeq = If(round Is Nothing, 0, round.Seq),
                .Role = msg.Role,
                .Content = Capture(msg.Content, If(cap > 0, cap, _options.MaxInlineMessageChars)),
                .ToolCallId = msg.ToolCallId,
                .TimestampUtc = DateTimeOffset.UtcNow.ToString("o")
            }

            If msg.ToolCalls IsNot Nothing AndAlso msg.ToolCalls.Length > 0 Then
                Dim calls As New List(Of LoggedToolCallRequest)()

                For Each requested In msg.ToolCalls
                    If requested Is Nothing Then
                        Continue For
                    End If

                    calls.Add(New LoggedToolCallRequest With {
                        .Id = requested.Id,
                        .Name = requested.FunctionName,
                        .Arguments = CopyArguments(requested.FunctionArguments),
                        .DsmlLeak = requested.DeepSeekDSMLLeak
                    })
                Next

                record.ToolCalls = calls
            End If

            Return record
        End Function

    End Class

End Namespace
