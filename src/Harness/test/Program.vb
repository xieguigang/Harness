Imports System.Linq

Namespace HarnessSelfTest

    ''' <summary>
    ''' entry point of the harness self test. without arguments the MCP client is
    ''' verified end to end; with <c>--stub-mcp-server</c> this very executable
    ''' becomes the dependency free MCP server the test talks to (see
    ''' <see cref="McpStubServer"/>).
    ''' </summary>
    Friend Module Program

        Function Main(args As String()) As Integer
            If args IsNot Nothing AndAlso args.Length > 0 Then
                Select Case args(0)
                    Case "--stub-mcp-server"
                        Return McpStubServer.Run(args.Skip(1).ToArray())
                    Case "--help", "-h"
                        PrintUsage()
                        Return 0
                End Select
            End If

            Dim mcpExit As Integer = McpSelfTest.RunAll()
            Dim skillExit As Integer = SkillSelfTest.RunAll()

            Console.WriteLine()
            Console.WriteLine($"HarnessSelfTest finished: mcp={(If(mcpExit = 0, "ok", "failed"))}, skills={(If(skillExit = 0, "ok", "failed"))}")

            Return If(mcpExit = 0 AndAlso skillExit = 0, 0, 1)
        End Function

        Private Sub PrintUsage()
            Console.WriteLine("HarnessSelfTest - harness mcp self test")
            Console.WriteLine()
            Console.WriteLine("  HarnessSelfTest                    run the offline mcp self test")
            Console.WriteLine("  HarnessSelfTest --stub-mcp-server  act as the stub mcp server on stdio")
            Console.WriteLine()
            Console.WriteLine("stub options:")
            Console.WriteLine("  --tool-name <name>            tool exposed by the stub (default: echo)")
            Console.WriteLine("  --delay-ms <n>                delay every response by n milliseconds")
            Console.WriteLine("  --hang-tool <name>            never answer this tool call")
            Console.WriteLine("  --paginate                    split tools/list into two cursor pages")
            Console.WriteLine("  --server-request-marker <f>   send a server request and record the answer code in <f>")
            Console.WriteLine("  --pid-file <f>                write the stub pid to <f>")
            Console.WriteLine("  --exit-marker <f>             write <f> when the stub exits gracefully")
            Console.WriteLine("  --crash-on-start              exit immediately with code 3")
            Console.WriteLine("  --legacy-framing              answer with lsp style Content-Length framing")
        End Sub

    End Module

End Namespace
