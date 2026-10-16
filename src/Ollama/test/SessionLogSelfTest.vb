Imports System.IO
Imports System.Linq
Imports System.Text
Imports System.Text.Json
Imports System.Threading
Imports Ollama
Imports Ollama.JSON.FunctionCall
Imports Ollama.Logging

''' <summary>
''' the offline verification of the structured chat session log. nothing here
''' talks to a model: a stub provider hands out a scripted stream of chunks,
''' so the whole chain - turn, round, tool call, usage, message - is exercised
''' without a network.
''' </summary>
Module SessionLogSelfTest

    Private _passed As Integer
    Private _failed As Integer
    Private ReadOnly _failures As New List(Of String)
    Private _root As String

    Friend Function RunAll() As Integer
        _root = Path.Combine(Path.GetTempPath(), "ollama-sessionlog-" & Guid.NewGuid().ToString("N").Substring(0, 8))
        Directory.CreateDirectory(_root)

        Try
            RunTest("a plain turn is logged end to end", AddressOf TestPlainTurn)
            RunTest("a tool call is logged with its result", AddressOf TestToolCall)
            RunTest("a failing tool is logged as failed", AddressOf TestFailingTool)
            RunTest("an unregistered tool is logged as missing", AddressOf TestMissingTool)
            RunTest("a network failure is logged as a retry", AddressOf TestNetworkRetry)
            RunTest("a turn without an answer is logged as max_rounds", AddressOf TestMaxRounds)
            RunTest("an oversized answer is spilled to a payload", AddressOf TestPayloadSpill)
            RunTest("the request messages can be turned off", AddressOf TestNoRequestMessages)
            RunTest("a shared session id is honoured", AddressOf TestSharedSessionId)
            RunTest("a damaged log tail is repaired", AddressOf TestTailRepair)
            RunTest("nothing is written while the log is off", AddressOf TestDisabled)
        Finally
            If Environment.GetEnvironmentVariable("OLLAMA_KEEP_SESSIONLOG") <> "1" Then
                Try
                    Directory.Delete(_root, True)
                Catch
                End Try
            End If
        End Try

        Console.WriteLine()
        Console.WriteLine($"[session log self test] {_passed} passed, {_failed} failed.")

        If Environment.GetEnvironmentVariable("OLLAMA_KEEP_SESSIONLOG") = "1" Then
            Console.WriteLine("the logs the test wrote are kept for review in " & _root)
        End If

        For Each failure In _failures
            Console.WriteLine("  FAILED: " & failure)
        Next

        Return If(_failed = 0, 0, 1)
    End Function

    Private Sub RunTest(name As String, body As Action)
        Console.WriteLine("== " & name)

        Try
            body()
        Catch ex As Exception
            Fail(name & ": unexpected " & ex.GetType().Name & ": " & ex.Message)
        End Try
    End Sub

    Private Sub Check(condition As Boolean, message As String)
        If condition Then
            _passed += 1
        Else
            Fail(message)
        End If
    End Sub

    Private Sub Fail(message As String)
        _failed += 1
        _failures.Add(message)
    End Sub

    ' ========================================================================
    ' the stub backend
    ' ========================================================================

    ''' <summary>
    ''' a provider that hands out scripted chunks instead of talking to a
    ''' model: one list of chunks per request, in order.
    ''' </summary>
    Private NotInheritable Class StubProvider
        Implements ILLMProvider

        Private ReadOnly _script As Queue(Of List(Of ChatResponseChunk))

        ''' <summary>how many requests reached the backend.</summary>
        Public Property Requests As Integer

        ''' <summary>let the next request fail the way a broken network would.</summary>
        Public Property FailNext As Boolean

        ''' <summary>name of a tool every request asks for: the turn never ends by itself.</summary>
        Public Property LoopTool As String

        Sub New(script As IEnumerable(Of List(Of ChatResponseChunk)))
            _script = New Queue(Of List(Of ChatResponseChunk))(script)
        End Sub

        Public ReadOnly Property ApiEndpoint As String Implements ILLMProvider.ApiEndpoint
            Get
                Return "http://stub.local/v1"
            End Get
        End Property

        Public ReadOnly Property SupportsCacheStats As Boolean Implements ILLMProvider.SupportsCacheStats
            Get
                Return True
            End Get
        End Property

        Public Function StreamChatAsync(options As ChatRequestOptions, cancellationToken As CancellationToken) As Task(Of IEnumerable(Of ChatResponseChunk)) Implements ILLMProvider.StreamChatAsync
            Requests += 1

            If FailNext Then
                FailNext = False
                Throw New InvalidOperationException("the network is down")
            End If

            If Not String.IsNullOrEmpty(LoopTool) Then
                Dim looping As New List(Of ChatResponseChunk) From {
                    RequestTool("loop-" & Requests, LoopTool),
                    Done()
                }

                Return Task.FromResult(Of IEnumerable(Of ChatResponseChunk))(looping)
            End If

            Dim round As List(Of ChatResponseChunk)

            If _script.Count > 0 Then
                round = _script.Dequeue()
            Else
                ' the script ran out: answer so the turn can finish
                round = New List(Of ChatResponseChunk) From {SessionLogSelfTest.Done()}
            End If

            Return Task.FromResult(Of IEnumerable(Of ChatResponseChunk))(round)
        End Function

        Public Function GetModelInformation(model As String, timeout As Double, verbose As Boolean) As Task(Of ModelInfo) Implements ILLMProvider.GetModelInformation
            Return Task.FromResult(Of ModelInfo)(Nothing)
        End Function

    End Class

    Private Function Think(text As String) As ChatResponseChunk
        Return New ChatResponseChunk With {.ThinkContent = text}
    End Function

    Private Function Say(text As String) As ChatResponseChunk
        Return New ChatResponseChunk With {.DeltaContent = text}
    End Function

    Private Function Done(Optional prompt As Long = 100, Optional completion As Long = 20) As ChatResponseChunk
        Return New ChatResponseChunk With {
            .IsDone = True,
            .Usage = New ChatUsage With {
                .PromptTokens = prompt,
                .CompletionTokens = completion,
                .CacheHitTokens = 40,
                .CacheMissTokens = 60
            }
        }
    End Function

    Private Function RequestTool(id As String, name As String, Optional arg As String = Nothing) As ChatResponseChunk
        Dim args As New Dictionary(Of String, String)()

        If arg IsNot Nothing Then
            args("text") = arg
        End If

        Return New ChatResponseChunk With {
            .ToolCalls = New List(Of ToolCallInfo) From {
                New ToolCallInfo With {.Id = id, .FunctionName = name, .FunctionArguments = args}
            }
        }
    End Function

    Private Function NewFolder(tag As String) As String
        Dim dir As String = Path.Combine(_root, tag)

        Directory.CreateDirectory(dir)

        Return dir
    End Function

    ''' <summary>parse a log file; a broken one is reported as a failure instead of throwing.</summary>
    Private Function ReadArray(path As String) As JsonElement
        Dim text As String = File.ReadAllText(path, New UTF8Encoding(False))

        Return JsonDocument.Parse(text).RootElement.Clone()
    End Function

    Private Function ReadObject(path As String) As JsonElement
        Return ReadArray(path)
    End Function

    Private Function Str(item As JsonElement, name As String) As String
        Dim value As JsonElement

        If Not item.TryGetProperty(name, value) Then
            Return Nothing
        End If

        If value.ValueKind = JsonValueKind.Null Then
            Return Nothing
        End If

        Return value.GetString()
    End Function

    Private Function Num(item As JsonElement, name As String) As Double
        Dim value As JsonElement

        If Not item.TryGetProperty(name, value) Then
            Return Double.NaN
        End If

        Return value.GetDouble()
    End Function

    ''' <summary>read a nested string value, e.g. prompt.text or result.text.</summary>
    Private Function Str2(item As JsonElement, ParamArray path As String()) As String
        Dim current As JsonElement = item

        For Each name As String In path
            Dim node As JsonElement

            If Not current.TryGetProperty(name, node) Then
                Return Nothing
            End If

            current = node
        Next

        If current.ValueKind = JsonValueKind.Null Then
            Return Nothing
        End If

        Return current.GetString()
    End Function

    Private Function Has(item As JsonElement, name As String) As Boolean
        Return item.TryGetProperty(name, Nothing)
    End Function

    ' ========================================================================
    ' the tests
    ' ========================================================================

    ''' <summary>
    ''' one ordinary question: the answer reaches the caller unchanged and the
    ''' log holds the question, the reasoning, the answer, the request and the
    ''' usage of the only round it needed.
    ''' </summary>
    Private Sub TestPlainTurn()
        Dim folder As String = NewFolder("plain")
        Dim provider As New StubProvider({New List(Of ChatResponseChunk) From {Think("let me think"), Say("hello"), Say(" world"), Done()}})
        Dim client As New LLMClient(provider, "stub-model", chatLogFolder:=folder)

        client.system_message = "you are a stub"

        Dim answer As LLMsResponse = client.Chat("hi there").GetAwaiter().GetResult()

        Check(answer IsNot Nothing AndAlso answer.output = "hello world", "the client must still answer, got " & If(answer Is Nothing, "Nothing", answer.output))
        Check(answer IsNot Nothing AndAlso answer.think = "let me think", "the reasoning must still come back")

        Call client.SessionLog.Close()

        Dim turns As JsonElement = ReadArray(Path.Combine(client.SessionLog.Folder, "turns.json"))

        Check(turns.ValueKind = JsonValueKind.Array, "turns.json must be a json array")
        Check(turns.GetArrayLength() = 1, "one turn was made, got " & turns.GetArrayLength())

        Dim turn As JsonElement = turns(0)

        Check(Str(turn, "status") = ChatLogText.StatusOk, "the turn must be ok, got " & Str(turn, "status"))
        Check(Str(turn, "model") = "stub-model", "the model must be recorded")
        Check(Str2(turn, "prompt", "text") = "hi there", "the question must be recorded")
        Check(Str2(turn, "think", "text") = "let me think", "the reasoning must be recorded")
        Check(Str2(turn, "output", "text") = "hello world", "the answer must be recorded")
        Check(Num(turn, "roundCount") = 1, "the turn needed one round, got " & Num(turn, "roundCount"))
        Check(Num(turn, "durationMs") >= 0, "the turn must carry a duration")

        Dim round As JsonElement = turn.GetProperty("rounds")(0)

        Check(Str(round, "status") = ChatLogText.StatusOk, "the round must be ok")
        Check(Num(round, "chunkCount") = 4, "four chunks arrived, got " & Num(round, "chunkCount"))
        Check(Num(round, "firstTokenMs") >= 0, "the round must carry the first token delay")
        Check(Num(round, "streamMs") >= 0, "the round must carry the stream duration")
        Check(Num(round, "requestMessageCount") = 2, "system and user went into the request, got " & Num(round, "requestMessageCount"))
        Check(Str(round.GetProperty("requestMessages")(0), "role") = "system", "the system prompt leads the request")
        Check(Str(round.GetProperty("requestMessages")(0).GetProperty("content"), "text") = "you are a stub", "the system prompt must be in the request")
        Check(Num(round.GetProperty("usage"), "promptTokens") = 100, "the usage of the round must be recorded")
        Check(Num(round.GetProperty("usage"), "cacheHitTokens") = 40, "the cache hits must be recorded")

        Dim usage As JsonElement = ReadArray(Path.Combine(client.SessionLog.Folder, "usage.json"))

        Check(usage.GetArrayLength() = 1, "one usage report, got " & usage.GetArrayLength())
        Check(Num(usage(0), "cumulativePromptTokens") = 100, "the cumulative usage must be recorded")

        Dim messages As JsonElement = ReadArray(Path.Combine(client.SessionLog.Folder, "messages.json"))

        Check(messages.GetArrayLength() = 2, "the question and the answer must be in the ledger, got " & messages.GetArrayLength())
        Check(Str(messages(0), "role") = "user", "the ledger starts with the question")
        Check(Str(messages(1), "role") = "assistant", "the answer follows")

        Dim session As JsonElement = ReadObject(Path.Combine(client.SessionLog.Folder, "session.json"))

        Check(Str(session, "model") = "stub-model", "session.json must name the model")
        Check(Str(session, "endpoint") = "http://stub.local/v1", "session.json must name the endpoint")
        Check(Num(session, "turns") = 1, "session.json must count the turns")
        Check(Num(session, "rounds") = 1, "session.json must count the rounds")
        Check(Num(session, "promptTokens") = 100, "session.json must count the tokens")
        Check(session.GetProperty("persistent").GetBoolean(), "the log must report itself as written to disk")
    End Sub

    ''' <summary>
    ''' a turn that calls a tool: the call, its result and the second request
    ''' the client made afterwards all show up.
    ''' </summary>
    Private Sub TestToolCall()
        Dim folder As String = NewFolder("tool")
        Dim provider As New StubProvider({
            New List(Of ChatResponseChunk) From {RequestTool("call-1", "echo", "hi"), Done()},
            New List(Of ChatResponseChunk) From {Say("all done"), Done()}
        })
        Dim client As New LLMClient(provider, "stub-model", chatLogFolder:=folder)

        Call client.AddFunction(
            New FunctionModel("echo", "echo a text", New ParameterProperties("text", "the text")),
            f:=Function(fc As FunctionCall) As String
                    Return "echo: " & fc.arguments("text")
                End Function)

        Dim answer As LLMsResponse = client.Chat("call the echo").GetAwaiter().GetResult()

        Check(answer IsNot Nothing AndAlso answer.output = "all done", "the client must answer after the tool ran")

        Call client.SessionLog.Close()

        Dim calls As JsonElement = ReadArray(Path.Combine(client.SessionLog.Folder, "tool-calls.json"))

        Check(calls.GetArrayLength() = 1, "one tool call, got " & calls.GetArrayLength())
        Check(Str(calls(0), "name") = "echo", "the name of the tool must be recorded")
        Check(Str(calls(0), "callId") = "call-1", "the id of the call must be recorded")
        Check(Str(calls(0), "source") = ChatLogText.SourceRegistry, "the call came from the registry")
        Check(Str(calls(0).GetProperty("arguments"), "text") = "hi", "the arguments must be recorded")
        Check(Str(calls(0).GetProperty("result"), "text") = "echo: hi", "the result must be recorded")
        Check(Str(calls(0), "status") = ChatLogText.StatusOk, "the call succeeded")
        Check(Num(calls(0), "turnSeq") = 1 AndAlso Num(calls(0), "roundSeq") = 1, "the call must point at its turn and round")

        Dim turn As JsonElement = ReadArray(Path.Combine(client.SessionLog.Folder, "turns.json"))(0)

        Check(Num(turn, "roundCount") = 2, "the turn needed two rounds, got " & Num(turn, "roundCount"))
        Check(Num(turn, "toolCallCount") = 1, "the turn ran one tool")

        Dim first As JsonElement = turn.GetProperty("rounds")(0)

        Check(Str(first.GetProperty("requestedToolCalls")(0), "name") = "echo", "the round must record the call it asked for")
        Check(Num(first.GetProperty("usage"), "promptTokens") = 100, "the first round must carry its usage")

        Dim messages As JsonElement = ReadArray(Path.Combine(client.SessionLog.Folder, "messages.json"))

        Check(messages.GetArrayLength() = 4, "user, assistant, tool and assistant, got " & messages.GetArrayLength())
        Check(Str(messages(2), "role") = "tool", "the third message is the tool result")
        Check(Str(messages(2), "toolCallId") = "call-1", "the tool message must carry the id of its call")
    End Sub

    ''' <summary>
    ''' a tool that throws: the client retries, and the log keeps both the
    ''' exception of the tool and the round it broke.
    ''' </summary>
    Private Sub TestFailingTool()
        Dim folder As String = NewFolder("fail")
        Dim provider As New StubProvider({
            New List(Of ChatResponseChunk) From {RequestTool("call-1", "boom"), Done()},
            New List(Of ChatResponseChunk) From {Say("recovered"), Done()}
        })
        Dim client As New LLMClient(provider, "stub-model", chatLogFolder:=folder)

        Call client.AddFunction(
            New FunctionModel("boom", "always throws", New ParameterProperties("text", "the text")),
            f:=Function(fc As FunctionCall) As String
                    Throw New InvalidOperationException("the tool exploded")
                End Function)

        Dim answer As LLMsResponse = client.Chat("break it").GetAwaiter().GetResult()

        Check(answer IsNot Nothing AndAlso answer.output = "recovered", "the client must recover in the next round")

        Call client.SessionLog.Close()

        Dim calls As JsonElement = ReadArray(Path.Combine(client.SessionLog.Folder, "tool-calls.json"))

        Check(Str(calls(0), "status") = ChatLogText.StatusFailed, "the call must be failed, got " & Str(calls(0), "status"))
        Check(Str(calls(0), "errorType") IsNot Nothing AndAlso Str(calls(0), "errorType").Contains("InvalidOperationException"),
              "the exception type must be recorded")
        Check(Str(calls(0), "errorMessage") = "the tool exploded", "the exception message must be recorded")

        Dim turn As JsonElement = ReadArray(Path.Combine(client.SessionLog.Folder, "turns.json"))(0)

        Check(turn.GetProperty("errors").GetArrayLength() = 1, "the retry must be listed on the turn")
        Check(Str(turn.GetProperty("errors")(0), "kind") = ChatLogText.EventRetry, "the failure must be a retry event")
        Check(Str(turn.GetProperty("rounds")(0), "status") = ChatLogText.StatusFailed, "the broken round must be marked")
        Check(Str(turn, "status") = ChatLogText.StatusOk, "the turn itself recovered")
    End Sub

    ''' <summary>
    ''' a tool nobody registered is reported through its result and is logged
    ''' as coming from nowhere.
    ''' </summary>
    Private Sub TestMissingTool()
        Dim folder As String = NewFolder("missing")
        Dim provider As New StubProvider({
            New List(Of ChatResponseChunk) From {RequestTool("call-1", "nope"), Done()},
            New List(Of ChatResponseChunk) From {Say("gave up"), Done()}
        })
        Dim client As New LLMClient(provider, "stub-model", chatLogFolder:=folder)

        Dim answer As LLMsResponse = client.Chat("call nothing").GetAwaiter().GetResult()

        Check(answer IsNot Nothing AndAlso answer.output = "gave up", "the client must answer")

        Call client.SessionLog.Close()

        Dim calls As JsonElement = ReadArray(Path.Combine(client.SessionLog.Folder, "tool-calls.json"))

        Check(Str(calls(0), "source") = ChatLogText.SourceMissing, "an unregistered tool must be logged as missing")
        Check(Str(calls(0), "status") = ChatLogText.StatusOk, "it is reported through the result, not through an exception")
        Check(Str(calls(0).GetProperty("result"), "text") IsNot Nothing AndAlso
              Str(calls(0).GetProperty("result"), "text").Contains("not registered"),
              "the message the model saw must be kept")
    End Sub

    ''' <summary>
    ''' a request that dies on the network is retried, and the log says so.
    ''' </summary>
    Private Sub TestNetworkRetry()
        Dim folder As String = NewFolder("retry")
        Dim provider As New StubProvider({New List(Of ChatResponseChunk) From {Say("second try"), Done()}})

        provider.FailNext = True

        Dim client As New LLMClient(provider, "stub-model", chatLogFolder:=folder)
        Dim answer As LLMsResponse = client.Chat("survive").GetAwaiter().GetResult()

        Check(answer IsNot Nothing AndAlso answer.output = "second try", "the client must retry and answer")
        Check(provider.Requests = 2, "the backend must have been asked twice, got " & provider.Requests)

        Call client.SessionLog.Close()

        Dim turn As JsonElement = ReadArray(Path.Combine(client.SessionLog.Folder, "turns.json"))(0)
        Dim errors As JsonElement = turn.GetProperty("errors")

        Check(errors.GetArrayLength() = 1, "the network failure must be listed, got " & errors.GetArrayLength())
        Check(Str(errors(0), "kind") = ChatLogText.EventRetry, "it must be a retry event")
        Check(Str(errors(0), "summary") IsNot Nothing AndAlso Str(errors(0), "summary").Contains("the network is down"),
              "the reason must be in the summary")

        Dim events As JsonElement = ReadObject(Path.Combine(client.SessionLog.Folder, "session.json")).GetProperty("events")

        Check(events.GetArrayLength() >= 1, "the failure must also show up in session.json")
    End Sub

    ''' <summary>
    ''' a turn that never answers runs out of rounds; the log says so instead
    ''' of silently dropping it.
    ''' </summary>
    Private Sub TestMaxRounds()
        Dim folder As String = NewFolder("maxrounds")
        Dim provider As New StubProvider({})

        ' every request asks for a tool, so the turn can never answer
        provider.LoopTool = "nope"

        Dim client As New LLMClient(provider, "stub-model", chatLogFolder:=folder, maxRound:=2)
        Dim thrown As Exception = Nothing

        Try
            Call client.Chat("loop forever").GetAwaiter().GetResult()
        Catch ex As Exception
            thrown = ex
        End Try

        Check(thrown IsNot Nothing AndAlso thrown.GetType().Name = "MaxTryRunException",
              "the client must still throw MaxTryRunException, got " & If(thrown Is Nothing, "nothing", thrown.GetType().Name))

        Call client.SessionLog.Close()

        Dim turn As JsonElement = ReadArray(Path.Combine(client.SessionLog.Folder, "turns.json"))(0)

        Check(Str(turn, "status") = ChatLogText.StatusMaxRounds, "the turn must be max_rounds_exceeded, got " & Str(turn, "status"))
        Check(Num(turn, "roundCount") >= 1, "the rounds it managed must be kept")

        Dim events As JsonElement = turn.GetProperty("errors")

        Check(events.EnumerateArray().Any(Function(e) Str(e, "kind") = ChatLogText.EventMaxRounds),
              "the turn must carry the max_rounds event")
    End Sub

    ''' <summary>
    ''' an answer too long to inline is written next to the log; the record
    ''' keeps its length, its hash, a preview and the path.
    ''' </summary>
    Private Sub TestPayloadSpill()
        Dim folder As String = NewFolder("spill")
        Dim big As String = New String("x"c, 4000)
        Dim provider As New StubProvider({New List(Of ChatResponseChunk) From {Say(big), Done()}})
        Dim options As New ChatSessionLogOptions With {.MaxInlineChars = 200, .PreviewChars = 50}
        Dim client As New LLMClient(provider, "stub-model", chatLogFolder:=folder, chatLogOptions:=options)

        Call client.Chat("a lot of text").GetAwaiter().GetResult()
        Call client.SessionLog.Close()

        Dim round As JsonElement = ReadArray(Path.Combine(client.SessionLog.Folder, "turns.json"))(0).GetProperty("rounds")(0)
        Dim output As JsonElement = round.GetProperty("output")

        Check(output.GetProperty("truncated").GetBoolean(), "an oversized answer must be marked as truncated")
        Check(Str(output, "text") Is Nothing, "an oversized answer must not be inlined")
        Check(Str(output, "sha256") IsNot Nothing, "a spilled answer must be hashed")
        Check(Str(output, "preview").Length = 50, "a spilled answer must keep a preview")
        Check(Num(output, "length") = 4000, "the complete length must be kept, got " & Num(output, "length"))

        Dim relative As String = Str(output, "payloadFile")
        Dim target As String = Path.Combine(client.SessionLog.Folder, relative.Replace("/"c, Path.DirectorySeparatorChar))

        Check(File.Exists(target), "the payload file must exist on disk")
        Check(File.ReadAllText(target).Length = 4000, "the payload file must hold the complete answer")
    End Sub

    ''' <summary>
    ''' the complete message list of every request can be turned off when only
    ''' the shape of the request is of interest.
    ''' </summary>
    Private Sub TestNoRequestMessages()
        Dim folder As String = NewFolder("norequest")
        Dim provider As New StubProvider({New List(Of ChatResponseChunk) From {Say("ok"), Done()}})
        Dim options As New ChatSessionLogOptions With {.CaptureRequestMessages = False}
        Dim client As New LLMClient(provider, "stub-model", chatLogFolder:=folder, chatLogOptions:=options)

        Call client.Chat("quiet").GetAwaiter().GetResult()
        Call client.SessionLog.Close()

        Dim round As JsonElement = ReadArray(Path.Combine(client.SessionLog.Folder, "turns.json"))(0).GetProperty("rounds")(0)

        Check(Num(round, "requestMessageCount") = 1, "the number of messages is still recorded, got " & Num(round, "requestMessageCount"))
        Check(Not Has(round, "requestMessages"), "the messages themselves must be left out")
    End Sub

    ''' <summary>
    ''' a host may hand in the id of its own log; the chat log then lives
    ''' under that id and points back at the host log.
    ''' </summary>
    Private Sub TestSharedSessionId()
        Dim folder As String = NewFolder("shared")
        Dim provider As New StubProvider({New List(Of ChatResponseChunk) From {Say("ok"), Done()}})
        Dim client As New LLMClient(provider, "stub-model")

        Check(client.SessionLog Is Nothing, "no folder, no log")

        Call client.EnableSessionLog(folder, sessionId:="harness-20261017-000000")
        client.SessionLog.HarnessLogFolder = "Z:\tmp\1\.harness\toolcalls\harness-20261017-000000"

        Call client.Chat("link me").GetAwaiter().GetResult()
        Call client.SessionLog.Close()

        Check(client.SessionLog.Folder.EndsWith("harness-20261017-000000"), "the log must live under the shared id: " & client.SessionLog.Folder)

        Dim session As JsonElement = ReadObject(Path.Combine(client.SessionLog.Folder, "session.json"))

        Check(Str(session, "sessionId") = "harness-20261017-000000", "the shared id must be in session.json")
        Check(Str(session, "harnessLogFolder") IsNot Nothing, "session.json must point at the tool log")
    End Sub

    ''' <summary>
    ''' a log a killed process left behind is reopened and repaired instead of
    ''' being corrupted further.
    ''' </summary>
    Private Sub TestTailRepair()
        Dim folder As String = NewFolder("repair")
        Dim provider As New StubProvider({New List(Of ChatResponseChunk) From {Say("ok"), Done()}})
        Dim client As New LLMClient(provider, "stub-model")

        Call client.EnableSessionLog(folder, sessionId:="repair-1")

        ' the tail of a file whose process died between two records
        Directory.CreateDirectory(client.SessionLog.Folder)
        File.WriteAllText(client.SessionLog.TurnsFile, "[" & vbLf & "{""seq"": 0, ""model"": ""ghost""},", New UTF8Encoding(False))

        Call client.Chat("repair me").GetAwaiter().GetResult()
        Call client.SessionLog.Close()

        Dim turns As JsonElement = ReadArray(client.SessionLog.TurnsFile)

        Check(turns.ValueKind = JsonValueKind.Array, "the repaired file must still be a json array")
        Check(turns.GetArrayLength() = 3, "the ghost, the null that closed the comma and the real turn, got " & turns.GetArrayLength())
        Check(Str2(turns(2), "prompt", "text") = "repair me", "the appended turn must be the real one")
    End Sub

    ''' <summary>
    ''' without a log folder the client behaves exactly as before and writes
    ''' nothing at all.
    ''' </summary>
    Private Sub TestDisabled()
        Dim folder As String = NewFolder("disabled")
        Dim provider As New StubProvider({New List(Of ChatResponseChunk) From {Say("ok"), Done()}})
        Dim client As New LLMClient(provider, "stub-model")
        Dim answer As LLMsResponse = client.Chat("no log").GetAwaiter().GetResult()

        Check(answer IsNot Nothing AndAlso answer.output = "ok", "the client must answer as always")
        Check(client.SessionLog Is Nothing, "the client must not hold a log")
        Check(Directory.GetDirectories(folder).Length = 0, "no session folder may be created")
        Check(Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Length = 0, "no file may be written")
    End Sub

End Module
