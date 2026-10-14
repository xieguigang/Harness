Imports System.Collections.Generic
Imports System.IO
Imports Microsoft.VisualBasic.MIME.application.json
Imports Microsoft.VisualBasic.MIME.application.json.Javascript
Imports jsobj = Microsoft.VisualBasic.MIME.application.json.Javascript.JsonObject

Namespace MCP

    ''' <summary>
    ''' loads the MCP server catalogue of a workspace from its configuration file
    ''' (<c>&lt;workspace&gt;\.harness\mcp.json</c> by default).
    '''
    ''' the file follows the common <c>mcpServers</c> shape:
    ''' <code>
    ''' {
    '''   "mcpServers": {
    '''     "paper":  { "command": "python", "args": ["server.py"], "env": { "KEY": "v" }, "cwd": "." },
    '''     "remote": { "url": "https://example.org/mcp", "headers": { "X-K": "v" } }
    '''   }
    ''' }
    ''' </code>
    ''' a bare map of server objects (without the <c>mcpServers</c> wrapper) is also
    ''' accepted. broken entries are skipped and reported through <c>errors</c>, so
    ''' one bad entry never hides the healthy servers.
    ''' </summary>
    Public Module McpConfig

        Public Const ConfigFileName As String = "mcp.json"

        ''' <summary>workspace relative folder holding the harness configuration, see <see cref="Sandbox.ArtifactFolder"/>.</summary>
        Public Const ConfigFolderName As String = ".harness"

        ''' <summary>the default configuration path of a workspace: <c>&lt;workspace&gt;\.harness\mcp.json</c>.</summary>
        Public Function DefaultPath(workspace As String) As String
            If String.IsNullOrWhiteSpace(workspace) Then Return Nothing
            Return IO.Path.Combine(workspace, ConfigFolderName, ConfigFileName)
        End Function

        ''' <summary>load the servers declared for a workspace; missing file yields no servers.</summary>
        Public Function LoadFromWorkspace(workspace As String, ByRef errors() As String) As McpServerDescriptor()
            Return Load(DefaultPath(workspace), workspace, errors)
        End Function

        ''' <summary>load the servers of a configuration file; a missing file yields no servers.</summary>
        Public Function Load(path As String) As McpServerDescriptor()
            Dim errors() As String = Nothing
            Return Load(path, Nothing, errors)
        End Function

        ''' <summary>
        ''' read and parse the configuration file. returns the usable server descriptors;
        ''' anything that could not be understood is described in <paramref name="errors"/>.
        ''' </summary>
        ''' <param name="path">the configuration file; a missing file simply yields an empty result.</param>
        ''' <param name="workspaceRoot">base folder of relative commands / working directories; defaults to the parent of the configuration folder.</param>
        ''' <param name="errors">human readable problems found while loading.</param>
        Public Function Load(path As String, workspaceRoot As String, ByRef errors() As String) As McpServerDescriptor()
            Dim list As New List(Of McpServerDescriptor)()
            Dim problems As New List(Of String)()

            If String.IsNullOrWhiteSpace(path) OrElse Not IO.File.Exists(path) Then
                errors = problems.ToArray()
                Return list.ToArray()
            End If

            Dim fullPath As String
            Try
                fullPath = IO.Path.GetFullPath(path)
            Catch ex As Exception
                problems.Add($"invalid mcp configuration path '{path}': {ex.Message}")
                errors = problems.ToArray()
                Return list.ToArray()
            End Try

            Dim baseDir As String = ResolveBaseDirectory(fullPath, workspaceRoot)

            Dim text As String

            Try
                text = IO.File.ReadAllText(fullPath)
            Catch ex As Exception
                problems.Add($"unable to read the mcp configuration '{fullPath}': {ex.Message}")
                errors = problems.ToArray()
                Return list.ToArray()
            End Try

            Dim root As JsonObject = Nothing

            Try
                root = TryCast(JsonElement.ParseJSON(text), jsobj)
            Catch ex As Exception
                problems.Add($"'{fullPath}' is not valid json: {ex.Message}")
                errors = problems.ToArray()
                Return list.ToArray()
            End Try

            If root Is Nothing Then
                problems.Add($"'{fullPath}' must contain a json object.")
                errors = problems.ToArray()
                Return list.ToArray()
            End If

            ' tolerate both {"mcpServers": {...}} and a bare {"name": {...}} map
            Dim servers As JsonObject = TryCast(root("mcpServers"), jsobj)
            If servers Is Nothing Then servers = root

            For Each entry In servers
                Dim name As String = entry.Name
                Dim spec As JsonObject = TryCast(entry.Value, JsonObject)

                If spec Is Nothing Then
                    problems.Add($"the mcp server '{name}' must be a json object.")
                    Continue For
                End If

                Dim descriptor As McpServerDescriptor = ParseServer(name, spec, baseDir, problems)
                If descriptor IsNot Nothing Then list.Add(descriptor)
            Next

            errors = problems.ToArray()
            Return list.ToArray()
        End Function

        Private Function ParseServer(name As String, spec As JsonObject, baseDir As String, problems As List(Of String)) As McpServerDescriptor
            Dim url As String = JsonRpc.Str(spec, "url")

            If Not String.IsNullOrEmpty(url) Then
                Dim http As New McpHttpServer(name, url)

                Dim headers = TryCast(spec("headers"), JsonObject)
                If headers IsNot Nothing Then
                    http.Headers = New Dictionary(Of String, String)()
                    For Each h In headers
                        http.Headers(h.Name) = h.Value.ToString()
                    Next
                End If

                Dim token As String = JsonRpc.Str(spec, "bearerToken")
                If String.IsNullOrEmpty(token) Then token = JsonRpc.Str(spec, "bearer_token")
                http.BearerToken = token

                Return http
            End If

            Dim command As String = JsonRpc.Str(spec, "command")

            If String.IsNullOrEmpty(command) Then
                problems.Add($"the mcp server '{name}' declares neither 'command' nor 'url'.")
                Return Nothing
            End If

            If IsRelativeCommand(command) Then
                command = IO.Path.GetFullPath(IO.Path.Combine(baseDir, command))
            End If

            Dim stdio As New McpStdioServer(name, command)

            Dim args = TryCast(spec("args"), JsonArray)
            If args IsNot Nothing Then
                For Each a As JsonElement In args
                    stdio.Arguments.Add(a.ToString())
                Next
            End If

            Dim env = TryCast(spec("env"), JsonObject)
            If env IsNot Nothing Then
                stdio.Environment = New Dictionary(Of String, String)()
                For Each e In env
                    stdio.Environment(e.Name) = e.Value.ToString()
                Next
            End If

            Dim cwd As String = JsonRpc.Str(spec, "cwd")
            If String.IsNullOrEmpty(cwd) Then cwd = JsonRpc.Str(spec, "workingDirectory")

            If Not String.IsNullOrEmpty(cwd) Then
                If IO.Path.IsPathRooted(cwd) Then
                    stdio.WorkingDirectory = cwd
                Else
                    stdio.WorkingDirectory = IO.Path.GetFullPath(IO.Path.Combine(baseDir, cwd))
                End If
            Else
                stdio.WorkingDirectory = baseDir
            End If

            Return stdio
        End Function

        ''' <summary>
        ''' a command is resolved against the workspace only when it looks like a path;
        ''' plain executables stay untouched so the system PATH keeps working.
        ''' </summary>
        Private Function IsRelativeCommand(command As String) As Boolean
            If IO.Path.IsPathRooted(command) Then Return False
            Return command.IndexOf("/"c) >= 0 OrElse command.IndexOf("\"c) >= 0
        End Function

        Private Function ResolveBaseDirectory(configPath As String, workspaceRoot As String) As String
            If Not String.IsNullOrWhiteSpace(workspaceRoot) Then
                Return IO.Path.GetFullPath(workspaceRoot)
            End If

            ' <workspace>\.harness\mcp.json -> the workspace is the parent of the configuration folder
            Dim configDir As String = IO.Path.GetDirectoryName(configPath)
            If String.IsNullOrEmpty(configDir) Then Return IO.Path.GetDirectoryName(configPath)

            Dim parent As String = IO.Path.GetDirectoryName(configDir)
            Return If(String.IsNullOrEmpty(parent), configDir, parent)
        End Function

    End Module

End Namespace
