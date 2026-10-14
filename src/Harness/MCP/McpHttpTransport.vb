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

        Public Sub New(server As McpHttpServer)
            _server = server
            _http = New HttpClient() With {.Timeout = TimeSpan.FromSeconds(120)}
        End Sub

        Public Function ConnectAsync(Optional cancellationToken As CancellationToken = Nothing) As Task Implements IMcpTransport.ConnectAsync
            ' the session is established on the first request (server replies Mcp-Session-Id)
            Return Task.CompletedTask
        End Function

        Public Async Function SendAsync(message As JsonObject, Optional cancellationToken As CancellationToken = Nothing) As Task Implements IMcpTransport.SendAsync
            Dim json As String = message.BuildJsonString()
            Dim content As New StringContent(json, Encoding.UTF8, "application/json")

            Dim request As New HttpRequestMessage(HttpMethod.Post, _server.Url) With {.Content = content}
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

            Dim response As HttpResponseMessage = Await _http.SendAsync(request, cancellationToken).ConfigureAwait(False)
            If Not response.IsSuccessStatusCode Then
                Dim body As String = If(response.Content IsNot Nothing, Await response.Content.ReadAsStringAsync().ConfigureAwait(False), "")
                Throw New Exception($"MCP HTTP request failed ({(CInt(response.StatusCode))} {response.ReasonPhrase}): {body}")
            End If

            If response.Headers.Contains("Mcp-Session-Id") Then
                _sessionId = response.Headers.GetValues("Mcp-Session-Id").FirstOrDefault()
            End If

            Dim mediaType As String = If(response.Content.Headers.ContentType?.MediaType, "")
            If mediaType.Contains("text/event-stream") Then
                Await ReadSseAsync(response.Content.ReadAsStream(), cancellationToken).ConfigureAwait(False)
            Else
                Dim txt As String = Await response.Content.ReadAsStringAsync().ConfigureAwait(False)
                If Not String.IsNullOrWhiteSpace(txt) Then
                    Dim node As JsonElement = JsonElement.ParseJSON(txt)
                    Dim obj = TryCast(node, jsobj)
                    If obj IsNot Nothing Then RaiseEvent MessageReceived(obj)
                End If
            End If
        End Function

        Private Async Function ReadSseAsync(stream As Stream, ct As CancellationToken) As Task
            Using reader As New StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks:=True, bufferSize:=1024, leaveOpen:=True)
                Dim data As New StringBuilder()
                Dim line As String
                Do
                    line = Await reader.ReadLineAsync().ConfigureAwait(False)
                    If line Is Nothing Then Exit Do
                    If line.StartsWith("data:") Then
                        data.Append(line.Substring(5).TrimStart())
                    ElseIf line = "" Then
                        If data.Length > 0 Then
                            Dim txt As String = data.ToString()
                            data.Clear()
                            If txt <> "[DONE]" Then
                                Try
                                    Dim node As JsonElement = JsonElement.ParseJSON(txt)
                                    Dim obj = TryCast(node, jsobj)
                                    If obj IsNot Nothing Then RaiseEvent MessageReceived(obj)
                                Catch ex As Exception
                                    Console.Error.WriteLine("[MCP http] sse parse error: " & ex.Message)
                                End Try
                            End If
                        End If
                    End If
                Loop
            End Using
        End Function

        Public Function CloseAsync() As Task Implements IMcpTransport.CloseAsync
            If Not _disposed Then
                _http?.Dispose()
                _http = Nothing
                _disposed = True
            End If
            Return Task.CompletedTask
        End Function

    End Class

End Namespace
