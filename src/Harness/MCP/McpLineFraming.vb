Imports System.IO
Imports System.Runtime.CompilerServices
Imports System.Text
Imports Microsoft.VisualBasic.MIME.application.json
Imports Microsoft.VisualBasic.MIME.application.json.Javascript
Imports jsobj = Microsoft.VisualBasic.MIME.application.json.Javascript.JsonObject

Namespace MCP

    ''' <summary>
    ''' the framing helper of the MCP stdio transport.
    '''
    ''' unlike the LSP flavoured <see cref="JsonRpc"/> transport (a Content-Length
    ''' header block followed by the raw body), MCP over stdio carries exactly one
    ''' json-rpc message per line: the message is serialized, written as a single
    ''' utf8 line terminated by a line feed, and the reader consumes one line at a
    ''' time. blank lines are skipped so a chatty server cannot break the framing.
    ''' </summary>
    ''' <remarks>
    ''' this helper deliberately lives inside the harness mcp namespace: the shared
    ''' json library still speaks Content-Length framing for language servers and
    ''' must keep that behaviour untouched. only the message shape helpers
    ''' (<see cref="JsonRpc.MakeRequest"/>, <see cref="JsonRpc.MakeError"/>, ...) are reused.
    ''' </remarks>
    Friend Module McpLineFraming

        ' one reader per stream: a StreamReader owns the utf8 decoder state and an
        ' internal buffer, so a second reader over the same pipe would lose bytes.
        Private ReadOnly _readers As New ConditionalWeakTable(Of Stream, StreamReader)

        ''' <summary>
        ''' write one message as a single utf8 line ('\n' terminated) and flush it,
        ''' so the peer sees the complete message immediately.
        ''' </summary>
        Friend Async Function WriteMessageAsync(stream As Stream, message As JsonElement) As Task
            Dim bytes As Byte() = Encoding.UTF8.GetBytes(message.BuildJsonString & vbLf)

            Await stream.WriteAsync(bytes, 0, bytes.Length)
            Await stream.FlushAsync()
        End Function

        ''' <summary>
        ''' read the next complete message, skipping blank lines and malformed lines.
        ''' returns <c>Nothing</c> when the peer closed the stream.
        ''' </summary>
        Friend Async Function ReadMessageAsync(stream As Stream) As Task(Of JsonObject)
            Dim reader As StreamReader = _readers.GetValue(stream, Function(s) New StreamReader(s, New UTF8Encoding(False), False, 4096))

            While True
                Dim line As String = Await reader.ReadLineAsync()

                If line Is Nothing Then
                    Return Nothing
                End If

                line = line.Trim()
                If line.Length = 0 Then Continue While

                Try
                    Dim node As JsonElement = JsonElement.ParseJSON(line)
                    Dim obj As JsonObject = TryCast(node, JsonObject)

                    If obj IsNot Nothing Then
                        Return obj
                    End If

                    Console.Error.WriteLine("[MCP stdio] ignored a json message that is not an object.")
                Catch ex As Exception
                    Console.Error.WriteLine("[MCP stdio] ignored a malformed json line: " & ex.Message)
                End Try
            End While

            Return Nothing
        End Function

        ''' <summary>
        ''' build a json-rpc error response; used to answer server initiated requests
        ''' whose method this client does not implement (-32601 method not found).
        ''' </summary>
        Friend Function MakeErrorResponse(id As JsonElement, code As Integer, message As String) As jsobj
            Return JsonRpc.MakeError(id, code, message)
        End Function

    End Module

End Namespace
