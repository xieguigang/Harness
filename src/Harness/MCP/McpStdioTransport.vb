Imports System.Diagnostics
Imports System.IO
Imports System.Threading
Imports Microsoft.VisualBasic.MIME.application.json
Imports Microsoft.VisualBasic.MIME.application.json.Javascript
Imports jsobj = Microsoft.VisualBasic.MIME.application.json.Javascript.JsonObject

Namespace MCP

    ''' <summary>
    ''' stdio transport: launches the MCP server as a child process and exchanges
    ''' length-framed json-rpc messages over its stdin/stdout, reusing the shared
    ''' <see cref="JsonRpc"/> framing from the base json library (the stream is not a
    ''' network socket, just a process pipe — which is exactly why the framing was
    ''' generalized from NetworkStream to Stream).
    ''' </summary>
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

            _process = Process.Start(psi)
            _stdout = _process.StandardOutput.BaseStream
            _stdin = _process.StandardInput.BaseStream

            _readerTask = Task.Run(AddressOf ReadLoop)
            _errTask = Task.Run(AddressOf ErrLoop)
            Return Task.CompletedTask
        End Function

        Private Async Function ReadLoop() As Task
            Try
                While Not _cts.IsCancellationRequested
                    Dim msg As jsobj = Await JsonRpc.ReadMessageAsync(_stdout).ConfigureAwait(False)
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
                Await JsonRpc.SendMessageAsync(_stdin, message).ConfigureAwait(False)
            Finally
                _writeSem.Release()
            End Try
        End Function

        Public Async Function CloseAsync() As Task Implements IMcpTransport.CloseAsync
            Try
                _cts?.Cancel()
                Try
                    _stdin?.Close()
                Catch
                End Try
                If _process IsNot Nothing AndAlso Not _process.HasExited Then
                    _process.CloseMainWindow()
                    If Not _process.WaitForExit(2000) Then
                        Try : _process.Kill() : Catch : End Try
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
