Imports System.IO
Imports System.Net.Http
Imports System.Net.Http.Headers
Imports System.Text
Imports System.Threading
Imports Microsoft.VisualBasic.MIME.application.json
Imports Microsoft.VisualBasic.MIME.application.json.Javascript
Imports jsobj = Microsoft.VisualBasic.MIME.application.json.Javascript.JsonObject

Namespace MCP

    ''' <summary>
    ''' Streamable HTTP transport (MCP 2025-03-26): each request is an HTTP POST whose
    ''' body is the json-rpc message; the response may be a plain json object or an
    ''' SSE stream (text/event-stream). the server's Mcp-Session-Id is captured and
    ''' replayed on subsequent requests.
    ''' </summary>
    ''' <remarks>
    ''' the request/response is inherently paired per POST, so correlation is done inline;
    ''' any server-initiated notifications piggy-backed on the POST response are also
    ''' surfaced through <see cref="MessageReceived"/>.
    ''' </remarks>
    Public Class McpHttpTransport : Implements IMcpTransport

        Private ReadOnly _server As McpHttpServer
        Private _http As HttpClient
        Private _sessionId As String
        Private _disposed As Boolean

        Public Event MessageReceived As Action(Of JsonObject) Implements IMcpTransport.MessageReceived
        Public Event TransportClosed As Action Implements IMcpTransport.TransportClosed

        Public Sub New(server As McpHttpServer)
            _server = server
            _http = New HttpClient() With {.Timeout = TimeSpan.FromSeconds(120)}
        End Sub

        Public Function ConnectAsync(Optional cancellationToken As CancellationToken = Nothing) As Task Implements IMcpTransport.ConnectAsync
            ' the session is established on the first request (server replies Mcp-Session-Id)
            Return Task.CompletedTask
        End Function

        Public Async Function SendAsync(message As JsonObject, Optional cancellationToken As CancellationToken = Nothing) As Task Implements IMcpTransport.SendAsync
            ' when the message is a request, the sse stream is released as soon as the
            ' matching response arrives: a streamable http server keeps the event stream
            ' open, so waiting for eof would always run into the http timeout.
            Dim expectId As String = Nothing
            Dim idNode As JsonElement = message("id")
            If idNode IsNot Nothing Then expectId = idNode.ToString()

            Using request As New HttpRequestMessage(HttpMethod.Post, _server.Url)
                request.Content = New StringContent(message.BuildJsonString(), Encoding.UTF8, "application/json")
                request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream")
                If Not String.IsNullOrEmpty(_sessionId) Then
                    request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId)
                End If
                If Not String.IsNullOrEmpty(_server.BearerToken) Then
                    request.Headers.Authorization = New AuthenticationHeaderValue("Bearer", _server.BearerToken)
                End If
                If _server.Headers IsNot Nothing Then
                    For Each kv In _server.Headers
                        request.Headers.TryAddWithoutValidation(kv.Key, kv.Value)
                    Next
                End If

                Using response As HttpResponseMessage = Await _http.SendAsync(request, cancellationToken).ConfigureAwait(False)
                    If Not response.IsSuccessStatusCode Then
                        Dim body As String = If(response.Content IsNot Nothing, Await response.Content.ReadAsStringAsync().ConfigureAwait(False), "")
                        Throw New Exception($"MCP HTTP request failed ({(CInt(response.StatusCode))} {response.ReasonPhrase}): {body}")
                    End If

                    If response.Headers.Contains("Mcp-Session-Id") Then
                        _sessionId = response.Headers.GetValues("Mcp-Session-Id").FirstOrDefault()
                    End If

                    Dim mediaType As String = If(response.Content.Headers.ContentType Is Nothing, "", response.Content.Headers.ContentType.MediaType)

                    If mediaType.Contains("text/event-stream") Then
                        Await ReadSseAsync(response.Content.ReadAsStream(), expectId, cancellationToken).ConfigureAwait(False)
                    Else
                        RaiseMessage(Await response.Content.ReadAsStringAsync().ConfigureAwait(False))
                    End If
                End Using
            End Using
        End Function

        Private Sub RaiseMessage(text As String)
            If String.IsNullOrWhiteSpace(text) Then Return

            Try
                Dim obj As JsonObject = TryCast(JsonElement.ParseJSON(text), jsobj)
                If obj IsNot Nothing Then RaiseEvent MessageReceived(obj)
            Catch ex As Exception
                Console.Error.WriteLine("[MCP http] response parse error: " & ex.Message)
            End Try
        End Sub

        Private Async Function ReadSseAsync(stream As Stream, expectId As String, ct As CancellationToken) As Task
            Using reader As New StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks:=True, bufferSize:=1024, leaveOpen:=True)
                Dim data As New StringBuilder()
                Dim line As String

                Do
                    line = Await reader.ReadLineAsync().ConfigureAwait(False)

                    If line Is Nothing Then
                        Exit Do
                    ElseIf line.Length = 0 Then
                        If data.Length > 0 Then
                            Dim txt As String = data.ToString()
                            data.Clear()

                            If txt <> "[DONE]" Then
                                ' the awaited response arrived: release the stream
                                If EmitSseMessage(txt, expectId) Then Return
                            End If
                        End If
                    ElseIf line.StartsWith("data:") Then
                        If data.Length > 0 Then data.Append(vbLf)
                        data.Append(line.Substring(5).TrimStart())
                    End If
                Loop

                ' an event not terminated by a blank line
                If data.Length > 0 AndAlso data.ToString() <> "[DONE]" Then
                    EmitSseMessage(data.ToString(), expectId)
                End If
            End Using
        End Function

        ''' <summary>
        ''' raise one sse payload through <see cref="MessageReceived"/>. returns true when
        ''' the payload is the response the caller waits for (so the stream can be released).
        ''' </summary>
        Private Function EmitSseMessage(text As String, expectId As String) As Boolean
            Dim obj As JsonObject

            Try
                obj = TryCast(JsonElement.ParseJSON(text), jsobj)
            Catch ex As Exception
                Console.Error.WriteLine("[MCP http] sse parse error: " & ex.Message)
                Return False
            End Try

            If obj Is Nothing Then Return False

            RaiseEvent MessageReceived(obj)

            ' a server initiated message carries a method and is never the awaited response
            If expectId Is Nothing OrElse Not String.IsNullOrEmpty(obj.Str("method")) Then
                Return False
            End If

            Dim idNode As JsonElement = obj("id")
            Return idNode IsNot Nothing AndAlso idNode.ToString() = expectId
        End Function

        Public Function CloseAsync() As Task Implements IMcpTransport.CloseAsync
            If Not _disposed Then
                _http?.Dispose()
                _http = Nothing
                _disposed = True
            End If

            RaiseEvent TransportClosed()
            Return Task.CompletedTask
        End Function

    End Class

End Namespace
