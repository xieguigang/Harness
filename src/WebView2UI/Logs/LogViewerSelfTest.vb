Imports System.IO
Imports System.Text.Json

Namespace Logs

    ''' <summary>
    ''' the headless verification of the harness log viewer data layer. it runs
    ''' the same code the web page calls - <see cref="LogHost"/> - against a real
    ''' <c>.harness</c> folder and checks that the sessions, the records, the
    ''' filters, the payload slices and the raw files all come out right, so the
    ''' viewer can be debugged without opening a window.
    ''' 
    ''' run it with <c>dotnet WebView2UI.dll --selftest [log folder]</c>.
    ''' </summary>
    Public Module LogViewerSelfTest

        Private _passed As Integer
        Private _failed As Integer
        Private ReadOnly _failures As New List(Of String)

        ''' <summary>
        ''' run every check against the given log folder, or against the smoke
        ''' test folder when none is given.
        ''' </summary>
        ''' <param name="args">the log folder as the first argument, if any.</param>
        ''' <returns>0 when every check passed, 1 otherwise.</returns>
        Public Function Run(args As String()) As Integer
            Dim root As String = "G:\OmicsWorks\test\smoke_20261017\.harness"

            If args IsNot Nothing AndAlso args.Length > 0 AndAlso Not String.IsNullOrWhiteSpace(args(0)) Then
                root = args(0)
            End If

            Console.WriteLine("=== harness log viewer self test ===")
            Console.WriteLine("log folder: " & root)
            Console.WriteLine()

            Dim host As LogHost

            Try
                host = New LogHost(root)
            Catch ex As Exception
                Console.WriteLine("cannot open the log folder: " & ex.Message)
                Return 1
            End Try

            Call TestListSessions(host)
            Call TestToolCallSession(host)
            Call TestChatSession(host)
            Call TestFilters(host)
            Call TestPayloads(host)
            Call TestRawAndText(host)
            Call TestGuards(host)

            Console.WriteLine()
            Console.WriteLine($"[log viewer self test] {_passed} passed, {_failed} failed.")

            For Each fault In _failures
                Console.WriteLine("  FAILED: " & fault)
            Next

            Return If(_failed = 0, 0, 1)
        End Function

        ' ====================================================================
        ' the checks
        ' ====================================================================

        Private Sub TestListSessions(host As LogHost)
            Dim reply As JsonElement = Parse(host.ListSessions())

            Check(HasError(reply) Is Nothing, "listing the sessions must not fail: " & HasError(reply))
            Check(reply.GetProperty("exists").GetBoolean(), "the log folder must exist")

            Dim sessions As JsonElement = reply.GetProperty("sessions")

            Check(sessions.GetArrayLength() = 4, "four sessions are expected, got " & sessions.GetArrayLength())

            Dim toolCalls As Integer = 0
            Dim llmSessions As Integer = 0

            For Each session In sessions.EnumerateArray()
                Dim kind As String = session.GetProperty("kind").GetString()

                If kind = HarnessLogIndex.KindToolCalls Then
                    toolCalls += 1
                ElseIf kind = HarnessLogIndex.KindLlmSessions Then
                    llmSessions += 1
                End If
            Next

            Check(toolCalls = 1, "one tool call session is expected, got " & toolCalls)
            Check(llmSessions = 3, "three chat sessions are expected, got " & llmSessions)

            Dim textFiles As JsonElement = reply.GetProperty("textFiles")

            Check(textFiles.GetArrayLength() = 5, "five rscript logs are expected, got " & textFiles.GetArrayLength())
        End Sub

        Private Sub TestToolCallSession(host As LogHost)
            Dim reply As JsonElement = Parse(host.OpenSession(HarnessLogIndex.KindToolCalls, "20261017-074532-ffdb15"))

            Check(HasError(reply) Is Nothing, "opening the tool call session must not fail: " & HasError(reply))
            Check(reply.GetProperty("ok").GetBoolean(), "the session must open")

            Dim summary As JsonElement = reply.GetProperty("summary")

            Check(summary.GetProperty("totalCalls").GetInt32() = 41, "the summary must count 41 calls")
            Check(summary.GetProperty("registeredTools").GetInt32() = 18, "the summary must count 18 tools")
            Check(summary.GetProperty("permissionEvents").GetInt32() = 30, "the summary must count 30 permission events")

            Dim byStatus As JsonElement = summary.GetProperty("callsByStatus")

            Check(byStatus.GetProperty("ok").GetInt32() = 35, "35 calls went through")
            Check(byStatus.GetProperty("error").GetInt32() = 6, "6 calls failed")

            Dim byTool As JsonElement = summary.GetProperty("callsByTool")

            Check(byTool.GetProperty("multi_edit").GetInt32() = 6, "multi_edit was called 6 times")
            Check(byTool.GetProperty("read").GetInt32() = 14, "read was called 14 times")

            ' the linkage between the two logs
            Dim linked As JsonElement = reply.GetProperty("linked")

            Check(linked.ValueKind = JsonValueKind.Object, "the tool call session must name its chat log")
            Check(linked.GetProperty("sessionId").GetString() = "20261017-074532-ffdb15", "the linked chat session must share the id")
            Check(linked.GetProperty("kind").GetString() = HarnessLogIndex.KindLlmSessions, "the linked session is a chat session")

            Dim calls As JsonElement = Parse(host.Records("tool-calls.json", 0, 30, Nothing))

            Check(calls.GetProperty("total").GetInt32() = 41, "the tool call file must hold 41 records, got " & calls.GetProperty("total").GetInt32())
            Check(calls.GetProperty("items").GetArrayLength() = 30, "the first page must hold 30 records")

            Dim second As JsonElement = Parse(host.Records("tool-calls.json", 30, 30, Nothing))

            Check(second.GetProperty("items").GetArrayLength() = 11, "the second page must hold the remaining 11 records, got " & second.GetProperty("items").GetArrayLength())
            Check(second.GetProperty("items")(0).GetProperty("seq").GetInt32() = 31, "the second page must start at seq 31")

            Dim first As JsonElement = calls.GetProperty("items")(0)

            Check(first.GetProperty("tool").GetString() = "ls", "the first call was ls, got " & first.GetProperty("tool").GetString())
            Check(first.GetProperty("source").GetString() = "sandbox", "the call came from the sandbox")
            Check(first.GetProperty("status").GetString() = "ok", "the first call went through")
            Check(first.GetProperty("arguments").GetProperty("path").GetString() = ".", "the arguments of the call must be readable")
            Check(first.GetProperty("parameters").GetArrayLength() > 0, "the declared parameters must be described")

            Dim permission As JsonElement = Parse(host.Records("permissions.json", 0, 30, Nothing))

            Check(permission.GetProperty("total").GetInt32() = 30, "the permission file must hold 30 records, got " & permission.GetProperty("total").GetInt32())

            Dim tools As JsonElement = Parse(host.Records("tools.json", 0, 30, Nothing))

            Check(tools.GetProperty("total").GetInt32() = 18, "the tool file must hold 18 records, got " & tools.GetProperty("total").GetInt32())
            Check(tools.GetProperty("items")(0).GetProperty("parameters").GetArrayLength() > 0, "a registered tool must describe its parameters")
        End Sub

        Private Sub TestChatSession(host As LogHost)
            Dim reply As JsonElement = Parse(host.OpenSession(HarnessLogIndex.KindLlmSessions, "20261017-074532-ffdb15"))

            Check(HasError(reply) Is Nothing, "opening the chat session must not fail: " & HasError(reply))

            Dim summary As JsonElement = reply.GetProperty("summary")

            Check(summary.GetProperty("model").GetString() = "deepseek-flash", "the model must be recorded")
            Check(summary.GetProperty("turns").GetInt32() = 1, "the session served one turn")
            Check(summary.GetProperty("rounds").GetInt32() = 40, "the turn needed 40 rounds")
            Check(summary.GetProperty("toolCalls").GetInt32() = 55, "the session ran 55 tool calls")
            Check(summary.GetProperty("promptTokens").GetInt64() = 2819128L, "the prompt tokens must be recorded")
            Check(summary.GetProperty("events")(0).GetProperty("kind").GetString() = "max_rounds", "the session ran out of rounds")

            Dim linked As JsonElement = reply.GetProperty("linked")

            Check(linked.GetProperty("sessionId").GetString() = "20261017-074532-ffdb15", "the chat session must point back at its tool call log")
            Check(linked.GetProperty("kind").GetString() = HarnessLogIndex.KindToolCalls, "the linked session is a tool call session")

            Dim turns As JsonElement = Parse(host.Records("turns.json", 0, 30, Nothing))

            Check(turns.GetProperty("total").GetInt32() = 1, "the turn file must hold one turn, got " & turns.GetProperty("total").GetInt32())

            Dim turn As JsonElement = turns.GetProperty("items")(0)

            Check(turn.GetProperty("seq").GetInt32() = 1, "the turn is seq 1")
            Check(turn.GetProperty("status").GetString() = "max_rounds_exceeded", "the turn ran out of rounds")
            Check(turn.GetProperty("roundCount").GetInt32() = 40, "the turn summary must count 40 rounds")
            Check(turn.GetProperty("promptLength").GetInt32() > 1000, "the question of the turn must be recorded")

            ' the summary must not carry the rounds: they are fetched one at a time
            Dim raw As String = turn.GetRawText()

            Check(Not raw.Contains("""requestMessages"""), "the turn summary must not carry the request messages")

            Dim full As JsonElement = Parse(host.Turn(1))

            Check(full.GetProperty("rounds").GetArrayLength() = 40, "the complete turn must hold 40 rounds, got " & full.GetProperty("rounds").GetArrayLength())
            Check(full.GetProperty("messages").GetArrayLength() > 0, "the complete turn must list the messages it added")

            Dim round As JsonElement = Parse(host.Round(1, 1))

            Check(round.GetProperty("requestMessages").GetArrayLength() > 0, "the round must carry the request it sent")
            Check(round.GetProperty("requestMessageCount").GetInt32() >= 2, "the request carried at least the prompt and the question")
            Check(round.GetProperty("chunkCount").GetInt32() > 0, "the round must count its chunks")
            Check(round.GetProperty("firstTokenMs").GetDouble() >= 0, "the round must carry the first token delay")

            Dim calls As JsonElement = Parse(host.Records("tool-calls.json", 0, 30, Nothing))

            Check(calls.GetProperty("total").GetInt32() = 55, "the chat log must hold 55 tool calls, got " & calls.GetProperty("total").GetInt32())

            Dim firstCall As JsonElement = calls.GetProperty("items")(0)

            Check(firstCall.GetProperty("name").GetString() = "ls", "the first chat side call was ls")
            Check(firstCall.GetProperty("source").GetString() = "registry", "the call came from the registry")
            Check(firstCall.GetProperty("callId").GetString().StartsWith("call_"), "the call id of the provider must be recorded")

            Dim usage As JsonElement = Parse(host.Records("usage.json", 0, 30, Nothing))

            Check(usage.GetProperty("total").GetInt32() = 40, "the usage file must hold 40 records, got " & usage.GetProperty("total").GetInt32())
            Check(usage.GetProperty("items")(0).GetProperty("promptTokens").GetInt64() > 0, "a usage record must carry its prompt tokens")
            Check(usage.GetProperty("items")(0).GetProperty("cacheHitTokens").GetInt64() > 0, "a usage record must carry its cache hits")

            Dim messages As JsonElement = Parse(host.Records("messages.json", 0, 30, Nothing))

            Check(messages.GetProperty("total").GetInt32() = 96, "the message ledger must hold 96 records, got " & messages.GetProperty("total").GetInt32())

            Dim roles As New List(Of String)()
            Dim distinct As New HashSet(Of String)()

            For Each item In messages.GetProperty("items").EnumerateArray()
                Dim role As String = item.GetProperty("role").GetString()
                roles.Add(role)
                distinct.Add(role)
            Next

            Check(distinct.Contains("user"), "the ledger must hold the questions")
            Check(distinct.Contains("assistant"), "the ledger must hold the answers")
            Check(distinct.Contains("tool"), "the ledger must hold the tool results")
            Check(roles(0) = "user", "the ledger starts with the question")
        End Sub

        Private Sub TestFilters(host As LogHost)
            ' 过滤跑在工具调用日志上：它的记录用 tool / status / source 字段
            Dim open As JsonElement = Parse(host.OpenSession(HarnessLogIndex.KindToolCalls, "20261017-074532-ffdb15"))

            Check(HasError(open) Is Nothing, "reopening the tool call session must not fail: " & HasError(open))

            Dim failed As JsonElement = Parse(host.Records("tool-calls.json", 0, 30, "{""tool"":""multi_edit""}"))

            Check(failed.GetProperty("total").GetInt32() = 6, "six calls went to multi_edit, got " & failed.GetProperty("total").GetInt32())

            Dim errors As JsonElement = Parse(host.Records("tool-calls.json", 0, 30, "{""status"":""error""}"))

            Check(errors.GetProperty("total").GetInt32() = 6, "six calls failed, got " & errors.GetProperty("total").GetInt32())

            Dim both As JsonElement = Parse(host.Records("tool-calls.json", 0, 30, "{""tool"":""multi_edit"",""status"":""error""}"))

            Check(both.GetProperty("total").GetInt32() = 6, "every multi_edit call failed, got " & both.GetProperty("total").GetInt32())

            Dim denied As JsonElement = Parse(host.Records("permissions.json", 0, 30, "{""granted"":0}"))

            Check(denied.GetProperty("total").GetInt32() = 0, "this smoke run refused nothing, got " & denied.GetProperty("total").GetInt32())

            Dim granted As JsonElement = Parse(host.Records("permissions.json", 0, 30, "{""granted"":1}"))

            Check(granted.GetProperty("total").GetInt32() = 30, "every permission request was allowed, got " & granted.GetProperty("total").GetInt32())
            Check(granted.GetProperty("total").GetInt32() + denied.GetProperty("total").GetInt32() = 30,
                  "every permission request was either allowed or refused")

            Dim keyword As JsonElement = Parse(host.Records("tool-calls.json", 0, 30, "{""keyword"":""captions.md""}"))

            Check(keyword.GetProperty("total").GetInt32() > 0, "a keyword must narrow the records down, got " & keyword.GetProperty("total").GetInt32())

            Dim none As JsonElement = Parse(host.Records("tool-calls.json", 0, 30, "{""keyword"":""zzz-no-such-text-zzz""}"))

            Check(none.GetProperty("total").GetInt32() = 0, "a keyword that matches nothing must return nothing, got " & none.GetProperty("total").GetInt32())

            Dim empty As JsonElement = Parse(host.Records("tool-calls.json", 0, 30, "not json"))

            Check(HasError(empty) Is Nothing, "a broken filter must not break the read")
            Check(empty.GetProperty("total").GetInt32() = 41, "a broken filter must read everything, got " & empty.GetProperty("total").GetInt32())
        End Sub

        Private Sub TestPayloads(host As LogHost)
            ' 这一批日志里被外置的正文都来自请求消息，所以先从完整轮次里取一个引用
            Dim reply As JsonElement = Parse(host.OpenSession(HarnessLogIndex.KindLlmSessions, "20261017-074532-ffdb15"))
            Dim turn As JsonElement = Parse(host.Turn(1))

            Check(HasError(reply) Is Nothing AndAlso HasError(turn) Is Nothing, "reading the big turn must not fail")

            Dim payloadFile As String = Nothing

            For Each round As JsonElement In turn.GetProperty("rounds").EnumerateArray()
                If Not round.TryGetProperty("requestMessages", Nothing) Then
                    Continue For
                End If

                For Each msg As JsonElement In round.GetProperty("requestMessages").EnumerateArray()
                    If Not msg.TryGetProperty("content", Nothing) Then
                        Continue For
                    End If

                    Dim content As JsonElement = msg.GetProperty("content")

                    If content.TryGetProperty("payloadFile", Nothing) Then
                        payloadFile = content.GetProperty("payloadFile").GetString()
                        Exit For
                    End If
                Next

                If payloadFile IsNot Nothing Then
                    Exit For
                End If
            Next

            Check(payloadFile IsNot Nothing, "the big session must have spilled a request message")

            If payloadFile Is Nothing Then
                Return
            End If

            Dim page As JsonElement = Parse(host.Payload(payloadFile, 0, 1000))

            Check(HasError(page) Is Nothing, "reading a payload must not fail: " & HasError(page))
            Check(page.GetProperty("read").GetInt32() = 1000, "the first slice must read 1000 characters, got " & page.GetProperty("read").GetInt32())
            Check(page.GetProperty("text").GetString().Length = 1000, "the slice must hold 1000 characters")
            Check(page.GetProperty("size").GetInt64() > 1000, "the payload must be longer than the slice")

            Dim rest As JsonElement = Parse(host.Payload(payloadFile, 1000, 1000))

            Check(rest.GetProperty("read").GetInt32() > 0, "the second slice must read the rest of the payload")
            Check(rest.GetProperty("text").GetString() <> page.GetProperty("text").GetString(), "the second slice must continue after the first")

            Dim beyond As JsonElement = Parse(host.Payload(payloadFile, 999999999, 1000))

            Check(beyond.GetProperty("read").GetInt32() = 0, "reading past the end must return nothing, got " & beyond.GetProperty("read").GetInt32())
        End Sub

        Private Sub TestRawAndText(host As LogHost)
            Dim raw As JsonElement = Parse(host.RawFile("session.json", 4 * 1024 * 1024))

            Check(HasError(raw) Is Nothing, "reading the raw session must not fail: " & HasError(raw))
            Check(raw.GetProperty("text").GetString().Contains("sessionId"), "the raw text must be the session itself")
            Check(Not raw.GetProperty("truncated").GetBoolean(), "a small file must not be truncated")

            Dim cut As JsonElement = Parse(host.RawFile("session.json", 100))

            Check(cut.GetProperty("truncated").GetBoolean(), "a small limit must truncate the raw text")
            Check(cut.GetProperty("text").GetString().Length = 100, "the truncated text must honour the limit, got " & cut.GetProperty("text").GetString().Length)

            Dim broken As JsonElement = Parse(host.RawFile("no-such-file.json", 1024))

            Check(HasError(broken) IsNot Nothing, "an unknown file must be refused")

            Dim text As JsonElement = Parse(host.TextFile("rscript\rscript-20261017-074556.log", 0, 50))

            Check(HasError(text) Is Nothing, "reading an rscript log must not fail: " & HasError(text))
            Check(text.GetProperty("lines").GetInt32() > 0, "the rscript log must have lines")
            Check(text.GetProperty("text").GetString().Length > 0, "the rscript log must have text")
        End Sub

        Private Sub TestGuards(host As LogHost)
            Check(HasError(Parse(host.OpenSession("llm-sessions", "..\..\windows"))) IsNot Nothing,
                  "a session id that is not a folder name must be refused")
            Check(HasError(Parse(host.OpenSession("llm-sessions", "no-such-session"))) IsNot Nothing,
                  "an unknown session must be refused")
            Check(HasError(Parse(host.Records("no-such-file.json", 0, 10, Nothing))) IsNot Nothing,
                  "an unknown array file must be refused")
            Check(HasError(Parse(host.Payload("..\..\..\windows\win.ini", 0, 100))) IsNot Nothing,
                  "a payload outside the session must be refused")
            Check(HasError(Parse(host.Payload("session.json", 0, 100))) IsNot Nothing,
                  "a payload that is not in the payload folder must be refused")
            Check(HasError(Parse(host.TextFile("..\..\..\windows\win.ini", 0, 100))) IsNot Nothing,
                  "a text file outside the log folder must be refused")

            ' 前面的拒绝把会话关掉了，重新打开一个再验证容错分页
            Check(HasError(Parse(host.OpenSession(HarnessLogIndex.KindLlmSessions, "20261017-074532-ffdb15"))) Is Nothing,
                  "reopening a session must work")
            Check(HasError(Parse(host.Records("tool-calls.json", -5, 0, Nothing))) Is Nothing,
                  "a nonsense page must still read something")

            Dim nonsense As JsonElement = Parse(host.Records("tool-calls.json", -5, 0, Nothing))

            Check(nonsenseOk(nonsense), "a nonsense page must fall back to the first page")
        End Sub

        Private Function nonsenseOk(reply As JsonElement) As Boolean
            If HasError(reply) IsNot Nothing Then
                Return False
            End If

            Return reply.GetProperty("offset").GetInt32() = 0 AndAlso reply.GetProperty("items").GetArrayLength() > 0
        End Function

        ' ====================================================================
        ' helpers
        ' ====================================================================

        Private Function Parse(json As String) As JsonElement
            If String.IsNullOrWhiteSpace(json) Then
                Dim empty As JsonElement = Nothing
                Return empty
            End If

            Return JsonDocument.Parse(json).RootElement.Clone()
        End Function

        Private Function HasError(reply As JsonElement) As String
            If reply.ValueKind <> JsonValueKind.Object Then
                Return "the reply is not a json object"
            End If

            Dim value As JsonElement

            If Not reply.TryGetProperty("error", value) Then
                Return Nothing
            End If

            If value.ValueKind = JsonValueKind.Null Then
                Return Nothing
            End If

            Return value.GetString()
        End Function

        Private Sub Check(condition As Boolean, message As String)
            If condition Then
                _passed += 1
            Else
                _failed += 1
                _failures.Add(message)
                Console.WriteLine("  FAILED: " & message)
            End If
        End Sub

    End Module

End Namespace
