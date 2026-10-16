Imports System.IO
Imports System.Linq
Imports System.Reflection
Imports System.Text
Imports System.Text.Json
Imports System.Threading
Imports System.Text.Json.Nodes
Imports Harness
Imports Harness.Logging
Imports Ollama
Imports Ollama.JSON.FunctionCall

Namespace HarnessSelfTest

    ''' <summary>
    ''' the offline verification of the structured tool call log. nothing here
    ''' talks to a model: the tools are registered on a client and then driven
    ''' through the very same function registry the llm response would use, so
    ''' the log is exercised end to end without a network.
    ''' </summary>
    Friend Module ToolLogSelfTest

        Private _passed As Integer
        Private _failed As Integer
        Private ReadOnly _failures As New List(Of String)
        Private _tempRoot As String

        Friend Function RunAll() As Integer
            _tempRoot = Path.Combine(Path.GetTempPath(), "harness-toollog-selftest-" & Guid.NewGuid().ToString("N").Substring(0, 8))
            Directory.CreateDirectory(_tempRoot)

            Try
                RunTest("registration keeps one schema per tool", AddressOf TestSchemaNotDuplicated)
                RunTest("a read call is logged end to end", AddressOf TestReadLogged)
                RunTest("a reported failure is marked as an error", AddressOf TestErrorLogged)
                RunTest("a refusal by the policy is marked as denied", AddressOf TestDeniedLogged)
                RunTest("the host context reaches the record", AddressOf TestHostContext)
                RunTest("an oversized result is spilled to its own file", AddressOf TestPayloadSpill)
                RunTest("concurrent calls are all recorded", AddressOf TestConcurrency)
                RunTest("a damaged log tail is repaired", AddressOf TestTailRepair)
                RunTest("permission decisions are recorded", AddressOf TestPermissionEvents)
                RunTest("an mcp tool is logged with its server", AddressOf TestMcpLogged)
                RunTest("the session summary closes the log", AddressOf TestSessionSummary)
            Finally
                If Environment.GetEnvironmentVariable("HARNESS_KEEP_TOOLLOG") <> "1" Then
                    Try
                        Directory.Delete(_tempRoot, True)
                    Catch
                    End Try
                End If
            End Try

            Console.WriteLine()
            Console.WriteLine($"[tool log self test] {_passed} passed, {_failed} failed.")

            If Environment.GetEnvironmentVariable("HARNESS_KEEP_TOOLLOG") = "1" Then
                Console.WriteLine("the logs the test wrote are kept for review in " & _tempRoot)
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

        ' ====================================================================
        ' helpers
        ' ====================================================================

        ''' <summary>
        ''' a client that is only used to register tools on: the endpoint is
        ''' never contacted, so the test stays offline.
        ''' </summary>
        Private Function NewClient() As LLMClient
            Return New LLMClient(New OpenAIProvider("http://127.0.0.1:1/", "no-key"), "tool-log-test")
        End Function

        Private Function NewWorkspace(tag As String) As String
            Dim dir As String = Path.Combine(_tempRoot, tag)

            Directory.CreateDirectory(dir)

            Return dir
        End Function

        ''' <summary>
        ''' drive one registered tool the way the llm response would: through
        ''' the function registry of the client. the registry is a private
        ''' field of the client, so the test reaches it by reflection - it is
        ''' the only way to exercise the wrapped backend without a model.
        ''' </summary>
        Private Function Drive(client As LLMClient, name As String, ParamArray args As String()) As String
            Dim field As FieldInfo = GetType(LLMClient).GetField("_caller", BindingFlags.Instance Or BindingFlags.NonPublic)

            If field Is Nothing Then
                Throw New InvalidOperationException("the llm client no longer holds a '_caller' field")
            End If

            Dim registry As Object = field.GetValue(client)
            Dim method As MethodInfo = registry.GetType().GetMethod("Call")
            Dim arguments As New Dictionary(Of String, String)(StringComparer.Ordinal)

            For i As Integer = 0 To args.Length - 1 Step 2
                arguments(args(i)) = args(i + 1)
            Next

            Dim invocation As New FunctionCall With {
                .name = name,
                .arguments = arguments
            }

            Return DirectCast(method.Invoke(registry, New Object() {invocation}), String)
        End Function

        ''' <summary>parse a log file; a broken one is reported as a failure instead of throwing.</summary>
        Private Function ReadArray(path As String) As JsonElement
            Dim text As String = File.ReadAllText(path, New UTF8Encoding(False))
            Dim document As JsonDocument = JsonDocument.Parse(text)

            Return document.RootElement.Clone()
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

        Private Sub CloseLog(sandbox As Sandbox)
            Call sandbox.CloseToolLog()
        End Sub

        ' ====================================================================
        ' the tests
        ' ====================================================================

        ''' <summary>
        ''' the two step registration must not leave the model with a doubled
        ''' tool list: every tool appears exactly once, with the schema ollama
        ''' built.
        ''' </summary>
        Private Sub TestSchemaNotDuplicated()
            Dim ws As String = NewWorkspace("schema")
            Dim sandbox As New Sandbox(ws, SecurityPolicy.FullControls)
            Dim client As LLMClient = NewClient()

            Call sandbox.RegisterTo(client)

            Dim names As List(Of String) = client.tools _
                .Select(Function(t) If(t.function Is Nothing, Nothing, t.function.name)) _
                .ToList()

            Check(names.Count = sandbox.ToolNames.Length,
                  $"the model must see {sandbox.ToolNames.Length} tools, saw {names.Count}")
            Check(names.Distinct(StringComparer.Ordinal).Count = names.Count,
                  "no tool may be registered twice: " & String.Join(", ", names))
            Check(sandbox.ToolLog.Registrations.Count = sandbox.ToolNames.Length,
                  $"the log must know {sandbox.ToolNames.Length} tools, knows {sandbox.ToolLog.Registrations.Count}")

            Dim read As ToolRegistrationRecord = sandbox.ToolLog.Registrations.FirstOrDefault(Function(r) r.Tool = "read")

            Check(read IsNot Nothing, "the 'read' tool must appear in the registration snapshot")
            Check(read IsNot Nothing AndAlso read.Parameters.Any(Function(p) p.Name = "file_path" AndAlso p.Required),
                  "the 'file_path' parameter of read must be exported as required")

            Call CloseLog(sandbox)
        End Sub

        ''' <summary>
        ''' one ordinary call: the result reaches the model unchanged and the
        ''' record carries the tool, its arguments, its result and a duration.
        ''' </summary>
        Private Sub TestReadLogged()
            Dim ws As String = NewWorkspace("read")
            Dim notePath As String = Path.Combine(ws, "note.txt")

            File.WriteAllText(notePath,"line one" & vbCrLf & "line two", New UTF8Encoding(False))

            Dim sandbox As New Sandbox(ws, SecurityPolicy.FullControls)
            Dim client As LLMClient = NewClient()

            Call sandbox.RegisterTo(client)

            Dim result As String = Drive(client, "read", "file_path", notePath)

            Check(result IsNot Nothing AndAlso result.Contains("line one"),
                  "the read tool must still return the content of the file")
            Check(result IsNot Nothing AndAlso result.Contains("<path>"),
                  "the framing of the read tool must not change")

            Call CloseLog(sandbox)

            Dim logFile As String = sandbox.ToolLog.ToolCallsFile

            Check(File.Exists(logFile), "tool-calls.json must exist: " & logFile)

            Dim records As JsonElement = ReadArray(logFile)

            Check(records.ValueKind = JsonValueKind.Array, "tool-calls.json must be a json array")
            Check(records.GetArrayLength() = 1, "exactly one call was made, got " & records.GetArrayLength())

            Dim record As JsonElement = records(0)

            Check(Str(record, "tool") = "read", "the record must name the tool, got " & Str(record, "tool"))
            Check(Str(record, "source") = ToolLogText.SourceSandbox, "the source must be 'sandbox'")
            Check(Str(record, "status") = ToolLogText.StatusOk, "a successful read must be 'ok', got " & Str(record, "status"))
            Check(Str(record, "workspace") = ws, "the record must carry the workspace")
            Check(Str(record, "securityPolicy") = "FullControls", "the record must carry the security policy")
            Check(Num(record, "durationMs") >= 0, "the record must carry a duration")
            Check(Num(record, "resultLength") > 0, "the record must carry the length of the result")
            Check(Str(record, "result") IsNot Nothing AndAlso Str(record, "result").Contains("line one"),
                  "the complete result must be inlined into the record")
            Dim arguments As JsonElement

            Check(record.TryGetProperty("arguments", arguments) AndAlso Str(arguments, "file_path") = notePath,
                  "the arguments of the call must be recorded")

            Dim parameters As JsonElement

            Check(record.TryGetProperty("parameters", parameters) AndAlso parameters.GetArrayLength() > 0,
                  "the declared parameters must be described one by one")
            Check(record.TryGetProperty("parameters", parameters) AndAlso
                  parameters.EnumerateArray().Any(Function(p) Str(p, "name") = "file_path" AndAlso Str(p, "origin") = "model"),
                  "file_path must be reported as filled by the model")
            Check(record.TryGetProperty("parameters", parameters) AndAlso
                  parameters.EnumerateArray().Any(Function(p) Str(p, "name") = "offset" AndAlso Str(p, "origin") = "default"),
                  "an omitted optional parameter must be reported as defaulted")

            Check(File.Exists(sandbox.ToolLog.ToolsFile), "tools.json must exist")
            Check(ReadArray(sandbox.ToolLog.ToolsFile).GetArrayLength() = sandbox.ToolNames.Length,
                  "tools.json must hold every registered tool")
        End Sub

        ''' <summary>
        ''' a tool that reports a failure through its result text keeps that
        ''' text for the model and is marked as an error in the log.
        ''' </summary>
        Private Sub TestErrorLogged()
            Dim ws As String = NewWorkspace("error")
            Dim sandbox As New Sandbox(ws, SecurityPolicy.FullControls)
            Dim client As LLMClient = NewClient()

            Call sandbox.RegisterTo(client)

            Dim missing As String = Path.Combine(ws, "nope.txt")
            Dim result As String = Drive(client, "read", "file_path", missing)

            Check(result IsNot Nothing AndAlso result.StartsWith("Error:", StringComparison.Ordinal),
                  "reading a missing file must still be reported as an error to the model")

            Call CloseLog(sandbox)

            Dim record As JsonElement = ReadArray(sandbox.ToolLog.ToolCallsFile)(0)

            Check(Str(record, "status") = ToolLogText.StatusError,
                  "a result starting with 'Error:' must be logged as 'error', got " & Str(record, "status"))
            Check(Str(record, "errorType") Is Nothing, "a handled failure is not an exception")
            Check(Str(record, "result") IsNot Nothing AndAlso Str(record, "result").Contains(missing),
                  "the message the model saw must be kept in the record")
        End Sub

        ''' <summary>
        ''' what the sandbox refuses is marked as denied and shows up in the
        ''' permission log as well.
        ''' </summary>
        Private Sub TestDeniedLogged()
            Dim ws As String = NewWorkspace("denied")
            Dim sandbox As New Sandbox(ws, SecurityPolicy.[Readonly])
            Dim client As LLMClient = NewClient()

            Call sandbox.RegisterTo(client)

            Dim result As String = Drive(client, "write", "file_path", "note.txt", "content", "hello")

            Check(result IsNot Nothing AndAlso result.Contains("security policy"),
                  "the write must be refused by the readonly policy")

            Call CloseLog(sandbox)

            Dim record As JsonElement = ReadArray(sandbox.ToolLog.ToolCallsFile)(0)

            Check(Str(record, "status") = ToolLogText.StatusDenied,
                  "a refusal must be logged as 'denied', got " & Str(record, "status"))

            Dim permissions As JsonElement = ReadArray(sandbox.ToolLog.PermissionsFile)

            Check(permissions.GetArrayLength() >= 1, "the refusal must appear in permissions.json")
            Check(permissions(0).GetProperty("granted").GetBoolean() = False, "the refusal must be recorded as not granted")
            Check(Str(permissions(0), "operation") = "Write", "the refused operation must be named")
        End Sub

        ''' <summary>
        ''' what the host was doing at the moment of a call is part of the
        ''' record when the host hands in a context provider.
        ''' </summary>
        Private Sub TestHostContext()
            Dim ws As String = NewWorkspace("context")
            Dim notePath As String = Path.Combine(ws, "note.txt")

            File.WriteAllText(notePath,"hello", New UTF8Encoding(False))

            Dim sandbox As New Sandbox(ws, SecurityPolicy.FullControls)

            sandbox.ToolLogContextProvider = Function() New ToolCallContext With {
                .Task = "polish the draft",
                .Round = "7",
                .Extra = New Dictionary(Of String, String)(StringComparer.Ordinal) From {{"job", "j-1"}}
            }

            Dim client As LLMClient = NewClient()

            Call sandbox.RegisterTo(client)
            Call Drive(client, "read", "file_path", notePath)
            Call CloseLog(sandbox)

            Dim record As JsonElement = ReadArray(sandbox.ToolLog.ToolCallsFile)(0)
            Dim context As JsonElement

            Check(record.TryGetProperty("context", context), "the record must carry the host context")
            Check(Str(context, "task") = "polish the draft", "the task of the host must be recorded")
            Check(Str(context, "round") = "7", "the round of the host must be recorded")
            Check(Str(context, "extra.job") = "j-1", "custom host fields must be recorded")
        End Sub

        ''' <summary>
        ''' a result too long to inline is written next to the log and the
        ''' record keeps its length, its hash, a preview and the path.
        ''' </summary>
        Private Sub TestPayloadSpill()
            Dim ws As String = NewWorkspace("spill")
            Dim notePath As String = Path.Combine(ws, "big.txt")

            File.WriteAllText(notePath,New String("x"c, 4000), New UTF8Encoding(False))

            Dim sandbox As New Sandbox(ws, SecurityPolicy.FullControls)

            sandbox.ToolLog.Options.MaxInlineChars = 200
            sandbox.ToolLog.Options.PreviewChars = 50

            Dim client As LLMClient = NewClient()

            Call sandbox.RegisterTo(client)
            Call Drive(client, "read", "file_path", notePath)
            Call CloseLog(sandbox)

            Dim record As JsonElement = ReadArray(sandbox.ToolLog.ToolCallsFile)(0)

            Check(record.GetProperty("resultTruncated").GetBoolean(), "an oversized result must be marked as truncated")
            Check(Str(record, "result") Is Nothing, "an oversized result must not be inlined")
            Check(Str(record, "resultSha256") IsNot Nothing, "a spilled result must be hashed")
            Check(Str(record, "resultPreview") IsNot Nothing AndAlso Str(record, "resultPreview").Length = 50,
                  "a spilled result must keep a preview")
            Check(Num(record, "resultLength") > 4000, "the record must carry the full length of the result")

            Dim relative As String = Str(record, "resultPayloadFile")

            Check(relative IsNot Nothing, "the record must point at the payload file")
            Check(File.Exists(Path.Combine(sandbox.ToolLog.Folder, relative.Replace("/"c, Path.DirectorySeparatorChar))),
                  "the payload file must exist on disk")

            Dim spilled As String = File.ReadAllText(Path.Combine(sandbox.ToolLog.Folder, relative.Replace("/"c, Path.DirectorySeparatorChar)))

            Check(spilled.Length > 4000, "the payload file must hold the complete result")
        End Sub

        ''' <summary>
        ''' background jobs call the same tools from other threads: every call
        ''' has to reach the log with its own sequence number.
        ''' </summary>
        Private Sub TestConcurrency()
            Dim ws As String = NewWorkspace("concurrent")
            Dim notePath As String = Path.Combine(ws, "note.txt")

            File.WriteAllText(notePath,"hello", New UTF8Encoding(False))

            Dim sandbox As New Sandbox(ws, SecurityPolicy.FullControls)
            Dim client As LLMClient = NewClient()

            Call sandbox.RegisterTo(client)

            Dim rounds As Integer = 12

            System.Threading.Tasks.Parallel.For(0, rounds,
                Sub(i As Integer)
                    Call Drive(client, "read", "file_path", notePath)
                End Sub)

            Call CloseLog(sandbox)

            Dim records As JsonElement = ReadArray(sandbox.ToolLog.ToolCallsFile)

            Check(records.GetArrayLength() = rounds,
                  $"all {rounds} concurrent calls must be recorded, got {records.GetArrayLength()}")

            Dim seqs As List(Of Integer) = records.EnumerateArray() _
                .Select(Function(r) CInt(r.GetProperty("seq").GetInt32())) _
                .ToList()

            Check(seqs.Distinct().Count() = rounds, "every record must have its own sequence number")
            Check(seqs.Min() = 1 AndAlso seqs.Max() = rounds, "the sequence numbers must run from 1 to the number of calls")
            Check(sandbox.ToolLog.Records.Count = rounds, "the in memory log must agree with the file")
        End Sub

        ''' <summary>
        ''' a log left behind by a killed process is reopened and repaired
        ''' instead of being corrupted further.
        ''' </summary>
        Private Sub TestTailRepair()
            Dim ws As String = NewWorkspace("repair")
            Dim notePath As String = Path.Combine(ws, "note.txt")

            File.WriteAllText(notePath,"hello", New UTF8Encoding(False))

            Dim sandbox As New Sandbox(ws, SecurityPolicy.FullControls)

            ' the tail of a file whose process died between two records
            Directory.CreateDirectory(sandbox.ToolLog.Folder)
            File.WriteAllText(sandbox.ToolLog.ToolCallsFile, "[" & vbLf & "{""seq"": 0, ""tool"": ""ghost""},", New UTF8Encoding(False))

            Dim client As LLMClient = NewClient()

            Call sandbox.RegisterTo(client)
            Call Drive(client, "read", "file_path", notePath)
            Call CloseLog(sandbox)

            Dim records As JsonElement = ReadArray(sandbox.ToolLog.ToolCallsFile)

            Check(records.ValueKind = JsonValueKind.Array, "the repaired file must still be a json array")
            ' the ghost record, the null that closed the dangling comma and
            ' the call of this session
            Check(records.GetArrayLength() = 3, "the reopened file must stay readable, got " & records.GetArrayLength())
            Check(Str(records(2), "tool") = "read", "the appended record must be the real call")
        End Sub

        ''' <summary>
        ''' a granted and a denied escalation both show up in permissions.json
        ''' with the mode and the justification the model gave.
        ''' </summary>
        Private Sub TestPermissionEvents()
            Dim ws As String = NewWorkspace("permission")
            Dim sandbox As New Sandbox(ws, SecurityPolicy.AskPermission)
            Dim asked As Integer = 0

            ' a host that refuses every plain request and only gives in for the
            ' one-shot escalation the model asks for
            AddHandler sandbox.RequestPermission, Sub(sender As Object, e As PermissionEventArg)
                                                      asked += 1
                                                      e.PermissionGranted = (e.RequestedPermission IsNot Nothing)
                                                  End Sub

            Dim client As LLMClient = NewClient()

            Call sandbox.RegisterTo(client)

            Dim first As String = Drive(client, "write", "file_path", "note.txt", "content", "hello")
            Dim second As String = Drive(client, "write",
                                         "file_path", "note.txt",
                                         "content", "hello",
                                         "sandbox_permissions", "workspace-write",
                                         "justification", "the draft has to be saved")

            Check(first IsNot Nothing AndAlso first.Contains("permission denied"), "the first write must be denied")
            Check(second IsNot Nothing AndAlso Not second.Contains("permission denied"), "the escalated write must go through")
            Check(File.Exists(Path.Combine(ws, "note.txt")), "the granted write must have created the file")

            Call CloseLog(sandbox)

            Dim permissions As JsonElement = ReadArray(sandbox.ToolLog.PermissionsFile)

            ' the first call is refused by the policy, the second one is
            ' refused as well and then goes through as an escalation
            Check(permissions.GetArrayLength() = 3, "all three decisions must be recorded, got " & permissions.GetArrayLength())
            Check(permissions(0).GetProperty("granted").GetBoolean() = False, "the first decision was a denial")
            Check(permissions(1).GetProperty("granted").GetBoolean() = False, "the second call is denied by the policy first")
            Check(permissions(2).GetProperty("granted").GetBoolean() = True, "the escalation was granted")
            Check(Str(permissions(2), "requestedPermission") = "workspace-write", "the requested mode must be recorded")
            Check(Str(permissions(2), "justification") = "the draft has to be saved", "the justification must be recorded")

            Dim log As JsonElement = ReadArray(sandbox.ToolLog.ToolCallsFile)

            Check(Str(log(0), "status") = ToolLogText.StatusDenied, "the denied call must be marked denied")
            Check(Str(log(1), "status") = ToolLogText.StatusOk, "the granted call must be marked ok")
        End Sub

        ''' <summary>
        ''' a primitive of an mcp server is logged as what it is: a call that
        ''' came from a server, not from the sandbox.
        ''' </summary>
        Private Sub TestMcpLogged()
            Dim ws As String = NewWorkspace("mcp")
            Dim configPath As String = MCP.McpConfig.DefaultPath(ws)

            Directory.CreateDirectory(Path.GetDirectoryName(configPath))

            ' the stub server of this very executable: no python, no network
            Dim exe As String = Environment.ProcessPath
            Dim dll As String = Assembly.GetEntryAssembly().Location
            Dim stubArgs As New List(Of String)()

            If Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase) Then
                stubArgs.Add(dll)
            End If

            stubArgs.Add("--stub-mcp-server")

            Dim spec As New JsonObject()
            spec("command") = JsonValue.Create(exe)

            Dim argv As New JsonArray()
            For Each a In stubArgs
                argv.Add(JsonValue.Create(a))
            Next
            spec("args") = argv

            Dim servers As New JsonObject()
            servers("stub") = spec

            Dim root As New JsonObject()
            root("mcpServers") = servers

            Dim config As String = root.ToJsonString()

            File.WriteAllText(configPath, config, New UTF8Encoding(False))

            Dim sandbox As New Sandbox(ws, SecurityPolicy.FullControls)
            Dim client As LLMClient = NewClient()

            Call sandbox.RegisterTo(client)

            Dim result As String = Drive(client, "echo", "text", "hello")

            Check(sandbox.McpBridge IsNot Nothing, "the configured server must have been bridged in")
            Check(result IsNot Nothing AndAlso result.Contains("stub result for echo"),
                  "the mcp tool must still answer through the wrapped backend")

            Call sandbox.CloseMcpServers()
            Call CloseLog(sandbox)

            Dim record As JsonElement = ReadArray(sandbox.ToolLog.ToolCallsFile)(0)

            Check(Str(record, "tool") = "echo", "the record must name the mcp tool, got " & Str(record, "tool"))
            Check(Str(record, "source") = ToolLogText.SourceMcp, "the source of an mcp call must be 'mcp'")
            Check(Str(record, "server") = "stub", "the record must carry the mcp server")
            Check(Str(record, "status") = ToolLogText.StatusOk, "the mcp call must be recorded as ok")

            Dim snapshot As ToolRegistrationRecord = sandbox.ToolLog.Registrations.FirstOrDefault(Function(r) r.Tool = "echo")

            Check(snapshot IsNot Nothing AndAlso snapshot.Server = "stub", "the snapshot must carry the server as well")
        End Sub

        ''' <summary>
        ''' closing the log writes session.json: what ran, how long it took
        ''' and everything that went wrong.
        ''' </summary>
        Private Sub TestSessionSummary()
            Dim ws As String = NewWorkspace("summary")
            Dim notePath As String = Path.Combine(ws, "note.txt")

            File.WriteAllText(notePath,"hello", New UTF8Encoding(False))

            Dim sandbox As New Sandbox(ws, SecurityPolicy.FullControls)
            Dim client As LLMClient = NewClient()

            Call sandbox.RegisterTo(client)
            Call Drive(client, "read", "file_path", notePath)
            Call Drive(client, "read", "file_path", Path.Combine(ws, "missing.txt"))
            Call CloseLog(sandbox)

            Dim text As String = File.ReadAllText(sandbox.ToolLog.SessionFile, New UTF8Encoding(False))
            Dim summary As JsonElement = JsonDocument.Parse(text).RootElement.Clone()

            Check(summary.ValueKind = JsonValueKind.Object, "session.json must be a json object")
            Check(Str(summary, "workspace") = ws, "the summary must name the workspace")
            Check(Num(summary, "totalCalls") = 2, "the summary must count the calls, got " & Num(summary, "totalCalls"))
            Check(summary.GetProperty("persistent").GetBoolean(), "the log must report itself as written to disk")
            Check(summary.GetProperty("registeredTools").GetInt32() = sandbox.ToolNames.Length, "the summary must count the tools")

            Dim byStatus As JsonElement = summary.GetProperty("callsByStatus")

            Check(byStatus.GetProperty("ok").GetInt32() = 1, "one call succeeded")
            Check(byStatus.GetProperty("error").GetInt32() = 1, "one call failed")

            Dim byTool As JsonElement = summary.GetProperty("callsByTool")

            Check(byTool.GetProperty("read").GetInt32() = 2, "both calls went to the read tool")

            Dim errors As JsonElement = summary.GetProperty("errors")

            Check(errors.GetArrayLength() = 1, "the failure must be listed, got " & errors.GetArrayLength())
            Check(Str(errors(0), "tool") = "read", "the failing tool must be named in the error list")
        End Sub

    End Module

End Namespace
