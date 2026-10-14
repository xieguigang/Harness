Imports System.Diagnostics
Imports System.IO
Imports System.Threading
Imports Microsoft.VisualBasic.MIME.application.json
Imports Microsoft.VisualBasic.MIME.application.json.Javascript
Imports jsobj = Microsoft.VisualBasic.MIME.application.json.Javascript.JsonObject

Namespace MCP

    ''' <summary>
    ''' stdio transport: launches the MCP server as a child process and exchanges
    ''' newline delimited json-rpc messages over its stdin/stdout (one complete
    ''' message per line, see <see cref="McpLineFraming"/>).
    ''' </summary>
    ''' <remarks>
    ''' MCP over stdio is *not* the LSP Content-Length framing: every message is a
    ''' single utf8 line. reusing the base library framing here would leave the
    ''' handshake stuck waiting for a header block that never arrives.
    ''' </remarks>
    Public Class McpStdioTransport : Implements IMcpTransport

        Private ReadOnly _server As McpStdioServer
        Private _process As Process
        Private _stdin As Stream
        Private _stdout As Stream
        Private _readerTask As Task
        Private _errTask As Task
        Private _cts As CancellationTokenSource
        Private ReadOnly _writeSem As New SemaphoreSlim(1, 1)

        Public Event MessageReceived As Action(Of JsonObject) Implements IMcpTransport.MessageReceived

        Public Sub New(server As McpStdioServer)
            _server = server
        End Sub

        Public Function ConnectAsync(Optional cancellationToken As CancellationToken = Nothing) As Task Implements IMcpTransport.ConnectAsync
            _cts = New CancellationTokenSource()

            Dim psi As New ProcessStartInfo With {
                .FileName = _server.Command,
                .UseShellExecute = False,
                .RedirectStandardInput = True,
                .RedirectStandardOutput = True,
                .RedirectStandardError = True,
                .CreateNoWindow = True
            }
            If Not String.IsNullOrEmpty(_server.WorkingDirectory) Then
                psi.WorkingDirectory = _server.WorkingDirectory
            End If
            If _server.Arguments IsNot Nothing Then
                For Each a In _server.Arguments
                    psi.ArgumentList.Add(a)
                Next
            End If
            If _server.Environment IsNot Nothing Then
                For Each kv In _server.Environment
                    psi.Environment(kv.Key) = kv.Value
                Next
            End If

            Try
                _process = Process.Start(psi)
            Catch ex As Exception
                Throw New InvalidOperationException($"unable to start the mcp server '{_server.Name}' ({_server.Command}): {ex.Message}", ex)
            End Try

            If _process Is Nothing Then
                Throw New InvalidOperationException($"unable to start the mcp server '{_server.Name}' ({_server.Command}).")
            End If

            _stdout = _process.StandardOutput.BaseStream
            _stdin = _process.StandardInput.BaseStream

            _readerTask = Task.Run(AddressOf ReadLoop)
            _errTask = Task.Run(AddressOf ErrLoop)
            Return Task.CompletedTask
        End Function

        Private Async Function ReadLoop() As Task
            Try
                While Not _cts.IsCancellationRequested
                    Dim msg As JsonObject = Await McpLineFraming.ReadMessageAsync(_stdout).ConfigureAwait(False)
                    If msg Is Nothing Then Exit While
                    RaiseEvent MessageReceived(msg)
                End While
            Catch ex As Exception
                If Not _cts.IsCancellationRequested Then
                    Console.Error.WriteLine("[MCP stdio] transport error: " & ex.Message)
                End If
            End Try
        End Function

        ' drain stderr so a chatty server never blocks on a full pipe
        Private Async Function ErrLoop() As Task
            Try
                Dim line As String
                Do
                    line = Await _process.StandardError.ReadLineAsync().ConfigureAwait(False)
                    If line Is Nothing Then Exit Do
                Loop
            Catch
            End Try
        End Function

        Public Async Function SendAsync(message As JsonObject, Optional cancellationToken As CancellationToken = Nothing) As Task Implements IMcpTransport.SendAsync
            Await _writeSem.WaitAsync().ConfigureAwait(False)
            Try
                Await McpLineFraming.WriteMessageAsync(_stdin, message).ConfigureAwait(False)
            Finally
                _writeSem.Release()
            End Try
        End Function

        Public Async Function CloseAsync() As Task Implements IMcpTransport.CloseAsync
            Try
                _cts?.Cancel()

                ' closing stdin signals eof: the polite shutdown request of a stdio server
                Try
                    _stdin?.Close()
                Catch
                End Try

                If _process IsNot Nothing AndAlso Not _process.HasExited Then
                    Dim exited As Boolean = Await Task.Run(Of Boolean)(Function() _process.WaitForExit(2000)).ConfigureAwait(False)
                    If Not exited Then
                        ' a wrapper such as uv/python may leave grand children behind,
                        ' so the whole process tree has to go
                        Try
                            _process.Kill(entireProcessTree:=True)
                        Catch ex As Exception
                            Console.Error.WriteLine("[MCP stdio] kill error: " & ex.Message)
                        End Try
                    End If
                End If

                If _readerTask IsNot Nothing Then Await _readerTask.ConfigureAwait(False)
                If _errTask IsNot Nothing Then Await _errTask.ConfigureAwait(False)
            Catch ex As Exception
                Console.Error.WriteLine("[MCP stdio] close error: " & ex.Message)
            Finally
                _process?.Dispose()
            End Try
        End Function

    End Class

End Namespace
