Imports System.Diagnostics
Imports System.IO
Imports System.Runtime.ExceptionServices
Imports System.Text
Imports System.Text.Encodings.Web
Imports System.Text.Json
Imports System.Text.Json.Serialization
Imports System.Threading
Imports Ollama.JSON.FunctionCall

Namespace Logging

    ''' <summary>
    ''' the structured tool call log of one sandbox session.
    ''' 
    ''' the logger never runs a tool itself: <see cref="Wrap"/> takes the
    ''' backend delegate a tool was registered with and hands back a delegate
    ''' that times the call, records everything about it and then calls the
    ''' original one. the result - including a thrown exception - is passed
    ''' through untouched, so enabling the log cannot change what the model
    ''' sees.
    ''' 
    ''' everything is written into
    ''' <c>&lt;workspace&gt;\.harness\toolcalls\&lt;session&gt;\</c>:
    ''' <list>
    '''   <item><c>tool-calls.json</c> - one json object per call, in a json array</item>
    '''   <item><c>tools.json</c> - the schema snapshot of every registered tool</item>
    '''   <item><c>permissions.json</c> - every permission request and its answer</item>
    '''   <item><c>session.json</c> - the closing summary of the session</item>
    '''   <item><c>payloads\*.txt</c> - results too long to inline</item>
    ''' </list>
    ''' 
    ''' the logger is thread safe: <see cref="Sandbox.Jobs"/> runs background
    ''' tasks that call the very same tools from other threads. a failure to
    ''' create the folder or to write a record only degrades the log to an
    ''' in memory one (<see cref="Persistent"/> turns False) - it never fails
    ''' a tool call.
    ''' </summary>
    Public Class ToolCallLogger

        ''' <summary>
        ''' append-only writer that keeps its file a valid json array at every
        ''' moment: the array is always closed with ']' and the next record
        ''' overwrites that bracket before appending itself plus a new one. a
        ''' crash therefore leaves a readable, complete file behind instead of
        ''' a truncated one.
        ''' </summary>
        Private NotInheritable Class JsonArrayAppender

            Private ReadOnly _path As String
            Private ReadOnly _sync As New Object()
            Private _stream As FileStream
            Private _tail As Long
            Private _empty As Boolean
            Private _broken As Boolean

            Sub New(path As String)
                _path = path
            End Sub

            Public ReadOnly Property Broken As Boolean
                Get
                    Return _broken
                End Get
            End Property

            Public ReadOnly Property Path As String
                Get
                    Return _path
                End Get
            End Property

            Public Function Open() As Boolean
                Try
                    Dim dir As String = IO.Path.GetDirectoryName(_path)

                    If Not String.IsNullOrEmpty(dir) AndAlso Not Directory.Exists(dir) Then
                        Call Directory.CreateDirectory(dir)
                    End If

                    _stream = New FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read)
                    Call RepairTail()

                    Return True
                Catch ex As Exception
                    _broken = True
                    Return False
                End Try
            End Function

            ''' <summary>
            ''' bring the tail of an existing file into a state the next
            ''' <see cref="Append"/> can continue from: a file left behind by
            ''' a killed process is repaired instead of being corrupted
            ''' further.
            ''' </summary>
            Private Sub RepairTail()
                If _stream.Length = 0 Then
                    Call AppendRaw("[")
                    _tail = 1
                    _empty = True
                    Return
                End If

                Dim last As Integer = ReadByteAt(_stream.Length - 1)

                Select Case last
                    Case AscW("]"c)
                        _tail = _stream.Length - 1
                        _empty = IsBracketAt(_stream.Length - 2)

                    Case AscW("["c)
                        _tail = _stream.Length
                        _empty = True

                    Case AscW(","c), 10, 13, 9, 32
                        ' the process died between a separator and its record
                        Call AppendRaw("null]")
                        _tail = _stream.Length - 1
                        _empty = False

                    Case Else
                        Call AppendRaw("]")
                        _tail = _stream.Length - 1
                        _empty = False
                End Select
            End Sub

            Private Function ReadByteAt(position As Long) As Integer
                If position < 0 OrElse position >= _stream.Length Then
                    Return -1
                End If

                _stream.Position = position

                Return _stream.ReadByte()
            End Function

            Private Function IsBracketAt(position As Long) As Boolean
                Return ReadByteAt(position) = AscW("["c)
            End Function

            Private Sub AppendRaw(text As String)
                _stream.Position = _stream.Length

                Dim bytes As Byte() = Encoding.UTF8.GetBytes(text)

                _stream.Write(bytes, 0, bytes.Length)
                _stream.Flush()
            End Sub

            ''' <summary>
            ''' append one already serialized json value to the array.
            ''' </summary>
            Public Function Append(json As String) As Boolean
                ' a background job calls tools from its own thread, so two
                ' records can be appended at the same time: the seek that
                ' steps back over the closing bracket and the write that
                ' replaces it have to stay one atomic step
                SyncLock _sync
                    If _broken OrElse _stream Is Nothing Then
                        Return False
                    End If

                    Try
                        Dim prefix As String = If(_empty, "", "," & vbLf)

                        _stream.Position = _tail

                        Dim bytes As Byte() = Encoding.UTF8.GetBytes(prefix & json & vbLf & "]")

                        _stream.Write(bytes, 0, bytes.Length)
                        _stream.Flush()

                        _tail = _stream.Length - 1
                        _empty = False

                        Return True
                    Catch ex As Exception
                        _broken = True
                        Return False
                    End Try
                End SyncLock
            End Function

            Public Sub Close()
                SyncLock _sync
                    Call CloseStream()
                End SyncLock
            End Sub

            Private Sub CloseStream()
                If _stream IsNot Nothing Then
                    Try
                        _stream.Flush()
                        _stream.Dispose()
                    Catch
                    End Try

                    _stream = Nothing
                End If
            End Sub

        End Class

        ' ====================================================================
        ' state
        ' ====================================================================

        Private ReadOnly _wsRoot As String
        Private ReadOnly _options As ToolLogOptions
        Private ReadOnly _policy As String
        Private ReadOnly _sessionId As String
        Private ReadOnly _startedUtc As DateTimeOffset
        Private ReadOnly _stopwatch As Stopwatch
        Private ReadOnly _records As New List(Of ToolCallRecord)
        Private ReadOnly _registrations As New List(Of ToolRegistrationRecord)
        Private ReadOnly _permissions As New List(Of ToolPermissionRecord)
        Private ReadOnly _byStatus As New Dictionary(Of String, Integer)(StringComparer.Ordinal)
        Private ReadOnly _byTool As New Dictionary(Of String, Integer)(StringComparer.Ordinal)
        Private ReadOnly _lock As New Object

        Private _seq As Integer
        Private _permissionSeq As Integer
        Private _totalDurationMs As Double

        Private ReadOnly _folder As String
        Private ReadOnly _callFile As String
        Private ReadOnly _toolsFile As String
        Private ReadOnly _permissionFile As String
        Private ReadOnly _sessionFile As String
        Private ReadOnly _payloadFolder As String

        Private _folderReady As Boolean
        Private _calls As JsonArrayAppender
        Private _permissionLog As JsonArrayAppender
        Private _lastError As String
        Private _closed As Boolean

        ''' <summary>result texts that announce a refusal rather than a failure.</summary>
        Private Shared ReadOnly DenialMarks As String() = {
            "permission denied",
            "security policy",
            "outside the sandbox",
            "the user denied",
            "does not permit",
            "escalation"
        }

        ' ====================================================================
        ' construction
        ' ====================================================================

        ''' <param name="workspace">the sandbox workspace root; the log lands in its artifact folder.</param>
        ''' <param name="options">how much detail to keep; the defaults keep everything.</param>
        ''' <param name="securityPolicy">the policy the session runs under, copied into every record.</param>
        Public Sub New(workspace As String, Optional options As ToolLogOptions = Nothing, Optional securityPolicy As String = Nothing)
            If String.IsNullOrWhiteSpace(workspace) Then
                Throw New ArgumentNullException(NameOf(workspace))
            End If

            _wsRoot = IO.Path.GetFullPath(workspace)
            _options = If(options, New ToolLogOptions())
            _policy = securityPolicy
            _sessionId = DateTime.Now.ToString("yyyyMMdd-HHmmss") & "-" & Guid.NewGuid().ToString("N").Substring(0, 6)
            _startedUtc = DateTimeOffset.UtcNow
            _stopwatch = Stopwatch.StartNew()

            _folder = IO.Path.Combine(IO.Path.Combine(IO.Path.Combine(_wsRoot, Sandbox.ArtifactFolder), "toolcalls"), _sessionId)
            _callFile = IO.Path.Combine(_folder, "tool-calls.json")
            _toolsFile = IO.Path.Combine(_folder, "tools.json")
            _permissionFile = IO.Path.Combine(_folder, "permissions.json")
            _sessionFile = IO.Path.Combine(_folder, "session.json")
            _payloadFolder = IO.Path.Combine(_folder, "payloads")
        End Sub

        ' ====================================================================
        ' the surface a host uses
        ' ====================================================================

        ''' <summary>the id of this session; also the name of its log folder.</summary>
        Public ReadOnly Property SessionId As String
            Get
                Return _sessionId
            End Get
        End Property

        ''' <summary>the folder the log files of this session live in.</summary>
        Public ReadOnly Property Folder As String
            Get
                Return _folder
            End Get
        End Property

        ''' <summary>the json array file holding one object per tool call.</summary>
        Public ReadOnly Property ToolCallsFile As String
            Get
                Return _callFile
            End Get
        End Property

        ''' <summary>the json array file holding the schema snapshot of the registered tools.</summary>
        Public ReadOnly Property ToolsFile As String
            Get
                Return _toolsFile
            End Get
        End Property

        ''' <summary>the json array file holding the permission events.</summary>
        Public ReadOnly Property PermissionsFile As String
            Get
                Return _permissionFile
            End Get
        End Property

        ''' <summary>the closing summary of the session.</summary>
        Public ReadOnly Property SessionFile As String
            Get
                Return _sessionFile
            End Get
        End Property

        ''' <summary>the tuning of this logger; mutable so a host can widen it before the first call.</summary>
        Public ReadOnly Property Options As ToolLogOptions
            Get
                Return _options
            End Get
        End Property

        ''' <summary>
        ''' False switches logging off completely: <see cref="Wrap"/> then
        ''' returns the original backend and nothing is written.
        ''' </summary>
        Public Property Enabled As Boolean
            Get
                Return _options.Enabled
            End Get
            Set(value As Boolean)
                _options.Enabled = value
            End Set
        End Property

        ''' <summary>
        ''' True once the log folder exists and records are reaching the disk.
        ''' False means the log is memory only - the directory could not be
        ''' created (a read only workspace, for instance) and the records stay
        ''' available through <see cref="Records"/>.
        ''' </summary>
        Public ReadOnly Property Persistent As Boolean
            Get
                Return _folderReady AndAlso _calls IsNot Nothing AndAlso Not _calls.Broken
            End Get
        End Property

        ''' <summary>the reason the log stopped reaching the disk, when it did.</summary>
        Public ReadOnly Property LastError As String
            Get
                Return _lastError
            End Get
        End Property

        ''' <summary>every call recorded so far, in order.</summary>
        Public ReadOnly Property Records As IReadOnlyList(Of ToolCallRecord)
            Get
                SyncLock _lock
                    Return _records.ToArray()
                End SyncLock
            End Get
        End Property

        ''' <summary>every tool registered through this logger, in registration order.</summary>
        Public ReadOnly Property Registrations As IReadOnlyList(Of ToolRegistrationRecord)
            Get
                SyncLock _lock
                    Return _registrations.ToArray()
                End SyncLock
            End Get
        End Property

        ''' <summary>every permission event recorded so far, in order.</summary>
        Public ReadOnly Property Permissions As IReadOnlyList(Of ToolPermissionRecord)
            Get
                SyncLock _lock
                    Return _permissions.ToArray()
                End SyncLock
            End Get
        End Property

        ''' <summary>
        ''' optional host hook: called at the start of every tool call so the
        ''' host can say what it was doing (task name, round, custom fields).
        ''' it must be cheap and must not throw - an exception is swallowed.
        ''' </summary>
        Public Property ContextProvider As Func(Of ToolCallContext)

        ' ====================================================================
        ' wrapping a tool backend
        ' ====================================================================

        ''' <summary>
        ''' record <paramref name="inner"/> as the backend of
        ''' <paramref name="meta"/> and hand back a delegate that logs every
        ''' invocation of it.
        ''' 
        ''' the returned delegate is what has to be handed to
        ''' <c>LLMClient.AddFunction</c> in place of <paramref name="inner"/>.
        ''' </summary>
        ''' <param name="meta">the schema that was exported to the model; kept in tools.json and used for the argument diagnostics.</param>
        ''' <param name="source">"sandbox", "mcp" or "skill".</param>
        ''' <param name="inner">the real backend: the clr method, the mcp call, the skill script.</param>
        ''' <param name="kind">"tool", "resource" or "prompt".</param>
        ''' <param name="server">the mcp server name, Nothing for the local tools.</param>
        ''' <param name="originalName">the name the server or skill gave the primitive.</param>
        Public Function Wrap(meta As FunctionModel,
                            source As String,
                            inner As Func(Of FunctionCall, String),
                            Optional kind As String = ToolLogText.KindTool,
                            Optional server As String = Nothing,
                            Optional originalName As String = Nothing) As Func(Of FunctionCall, String)

            If inner Is Nothing Then
                Throw New ArgumentNullException(NameOf(inner))
            End If

            Dim snapshot As FunctionModel = meta

            Call RegisterTool(snapshot, source, kind, server, originalName)

            If Not _options.Enabled Then
                Return inner
            End If

            Dim toolName As String = If(snapshot Is Nothing, Nothing, snapshot.name)

            Return Function(invocation As FunctionCall) InvokeLogged(snapshot, toolName, source, kind, server, originalName, invocation, inner)
        End Function

        ''' <summary>
        ''' the body of every wrapped call: time it, record it, and hand the
        ''' outcome - result or exception - back exactly as it came.
        ''' </summary>
        Private Function InvokeLogged(meta As FunctionModel,
                                      toolName As String,
                                      source As String,
                                      kind As String,
                                      server As String,
                                      originalName As String,
                                      invocation As FunctionCall,
                                      inner As Func(Of FunctionCall, String)) As String

            Dim startedUtc As DateTimeOffset = DateTimeOffset.UtcNow
            Dim elapsed As Double = _stopwatch.Elapsed.TotalMilliseconds
            Dim watch As Stopwatch = Stopwatch.StartNew()

            Dim result As String = Nothing
            Dim thrown As Exception = Nothing

            Try
                result = inner(invocation)
            Catch ex As Exception
                thrown = ex
            End Try

            watch.Stop()

            Dim record As New ToolCallRecord With {
                .Seq = Interlocked.Increment(_seq),
                .Id = Guid.NewGuid().ToString("N").Substring(0, 12),
                .SessionId = _sessionId,
                .TimestampUtc = startedUtc.ToString("o"),
                .TimestampLocal = startedUtc.ToLocalTime().ToString("o"),
                .ElapsedSinceStartMs = Math.Round(elapsed, 3),
                .Tool = If(toolName, If(invocation Is Nothing, Nothing, invocation.name)),
                .Source = source,
                .Kind = kind,
                .Server = server,
                .OriginalName = originalName,
                .Description = If(meta Is Nothing, Nothing, meta.description),
                .ThreadId = Thread.CurrentThread.ManagedThreadId,
                .ThreadName = Thread.CurrentThread.Name,
                .DurationMs = Math.Round(watch.Elapsed.TotalMilliseconds, 3),
                .Workspace = _wsRoot,
                .SecurityPolicy = _policy,
                .Context = CaptureContext()
            }

            If invocation IsNot Nothing Then
                record.CallIndex = invocation.index
                record.Strict = invocation.strict
            End If

            Call FillArguments(record, meta, invocation)
            Call FillResult(record, result)

            If thrown IsNot Nothing Then
                ' a reflected clr call reports the real failure as its inner
                ' exception; the wrapper is what the reader cares about, not
                ' the reflection layer around it
                Dim real As Exception = thrown

                If TypeOf thrown Is Reflection.TargetInvocationException AndAlso thrown.InnerException IsNot Nothing Then
                    real = thrown.InnerException
                End If

                record.Status = ToolLogText.StatusFailed
                record.ErrorType = real.GetType().FullName
                record.ErrorMessage = real.Message
                record.ErrorStackTrace = real.StackTrace
            Else
                record.Status = Classify(result)
            End If

            Call Write(record)

            If thrown IsNot Nothing Then
                ' rethrow with the original stack: the caller must not be able
                ' to tell that a log wrapper was in the way
                Call ExceptionDispatchInfo.Capture(thrown).Throw()
            End If

            Return result
        End Function

        ' ====================================================================
        ' record building
        ' ====================================================================

        ''' <summary>
        ''' copy the arguments of the call into the record and compare them
        ''' with the declared schema: which parameter got what, what was
        ''' missing and what was surplus.
        ''' </summary>
        Private Sub FillArguments(record As ToolCallRecord, meta As FunctionModel, invocation As FunctionCall)
            Dim provided As New Dictionary(Of String, String)(StringComparer.Ordinal)

            If invocation IsNot Nothing AndAlso invocation.arguments IsNot Nothing Then
                For Each pair In invocation.arguments
                    provided(pair.Key) = pair.Value
                Next
            End If

            If provided.Count > 0 Then
                Dim capped As New Dictionary(Of String, String)(StringComparer.Ordinal)
                Dim cut As Boolean = False

                For Each pair In provided
                    Dim text As String = pair.Value

                    If text IsNot Nothing AndAlso text.Length > _options.MaxInlineArgumentChars Then
                        text = text.Substring(0, _options.MaxInlineArgumentChars)
                        cut = True
                    End If

                    capped(pair.Key) = text
                Next

                record.Arguments = capped
                record.ArgumentsTruncated = cut
            End If

            If meta Is Nothing OrElse meta.parameters Is Nothing OrElse meta.parameters.properties Is Nothing Then
                Return
            End If

            Dim declared As Dictionary(Of String, Ollama.JSON.FunctionCall.ParameterProperties) = meta.parameters.properties
            Dim required As New HashSet(Of String)(StringComparer.Ordinal)

            If meta.parameters.required IsNot Nothing Then
                For Each name In meta.parameters.required
                    If name IsNot Nothing Then required.Add(name)
                Next
            End If

            Dim detail As New List(Of ToolCallParameterRecord)()
            Dim missing As New List(Of String)()
            Dim unknown As New List(Of String)()

            For Each pair In declared
                Dim isRequired As Boolean = required.Contains(pair.Key)
                Dim sent As Boolean = provided.ContainsKey(pair.Key)
                Dim value As String = If(sent, provided(pair.Key), Nothing)
                Dim truncated As Boolean = False

                If value IsNot Nothing AndAlso value.Length > _options.MaxInlineArgumentChars Then
                    value = value.Substring(0, _options.MaxInlineArgumentChars)
                    truncated = True
                End If

                detail.Add(New ToolCallParameterRecord With {
                    .Name = pair.Key,
                    .Type = If(pair.Value Is Nothing, Nothing, pair.Value.type),
                    .Required = isRequired,
                    .Default = If(pair.Value Is Nothing, Nothing, pair.Value.default),
                    .Origin = If(sent, "model", "default"),
                    .Value = value,
                    .Truncated = truncated
                })

                If isRequired AndAlso Not sent Then
                    missing.Add(pair.Key)
                End If
            Next

            For Each key In provided.Keys
                If Not declared.ContainsKey(key) Then
                    unknown.Add(key)
                End If
            Next

            record.Parameters = detail

            If missing.Count > 0 Then record.MissingRequired = missing
            If unknown.Count > 0 Then record.UnknownArguments = unknown
        End Sub

        ''' <summary>
        ''' store the result text: inlined in full while it is short enough,
        ''' spilled into its own file beyond that, always with its length and
        ''' (when it was spilled, or when hashing is on) its sha256.
        ''' </summary>
        Private Sub FillResult(record As ToolCallRecord, result As String)
            If result Is Nothing Then
                record.ResultLength = 0
                Return
            End If

            record.ResultLength = result.Length

            If _options.SpillPayloads AndAlso result.Length > _options.MaxInlineChars Then
                Dim relative As String = TrySpill(record.Seq, record.Tool, result)

                If relative IsNot Nothing Then
                    record.ResultTruncated = True
                    record.ResultPayloadFile = relative
                    record.ResultPreview = result.Substring(0, Math.Min(_options.PreviewChars, result.Length))
                    record.ResultSha256 = Sha256(result)
                    Return
                End If
            End If

            record.Result = result

            If _options.HashResults Then
                record.ResultSha256 = Sha256(result)
            End If
        End Sub

        Private Function TrySpill(seq As Integer, tool As String, text As String) As String
            Try
                If Not Directory.Exists(_payloadFolder) Then
                    Call Directory.CreateDirectory(_payloadFolder)
                End If

                Dim name As String = seq.ToString("0000") & "-" & SafeName(tool) & ".txt"
                Dim target As String = IO.Path.Combine(_payloadFolder, name)

                Call File.WriteAllText(target, text, New UTF8Encoding(False))

                Return "payloads/" & name
            Catch ex As Exception
                _lastError = "payload spill failed: " & ex.Message
                Return Nothing
            End Try
        End Function

        Private Shared Function SafeName(tool As String) As String
            If String.IsNullOrEmpty(tool) Then
                Return "tool"
            End If

            Dim sb As New StringBuilder(tool.Length + 1)

            For Each c In tool
                sb.Append(If(Char.IsLetterOrDigit(c) OrElse c = "_"c OrElse c = "-"c, c, "_"c))
            Next

            Dim text As String = sb.ToString()

            If text.Length > 40 Then
                text = text.Substring(0, 40)
            End If

            Return text
        End Function

        ''' <summary>
        ''' sha256 of a result text. the type is fully qualified because this
        ''' class also has a member called Sha256 and vb resolves names
        ''' without case sensitivity.
        ''' </summary>
        Private Shared Function Sha256(text As String) As String
            Using sha As System.Security.Cryptography.SHA256 = System.Security.Cryptography.SHA256.Create()
                Dim hash As Byte() = sha.ComputeHash(Encoding.UTF8.GetBytes(text))
                Dim sb As New StringBuilder(hash.Length * 2)

                For Each b In hash
                    sb.Append(b.ToString("x2"))
                Next

                Return sb.ToString()
            End Using
        End Function

        ''' <summary>
        ''' guess what a result text means. the complete text is always kept
        ''' in the record, so a wrong guess never loses information - it only
        ''' decides which calls show up in the error list of the summary.
        ''' </summary>
        Private Shared Function Classify(text As String) As String
            If String.IsNullOrEmpty(text) Then
                Return ToolLogText.StatusOk
            End If

            Dim lowered As String = text.ToLowerInvariant()

            For Each mark In DenialMarks
                If lowered.Contains(mark) Then
                    Return ToolLogText.StatusDenied
                End If
            Next

            Dim head As String = text.TrimStart()

            If head.StartsWith("error", StringComparison.OrdinalIgnoreCase) Then
                Return ToolLogText.StatusError
            End If

            If head.StartsWith("{""error""", StringComparison.OrdinalIgnoreCase) Then
                Return ToolLogText.StatusError
            End If

            If lowered.StartsWith("tool error:", StringComparison.Ordinal) OrElse
               lowered.Contains("mcp tool call failed") OrElse
               lowered.Contains("is not registered") OrElse
               lowered.StartsWith("the given function", StringComparison.Ordinal) Then

                Return ToolLogText.StatusError
            End If

            Return ToolLogText.StatusOk
        End Function

        Private Function CaptureContext() As Dictionary(Of String, String)
            Dim provider As Func(Of ToolCallContext) = ContextProvider

            If provider Is Nothing Then
                Return Nothing
            End If

            Try
                Dim ctx As ToolCallContext = provider()

                If ctx Is Nothing Then
                    Return Nothing
                End If

                Dim map As New Dictionary(Of String, String)(StringComparer.Ordinal)

                If Not String.IsNullOrEmpty(ctx.Task) Then
                    map("task") = ctx.Task
                End If

                If Not String.IsNullOrEmpty(ctx.Round) Then
                    map("round") = ctx.Round
                End If

                If ctx.Extra IsNot Nothing Then
                    For Each pair In ctx.Extra
                        Dim key As String = If(String.IsNullOrEmpty(pair.Key), "extra", "extra." & pair.Key)
                        map(key) = pair.Value
                    Next
                End If

                Return If(map.Count = 0, Nothing, map)
            Catch
                Return Nothing
            End Try
        End Function

        ' ====================================================================
        ' registration snapshot
        ' ====================================================================

        ''' <summary>
        ''' note down the schema a tool was registered with. called by
        ''' <see cref="Wrap"/>, so a tool can only ever be logged if it is
        ''' also registered.
        ''' </summary>
        Public Sub RegisterTool(meta As FunctionModel,
                                Optional source As String = ToolLogText.SourceSandbox,
                                Optional kind As String = ToolLogText.KindTool,
                                Optional server As String = Nothing,
                                Optional originalName As String = Nothing)

            Dim snapshot As New ToolRegistrationRecord With {
                .Tool = If(meta Is Nothing, Nothing, meta.name),
                .Source = source,
                .Kind = kind,
                .Server = server,
                .OriginalName = originalName,
                .Description = If(meta Is Nothing, Nothing, meta.description),
                .RegisteredAtUtc = DateTimeOffset.UtcNow.ToString("o")
            }

            If meta IsNot Nothing AndAlso meta.parameters IsNot Nothing AndAlso meta.parameters.properties IsNot Nothing Then
                Dim required As New HashSet(Of String)(StringComparer.Ordinal)

                If meta.parameters.required IsNot Nothing Then
                    For Each name In meta.parameters.required
                        If name IsNot Nothing Then required.Add(name)
                    Next
                End If

                Dim list As New List(Of ToolRegistrationParameter)()

                For Each pair In meta.parameters.properties
                    list.Add(New ToolRegistrationParameter With {
                        .Name = pair.Key,
                        .Type = If(pair.Value Is Nothing, Nothing, pair.Value.type),
                        .Required = required.Contains(pair.Key),
                        .Default = If(pair.Value Is Nothing, Nothing, pair.Value.default),
                        .Description = If(pair.Value Is Nothing, Nothing, pair.Value.description)
                    })
                Next

                snapshot.Parameters = list
            End If

            SyncLock _lock
                _registrations.Add(snapshot)
            End SyncLock

            If Not _options.Enabled Then
                Return
            End If

            Call SaveRegistrations()
        End Sub

        ' ====================================================================
        ' permission events
        ' ====================================================================

        ''' <summary>
        ''' record one permission decision: what the sandbox asked for, what
        ''' the model said to justify it and whether it was let through.
        ''' </summary>
        ''' <param name="operation">"Read", "Write" or "Execute".</param>
        ''' <param name="target">the resolved absolute target of the operation.</param>
        ''' <param name="requestedPermission">"workspace-write" / "danger-full-access" for an escalation, Nothing otherwise.</param>
        ''' <param name="justification">the sentence the model gave for the escalation.</param>
        ''' <param name="granted">True when the host or the user allowed it.</param>
        ''' <param name="reason">why the request happened, or why it was refused without asking.</param>
        ''' <param name="insideWorkspace">whether <paramref name="target"/> is inside the workspace.</param>
        Public Sub LogPermission(operation As String,
                                 target As String,
                                 requestedPermission As String,
                                 justification As String,
                                 granted As Boolean,
                                 reason As String,
                                 Optional insideWorkspace As Boolean = True)

            If Not _options.Enabled Then
                Return
            End If

            Dim record As New ToolPermissionRecord With {
                .Seq = Interlocked.Increment(_permissionSeq),
                .SessionId = _sessionId,
                .TimestampUtc = DateTimeOffset.UtcNow.ToString("o"),
                .Operation = operation,
                .Target = target,
                .InsideWorkspace = insideWorkspace,
                .RequestedPermission = requestedPermission,
                .Justification = justification,
                .Granted = granted,
                .Reason = reason
            }

            SyncLock _lock
                _permissions.Add(record)
            End SyncLock

            If Not EnsureFolder() Then
                Return
            End If

            SyncLock _lock
                If _permissionLog Is Nothing Then
                    Dim writer As New JsonArrayAppender(_permissionFile)

                    If writer.Open() Then
                        _permissionLog = writer
                    Else
                        _lastError = "cannot open " & _permissionFile
                    End If
                End If
            End SyncLock

            If _permissionLog Is Nothing Then
                Return
            End If

            Try
                If Not _permissionLog.Append(JsonSerializer.Serialize(record, SerializerOptions())) Then
                    _lastError = "cannot append to " & _permissionFile
                End If
            Catch ex As Exception
                _lastError = "permission log failed: " & ex.Message
            End Try
        End Sub

        ' ====================================================================
        ' writing
        ' ====================================================================

        ''' <summary>
        ''' create the log folder and open the appenders, once. a failure is
        ''' remembered in <see cref="LastError"/> and degrades the log to
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
                    If Not Directory.Exists(_folder) Then
                        Call Directory.CreateDirectory(_folder)
                    End If

                    _calls = New JsonArrayAppender(_callFile)

                    If Not _calls.Open() Then
                        _lastError = "cannot open " & _callFile
                        Return False
                    End If

                    _folderReady = True

                    Call SaveRegistrations()
                    Call SaveSession()
                Catch ex As Exception
                    _lastError = "cannot create the tool log folder '" & _folder & "': " & ex.Message
                    Return False
                End Try
            End SyncLock

            Return True
        End Function

        ''' <summary>append one finished call record to tool-calls.json.</summary>
        Private Sub Write(record As ToolCallRecord)
            SyncLock _lock
                _records.Add(record)

                Dim key As String = If(record.Status, ToolLogText.StatusOk)
                Dim statusCount As Integer = 0
                _byStatus.TryGetValue(key, statusCount)
                _byStatus(key) = statusCount + 1

                Dim toolKey As String = If(record.Tool, "(unknown)")
                Dim toolCount As Integer = 0
                _byTool.TryGetValue(toolKey, toolCount)
                _byTool(toolKey) = toolCount + 1

                _totalDurationMs += record.DurationMs
            End SyncLock

            If Not EnsureFolder() Then
                Return
            End If

            Try
                If Not _calls.Append(JsonSerializer.Serialize(record, SerializerOptions())) Then
                    _lastError = "cannot append to " & _callFile
                End If
            Catch ex As Exception
                _lastError = "tool call log failed: " & ex.Message
            End Try
        End Sub

        Private Function SerializerOptions() As JsonSerializerOptions
            Return New JsonSerializerOptions With {
                .WriteIndented = _options.WriteIndented,
                .DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                .Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }
        End Function

        ''' <summary>rewrite tools.json with the current registration list.</summary>
        Private Sub SaveRegistrations()
            If Not _folderReady Then
                Return
            End If

            Try
                Dim snapshot As ToolRegistrationRecord()

                SyncLock _lock
                    snapshot = _registrations.ToArray()
                End SyncLock

                Call File.WriteAllText(_toolsFile, JsonSerializer.Serialize(snapshot, SerializerOptions()), New UTF8Encoding(False))
            Catch ex As Exception
                _lastError = "cannot write " & _toolsFile & ": " & ex.Message
            End Try
        End Sub

        ''' <summary>
        ''' write session.json now. the host does not have to call it:
        ''' <see cref="Close"/> does it as well.
        ''' </summary>
        Public Sub SaveSession()
            If Not _folderReady Then
                Return
            End If

            Try
                Dim summary As ToolSessionSummary

                SyncLock _lock
                    Dim errors As New List(Of ToolSessionError)()

                    For Each r In _records
                        If r.Status = ToolLogText.StatusFailed OrElse
                           r.Status = ToolLogText.StatusDenied OrElse
                           r.Status = ToolLogText.StatusError Then

                            errors.Add(New ToolSessionError With {
                                .Seq = r.Seq,
                                .Tool = r.Tool,
                                .Status = r.Status,
                                .Message = If(r.ErrorMessage, FirstLine(r.Result))
                            })
                        End If
                    Next

                    summary = New ToolSessionSummary With {
                        .SessionId = _sessionId,
                        .Workspace = _wsRoot,
                        .SecurityPolicy = _policy,
                        .LogFolder = _folder,
                        .StartedUtc = _startedUtc.ToString("o"),
                        .ClosedUtc = DateTimeOffset.UtcNow.ToString("o"),
                        .DurationMs = Math.Round(_stopwatch.Elapsed.TotalMilliseconds, 3),
                        .Machine = Environment.MachineName,
                        .User = Environment.UserName,
                        .OsVersion = Environment.OSVersion.ToString(),
                        .ProcessId = Process.GetCurrentProcess().Id,
                        .ClrVersion = Environment.Version.ToString(),
                        .TotalCalls = _records.Count,
                        .TotalToolDurationMs = Math.Round(_totalDurationMs, 3),
                        .CallsByStatus = New Dictionary(Of String, Integer)(_byStatus, StringComparer.Ordinal),
                        .CallsByTool = New Dictionary(Of String, Integer)(_byTool, StringComparer.Ordinal),
                        .Errors = errors,
                        .RegisteredTools = _registrations.Count,
                        .PermissionEvents = _permissions.Count,
                        .Persistent = Persistent,
                        .LastError = _lastError,
                        .Files = New Dictionary(Of String, String)(StringComparer.Ordinal) From {
                            {"toolCalls", _callFile},
                            {"tools", _toolsFile},
                            {"permissions", _permissionFile},
                            {"payloads", _payloadFolder}
                        }
                    }
                End SyncLock

                Call File.WriteAllText(_sessionFile, JsonSerializer.Serialize(summary, SerializerOptions()), New UTF8Encoding(False))
            Catch ex As Exception
                _lastError = "cannot write " & _sessionFile & ": " & ex.Message
            End Try
        End Sub

        Private Shared Function FirstLine(text As String) As String
            If String.IsNullOrEmpty(text) Then
                Return Nothing
            End If

            Dim idx As Integer = text.IndexOfAny(New Char() {ChrW(10), ChrW(13)})

            If idx < 0 Then
                Return text
            End If

            Return text.Substring(0, idx)
        End Function

        ''' <summary>
        ''' bring the log up to date on disk. every record is flushed the
        ''' moment it is appended, so this only refreshes session.json.
        ''' </summary>
        Public Sub Flush()
            Call SaveSession()
        End Sub

        ''' <summary>
        ''' finish the session: write session.json and release the file
        ''' handles. the log stays on disk; calling this twice is harmless.
        ''' </summary>
        Public Sub Close()
            If _closed Then
                Return
            End If

            _closed = True

            Call SaveSession()

            If _calls IsNot Nothing Then
                _calls.Close()
            End If

            If _permissionLog IsNot Nothing Then
                _permissionLog.Close()
            End If
        End Sub

    End Class

End Namespace
