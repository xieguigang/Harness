Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.VisualBasic.MIME.application.json
Imports Microsoft.VisualBasic.MIME.application.json.Javascript
Imports jsobj = Microsoft.VisualBasic.MIME.application.json.Javascript.JsonObject
Imports jsVal = Microsoft.VisualBasic.MIME.application.json.Javascript.JsonValue

Namespace MCP

    ''' <summary>
    ''' MCP protocol client: performs the handshake (initialize + notifications/initialized),
    ''' and exposes typed wrappers over the json-rpc methods. request/response correlation by
    ''' id is handled here through the transport's <see cref="IMcpTransport.MessageReceived"/> pump.
    ''' </summary>
    Public Class McpClient : Implements IDisposable

        Private ReadOnly _name As String
        Private ReadOnly _transport As IMcpTransport
        Private _nextId As Long = 0
        Private ReadOnly _pending As New Dictionary(Of String, TaskCompletionSource(Of JsonObject))
        Private _capabilities As McpServerCapabilities
        Private _disposed As Boolean

        Public ReadOnly Property Name As String
            Get
                Return _name
            End Get
        End Property

        Public ReadOnly Property Capabilities As McpServerCapabilities
            Get
                Return _capabilities
            End Get
        End Property

        Public Sub New(name As String, transport As IMcpTransport)
            _name = name
            _transport = transport
            AddHandler _transport.MessageReceived, AddressOf OnMessage
        End Sub

        Private Sub OnMessage(msg As JsonObject)
            Dim idNode As JsonElement = msg("id")
            Dim idKey As String = If(idNode Is Nothing, Nothing, idNode.ToString())
            If idKey IsNot Nothing AndAlso _pending.ContainsKey(idKey) Then
                Dim tcs As TaskCompletionSource(Of JsonObject) = _pending(idKey)
                _pending.Remove(idKey)
                Dim errNode As JsonElement = msg("error")
                If errNode IsNot Nothing Then
                    Dim code As Integer = JsonRpc.Int(msg, "error", "code")
                    Dim message As String = JsonRpc.Str(msg, "error", "message")
                    tcs.TrySetException(New JsonRpcException(code, message))
                Else
                    tcs.TrySetResult(msg)
                End If
            End If
            ' server-initiated notifications (no id) are intentionally ignored here
        End Sub

        Public Async Function ConnectAsync(Optional cancellationToken As CancellationToken = Nothing) As Task
            Await _transport.ConnectAsync(cancellationToken).ConfigureAwait(False)

            Dim initParams As New jsobj()
            initParams("protocolVersion") = New jsVal("2025-06-18")
            initParams("capabilities") = New jsobj()
            Dim clientInfo As New jsobj()
            clientInfo("name") = New jsVal("harness-mcp-client")
            clientInfo("version") = New jsVal("1.0.0")
            initParams("clientInfo") = clientInfo

            Dim initResp As JsonObject = Await RequestAsync("initialize", initParams, cancellationToken).ConfigureAwait(False)
            _capabilities = McpServerCapabilities.FromJson(TryCast(initResp("result"), jsobj))

            Dim notif As jsobj = JsonRpc.MakeNotification("notifications/initialized", New jsobj())
            Await _transport.SendAsync(notif, cancellationToken).ConfigureAwait(False)
        End Function

        Private Async Function RequestAsync(method As String, params As JsonObject, ct As CancellationToken) As Task(Of JsonObject)
            Dim id As String = Interlocked.Increment(_nextId).ToString()
            Dim req As jsobj = JsonRpc.MakeRequest(New jsVal(id), method, params)
            Dim tcs As New TaskCompletionSource(Of JsonObject)()
            _pending(id) = tcs
            Await _transport.SendAsync(req, ct).ConfigureAwait(False)
            Return Await tcs.Task.ConfigureAwait(False)
        End Function

        Public Async Function ListToolsAsync(Optional ct As CancellationToken = Nothing) As Task(Of List(Of McpTool))
            Dim resp As JsonObject = Await RequestAsync("tools/list", New jsobj(), ct).ConfigureAwait(False)
            Dim result As jsobj = TryCast(resp("result"), jsobj)
            Dim list As New List(Of McpTool)()
            If result IsNot Nothing Then
                Dim arr = TryCast(result("tools"), JsonArray)
                If arr IsNot Nothing Then
                    For Each item As JsonElement In arr
                        Dim o = TryCast(item, JsonObject)
                        If o IsNot Nothing Then list.Add(McpTool.FromJson(o))
                    Next
                End If
            End If
            Return list
        End Function

        Public Async Function CallToolAsync(toolName As String, arguments As JsonObject, Optional ct As CancellationToken = Nothing) As Task(Of McpCallToolResult)
            Dim params As New jsobj()
            params("name") = New jsVal(toolName)
            params("arguments") = If(arguments, New jsobj())
            Dim resp As JsonObject = Await RequestAsync("tools/call", params, ct).ConfigureAwait(False)
            Dim result As jsobj = TryCast(resp("result"), jsobj)
            Return McpCallToolResult.FromJson(result)
        End Function

        Public Async Function ListResourcesAsync(Optional ct As CancellationToken = Nothing) As Task(Of List(Of McpResource))
            Dim resp As JsonObject = Await RequestAsync("resources/list", New jsobj(), ct).ConfigureAwait(False)
            Dim result As jsobj = TryCast(resp("result"), jsobj)
            Dim list As New List(Of McpResource)()
            If result IsNot Nothing Then
                Dim arr = TryCast(result("resources"), JsonArray)
                If arr IsNot Nothing Then
                    For Each item As JsonElement In arr
                        Dim o = TryCast(item, JsonObject)
                        If o IsNot Nothing Then list.Add(McpResource.FromJson(o))
                    Next
                End If
            End If
            Return list
        End Function

        Public Async Function ReadResourceAsync(uri As String, Optional ct As CancellationToken = Nothing) As Task(Of McpReadResourceResult)
            Dim params As New jsobj()
            params("uri") = New jsVal(uri)
            Dim resp As JsonObject = Await RequestAsync("resources/read", params, ct).ConfigureAwait(False)
            Dim result As jsobj = TryCast(resp("result"), jsobj)
            Return McpReadResourceResult.FromJson(result)
        End Function

        Public Async Function ListPromptsAsync(Optional ct As CancellationToken = Nothing) As Task(Of List(Of McpPrompt))
            Dim resp As JsonObject = Await RequestAsync("prompts/list", New jsobj(), ct).ConfigureAwait(False)
            Dim result As jsobj = TryCast(resp("result"), jsobj)
            Dim list As New List(Of McpPrompt)()
            If result IsNot Nothing Then
                Dim arr = TryCast(result("prompts"), JsonArray)
                If arr IsNot Nothing Then
                    For Each item As JsonElement In arr
                        Dim o = TryCast(item, JsonObject)
                        If o IsNot Nothing Then list.Add(McpPrompt.FromJson(o))
                    Next
                End If
            End If
            Return list
        End Function

        Public Async Function GetPromptAsync(name As String, arguments As JsonObject, Optional ct As CancellationToken = Nothing) As Task(Of McpGetPromptResult)
            Dim params As New jsobj()
            params("name") = New jsVal(name)
            params("arguments") = If(arguments, New jsobj())
            Dim resp As JsonObject = Await RequestAsync("prompts/get", params, ct).ConfigureAwait(False)
            Dim result As jsobj = TryCast(resp("result"), jsobj)
            Return McpGetPromptResult.FromJson(result)
        End Function

        Public Async Function PingAsync(Optional ct As CancellationToken = Nothing) As Task
            Await RequestAsync("ping", New jsobj(), ct).ConfigureAwait(False)
        End Function

        Public Async Function CloseAsync() As Task
            If _transport IsNot Nothing Then Await _transport.CloseAsync().ConfigureAwait(False)
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            If Not _disposed Then
                RemoveHandler _transport.MessageReceived, AddressOf OnMessage
                Try
                    _transport.CloseAsync().GetAwaiter().GetResult()
                Catch
                End Try
                _disposed = True
            End If
        End Sub

    End Class

End Namespace
