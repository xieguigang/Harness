Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.IO
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.VisualBasic.Serialization.JSON
Imports Harness
Imports Ollama

Namespace HarnessSelfTest

    ''' <summary>
    ''' offline, deterministic verification of the "ask_user_question" tool:
    ''' the question parsing and validation, the blocking wait, the timed
    ''' mode with its pending result and the later answer collection, and
    ''' the host event contract of the sandbox. every test drives the tool
    ''' through a host handler that answers programmatically, so nothing
    ''' here needs a network or a real user.
    ''' </summary>
    Friend Module AskUserQuestionSelfTest

        Private _passed As Integer
        Private _failed As Integer
        Private ReadOnly _failures As New List(Of String)
        Private _tempRoot As String

        Friend Function RunAll() As Integer
            _tempRoot = Path.Combine(Path.GetTempPath(), "harness-ask-selftest-" & Guid.NewGuid().ToString("N").Substring(0, 8))
            Directory.CreateDirectory(_tempRoot)

            Try
                RunTest("the tool is registered", AddressOf TestRegistered)
                RunTest("question parsing", AddressOf TestParsing)
                RunTest("question parsing failures", AddressOf TestParsingFailures)
                RunTest("blocking mode is answered", AddressOf TestBlockingAnswered)
                RunTest("timed mode times out into a pending result", AddressOf TestTimedPending)
                RunTest("timed mode collects a late answer", AddressOf TestTimedLateAnswer)
                RunTest("timed mode with timeout -1 blocks until answered", AddressOf TestTimedInfinite)
                RunTest("timed mode with timeout 0 returns at once", AddressOf TestTimedZero)
                RunTest("a missing host channel fails fast", AddressOf TestNoChannel)
                RunTest("the user may decline", AddressOf TestDeclined)
                RunTest("multi select answers", AddressOf TestMultiSelect)
                RunTest("an answered question is not asked twice", AddressOf TestIdempotent)
                RunTest("mode and timeout validation", AddressOf TestModeTimeoutValidation)
            Finally
                Try
                    Directory.Delete(_tempRoot, True)
                Catch
                End Try
            End Try

            Console.WriteLine()
            Console.WriteLine($"[ask_user_question self test] {_passed} passed, {_failed} failed.")

            For Each failure In _failures
                Console.WriteLine("  FAILED: " & failure)
            Next

            Return If(_failed = 0, 0, 1)
        End Function

        ' ====================================================================
        ' the tests
        ' ====================================================================

        ''' <summary>
        ''' the wiring: Sandbox.RegisterTo must expose the tool under its
        ''' exported name, next to the local tools.
        ''' </summary>
        Private Sub TestRegistered()
            Dim sandbox As New Sandbox(Workspace("registered"))
            Dim client As New LLMClient(New OllamaProvider(), "harness-self-test")

            sandbox.RegisterTo(client)

            Dim names As List(Of String) = ToolNames(client)

            Check(names.Contains("ask_user_question"),
                  "ask_user_question must be registered by Sandbox.RegisterTo, got " & String.Join(", ", names))
            Check(names.Contains("read"), "the local tools must still be registered")
        End Sub

        ''' <summary>
        ''' the happy paths of the parser: a full question, a single object
        ''' without the array, a double encoded array and the camelCase
        ''' spelling of the flags.
        ''' </summary>
        Private Sub TestParsing()
            Dim sandbox As New Sandbox(Workspace("parsing"))
            Dim tool As New Tools.AskUserQuestionTool(sandbox)
            Dim asked As New List(Of IReadOnlyList(Of Tools.UserQuestion))()

            AddHandler sandbox.AskUser,
                Sub(sender As Object, e As Tools.QuestionEventArg)
                    asked.Add(e.Questions)
                    e.Reply(New Dictionary(Of String, String) From {{"mode", "Fast"}})
                End Sub

            Dim result As String = tool.ask_user_question(
                "[{""id"":""mode"",""question"":""Which mode?"",""header"":""Choose Mode""," &
                """options"":[{""label"":""Fast (Recommended)"",""description"":""quicker""},{""label"":""Thorough""}]}]")

            Check(asked.Count = 1, "exactly one question batch must be asked, got " & asked.Count)
            Check(result.Contains("<answer id=""mode"">"), "the answer must echo the question id: " & result)
            Check(result.Contains("<response>Fast</response>"), "the answer must carry the response: " & result)

            If asked.Count = 1 AndAlso asked(0).Count = 1 Then
                Dim q = asked(0)(0)

                Check(q.Id = "mode", "the id must be parsed, got '" & If(q.Id, "") & "'")
                Check(q.Question = "Which mode?", "the question must be parsed, got '" & If(q.Question, "") & "'")
                Check(q.Header = "Choose Mode", "the header must be parsed, got '" & If(q.Header, "") & "'")
                Check(q.Options IsNot Nothing AndAlso q.Options.Count = 2, "both options must be parsed")
                Check(q.Options IsNot Nothing AndAlso q.Options.Count = 2 AndAlso q.Options(0).Label = "Fast (Recommended)",
                      "the option label must be parsed")
                Check(q.Options IsNot Nothing AndAlso q.Options.Count = 2 AndAlso q.Options(0).Description = "quicker",
                      "the option description must be parsed")
                Check(Not q.MultiSelect, "multi_select must default to false")
            Else
                Fail("the question must reach the host as a single entry")
            End If

            ' a single object without the surrounding array is accepted
            asked.Clear()
            result = tool.ask_user_question("{""id"":""solo"",""question"":""Ok?""}")

            Check(asked.Count = 1 AndAlso asked(0).Count = 1 AndAlso asked(0)(0).Id = "solo",
                  "a single question object must be accepted")

            ' the double encoded array is unwrapped
            asked.Clear()
            Dim encoded As String = "[{""id"":""wrapped"",""question"":""Ok?""}]".GetJson
            result = tool.ask_user_question(encoded)

            Check(asked.Count = 1 AndAlso asked(0).Count = 1 AndAlso asked(0)(0).Id = "wrapped",
                  "a double encoded question array must be unwrapped")

            ' camelCase spellings of the flags are tolerated, and an empty
            ' option list turns the question into an open one
            asked.Clear()
            result = tool.ask_user_question(
                "[{""id"":""pick"",""question"":""Pick"",""multiSelect"":true," &
                """options"":[{""label"":""A""},{""label"":""B""}]},{""id"":""open"",""question"":""Say"",""options"":[]}]")

            Check(asked.Count = 1 AndAlso asked(0).Count = 2, "both questions of the call must be asked, got " & asked.Count)
            Check(asked.Count = 1 AndAlso asked(0).Count = 2 AndAlso asked(0)(0).MultiSelect,
                  "multiSelect must be tolerated as multi_select")
            Check(asked.Count = 1 AndAlso asked(0).Count = 2 AndAlso asked(0)(0).Options.Count = 2,
                  "the options of the camelCase question must be parsed")
            Check(asked.Count = 1 AndAlso asked(0).Count = 2 AndAlso asked(0)(1).Options IsNot Nothing AndAlso asked(0)(1).Options.Count = 0,
                  "an empty option list must be an open question")
        End Sub

        ''' <summary>
        ''' every malformed call is rejected with a readable error, and none
        ''' of them may reach the user.
        ''' </summary>
        Private Sub TestParsingFailures()
            Dim sandbox As New Sandbox(Workspace("parsing-failures"))
            Dim tool As New Tools.AskUserQuestionTool(sandbox)
            Dim asked As New List(Of IReadOnlyList(Of Tools.UserQuestion))()

            AddHandler sandbox.AskUser,
                Sub(sender As Object, e As Tools.QuestionEventArg)
                    asked.Add(e.Questions)
                    e.Reply(New Dictionary(Of String, String)())
                End Sub

            Dim cases As (json As String, expect As String)() = {
                ("", "empty"),
                ("not json at all", "must be a JSON array"),
                ("[]", "no question"),
                ("[1,2]", "must be a JSON object"),
                ("[{""question"":""no id""}]", "missing its 'id'"),
                ("[{""id"":""q""}]", "missing its 'question'"),
                ("[{""id"":""a"",""question"":""x""},{""id"":""a"",""question"":""y""}]", "more than once"),
                ("[{""id"":""q"",""question"":""x"",""options"":[{""description"":""no label""}]}]", "missing its 'label'"),
                ("[{""id"":""q"",""question"":""x"",""options"":""nope""}]", "must be an array")
            }

            For Each c In cases
                Dim result As String = tool.ask_user_question(c.json)

                Check(result.StartsWith("Error:") AndAlso result.Contains(c.expect),
                      $"'{c.json}' must be rejected with a message containing '{c.expect}', got: {result}")
            Next

            Check(asked.Count = 0, "a malformed call must not reach the user, got " & asked.Count & " batches")
        End Sub

        ''' <summary>
        ''' the original behaviour: without a mode the call blocks until the
        ''' host answered, and the answer is echoed back.
        ''' </summary>
        Private Sub TestBlockingAnswered()
            Dim sandbox As New Sandbox(Workspace("blocking"))
            Dim tool As New Tools.AskUserQuestionTool(sandbox)
            Dim asked As New List(Of IReadOnlyList(Of Tools.UserQuestion))()

            AddHandler sandbox.AskUser,
                Sub(sender As Object, e As Tools.QuestionEventArg)
                    asked.Add(e.Questions)
                    e.Reply(New Dictionary(Of String, String) From {{"confirm", "yes"}})
                End Sub

            Dim result As String = tool.ask_user_question("[{""id"":""confirm"",""question"":""Proceed?""}]")

            Check(asked.Count = 1, "the question must be asked exactly once, got " & asked.Count)
            Check(result.Contains("<answer id=""confirm"">"), "the answer must echo the question id: " & result)
            Check(result.Contains("<question>Proceed?</question>"), "the answer must echo the question: " & result)
            Check(result.Contains("<response>yes</response>"), "the answer must carry the response: " & result)
        End Sub

        ''' <summary>
        ''' the timed mode: a host that does not answer leaves the call with
        ''' a pending result after the timeout, and the question stays open.
        ''' </summary>
        Private Sub TestTimedPending()
            Dim sandbox As New Sandbox(Workspace("timed-pending"))
            Dim tool As New Tools.AskUserQuestionTool(sandbox)
            Dim asked As New List(Of IReadOnlyList(Of Tools.UserQuestion))()

            AddHandler sandbox.AskUser,
                Sub(sender As Object, e As Tools.QuestionEventArg)
                    asked.Add(e.Questions)
                    ' no answer: the host stays silent
                End Sub

            Dim watch As Stopwatch = Stopwatch.StartNew()
            Dim result As String = tool.ask_user_question("[{""id"":""pick"",""question"":""Which one?""}]", "timed", 100)
            watch.Stop()

            Check(asked.Count = 1, "the question must be asked exactly once, got " & asked.Count)
            Check(result.Contains("<status>pending</status>"), "the timed timeout must report a pending result: " & result)
            Check(result.Contains("<question_ids>pick</question_ids>"), "the pending result must list the open question id: " & result)
            Check(result.Contains("remains open and answerable"), "the pending result must say the question stays answerable: " & result)
            Check(watch.ElapsedMilliseconds >= 80 AndAlso watch.ElapsedMilliseconds < 5000,
                  "the timed call must return after its timeout, took " & watch.ElapsedMilliseconds & " ms")
        End Sub

        ''' <summary>
        ''' the pending question of a timed call is collected by a later call
        ''' with the same id, once the host answered from another thread -
        ''' and the user is not asked a second time.
        ''' </summary>
        Private Sub TestTimedLateAnswer()
            Dim sandbox As New Sandbox(Workspace("timed-late"))
            Dim tool As New Tools.AskUserQuestionTool(sandbox)
            Dim asked As New List(Of IReadOnlyList(Of Tools.UserQuestion))()
            Dim pendingArg As Tools.QuestionEventArg = Nothing
            Dim json As String = "[{""id"":""pick"",""question"":""Which one?""}]"

            AddHandler sandbox.AskUser,
                Sub(sender As Object, e As Tools.QuestionEventArg)
                    asked.Add(e.Questions)
                    pendingArg = e
                End Sub

            Dim first As String = tool.ask_user_question(json, "timed", 100)

            Check(first.Contains("<status>pending</status>"), "the first timed call must time out: " & first)

            ' the host answers later, from a background thread
            Task.Run(Sub()
                         Thread.Sleep(150)
                         pendingArg.Reply(New Dictionary(Of String, String) From {{"pick", "the late one"}})
                     End Sub)

            Dim second As String = tool.ask_user_question(json, "timed", 5000)

            Check(asked.Count = 1, "the pending question must not be asked a second time, got " & asked.Count & " batches")
            Check(second.Contains("<response>the late one</response>"), "the late answer must be collected: " & second)
            Check(Not second.Contains("<status>pending</status>"), "the collected answer must not be pending: " & second)
        End Sub

        ''' <summary>
        ''' a timed call with timeout -1 keeps blocking indefinitely: it
        ''' waits for a host that answers after a while.
        ''' </summary>
        Private Sub TestTimedInfinite()
            Dim sandbox As New Sandbox(Workspace("timed-infinite"))
            Dim tool As New Tools.AskUserQuestionTool(sandbox)
            Dim asked As New List(Of IReadOnlyList(Of Tools.UserQuestion))()

            AddHandler sandbox.AskUser,
                Sub(sender As Object, e As Tools.QuestionEventArg)
                    asked.Add(e.Questions)

                    ' a slow synchronous host: the call must wait for it
                    Thread.Sleep(250)
                    e.Reply(New Dictionary(Of String, String) From {{"confirm", "yes"}})
                End Sub

            Dim watch As Stopwatch = Stopwatch.StartNew()
            Dim result As String = tool.ask_user_question("[{""id"":""confirm"",""question"":""Proceed?""}]", "timed", -1)
            watch.Stop()

            Check(asked.Count = 1, "the question must be asked exactly once, got " & asked.Count)
            Check(result.Contains("<response>yes</response>"), "timeout -1 must block until the host answered: " & result)
            Check(watch.ElapsedMilliseconds >= 200, "timeout -1 must not return before the answer, took " & watch.ElapsedMilliseconds & " ms")
        End Sub

        ''' <summary>
        ''' a timed call with timeout 0 is the non blocking poll: it returns
        ''' the pending result at once.
        ''' </summary>
        Private Sub TestTimedZero()
            Dim sandbox As New Sandbox(Workspace("timed-zero"))
            Dim tool As New Tools.AskUserQuestionTool(sandbox)
            Dim asked As New List(Of IReadOnlyList(Of Tools.UserQuestion))()

            AddHandler sandbox.AskUser,
                Sub(sender As Object, e As Tools.QuestionEventArg)
                    asked.Add(e.Questions)
                End Sub

            Dim watch As Stopwatch = Stopwatch.StartNew()
            Dim result As String = tool.ask_user_question("[{""id"":""pick"",""question"":""Which one?""}]", "timed", 0)
            watch.Stop()

            Check(asked.Count = 1, "the question must be asked exactly once, got " & asked.Count)
            Check(result.Contains("<status>pending</status>"), "timeout 0 must return the pending result: " & result)
            Check(watch.ElapsedMilliseconds < 2000, "timeout 0 must return at once, took " & watch.ElapsedMilliseconds & " ms")
        End Sub

        ''' <summary>
        ''' a session without a host channel reports the missing channel
        ''' instead of blocking forever - including in the blocking mode,
        ''' where no timeout would ever end the wait.
        ''' </summary>
        Private Sub TestNoChannel()
            Dim sandbox As New Sandbox(Workspace("no-channel"))
            Dim tool As New Tools.AskUserQuestionTool(sandbox)

            Dim watch As Stopwatch = Stopwatch.StartNew()
            Dim result As String = tool.ask_user_question("[{""id"":""confirm"",""question"":""Proceed?""}]")
            watch.Stop()

            Check(result.StartsWith("Error:") AndAlso result.Contains("no user channel"),
                  "a session without a host must report the missing channel: " & result)
            Check(watch.ElapsedMilliseconds < 2000, "the missing channel must fail fast, took " & watch.ElapsedMilliseconds & " ms")

            ' a timed call reports the same problem instead of hanging
            result = tool.ask_user_question("[{""id"":""confirm"",""question"":""Proceed?""}]", "timed", 50)

            Check(result.StartsWith("Error:") AndAlso result.Contains("no user channel"),
                  "a timed call without a host must report the missing channel too: " & result)
        End Sub

        ''' <summary>
        ''' a user that dismisses the question gets a declined result the
        ''' model can act on.
        ''' </summary>
        Private Sub TestDeclined()
            Dim sandbox As New Sandbox(Workspace("declined"))
            Dim tool As New Tools.AskUserQuestionTool(sandbox)
            Dim asked As New List(Of IReadOnlyList(Of Tools.UserQuestion))()

            AddHandler sandbox.AskUser,
                Sub(sender As Object, e As Tools.QuestionEventArg)
                    asked.Add(e.Questions)
                    e.Decline()
                End Sub

            Dim result As String = tool.ask_user_question("[{""id"":""confirm"",""question"":""Proceed?""}]")

            Check(asked.Count = 1, "the question must be asked exactly once, got " & asked.Count)
            Check(result.Contains("declined to answer"), "a dismissed question must be reported as declined: " & result)
            Check(result.Contains("<answer id=""confirm"">"), "the declined answer must still echo the question id: " & result)
        End Sub

        ''' <summary>
        ''' a multi select question carries the joined labels of the chosen
        ''' options back to the model.
        ''' </summary>
        Private Sub TestMultiSelect()
            Dim sandbox As New Sandbox(Workspace("multi-select"))
            Dim tool As New Tools.AskUserQuestionTool(sandbox)
            Dim asked As New List(Of IReadOnlyList(Of Tools.UserQuestion))()

            AddHandler sandbox.AskUser,
                Sub(sender As Object, e As Tools.QuestionEventArg)
                    asked.Add(e.Questions)
                    e.Reply(New Dictionary(Of String, String) From {{"pick", "Alpha, Beta"}})
                End Sub

            Dim result As String = tool.ask_user_question(
                "[{""id"":""pick"",""question"":""Pick any""," +
                """multi_select"":true,""options"":[{""label"":""Alpha""},{""label"":""Beta""},{""label"":""Gamma""}]}]")

            Check(asked.Count = 1 AndAlso asked(0).Count = 1 AndAlso asked(0)(0).MultiSelect,
                  "multi_select must reach the host as true")
            Check(result.Contains("<response>Alpha, Beta</response>"), "the joined labels must be reported: " & result)
        End Sub

        ''' <summary>
        ''' a repeated call with an already answered id reports the recorded
        ''' answer without raising the event a second time.
        ''' </summary>
        Private Sub TestIdempotent()
            Dim sandbox As New Sandbox(Workspace("idempotent"))
            Dim tool As New Tools.AskUserQuestionTool(sandbox)
            Dim calls As Integer = 0
            Dim json As String = "[{""id"":""confirm"",""question"":""Proceed?""}]"

            AddHandler sandbox.AskUser,
                Sub(sender As Object, e As Tools.QuestionEventArg)
                    calls += 1
                    e.Reply(New Dictionary(Of String, String) From {{"confirm", "yes"}})
                End Sub

            Dim first As String = tool.ask_user_question(json)
            Dim second As String = tool.ask_user_question(json)

            Check(first.Contains("<response>yes</response>"), "the first call must be answered: " & first)
            Check(second.Contains("<response>yes</response>"), "the repeated call must report the recorded answer: " & second)
            Check(calls = 1, "the user must not be asked a second time, got " & calls & " events")
        End Sub

        ''' <summary>
        ''' the mode and the timeout are validated, while a bogus timeout is
        ''' simply ignored in the blocking mode - and a missing timeout of
        ''' the timed mode falls back to its default instead of failing.
        ''' </summary>
        Private Sub TestModeTimeoutValidation()
            Dim sandbox As New Sandbox(Workspace("validation"))
            Dim tool As New Tools.AskUserQuestionTool(sandbox)
            Dim asked As New List(Of IReadOnlyList(Of Tools.UserQuestion))()
            Dim json As String = "[{""id"":""confirm"",""question"":""Proceed?""}]"

            AddHandler sandbox.AskUser,
                Sub(sender As Object, e As Tools.QuestionEventArg)
                    asked.Add(e.Questions)

                    ' answer whatever id this call asked
                    e.Reply(New Dictionary(Of String, String) From {{e.Questions(0).Id, "yes"}})
                End Sub

            Dim result As String = tool.ask_user_question(json, "wat")

            Check(result.StartsWith("Error:") AndAlso result.Contains("unknown mode"),
                  "an unknown mode must be rejected: " & result)

            result = tool.ask_user_question(json, "timed", -5)

            Check(result.StartsWith("Error:") AndAlso result.Contains("timeout"),
                  "a negative timeout other than -1 must be rejected: " & result)

            ' the blocking mode ignores the timeout entirely
            asked.Clear()
            result = tool.ask_user_question(json, mode:="blocking", timeout:=-5)

            Check(result.Contains("<response>yes</response>"), "the blocking mode must ignore a bogus timeout: " & result)
            Check(asked.Count = 1, "the blocking call must still be asked, got " & asked.Count & " batches")

            ' a NaN timeout of the timed mode falls back to the default: the
            ' question is asked under a fresh id (an already answered one is
            ' reported from the registry instead of being asked again)
            asked.Clear()
            result = tool.ask_user_question("[{""id"":""late"",""question"":""Proceed?""}]", "timed", Double.NaN)

            Check(result.Contains("<response>yes</response>"), "a NaN timeout must fall back to the default: " & result)
            Check(asked.Count = 1 AndAlso asked(0).Count = 1 AndAlso asked(0)(0).Id = "late",
                  "the NaN timeout call must still be asked, got " & asked.Count & " batches")
        End Sub

        ' ====================================================================
        ' helpers
        ' ====================================================================

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
            Console.WriteLine("   FAIL: " & message)
        End Sub

        Private Function Workspace(name As String) As String
            Return Path.Combine(_tempRoot, name)
        End Function

        Private Function ToolNames(client As LLMClient) As List(Of String)
            Dim names As New List(Of String)()

            If client.tools IsNot Nothing Then
                For Each tool In client.tools
                    names.Add(tool.[function].name)
                Next
            End If

            Return names
        End Function

    End Module

End Namespace
