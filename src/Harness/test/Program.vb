Imports System.Linq
Imports Microsoft.VisualBasic.ComponentModel.Settings.Inf
Imports Ollama

Namespace HarnessSelfTest

    ''' <summary>
    ''' entry point of the harness self test. without arguments the MCP client is
    ''' verified end to end; with <c>--stub-mcp-server</c> this very executable
    ''' becomes the dependency free MCP server the test talks to (see
    ''' <see cref="McpStubServer"/>).
    ''' </summary>
    Friend Module Program

        ReadOnly apikey As String

        Sub New()
            apikey = INIProfile.GetPrivateProfileString("LLMs", "apiKey", "C:\Users\Administrator\AppData\Local\OmicsWorks\omics-works-config.ini")
        End Sub

        Public Function GetLLMs() As LLMClient
            Return New LLMClient(New OpenAIProvider("https://api.deepseek.com", apikey), "deepseek-flash")
        End Function

        ''' <summary>
        ''' The same client as <see cref="GetLLMs"/>, with the model name overridden -
        ''' same endpoint and key, only the model changes.
        ''' </summary>
        Public Function GetLLMs(model As String) As LLMClient
            Return New LLMClient(New OpenAIProvider("https://api.deepseek.com", apikey), model)
        End Function

        Function Main(args As String()) As Integer
            If args IsNot Nothing AndAlso args.Length > 0 Then
                Select Case args(0)
                    Case "--stub-mcp-server"
                        Return McpStubServer.Run(args.Skip(1).ToArray())
                    Case "--polish"
                        ' the async test is awaited synchronously: a console application
                        ' has no SynchronizationContext, so this cannot deadlock
                        Return NaturePolishingSelfTest.RunAsync(args.Skip(1).ToArray()).GetAwaiter().GetResult()
                    Case "--help", "-h"
                        PrintUsage()
                        Return 0
                End Select
            End If

            Dim mcpExit As Integer = McpSelfTest.RunAll()
            Dim skillExit As Integer = SkillSelfTest.RunAll()
            Dim askExit As Integer = AskUserQuestionSelfTest.RunAll()

            Console.WriteLine()
            Console.WriteLine($"HarnessSelfTest finished: mcp={(If(mcpExit = 0, "ok", "failed"))}, skills={(If(skillExit = 0, "ok", "failed"))}, ask={(If(askExit = 0, "ok", "failed"))}")

            Return If(mcpExit = 0 AndAlso skillExit = 0 AndAlso askExit = 0, 0, 1)
        End Function

        Private Sub PrintUsage()
            Console.WriteLine("HarnessSelfTest - harness self test (mcp client, skill system, ask_user_question)")
            Console.WriteLine()
            Console.WriteLine("  HarnessSelfTest                    run the offline self tests (mcp, skills, ask_user_question)")
            Console.WriteLine("  HarnessSelfTest --polish           run the real nature-polishing skill end to end test")
            Console.WriteLine("  HarnessSelfTest --stub-mcp-server  act as the stub mcp server on stdio")
            Console.WriteLine()
            Console.WriteLine("polishing options (used after --polish):")
            Console.WriteLine("  --file <path>     the draft to polish (default: a built in sample paragraph)")
            Console.WriteLine("  --skills <path>   the nature skills root (default: C:\Users\Administrator\Downloads\nature-skills\skills)")
            Console.WriteLine("  --model <name>    override the model name (default: the one GetLLMs uses)")
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
