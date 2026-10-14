Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.IO
Imports System.Linq
Imports System.Reflection
Imports System.Threading
Imports Microsoft.VisualBasic.MIME.application.json
Imports Microsoft.VisualBasic.MIME.application.json.Javascript
Imports Harness
Imports Harness.MCP
Imports Ollama

Namespace HarnessSelfTest

    ''' <summary>
    ''' the offline, deterministic end to end verification of the harness MCP client.
    ''' every test talks to <see cref="McpStubServer"/>, the stub server built into
    ''' this same executable, so nothing here needs python, the mcp package or a network.
    ''' </summary>
    Friend Module McpSelfTest

        Private _passed As Integer
        Private _failed As Integer
        Private ReadOnly _failures As New List(Of String)
        Private _tempRoot As String

        Friend Function RunAll() As Integer
            _tempRoot = Path.Combine(Path.GetTempPath(), "harness-mcp-selftest-" & Guid.NewGuid().ToString("N").Substring(0, 8))
            Directory.CreateDirectory(_tempRoot)

            Console.WriteLine("[MCP self test] stub server: " & StubInfo().command)

            Try
                RunTest("basic round trip", AddressOf TestBasicRoundTrip)
                RunTest("cursor pagination", AddressOf TestPagination)
                RunTest("server request is answered", AddressOf TestServerRequest)
                RunTest("request timeout", AddressOf TestTimeout)
                RunTest("request cancellation", AddressOf TestCancellation)
                RunTest("transport close fails pending requests", AddressOf TestTransportClosed)
                RunTest("legacy framing tolerance", AddressOf TestLegacyFraming)
                RunTest("single server failure isolation", AddressOf TestFailureIsolation)
                RunTest("tool naming policy", AddressOf TestNamingPolicy)
                RunTest("configuration loading", AddressOf TestConfigLoading)
                RunTest("sandbox wiring", AddressOf TestSandboxWiring)
                RunTest("server process is reaped", AddressOf TestProcessReaped)
            Finally
                Try
                    Directory.Delete(_tempRoot, True)
                Catch
                End Try
            End Try

            Console.WriteLine()
            Console.WriteLine($"[MCP self test] {_passed} passed, {_failed} failed.")

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

        ' ====================================================================
        ' the tests
        ' ====================================================================

        Private Sub TestBasicRoundTrip()
            Dim server As McpStdioServer = StubServer("basic")

            Using client As New McpClient(server.Name, server.CreateTransport())
                client.ConnectAsync().GetAwaiter().GetResult()

                Check(client.Capabilities IsNot Nothing AndAlso client.Capabilities.Tools,
                      "initialize must report the tool capability")

                Dim tools As List(Of McpTool) = client.ListToolsAsync().GetAwaiter().GetResult()
                Check(tools.Count = 2, "tools/list must return 2 tools, got " & tools.Count)

                Dim echo As McpTool = tools.FirstOrDefault(Function(t) t.Name = "echo")
                Check(echo IsNot Nothing, "the stub must expose the 'echo' tool")
                Check(echo IsNot Nothing AndAlso echo.InputSchema IsNot Nothing, "a tool must carry its input schema")

                Dim args As New JsonObject()
                args("text") = New JsonValue("hello")
                args("count") = New JsonValue(2)

                Dim result As McpCallToolResult = client.CallToolAsync("echo", args).GetAwaiter().GetResult()
                Dim text As String = result.ToText()

                Check(text.Contains("stub result for echo"), "the text block must be returned: " & text)
                Check(text.Contains("stub://doc/1"), "a resource_link block must be rendered: " & text)
                Check(text.Contains("""tool"""), "the structured content must be rendered: " & text)

                Try
                    client.PingAsync().GetAwaiter().GetResult()
                    Check(True, "ping must succeed")
                Catch ex As Exception
                    Fail("ping must succeed: " & ex.Message)
                End Try
            End Using
        End Sub

        Private Sub TestPagination()
            Dim server As McpStdioServer = StubServer("paged", "--paginate")

            Using client As New McpClient(server.Name, server.CreateTransport())
                client.ConnectAsync().GetAwaiter().GetResult()

                Dim tools As List(Of McpTool) = client.ListToolsAsync().GetAwaiter().GetResult()
                Check(tools.Count = 2, "the client must follow nextCursor and merge the pages, got " & tools.Count)
            End Using
        End Sub

        Private Sub TestServerRequest()
            Dim marker As String = TempFile("server-request.txt")
            Dim server As McpStdioServer = StubServer("asker", "--server-request-marker", marker)

            Using client As New McpClient(server.Name, server.CreateTransport())
                client.ConnectAsync().GetAwaiter().GetResult()

                Check(WaitForFile(marker, 8000), "the stub must receive an answer to its server initiated request")

                If File.Exists(marker) Then
                    Dim code As String = File.ReadAllText(marker).Trim()
                    Check(code = "-32601", "the client must answer an unknown server method with -32601, got '" & code & "'")
                End If
            End Using
        End Sub

        Private Sub TestTimeout()
            Dim server As McpStdioServer = StubServer("slow", "--delay-ms", "4000")

            Using client As New McpClient(server.Name, server.CreateTransport())
                client.RequestTimeout = TimeSpan.FromMilliseconds(600)

                Dim timedOut As Boolean = False

                Try
                    client.ConnectAsync().GetAwaiter().GetResult()
                Catch ex As TimeoutException
                    timedOut = True
                Catch ex As Exception
                    Fail("a slow handshake must raise a timeout, got " & ex.GetType().Name & ": " & ex.Message)
                End Try

                Check(timedOut, "a slow server must fail with a TimeoutException instead of hanging")
            End Using
        End Sub

        Private Sub TestCancellation()
            Dim server As McpStdioServer = StubServer("cancel", "--delay-ms", "4000")

            Using client As New McpClient(server.Name, server.CreateTransport())
                Using cts As New CancellationTokenSource(500)
                    Dim cancelled As Boolean = False

                    Try
                        client.ConnectAsync(cts.Token).GetAwaiter().GetResult()
                    Catch ex As OperationCanceledException
                        cancelled = True
                    Catch ex As Exception
                        Fail("a cancelled request must surface as OperationCanceledException, got " & ex.GetType().Name)
                    End Try

                    Check(cancelled, "a cancelled request must end as cancelled")
                End Using
            End Using
        End Sub

        ''' <summary>
        ''' a server that dies during the handshake must fail the pending request at
        ''' once (the transport gone event), not after the 20 second timeout.
        ''' </summary>
        Private Sub TestTransportClosed()
            Dim server As McpStdioServer = StubServer("dead", "--crash-on-start")

            Using client As New McpClient(server.Name, server.CreateTransport())
                client.RequestTimeout = TimeSpan.FromSeconds(20)

                Dim watch As Stopwatch = Stopwatch.StartNew()
                Dim failed As Boolean = False

                Try
                    client.ConnectAsync().GetAwaiter().GetResult()
                Catch
                    failed = True
                End Try

                watch.Stop()
                Check(failed, "a server that dies on startup must fail the handshake")
                Check(watch.ElapsedMilliseconds < 10000,
                      $"the failure must be reported immediately, not after the 20s timeout (took {watch.ElapsedMilliseconds}ms)")
            End Using
        End Sub

        ''' <summary>
        ''' the client framing is newline delimited json; a server that writes an
        ''' unparsable lsp header line in front of the message must still be understood
        ''' (the line is skipped, the body is parsed).
        ''' </summary>
        Private Sub TestLegacyFraming()
            Dim server As McpStdioServer = StubServer("legacy", "--legacy-framing")

            Using client As New McpClient(server.Name, server.CreateTransport())
                client.ConnectAsync().GetAwaiter().GetResult()

                Check(client.Capabilities IsNot Nothing AndAlso client.Capabilities.Tools,
                      "the client must skip the legacy header line and still complete the handshake")

                Dim tools As List(Of McpTool) = client.ListToolsAsync().GetAwaiter().GetResult()
                Check(tools.Count = 2, "the tools must be listed despite the legacy framing, got " & tools.Count)
            End Using
        End Sub

        Private Sub TestFailureIsolation()
            Dim bridge As New McpToolBridge(StubServer("dead-one", "--crash-on-start"), StubServer("alive-one"))

            Try
                bridge.ConnectAllAsync().GetAwaiter().GetResult()

                Check(bridge.Connections.Count = 2, "both configured servers must be tracked")
                Check(bridge.Failures.Count = 1, "only the dead server may be reported as a failure, got " & bridge.Failures.Count)
                Check(bridge.Connections.Any(Function(c) c.IsConnected), "the healthy server must stay connected")

                Dim client As LLMClient = NewClient()
                bridge.Attach(client)

                Check(ToolNames(client).Contains("echo"), "the healthy server tools must be registered despite the dead one")
            Finally
                bridge.Dispose()
            End Try
        End Sub

        Private Sub TestNamingPolicy()
            ' the same tool name on two servers has to be namespaced
            Dim clashing As New McpToolBridge(StubServer("alpha"), StubServer("beta"))

            Try
                clashing.ConnectAllAsync().GetAwaiter().GetResult()

                Dim client As LLMClient = NewClient()
                clashing.Attach(client)

                Dim names As List(Of String) = ToolNames(client)
                Check(names.Contains("alpha_echo") AndAlso names.Contains("beta_echo"),
                      "clashing tool names must be prefixed with the server name, got " & String.Join(", ", names))

                Dim registration As McpRegisteredPrimitive =
                    clashing.Registrations.FirstOrDefault(Function(r) r.OriginalName = "echo" AndAlso r.ServerName = "alpha")

                Check(registration IsNot Nothing AndAlso registration.RegisteredName = "alpha_echo",
                      "the registration map must expose the resolved name")
            Finally
                clashing.Dispose()
            End Try

            ' a unique tool name keeps the name the server gave it (no prefix by default)
            Dim soloBridge As New McpToolBridge(StubServer("solo"))

            Try
                soloBridge.ConnectAllAsync().GetAwaiter().GetResult()

                Dim client As LLMClient = NewClient()
                soloBridge.Attach(client)

                Check(ToolNames(client).Contains("echo"),
                      "a unique tool name must be registered as is, got " & String.Join(", ", ToolNames(client)))
            Finally
                soloBridge.Dispose()
            End Try
        End Sub

        Private Sub TestConfigLoading()
            Dim workspace As String = Path.Combine(_tempRoot, "cfg-ws")
            Dim configPath As String = McpConfig.DefaultPath(workspace)

            Check(configPath = Path.Combine(workspace, ".harness", "mcp.json"),
                  "the default configuration path must be <workspace>\.harness\mcp.json, got " & configPath)

            Directory.CreateDirectory(Path.GetDirectoryName(configPath))

            Dim root As New JsonObject()
            Dim servers As New JsonObject()

            ' a broken entry: neither command nor url
            Dim broken As New JsonObject()
            broken("args") = New JsonArray()

            ' a valid stdio entry
            Dim good As New JsonObject()
            good("command") = New JsonValue("dotnet")
            good("args") = New JsonArray(New JsonValue("stub.dll"))
            good("cwd") = New JsonValue(".")

            servers("broken") = broken
            servers("good") = good
            root("mcpServers") = servers

            File.WriteAllText(configPath, root.BuildJsonString)

            Dim errors() As String = Nothing
            Dim loaded As McpServerDescriptor() = McpConfig.Load(configPath, workspace, errors)

            Check(loaded.Length = 1, "only the valid server must be loaded, got " & loaded.Length)
            Check(errors.Length = 1, "the broken entry must be reported, got " & errors.Length & " errors")
            Check(loaded.Length = 1 AndAlso loaded(0).Name = "good", "the loaded server must be the valid one")

            Dim stdio As McpStdioServer = TryCast(If(loaded.Length > 0, loaded(0), Nothing), McpStdioServer)

            If stdio IsNot Nothing Then
                Check(stdio.Arguments.Count = 1 AndAlso stdio.Arguments(0) = "stub.dll", "the args must be parsed")
                Check(stdio.WorkingDirectory = workspace, "a relative cwd must resolve against the workspace, got " & stdio.WorkingDirectory)
            End If

            ' a missing file is not an error
            Dim missing() As String = Nothing
            Dim none As McpServerDescriptor() = McpConfig.Load(Path.Combine(_tempRoot, "does-not-exist.json"), _tempRoot, missing)
            Check(none.Length = 0 AndAlso missing.Length = 0, "a missing configuration file must yield no server and no error")
        End Sub

        ''' <summary>
        ''' the full wiring: a workspace configuration pointing at the stub must end up
        ''' as tools on the llm client after <see cref="Sandbox.RegisterTo"/>, next to
        ''' the local tools.
        ''' </summary>
        Private Sub TestSandboxWiring()
            Dim workspace As String = Path.Combine(_tempRoot, "sandbox-ws")
            Dim configPath As String = McpConfig.DefaultPath(workspace)
            Directory.CreateDirectory(Path.GetDirectoryName(configPath))

            Dim info = StubInfo()

            Dim spec As New JsonObject()
            spec("command") = New JsonValue(info.command)

            Dim args As New JsonArray()
            For Each a In info.args
                args.Add(New JsonValue(a))
            Next
            spec("args") = args

            Dim servers As New JsonObject()
            servers("wired") = spec

            Dim root As New JsonObject()
            root("mcpServers") = servers
            File.WriteAllText(configPath, root.BuildJsonString)

            Dim client As LLMClient = NewClient()
            Dim sandbox As New Sandbox(workspace)

            sandbox.RegisterTo(client)

            Dim names As List(Of String) = ToolNames(client)
            Check(names.Contains("echo"),
                  "the mcp tool of the configured server must be registered by Sandbox.RegisterTo, got " & String.Join(", ", names))
            Check(names.Contains("read"), "the local tools must still be registered")
            Check(sandbox.McpBridge IsNot Nothing, "the sandbox must keep the mcp bridge")

            sandbox.CloseMcpServers()
        End Sub

        ''' <summary>
        ''' closing the bridge has to reap the server process (a graceful exit through
        ''' the closed stdin, and a hard kill as the fallback).
        ''' </summary>
        Private Sub TestProcessReaped()
            Dim pidFile As String = TempFile("stub.pid")
            Dim exitMarker As String = TempFile("stub.exited")

            Dim bridge As New McpToolBridge(StubServer("reap", "--pid-file", pidFile, "--exit-marker", exitMarker))

            Try
                bridge.ConnectAllAsync().GetAwaiter().GetResult()
                bridge.Attach(NewClient())

                Check(WaitForFile(pidFile, 5000), "the stub server must report its pid")

                Dim pid As Integer = -1
                If File.Exists(pidFile) Then Integer.TryParse(File.ReadAllText(pidFile).Trim(), pid)

                Check(pid > 0, "the stub pid must be readable")
            Finally
                bridge.Dispose()
            End Try

            Dim pid2 As Integer = -1
            If File.Exists(pidFile) Then Integer.TryParse(File.ReadAllText(pidFile).Trim(), pid2)

            Check(WaitForProcessExit(pid2, 10000), $"the stub server process {pid2} must be reaped after the bridge is disposed")
            Check(WaitForFile(exitMarker, 3000), "closing stdin must let the stub exit gracefully")
        End Sub

        ' ====================================================================
        ' helpers
        ' ====================================================================

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

        Private Function NewClient() As LLMClient
            Return New LLMClient(New OllamaProvider(), "harness-self-test")
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

        ''' <summary>
        ''' how this executable has to be started as the stub server: through the dotnet
        ''' host when it runs as a dll, directly when it runs as an apphost.
        ''' </summary>
        Private Function StubInfo() As (command As String, args As List(Of String))
            Dim exe As String = Environment.ProcessPath
            Dim dll As String = Assembly.GetEntryAssembly().Location
            Dim list As New List(Of String)()

            If Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase) Then
                list.Add(dll)
            End If

            list.Add("--stub-mcp-server")
            Return (exe, list)
        End Function

        Private Function StubServer(name As String, ParamArray extra As String()) As McpStdioServer
            Dim info = StubInfo()
            Dim args As New List(Of String)(info.args)

            args.AddRange(extra)
            Return New McpStdioServer(name, info.command, args.ToArray())
        End Function

        Private Function TempFile(name As String) As String
            Return Path.Combine(_tempRoot, name)
        End Function

        Private Function WaitForFile(path As String, timeoutMs As Integer) As Boolean
            Dim watch As Stopwatch = Stopwatch.StartNew()

            While watch.ElapsedMilliseconds < timeoutMs
                If File.Exists(path) Then Return True
                Thread.Sleep(50)
            End While

            Return File.Exists(path)
        End Function

        Private Function WaitForProcessExit(pid As Integer, timeoutMs As Integer) As Boolean
            If pid <= 0 Then Return False

            Dim watch As Stopwatch = Stopwatch.StartNew()

            While watch.ElapsedMilliseconds < timeoutMs
                Try
                    Dim p As Process = Process.GetProcessById(pid)

                    If p.HasExited Then Return True
                    Thread.Sleep(50)
                Catch ex As ArgumentException
                    ' no such process: it is reaped
                    Return True
                End Try
            End While

            Return False
        End Function

    End Module

End Namespace
