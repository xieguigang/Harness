Imports System.Threading
Imports Microsoft.VisualBasic.MIME.application.json.Javascript

Namespace MCP

    ''' <summary>
    ''' low level transport for a single MCP server connection. the transport only
    ''' moves framed json-rpc messages in both directions; request/response
    ''' correlation (by id) and notification dispatch are handled by <see cref="McpClient"/>.
    ''' </summary>
    Public Interface IMcpTransport

        ''' <summary>open the connection (spawn the subprocess, open the http session, ...).</summary>
        Function ConnectAsync(Optional cancellationToken As CancellationToken = Nothing) As Task

        ''' <summary>send one json-rpc message (request or notification) to the server.</summary>
        Function SendAsync(message As JsonObject, Optional cancellationToken As CancellationToken = Nothing) As Task

        ''' <summary>
        ''' raised for every complete message received from the server (both responses
        ''' and server-initiated notifications). <see cref="McpClient"/> subscribes to it.
        ''' </summary>
        Event MessageReceived As Action(Of JsonObject)

        ''' <summary>
        ''' raised when the transport can no longer carry messages, for example because
        ''' the child process exited or the peer closed the connection. it lets
        ''' <see cref="McpClient"/> fail the pending requests immediately instead of
        ''' waiting for their timeout.
        ''' </summary>
        Event TransportClosed As Action

        ''' <summary>close the connection and release resources.</summary>
        Function CloseAsync() As Task

    End Interface

End Namespace
