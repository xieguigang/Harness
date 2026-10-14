Imports System.Collections.Concurrent
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
    ''' <remarks>
    ''' robustness contract:
    ''' <list type="bullet">
    ''' <item>client request ids are prefixed so they can never collide with the ids a
    ''' server picks for its own requests;</item>
    ''' <item>a request always completes (result, error, timeout, cancellation or
    ''' transport close) — a caller is never left waiting forever;</item>
    ''' <item>server initiated requests are answered with standard json-rpc errors
    ''' instead of being silently dropped;</item>
    ''' <item>the list methods follow the <c>nextCursor</c> continuation until the
    ''' server reports the end (with a page cap as a safety net).</item>
    ''' </list>
    ''' </remarks>
    Public Class McpClient : Implements IDisposable

        ''' <summary>default response wait time of a single request, in milliseconds.</summary>
        Public Const DefaultRequestTimeoutMs As Integer = 30000

        ''' <summary>json-rpc "method not found" error code.</summary>
        Public Const MethodNotFoundErrorCode As Integer = -32601

        ''' <summary>hard cap of the pages followed through <c>nextCursor</c> by a single list call.</summary>
        Public Const MaxListPages As Integer = 50

        Private Shared _clientSeq As Long = 0

        Private ReadOnly _name As String
        Private ReadOnly _transport As IMcpTransport
        Private ReadOnly _idPrefix As String
        Private ReadOnly _pending As New ConcurrentDictionary(Of String, TaskCompletionSource(Of JsonObject))
        Private _nextId As Long = 0
        Private _capabilities As McpServerCapabilities
        Private _requestTimeout As TimeSpan = TimeSpan.FromMilliseconds(DefaultRequestTimeoutMs)
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

        ''' <summary>
        ''' how long a single request waits for its response before failing with a
        ''' <see cref="TimeoutException"/>. non positive values restore the default.
        ''' </summary>
        Public Property RequestTimeout As TimeSpan
            Get
                Return _requestTimeout
            End Get
            Set(value As TimeSpan)
                If value <= TimeSpan.Zero Then
                    value = TimeSpan.FromMilliseconds(DefaultRequestTimeoutMs)
                End If
                _requestTimeout = value
            End Set
        End Property

        Public Sub New(name As String, transport As IMcpTransport)
            _name = name
            _transport = transport
            _idPrefix = $"h{Interlocked.Increment(_clientSeq)}-"
            AddHandler _transport.MessageReceived, AddressOf OnMessage
        End Sub

        Private Sub OnMessage(msg As JsonObject)
            Dim idNode As JsonElement = msg("id")
            Dim method As String = msg.Str("method")

            ' a message carrying a method is sent by the server: with an id it is a
            ' request (must be answered), without an id it is a notification
            If Not String.IsNullOrEmpty(method) Then
                If idNode Is Nothing Then
                    Console.Error.WriteLine($"[MCP {_name}] ignored the server notification '{method}'.")
                Else
                    ReplyMethodNotFound(idNode, method)
                End If
                Return
            End If

            If idNode Is Nothing Then
                Return
            End If

            Dim key As String = idNode.ToString()
            Dim tcs As TaskCompletionSource(Of JsonObject) = Nothing

            If String.IsNullOrEmpty(key) OrElse Not _pending.TryRemove(key, tcs) Then
                Console.Error.WriteLine($"[MCP {_name}] received a response for an unknown request id '{key}'.")
                Return
            End If

            Dim errNode As JsonElement = msg("error")

            If errNode IsNot Nothing Then
                Dim code As Integer = JsonRpc.Int(msg, "error", "code")
                Dim message As String = JsonRpc.Str(msg, "error", "message")
                Dim data As JsonElement = Nothing
                Dim errObj As JsonObject = TryCast(errNode, JsonObject)

                If errObj IsNot Nothing Then
                    data = errObj("data")
                End If

                tcs.TrySetException(New JsonRpcException(code, message, data))
            Else
                tcs.TrySetResult(msg)
            End If
        End Sub

        ''' <summary>
        ''' answer a server initiated request whose method this client does not
        ''' implement with the standard -32601 error, so the server does not wait
        ''' for a response that would never come.
        ''' </summary>
        Private Sub ReplyMethodNotFound(idNode As JsonElement, method As String)
            Console.Error.WriteLine($"[MCP {_name}] the server asked for the unsupported method '{method}', replying -32601.")

            Dim response As JsonObject = McpLineFraming.MakeErrorResponse(idNode, MethodNotFoundErrorCode, $"method not found: {method}")

            Task.Run(Sub()
                         Try
                             _transport.SendAsync(response).GetAwaiter().GetResult()
                         Catch ex As Exception
                             Console.Error.WriteLine($"[MCP {_name}] failed to answer the server request '{method}': {ex.Message}")
                         End Try
                     End Sub)
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
            If _disposed Then
                Throw New ObjectDisposedException(NameOf(McpClient))
            End If

            Dim id As String = _idPrefix & Interlocked.Increment(_nextId).ToString()
            Dim req As jsobj = JsonRpc.MakeRequest(New jsVal(id), method, params)
            Dim tcs As New TaskCompletionSource(Of JsonObject)(TaskCreationOptions.RunContinuationsAsynchronously)

            If Not _pending.TryAdd(id, tcs) Then
                Throw New InvalidOperationException($"duplicated mcp request id '{id}' ({method}).")
            End If

            Dim registration As CancellationTokenRegistration = Nothing
            If ct.CanBeCanceled Then
                registration = ct.Register(Sub()
                                               Dim removed As TaskCompletionSource(Of JsonObject) = Nothing
                                               If _pending.TryRemove(id, removed) Then
                                                   removed.TrySetCanceled(ct)
                                               End If
                                           End Sub)
            End If

            Try
                Await _transport.SendAsync(req, ct).ConfigureAwait(False)

                Dim finished As Task = Await Task.WhenAny(tcs.Task, Task.Delay(_requestTimeout, ct)).ConfigureAwait(False)

                If finished IsNot tcs.Task Then
                    Dim removed As TaskCompletionSource(Of JsonObject) = Nothing
                    _pending.TryRemove(id, removed)

                    If ct.IsCancellationRequested Then
                        Throw New OperationCanceledException($"the mcp request '{method}' was cancelled.", ct)
                    End If

                    Throw New TimeoutException($"the mcp request '{method}' to the server '{_name}' timed out after {_requestTimeout.TotalSeconds:0.#}s.")
                End If

                Return Await tcs.Task.ConfigureAwait(False)
            Catch
                Dim removed As TaskCompletionSource(Of JsonObject) = Nothing
                _pending.TryRemove(id, removed)
                Throw
            Finally
                registration.Dispose()
            End Try
        End Function

        ''' <summary>
        ''' fail every pending request, so no caller keeps waiting on a connection
        ''' that is going away.
        ''' </summary>
        Private Sub FailPendingRequests(reason As String)
            For Each key As String In _pending.Keys
                Dim tcs As TaskCompletionSource(Of JsonObject) = Nothing
                If _pending.TryRemove(key, tcs) Then
                    tcs.TrySetException(New InvalidOperationException(reason))
                End If
            Next
        End Sub

        ''' <summary>collect every page of a list method through its <c>nextCursor</c>.</summary>
        Private Async Function ListPagesAsync(method As String, collectionKey As String, ct As CancellationToken) As Task(Of List(Of JsonObject))
            Dim items As New List(Of JsonObject)()
            Dim cursor As String = Nothing
            Dim pages As Integer = 0

            Do
                Dim params As New jsobj()
                If Not String.IsNullOrEmpty(cursor) Then params("cursor") = New jsVal(cursor)

                Dim resp As JsonObject = Await RequestAsync(method, params, ct).ConfigureAwait(False)
                Dim result As jsobj = TryCast(resp("result"), jsobj)

                If result Is Nothing Then Exit Do

                Dim arr = TryCast(result(collectionKey), JsonArray)
                If arr IsNot Nothing Then
                    For Each item As JsonElement In arr
                        Dim o As JsonObject = TryCast(item, JsonObject)
                        If o IsNot Nothing Then items.Add(o)
                    Next
                End If

                cursor = result.Str("nextCursor")
                pages += 1
            Loop While Not String.IsNullOrEmpty(cursor) AndAlso pages < MaxListPages

            If Not String.IsNullOrEmpty(cursor) Then
                Console.Error.WriteLine($"[MCP {_name}] '{method}' reached the page cap ({MaxListPages}) and stopped early.")
            End If

            Return items
        End Function

        Public Async Function ListToolsAsync(Optional ct As CancellationToken = Nothing) As Task(Of List(Of McpTool))
            Dim list As New List(Of McpTool)()
            For Each o As JsonObject In Await ListPagesAsync("tools/list", "tools", ct).ConfigureAwait(False)
                list.Add(McpTool.FromJson(o))
            Next
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
            Dim list As New List(Of McpResource)()
            For Each o As JsonObject In Await ListPagesAsync("resources/list", "resources", ct).ConfigureAwait(False)
                list.Add(McpResource.FromJson(o))
            Next
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
            Dim list As New List(Of McpPrompt)()
            For Each o As JsonObject In Await ListPagesAsync("prompts/list", "prompts", ct).ConfigureAwait(False)
                list.Add(McpPrompt.FromJson(o))
            Next
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
            FailPendingRequests($"the mcp connection to the server '{_name}' was closed.")
            If _transport IsNot Nothing Then Await _transport.CloseAsync().ConfigureAwait(False)
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            If Not _disposed Then
                _disposed = True
                RemoveHandler _transport.MessageReceived, AddressOf OnMessage
                FailPendingRequests($"the mcp client '{_name}' was disposed.")
                Try
                    _transport.CloseAsync().GetAwaiter().GetResult()
                Catch
                End Try
            End If
        End Sub

    End Class

End Namespace
