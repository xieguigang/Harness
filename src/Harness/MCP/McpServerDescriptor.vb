Imports System.Collections.Generic
Imports Microsoft.VisualBasic.MIME.application.json.Javascript

Namespace MCP

    ''' <summary>
    ''' describes an MCP server to connect to. the bridge builds a concrete
    ''' <see cref="IMcpTransport"/> from it via <see cref="CreateTransport"/>.
    ''' </summary>
    Public MustInherit Class McpServerDescriptor
        ''' <summary>a friendly, unique name for this server; used to namespace the exposed tool names.</summary>
        Public Property Name As String

        Public MustOverride Function CreateTransport() As IMcpTransport

        ''' <summary>turn the server name into a safe identifier fragment (letters/digits/_ only).</summary>
        Public Function SafePrefix() As String
            Return McpToolBridge.Sanitize(Name)
        End Function
    End Class

    ''' <summary>
    ''' a local MCP server launched as a child process; communication happens over its
    ''' stdin/stdout using newline delimited json-rpc messages (one complete message
    ''' per line, <b>not</b> the LSP Content-Length framing).
    ''' </summary>
    Public Class McpStdioServer : Inherits McpServerDescriptor
        Public Property Command As String
        Public Property Arguments As List(Of String)
        Public Property Environment As Dictionary(Of String, String)
        Public Property WorkingDirectory As String

        Public Sub New(name As String, command As String, ParamArray args As String())
            Me.Name = name
            Me.Command = command
            Me.Arguments = New List(Of String)()
            If args IsNot Nothing Then Me.Arguments.AddRange(args)
        End Sub

        Public Overrides Function CreateTransport() As IMcpTransport
            Return New McpStdioTransport(Me)
        End Function
    End Class

    ''' <summary>
    ''' a remote MCP server reached over the Streamable HTTP transport.
    ''' </summary>
    Public Class McpHttpServer : Inherits McpServerDescriptor
        Public Property Url As String
        Public Property Headers As Dictionary(Of String, String)
        Public Property BearerToken As String

        Public Sub New(name As String, url As String)
            Me.Name = name
            Me.Url = url
        End Sub

        Public Overrides Function CreateTransport() As IMcpTransport
            Return New McpHttpTransport(Me)
        End Function
    End Class

End Namespace
