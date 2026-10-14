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
    ''' bridges one or more connected MCP servers into an <see cref="Ollama.LLMClient"/>: for every
    ''' tool / resource / prompt it builds a <see cref="FunctionModel"/> and registers it on the client
    ''' via <see cref="LLMClient.AddFunction"/>, so the LLM can invoke MCP primitives exactly like local
    ''' functions. <see cref="LLMClient"/> itself is never modified.
    ''' </summary>
    Public Class McpToolBridge : Implements IDisposable

        Private ReadOnly _servers As McpServerDescriptor()
        Private ReadOnly _clients As New List(Of McpClient)

        Public Sub New(ParamArray servers As McpServerDescriptor())
            _servers = servers
        End Sub

        ''' <summary>connect to every configured server and perform the MCP handshake.</summary>
        Public Async Function ConnectAllAsync(Optional ct As CancellationToken = Nothing) As Task
            For Each s In _servers
                Dim transport As IMcpTransport = s.CreateTransport()
                Dim client As New McpClient(s.Name, transport)
                Await client.ConnectAsync(ct).ConfigureAwait(False)
                _clients.Add(client)
            Next
        End Function

        ''' <summary>
        ''' register every discovered MCP primitive on the given LLM client. call this once after
        ''' <see cref="ConnectAllAsync"/>. <paramref name="usePrefix"/> namespaces the exposed function
        ''' names with the server name to avoid collisions between servers.
        ''' </summary>
        Public Function Attach(client As LLMClient, Optional usePrefix As Boolean = True) As McpToolBridge
            For Each mc In _clients
                RegisterServerPrimitives(mc, client, usePrefix)
            Next
            Return Me
        End Function

        Private Shared Sub RegisterServerPrimitives(mc As McpClient, client As LLMClient, usePrefix As Boolean)
            Dim prefix As String = If(usePrefix, Sanitize(mc.Name) & "_", "")

            ' --- Tools ---
            Dim tools As List(Of McpTool) = mc.ListToolsAsync().GetAwaiter().GetResult()
            For Each t In tools
                Dim toolName As String = t.Name
                Dim func As FunctionModel = ToolToFunctionModel(prefix, t)
                Dim refClient As McpClient = mc
                client.AddFunction(func, Function(fc As FunctionCall)
                                            Return InvokeTool(refClient, toolName, fc)
                                        End Function)
            Next

            ' --- Resources ---
            If mc.Capabilities IsNot Nothing AndAlso mc.Capabilities.Resources Then
                client.AddFunction(New FunctionModel(prefix & "list_resources",
                    "List the resources available from the MCP server '" & mc.Name & "'."),
                    Function(fc As FunctionCall)
                        Dim res = mc.ListResourcesAsync().GetAwaiter().GetResult()
                        Return ResourcesToText(res)
                    End Function)

                client.AddFunction(New FunctionModel(prefix & "read_resource",
                    "Read an MCP resource by its uri from the server '" & mc.Name & "'.",
                    New ParameterProperties("uri", "the uri of the resource to read", TypeCode.String)),
                    Function(fc As FunctionCall)
                        Dim uri As String = If(fc("uri"), "")
                        Dim res = mc.ReadResourceAsync(uri).GetAwaiter().GetResult()
                        Return If(res Is Nothing, "(no resource)", res.ToText())
                    End Function)
            End If

            ' --- Prompts ---
            If mc.Capabilities IsNot Nothing AndAlso mc.Capabilities.Prompts Then
                client.AddFunction(New FunctionModel(prefix & "list_prompts",
                    "List the prompts available from the MCP server '" & mc.Name & "'."),
                    Function(fc As FunctionCall)
                        Dim ps = mc.ListPromptsAsync().GetAwaiter().GetResult()
                        Return PromptsToText(ps)
                    End Function)

                Dim prompts As List(Of McpPrompt) = mc.ListPromptsAsync().GetAwaiter().GetResult()
                For Each p In prompts
                    Dim promptName As String = p.Name
                    Dim func As FunctionModel = PromptToFunctionModel(prefix, p)
                    Dim refClient As McpClient = mc
                    client.AddFunction(func, Function(fc As FunctionCall)
                                              Dim args As jsobj = RebuildArguments(fc)
                                              Dim res = refClient.GetPromptAsync(promptName, args).GetAwaiter().GetResult()
                                              Return If(res Is Nothing, "", res.ToText())
                                          End Function)
                Next
            End If
        End Sub

        Private Shared Function ToolToFunctionModel(prefix As String, t As McpTool) As FunctionModel
            Dim fm As New FunctionModel()
            fm.name = prefix & t.Name
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

        Private Shared Function PromptToFunctionModel(prefix As String, p As McpPrompt) As FunctionModel
            Dim fm As New FunctionModel()
            fm.name = prefix & "prompt_" & Sanitize(p.Name)
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
            Dim pp As New ParameterProperties(name, JsonRpc.Str(schema, "description"), TypeCode.String)
            pp.type = SchemaType(JsonRpc.Str(schema, "type"))
            Return pp
        End Function

        Private Shared Function SchemaType(t As String) As String
            Select Case If(t, "").ToLower()
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
                Console.Error.WriteLine("[MCP] tool '" & toolName & "' failed: " & ex.Message)
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

        Public Sub Dispose() Implements IDisposable.Dispose
            For Each c In _clients
                Try : c.Dispose() : Catch : End Try
            Next
            _clients.Clear()
        End Sub

    End Class

End Namespace
