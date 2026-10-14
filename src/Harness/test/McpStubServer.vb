Imports System.IO
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.VisualBasic.MIME.application.json
Imports Microsoft.VisualBasic.MIME.application.json.Javascript

Namespace HarnessSelfTest

    ''' <summary>
    ''' command line options of the built in stub MCP server.
    ''' </summary>
    Friend Class StubOptions

        Public Property ToolName As String = "echo"
        Public Property DelayMs As Integer = 0
        Public Property HangTool As String
        Public Property Paginate As Boolean
        Public Property ServerRequestMarker As String
        Public Property PidFile As String
        Public Property ExitMarker As String
        Public Property CrashOnStart As Boolean
        Public Property LegacyFraming As Boolean

        Public Sub New(args As String())
            Dim i As Integer = 0

            While args IsNot Nothing AndAlso i < args.Length
                Dim key As String = args(i)

                Select Case key
                    Case "--tool-name"
                        i += 1 : ToolName = args(i)
                    Case "--delay-ms"
                        i += 1 : DelayMs = Integer.Parse(args(i))
                    Case "--hang-tool"
                        i += 1 : HangTool = args(i)
                    Case "--paginate"
                        Paginate = True
                    Case "--server-request-marker"
                        i += 1 : ServerRequestMarker = args(i)
                    Case "--pid-file"
                        i += 1 : PidFile = args(i)
                    Case "--exit-marker"
                        i += 1 : ExitMarker = args(i)
                    Case "--crash-on-start"
                        CrashOnStart = True
                    Case "--legacy-framing"
                        LegacyFraming = True
                End Select

                i += 1
            End While
        End Sub
    End Class

    ''' <summary>
    ''' a dependency free MCP server spoken over stdio: one json-rpc message per line,
    ''' exactly like the MCP specification requires. it implements initialize,
    ''' notifications/initialized, ping, tools/list (with optional cursor paging),
    ''' tools/call, resources/list and prompts/list, which is everything the harness
    ''' client needs to be verified offline - no python, no mcp package, no network.
    ''' </summary>
    Friend Module McpStubServer

        Friend Function Run(args As String()) As Integer
            Dim options As New StubOptions(args)

            ' a server that cannot start: the client must survive it and report it
            If options.CrashOnStart Then
                Console.Error.WriteLine("harness stub: simulated startup failure")
                Return 3
            End If

            WriteTextFile(options.PidFile, Process.GetCurrentProcess().Id.ToString())

            Dim reader As New StreamReader(Console.OpenStandardInput(), New UTF8Encoding(False), False, 4096)
            Dim stdout As Stream = Console.OpenStandardOutput()

            Try
                While True
                    Dim line As String = reader.ReadLine()
                    If line Is Nothing Then Exit While

                    line = line.Trim()
                    If line.Length = 0 Then Continue While

                    Dim msg As JsonObject = ParseLine(line)
                    If msg Is Nothing Then Continue While

                    Dim method As String = JsonRpc.Str(msg, "method")
                    Dim idNode As JsonElement = msg("id")

                    ' a message without a method is the answer to a request we sent
                    If String.IsNullOrEmpty(method) Then
                        RecordAnswer(msg, options)
                        Continue While
                    End If

                    ' notifications (notifications/initialized, ...) carry no id
                    If idNode Is Nothing Then Continue While

                    If options.DelayMs > 0 Then Thread.Sleep(options.DelayMs)

                    Dispatch(msg, method, idNode, options, stdout)
                End While
            Finally
                WriteTextFile(options.ExitMarker, "exited")
            End Try

            Return 0
        End Function

        Private Sub Dispatch(msg As JsonObject, method As String, idNode As JsonElement, options As StubOptions, stdout As Stream)
            Select Case method
                Case "initialize"
                    Send(stdout, options, JsonRpc.MakeResponse(idNode, InitializeResult()))

                    If Not String.IsNullOrEmpty(options.ServerRequestMarker) Then
                        ' a server initiated request the client does not implement: the
                        ' client has to answer it with the standard -32601 error instead
                        ' of letting us wait forever
                        Dim request As New JsonObject()
                        request("jsonrpc") = New JsonValue("2.0")
                        request("id") = New JsonValue("stub-request-1")
                        request("method") = New JsonValue("sampling/createMessage")
                        request("params") = New JsonObject()

                        Send(stdout, options, request)
                    End If

                Case "ping"
                    Send(stdout, options, JsonRpc.MakeResponse(idNode, New JsonObject()))

                Case "tools/list"
                    Send(stdout, options, JsonRpc.MakeResponse(idNode, ToolList(msg, options)))

                Case "tools/call"
                    Dim toolName As String = JsonRpc.Str(msg, "params", "name")

                    If Not String.IsNullOrEmpty(options.HangTool) AndAlso toolName = options.HangTool Then
                        ' never answer, but keep serving the other requests: the caller has
                        ' to time out (or cancel) on its own
                        Task.Run(Sub() Thread.Sleep(60000))
                    Else
                        Send(stdout, options, JsonRpc.MakeResponse(idNode, ToolResult(toolName)))
                    End If

                Case "resources/list"
                    Send(stdout, options, JsonRpc.MakeResponse(idNode, ResourceList()))

                Case "prompts/list"
                    Send(stdout, options, JsonRpc.MakeResponse(idNode, PromptList()))

                Case Else
                    Send(stdout, options, JsonRpc.MakeError(idNode, -32601, "method not found: " & method))
            End Select
        End Sub

        ''' <summary>record the error code the client answered to our own request with.</summary>
        Private Sub RecordAnswer(msg As JsonObject, options As StubOptions)
            If String.IsNullOrEmpty(options.ServerRequestMarker) Then Exit Sub

            Dim code As Integer = JsonRpc.Int(msg, "error", "code")
            WriteTextFile(options.ServerRequestMarker, code.ToString())
        End Sub

        Private Function InitializeResult() As JsonObject
            Dim tools As New JsonObject()
            tools("listChanged") = New JsonValue(True)

            Dim prompts As New JsonObject()
            prompts("listChanged") = New JsonValue(False)

            Dim caps As New JsonObject()
            caps("tools") = tools
            caps("resources") = New JsonObject()
            caps("prompts") = prompts

            Dim info As New JsonObject()
            info("name") = New JsonValue("harness-stub")
            info("version") = New JsonValue("1.0.0")

            Dim result As New JsonObject()
            result("protocolVersion") = New JsonValue("2025-06-18")
            result("capabilities") = caps
            result("serverInfo") = info

            Return result
        End Function

        Private Function ToolList(msg As JsonObject, options As StubOptions) As JsonObject
            Dim cursor As String = JsonRpc.Str(msg, "params", "cursor")

            Dim result As New JsonObject()
            Dim arr As New JsonArray()

            If options.Paginate AndAlso String.IsNullOrEmpty(cursor) Then
                ' first page plus a continuation cursor
                arr.Add(ToolDefinition(options.ToolName))
                result("tools") = arr
                result("nextCursor") = New JsonValue("page-2")
                Return result
            End If

            arr.Add(ToolDefinition(options.ToolName))

            If Not options.Paginate Then
                arr.Add(ToolDefinition(options.ToolName & "_second"))
            End If

            result("tools") = arr
            Return result
        End Function

        ''' <summary>
        ''' a tool whose schema exercises every shape the bridge has to survive:
        ''' a plain string with a default, an integer, a boolean, an enum and an array.
        ''' </summary>
        Private Function ToolDefinition(name As String) As JsonObject
            Dim props As New JsonObject()
            props("text") = ScalarSchema("string", "the text to echo", "hello")
            props("count") = ScalarSchema("integer", "how many times to echo", "1")
            props("flag") = ScalarSchema("boolean", "a boolean switch", "false")
            props("tags") = ArraySchema("array", "the tags to attach")
            props("mode") = EnumSchema("string", "the echo mode", {"fast", "safe"})

            Dim schema As New JsonObject()
            schema("type") = New JsonValue("object")
            schema("properties") = props
            schema("required") = New JsonArray(New JsonValue("text"))

            Dim tool As New JsonObject()
            tool("name") = New JsonValue(name)
            tool("description") = New JsonValue("harness stub tool " & name)
            tool("inputSchema") = schema

            Return tool
        End Function

        Private Function ScalarSchema(typeName As String, description As String, defaultValue As String) As JsonObject
            Dim schema As New JsonObject()
            schema("type") = New JsonValue(typeName)
            schema("description") = New JsonValue(description)
            schema("default") = New JsonValue(defaultValue)
            Return schema
        End Function

        Private Function EnumSchema(typeName As String, description As String, values As String()) As JsonObject
            Dim schema As JsonObject = ScalarSchema(typeName, description, values(0))
            Dim arr As New JsonArray()

            For Each v In values
                arr.Add(New JsonValue(v))
            Next

            schema("enum") = arr
            Return schema
        End Function

        Private Function ArraySchema(typeName As String, description As String) As JsonObject
            Dim schema As New JsonObject()
            schema("type") = New JsonValue(typeName)
            schema("description") = New JsonValue(description)
            schema("items") = ScalarSchema("string", "a tag", "")
            Return schema
        End Function

        ''' <summary>
        ''' the call result carries a text block, a resource link and a structured
        ''' payload, so the test can assert none of them is dropped.
        ''' </summary>
        Private Function ToolResult(toolName As String) As JsonObject
            Dim text As New JsonObject()
            text("type") = New JsonValue("text")
            text("text") = New JsonValue("stub result for " & toolName)

            Dim link As New JsonObject()
            link("type") = New JsonValue("resource_link")
            link("uri") = New JsonValue("stub://doc/1")
            link("name") = New JsonValue("stub-doc")
            link("description") = New JsonValue("a stub document")

            Dim content As New JsonArray()
            content.Add(text)
            content.Add(link)

            Dim structured As New JsonObject()
            structured("tool") = New JsonValue(toolName)
            structured("ok") = New JsonValue(True)

            Dim result As New JsonObject()
            result("content") = content
            result("structuredContent") = structured

            Return result
        End Function

        Private Function ResourceList() As JsonObject
            Dim res As New JsonObject()
            res("uri") = New JsonValue("stub://doc/1")
            res("name") = New JsonValue("stub-doc")
            res("mimeType") = New JsonValue("text/plain")

            Dim arr As New JsonArray()
            arr.Add(res)

            Dim result As New JsonObject()
            result("resources") = arr
            Return result
        End Function

        Private Function PromptList() As JsonObject
            Dim arg As New JsonObject()
            arg("name") = New JsonValue("who")
            arg("description") = New JsonValue("who to greet")
            arg("required") = New JsonValue(True)

            Dim args As New JsonArray()
            args.Add(arg)

            Dim prompt As New JsonObject()
            prompt("name") = New JsonValue("greet")
            prompt("description") = New JsonValue("a stub prompt")
            prompt("arguments") = args

            Dim prompts As New JsonArray()
            prompts.Add(prompt)

            Dim result As New JsonObject()
            result("prompts") = prompts
            Return result
        End Function

        ''' <summary>
        ''' write one message. the default path is the MCP framing (a single line);
        ''' <c>--legacy-framing</c> deliberately writes the LSP Content-Length header,
        ''' which the client must skip without losing the message.
        ''' </summary>
        Private Sub Send(stdout As Stream, options As StubOptions, message As JsonElement)
            Dim json As String = message.BuildJsonString

            If options.LegacyFraming Then
                Dim body As Byte() = Encoding.UTF8.GetBytes(json)
                Dim header As String = $"Content-Length: {body.Length}" & vbCrLf & vbCrLf
                Dim headerBytes As Byte() = Encoding.UTF8.GetBytes(header)

                stdout.Write(headerBytes, 0, headerBytes.Length)
                stdout.Write(body, 0, body.Length)

                ' the body still ends with a line feed so the client can detect the
                ' end of the message, exactly like in the newline framed mode
                Dim lf As Byte() = {CByte(10)}
                stdout.Write(lf, 0, 1)
            Else
                Dim bytes As Byte() = Encoding.UTF8.GetBytes(json & vbLf)
                stdout.Write(bytes, 0, bytes.Length)
            End If

            stdout.Flush()
        End Sub

        Private Function ParseLine(line As String) As JsonObject
            Try
                Return TryCast(JsonElement.ParseJSON(line), JsonObject)
            Catch
                Return Nothing
            End Try
        End Function

        Private Sub WriteTextFile(path As String, text As String)
            If String.IsNullOrEmpty(path) Then Exit Sub

            Try
                File.WriteAllText(path, text)
            Catch
            End Try
        End Sub

    End Module

End Namespace
