Imports System.Collections.Generic
Imports System.Text
Imports System.Threading
Imports Microsoft.VisualBasic.MIME.application.json
Imports Microsoft.VisualBasic.MIME.application.json.Javascript
Imports Ollama
Imports Ollama.JSON.FunctionCall
Imports jsobj = Microsoft.VisualBasic.MIME.application.json.Javascript.JsonObject
Imports jsVal = Microsoft.VisualBasic.MIME.application.json.Javascript.JsonValue

Namespace MCP

    ''' <summary>
    ''' one mcp primitive exposed to the llm client, with the name it was registered
    ''' under and the original name reported by the server.
    ''' </summary>
    Public Class McpRegisteredPrimitive
        Public Property RegisteredName As String
        Public Property OriginalName As String
        Public Property ServerName As String
        ''' <summary>tool / prompt / resource.</summary>
        Public Property Kind As String
    End Class

    ''' <summary>
    ''' bridges one or more connected MCP servers into an <see cref="Ollama.LLMClient"/>: for every
    ''' tool / resource / prompt it builds a <see cref="FunctionModel"/> and registers it on the client
    ''' via <see cref="LLMClient.AddFunction"/>, so the LLM can invoke MCP primitives exactly like local
    ''' functions. <see cref="LLMClient"/> itself is never modified.
    ''' </summary>
    ''' <remarks>
    ''' naming policy: a primitive keeps the name the server gave it, so the model can
    ''' call the tools exactly as the skill documents describe them. only when the same
    ''' name is offered by more than one server is the server name prefixed; the resolved
    ''' names are exposed through <see cref="Registrations"/>.
    ''' </remarks>
    Public Class McpToolBridge : Implements IDisposable

        ''' <summary>connection state of one configured server.</summary>
        Public Class ServerConnection
            Public Property Descriptor As McpServerDescriptor
            Public Property Client As McpClient
            Public Property Tools As List(Of McpTool)
            Public Property Prompts As List(Of McpPrompt)
            ''' <summary>set when connect/handshake failed; the other servers keep working.</summary>
            Public Property ErrorMessage As String

            Public ReadOnly Property IsConnected As Boolean
                Get
                    Return Client IsNot Nothing AndAlso String.IsNullOrEmpty(ErrorMessage)
                End Get
            End Property
        End Class

        ''' <summary>a primitive candidate before its final name is decided.</summary>
        Private Class Registration
            Public Property OriginalName As String
            Public Property ProposedName As String
            Public Property Prefix As String
            Public Property ServerName As String
            Public Property Kind As String
            Public Property Model As FunctionModel
            Public Property Invoke As Func(Of FunctionCall, String)
        End Class

        Private ReadOnly _servers As McpServerDescriptor()
        Private ReadOnly _connections As New List(Of ServerConnection)
        Private ReadOnly _failures As New List(Of String)
        Private ReadOnly _registrations As New List(Of McpRegisteredPrimitive)

        ''' <summary>
        ''' the tool call log the primitives of this bridge should be recorded
        ''' in. <see cref="Sandbox.RegisterMcpServers"/> sets it to the log of
        ''' the session before <see cref="Attach"/> runs; Nothing keeps the
        ''' bridge unlogged.
        ''' </summary>
        Public Property ToolLog As Logging.ToolCallLogger

        Public Sub New(ParamArray servers As McpServerDescriptor())
            _servers = If(servers Is Nothing, New McpServerDescriptor() {}, servers)
        End Sub

        ''' <summary>the per server connection state, including the ones that failed.</summary>
        Public ReadOnly Property Connections As IReadOnlyList(Of ServerConnection)
            Get
                Return _connections
            End Get
        End Property

        ''' <summary>human readable description of the servers that could not be used.</summary>
        Public ReadOnly Property Failures As IReadOnlyList(Of String)
            Get
                Return _failures
            End Get
        End Property

        ''' <summary>The name mapping of the last <see cref="Attach"/> call.</summary>
        Public ReadOnly Property Registrations As IReadOnlyList(Of McpRegisteredPrimitive)
            Get
                Return _registrations
            End Get
        End Property

        ''' <summary>
        ''' connect to every configured server, perform the handshake and prefetch the
        ''' tool / prompt catalogues. a server that fails to start or hangs is recorded in
        ''' <see cref="Failures"/> and skipped — it never takes the other servers down.
        ''' </summary>
        Public Async Function ConnectAllAsync(Optional ct As CancellationToken = Nothing) As Task
            DisposeClients()
            _failures.Clear()

            For Each descriptor In _servers
                Dim conn As New ServerConnection With {.Descriptor = descriptor}
                _connections.Add(conn)

                Try
                    conn.Client = New McpClient(descriptor.Name, descriptor.CreateTransport())
                    Await conn.Client.ConnectAsync(ct).ConfigureAwait(False)

                    Dim caps As McpServerCapabilities = conn.Client.Capabilities

                    If caps Is Nothing OrElse caps.Tools Then
                        conn.Tools = Await conn.Client.ListToolsAsync(ct).ConfigureAwait(False)
                    End If

                    If caps IsNot Nothing AndAlso caps.Prompts Then
                        conn.Prompts = Await conn.Client.ListPromptsAsync(ct).ConfigureAwait(False)
                    End If
                Catch ex As Exception
                    conn.ErrorMessage = ex.Message
                    _failures.Add($"{descriptor.Name}: {ex.Message}")
                    Console.Error.WriteLine($"[MCP] the server '{descriptor.Name}' is unavailable: {ex.Message}")

                    If conn.Client IsNot Nothing Then
                        Try
                            conn.Client.Dispose()
                        Catch
                        End Try
                        conn.Client = Nothing
                    End If
                End Try
            Next
        End Function

        ''' <summary>
        ''' register every prefetched MCP primitive on the given LLM client. call this
        ''' once after <see cref="ConnectAllAsync"/>.
        ''' </summary>
        ''' <param name="forcePrefix">
        ''' <c>True</c> namespaces every function with the server name; the default
        ''' (<c>False</c>) keeps the original names and only prefixes the ones that clash
        ''' across servers.
        ''' </param>
        Public Function Attach(client As LLMClient, Optional forcePrefix As Boolean = False) As McpToolBridge
            _registrations.Clear()

            If client Is Nothing Then Return Me

            Dim candidates As New List(Of Registration)()

            For Each conn In _connections
                If conn.Client Is Nothing Then Continue For
                CollectCandidates(conn, candidates)
            Next

            ' a proposed name offered by more than one server has to be namespaced
            Dim counts As New Dictionary(Of String, Integer)(StringComparer.Ordinal)
            For Each c In candidates
                Dim n As Integer = 0
                counts.TryGetValue(c.ProposedName, n)
                counts(c.ProposedName) = n + 1
            Next

            Dim used As New HashSet(Of String)(StringComparer.Ordinal)

            For Each c In candidates
                Dim finalName As String = c.ProposedName

                If forcePrefix OrElse counts(c.ProposedName) > 1 Then
                    finalName = c.Prefix & c.ProposedName
                End If

                Dim unique As String = finalName
                Dim suffix As Integer = 2
                While used.Contains(unique)
                    unique = finalName & "_" & suffix
                    suffix += 1
                End While
                used.Add(unique)

                c.Model.name = unique

                ' every mcp primitive reaches the model through the same
                ' registry as the local tools, so it is wrapped by the same
                ' log: the record then carries the server, the kind and the
                ' original name next to the call itself
                Dim backend As Func(Of FunctionCall, String) = c.Invoke

                If ToolLog IsNot Nothing Then
                    backend = ToolLog.Wrap(c.Model, Logging.ToolLogText.SourceMcp, backend,
                                           kind:=c.Kind, server:=c.ServerName, originalName:=c.OriginalName)
                End If

                client.AddFunction(c.Model, backend)

                _registrations.Add(New McpRegisteredPrimitive With {
                    .RegisteredName = unique,
                    .OriginalName = c.OriginalName,
                    .ServerName = c.ServerName,
                    .Kind = c.Kind
                })
            Next

            Return Me
        End Function

        Private Shared Sub CollectCandidates(conn As ServerConnection, candidates As List(Of Registration))
            Dim serverName As String = conn.Descriptor.Name
            Dim prefix As String = Sanitize(serverName) & "_"
            Dim refClient As McpClient = conn.Client

            ' --- tools ---
            If conn.Tools IsNot Nothing Then
                For Each t In conn.Tools
                    Dim toolName As String = t.Name
                    candidates.Add(New Registration With {
                        .OriginalName = toolName,
                        .ProposedName = NormalizeName(toolName),
                        .Prefix = prefix,
                        .ServerName = serverName,
                        .Kind = "tool",
                        .Model = ToolToFunctionModel(t),
                        .Invoke = Function(fc As FunctionCall) InvokeTool(refClient, toolName, fc)
                    })
                Next
            End If

            Dim caps As McpServerCapabilities = conn.Client.Capabilities

            ' --- resources ---
            If caps IsNot Nothing AndAlso caps.Resources Then
                candidates.Add(New Registration With {
                    .OriginalName = "list_resources",
                    .ProposedName = "list_resources",
                    .Prefix = prefix,
                    .ServerName = serverName,
                    .Kind = "resource",
                    .Model = New FunctionModel("list_resources",
                        "List the resources available from the MCP server '" & serverName & "'."),
                    .Invoke = Function(fc As FunctionCall) ResourcesToText(refClient.ListResourcesAsync().GetAwaiter().GetResult())
                })

                candidates.Add(New Registration With {
                    .OriginalName = "read_resource",
                    .ProposedName = "read_resource",
                    .Prefix = prefix,
                    .ServerName = serverName,
                    .Kind = "resource",
                    .Model = New FunctionModel("read_resource",
                        "Read an MCP resource by its uri from the server '" & serverName & "'.",
                        New ParameterProperties("uri", "the uri of the resource to read", TypeCode.String)),
                    .Invoke = Function(fc As FunctionCall)
                                  Dim uri As String = If(fc("uri") Is Nothing, "", fc("uri"))
                                  Dim res = refClient.ReadResourceAsync(uri).GetAwaiter().GetResult()
                                  Return If(res Is Nothing, "(no resource)", res.ToText())
                              End Function
                })
            End If

            ' --- prompts (the catalogue is used as prefetched: no second prompts/list) ---
            If caps IsNot Nothing AndAlso caps.Prompts Then
                Dim prompts As List(Of McpPrompt) = conn.Prompts
                If prompts Is Nothing Then prompts = New List(Of McpPrompt)()

                candidates.Add(New Registration With {
                    .OriginalName = "list_prompts",
                    .ProposedName = "list_prompts",
                    .Prefix = prefix,
                    .ServerName = serverName,
                    .Kind = "prompt",
                    .Model = New FunctionModel("list_prompts",
                        "List the prompts available from the MCP server '" & serverName & "'."),
                    .Invoke = Function(fc As FunctionCall) PromptsToText(prompts)
                })

                For Each p In prompts
                    Dim promptName As String = p.Name
                    candidates.Add(New Registration With {
                        .OriginalName = promptName,
                        .ProposedName = "prompt_" & NormalizeName(promptName),
                        .Prefix = prefix,
                        .ServerName = serverName,
                        .Kind = "prompt",
                        .Model = PromptToFunctionModel(p),
                        .Invoke = Function(fc As FunctionCall)
                                      Dim args As jsobj = RebuildArguments(fc)
                                      Dim res = refClient.GetPromptAsync(promptName, args).GetAwaiter().GetResult()
                                      Return If(res Is Nothing, "", res.ToText())
                                  End Function
                    })
                Next
            End If
        End Sub

        Private Shared Function ToolToFunctionModel(t As McpTool) As FunctionModel
            Dim fm As New FunctionModel()
            fm.name = NormalizeName(t.Name)
            fm.description = t.Description
            fm.parameters = New FunctionParameters() With {
                .type = "object",
                .properties = New Dictionary(Of String, ParameterProperties)(),
                .required = New String() {}
            }
            If t.InputSchema IsNot Nothing Then
                Dim props = TryCast(t.InputSchema("properties"), jsobj)
                If props IsNot Nothing Then
                    For Each kv In props
                        fm.parameters.properties(kv.Name) = SchemaPropertyToParameter(kv.Name, TryCast(kv.Value, jsobj))
                    Next
                End If
                Dim reqArr = TryCast(t.InputSchema("required"), JsonArray)
                If reqArr IsNot Nothing Then
                    Dim reqList As New List(Of String)()
                    For Each r As JsonElement In reqArr
                        Dim sv = TryCast(r, jsVal)
                        If sv IsNot Nothing Then reqList.Add(sv.ToString())
                    Next
                    fm.parameters.required = reqList.ToArray()
                End If
            End If
            Return fm
        End Function

        Private Shared Function PromptToFunctionModel(p As McpPrompt) As FunctionModel
            Dim fm As New FunctionModel()
            fm.name = "prompt_" & NormalizeName(p.Name)
            fm.description = p.Description
            fm.parameters = New FunctionParameters() With {
                .type = "object",
                .properties = New Dictionary(Of String, ParameterProperties)(),
                .required = New String() {}
            }
            If p.Arguments IsNot Nothing Then
                Dim reqList As New List(Of String)()
                For Each a In p.Arguments
                    fm.parameters.properties(a.Name) = New ParameterProperties(a.Name, a.Description, TypeCode.String)
                    If a.Required Then reqList.Add(a.Name)
                Next
                fm.parameters.required = reqList.ToArray()
            End If
            Return fm
        End Function

        Private Shared Function SchemaPropertyToParameter(name As String, schema As jsobj) As ParameterProperties
            If schema Is Nothing Then Return New ParameterProperties(name, "", TypeCode.String)

            Dim description As String = JsonRpc.Str(schema, "description")
            Dim details As String = SchemaDetails(schema)

            If Not String.IsNullOrEmpty(details) Then
                description = If(String.IsNullOrEmpty(description), details, description & " " & details)
            End If

            Dim pp As New ParameterProperties(name, description, TypeCode.String) With {
                .type = SchemaType(JsonRpc.Str(schema, "type"))
            }

            Dim defaultValue As String = JsonRpc.Str(schema, "default")
            If Not String.IsNullOrEmpty(defaultValue) Then pp.default = defaultValue

            Return pp
        End Function

        ''' <summary>
        ''' the ollama function schema carries only a scalar type per parameter, so the
        ''' richer json-schema details (enum values, array items, object keys) are folded
        ''' into the parameter description instead of being silently dropped.
        ''' </summary>
        Private Shared Function SchemaDetails(schema As jsobj) As String
            Dim details As New List(Of String)()

            Dim enumArr = TryCast(schema("enum"), JsonArray)
            If enumArr IsNot Nothing Then
                Dim values As New List(Of String)()
                For Each v As JsonElement In enumArr
                    values.Add("'" & v.ToString() & "'")
                Next
                If values.Count > 0 Then details.Add("one of: " & String.Join(", ", values))
            End If

            Dim items = TryCast(schema("items"), jsobj)
            If items IsNot Nothing Then
                Dim itemType As String = JsonRpc.Str(items, "type")
                details.Add(If(String.IsNullOrEmpty(itemType), "array of items", "array of " & itemType))
            End If

            Dim props = TryCast(schema("properties"), jsobj)
            If props IsNot Nothing Then
                Dim names As New List(Of String)()
                For Each kv In props
                    names.Add(kv.Name)
                Next
                If names.Count > 0 Then details.Add("object with keys: " & String.Join(", ", names))
            End If

            Dim defaultValue As String = JsonRpc.Str(schema, "default")
            If Not String.IsNullOrEmpty(defaultValue) Then details.Add("default: " & defaultValue)

            If details.Count = 0 Then Return ""

            Return "(" & String.Join("; ", details) & ")"
        End Function

        Private Shared Function SchemaType(t As String) As String
            Select Case If(t Is Nothing, "", t).ToLower()
                Case "integer", "int", "int32", "int64" : Return "integer"
                Case "number", "float", "double", "decimal" : Return "number"
                Case "boolean", "bool" : Return "boolean"
                Case Else : Return "string"
            End Select
        End Function

        ''' <summary>
        ''' rebuild a json object from the flat string dictionary the LLM returns. each value is tried
        ''' as json first (so numbers / booleans / nested objects survive); failing that, it is kept
        ''' as a plain string — matching how Ollama deserializes tool arguments into Dictionary(Of String, String).
        ''' </summary>
        Private Shared Function RebuildArguments(fc As FunctionCall) As jsobj
            Dim obj As New jsobj()
            If fc.arguments IsNot Nothing Then
                For Each kv In fc.arguments
                    Dim raw As String = kv.Value
                    If raw Is Nothing Then
                        obj(kv.Key) = New jsVal(Nothing)
                    Else
                        Dim parsed As JsonElement = Nothing
                        Try
                            parsed = JsonElement.ParseJSON(raw)
                        Catch
                            parsed = Nothing
                        End Try
                        If parsed IsNot Nothing Then
                            obj(kv.Key) = parsed
                        Else
                            obj(kv.Key) = New jsVal(raw)
                        End If
                    End If
                Next
            End If
            Return obj
        End Function

        Private Shared Function InvokeTool(client As McpClient, toolName As String, fc As FunctionCall) As String
            Try
                Dim args As jsobj = RebuildArguments(fc)
                Dim result As McpCallToolResult = client.CallToolAsync(toolName, args).GetAwaiter().GetResult()
                If result Is Nothing Then Return "(no result)"
                If result.IsError Then Return "Tool error: " & result.ToText()
                Return result.ToText()
            Catch ex As Exception
                Console.Error.WriteLine("[MCP] the tool '" & toolName & "' failed: " & ex.Message)
                Return "MCP tool call failed: " & ex.Message
            End Try
        End Function

        Private Shared Function ResourcesToText(resources As List(Of McpResource)) As String
            If resources Is Nothing OrElse resources.Count = 0 Then Return "(no resources)"
            Dim sb As New StringBuilder()
            For Each r In resources
                sb.AppendLine("uri: " & r.Uri)
                sb.AppendLine("  name: " & r.Name)
                If Not String.IsNullOrEmpty(r.Description) Then sb.AppendLine("  description: " & r.Description)
                If Not String.IsNullOrEmpty(r.MimeType) Then sb.AppendLine("  mimeType: " & r.MimeType)
            Next
            Return sb.ToString().TrimEnd()
        End Function

        Private Shared Function PromptsToText(prompts As List(Of McpPrompt)) As String
            If prompts Is Nothing OrElse prompts.Count = 0 Then Return "(no prompts)"
            Dim sb As New StringBuilder()
            For Each p In prompts
                sb.AppendLine("name: " & p.Name)
                If Not String.IsNullOrEmpty(p.Description) Then sb.AppendLine("  description: " & p.Description)
                If p.Arguments IsNot Nothing AndAlso p.Arguments.Length > 0 Then
                    Dim names As New List(Of String)()
                    For Each a In p.Arguments
                        names.Add(a.Name & If(a.Required, " (required)", ""))
                    Next
                    sb.AppendLine("  arguments: " & String.Join(", ", names))
                End If
            Next
            Return sb.ToString().TrimEnd()
        End Function

        ''' <summary>
        ''' keep a server supplied name as close to the original as possible; only
        ''' characters that cannot appear in a function name are replaced.
        ''' </summary>
        Private Shared Function NormalizeName(name As String) As String
            If String.IsNullOrEmpty(name) Then Return "unnamed"

            Dim sb As New StringBuilder(name.Length + 1)

            For Each c In name
                sb.Append(If(Char.IsLetterOrDigit(c) OrElse c = "_"c, c, "_"c))
            Next

            Dim s As String = sb.ToString()
            If Char.IsDigit(s(0)) Then s = "_" & s
            Return s
        End Function

        ''' <summary>turn an arbitrary server/tool name into a safe identifier fragment (letters/digits/_ only).</summary>
        Public Shared Function Sanitize(name As String) As String
            If String.IsNullOrEmpty(name) Then Return "mcp"
            Dim sb As New StringBuilder()
            For Each c In name
                If Char.IsLetterOrDigit(c) OrElse c = "_"c Then
                    sb.Append(c)
                Else
                    sb.Append("_"c)
                End If
            Next
            Dim s As String = sb.ToString().Trim("_"c)
            Return If(s.Length = 0, "mcp", s)
        End Function

        Private Sub DisposeClients()
            For Each c In _connections
                If c.Client IsNot Nothing Then
                    Try
                        c.Client.Dispose()
                    Catch
                    End Try
                End If
            Next
            _connections.Clear()
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            DisposeClients()
            _registrations.Clear()
        End Sub

    End Class

End Namespace
