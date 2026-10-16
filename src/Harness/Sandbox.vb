Imports System.Runtime.ExceptionServices
Imports Ollama
Imports Ollama.JSON.FunctionCall

''' <summary>
''' Sandbox - the <see cref="SecurityPolicy"/> gated filesystem backend that
''' all tools share.
''' 
''' This class owns the workspace root and the security policy, and provides
''' the two services every tool needs:
''' 
''' 1. Path resolution and the workspace boundary check
'''    (<see cref="ResolveAndAuthorize"/>, <see cref="ResolvePath"/>,
'''    <see cref="IsInsideWorkspace"/>, <see cref="WorkspaceRelative"/>).
''' 2. Permission decisions, including the <see cref="RequestPermission"/>
'''    event used to ask the host under <c>AskPermission</c>
'''    (<see cref="Authorize"/>).
''' 
''' The tools themselves live in the <c>Harness.Tools</c> namespace, one
''' file per tool, each holding a reference to a sandbox instance.
''' 
''' Path handling: a user supplied path is resolved against the workspace
''' root, then the active <see cref="SecurityPolicy"/> decides whether the
''' resolved target may be touched. A target inside the workspace is
''' available under every policy for read operations; a target that escapes
''' the workspace is denied by <c>Readonly</c>, allowed by
''' <c>FullControls</c>, and requires an explicit user confirmation through
''' the <see cref="RequestPermission"/> event under <c>AskPermission</c>.
''' </summary>
Public Class Sandbox

    ''' <summary>
    ''' Workspace relative folder that holds the side artifacts produced by
    ''' tools, such as the complete match list of a truncated grep result.
    ''' </summary>
    Public Const ArtifactFolder As String = ".harness"

    ReadOnly wsRoot As String
    ReadOnly wsSecurity As SecurityPolicy

    ''' <summary>
    ''' The background job registry of this session. It is created on first
    ''' use and lives as long as the sandbox does, so the jobs started
    ''' through it - and their output - belong to this session exactly like
    ''' the files the other tools touch.
    ''' </summary>
    Private ReadOnly wsJobs As New Lazy(Of Tools.BackgroundJobManager)(
        Function() New Tools.BackgroundJobManager(wsRoot))

    ''' <summary>
    ''' The MCP bridge created by <see cref="RegisterMcpServers"/>, if the
    ''' workspace declares any MCP server.
    ''' </summary>
    Private wsMcp As MCP.McpToolBridge

    ''' <summary>
    ''' The structured tool call log of this session. Every tool registered
    ''' through this sandbox - the local ones, the mcp primitives and the two
    ''' skill tools - runs through a logging wrapper installed by
    ''' <see cref="RegisterTool(Of T)"/>, so the log sees each call with its
    ''' arguments, its result and the time it took.
    ''' 
    ''' The log lives in <c>&lt;workspace&gt;\.harness\toolcalls\&lt;session&gt;\</c>
    ''' and is written as json arrays. A workspace that cannot be written to
    ''' degrades it to an in memory log; a tool call never fails because of it.
    ''' </summary>
    Private ReadOnly wsLog As Logging.ToolCallLogger

    ''' <summary>
    ''' Raised when a write or execute operation needs the user's consent.
    ''' Set <see cref="PermissionEventArg.PermissionGranted"/> to True inside
    ''' the handler to let the operation proceed.
    ''' </summary>
    Public Event RequestPermission(sender As Object, e As PermissionEventArg)

    ''' <summary>
    ''' Raised when the model asks the user a question through the
    ''' "ask_user_question" tool. The handler shows
    ''' <see cref="Tools.QuestionEventArg.Questions"/> to the user and
    ''' answers through the event argument: call
    ''' <see cref="Tools.QuestionEventArg.Reply"/> - with the response of
    ''' every question, keyed by its id - or
    ''' <see cref="Tools.QuestionEventArg.Decline"/> once the user decided.
    '''
    ''' A handler may answer before it returns, and the tool call then
    ''' continues at once; it may also return immediately and answer later
    ''' from any thread, which is what keeps a "timed" ask_user_question
    ''' call pending while the question remains answerable. A host that
    ''' does not subscribe leaves every question unanswered, and the tool
    ''' reports that instead of waiting forever.
    ''' </summary>
    Public Event AskUser(sender As Object, e As Tools.QuestionEventArg)

    Sub New(workspace As String, Optional security As SecurityPolicy = SecurityPolicy.AskPermission)
        If String.IsNullOrWhiteSpace(workspace) Then
            Throw New ArgumentNullException(NameOf(workspace))
        End If

        Dim sep As Char() = {IO.Path.DirectorySeparatorChar, IO.Path.AltDirectorySeparatorChar}
        Dim root As String = IO.Path.GetFullPath(workspace)
        Dim trimmed As String = root.TrimEnd(sep)

        ' keep the trailing separator of a drive root such as "C:\"
        If trimmed.Length > 2 Then
            root = trimmed
        End If

        wsRoot = root
        wsSecurity = security
        wsLog = New Logging.ToolCallLogger(root, securityPolicy:=security.ToString())
    End Sub

    ''' <summary>
    ''' The absolute path of the workspace root that bounds this sandbox.
    ''' </summary>
    Public ReadOnly Property Workspace As String
        Get
            Return wsRoot
        End Get
    End Property

    ''' <summary>
    ''' The security policy currently enforced by this sandbox.
    ''' </summary>
    Public ReadOnly Property Security As SecurityPolicy
        Get
            Return wsSecurity
        End Get
    End Property

    ''' <summary>
    ''' The background jobs of this session: what job_start starts, and what
    ''' job_list, job_output and job_kill work on. The registry is created on
    ''' first use and is shared by the sandbox and its tools.
    ''' </summary>
    Public ReadOnly Property Jobs As Tools.BackgroundJobManager
        Get
            Return wsJobs.Value
        End Get
    End Property

    ' ========================================================================
    ' Tool call log
    ' ========================================================================

    ''' <summary>
    ''' The structured log of every tool call of this session. It writes
    ''' <c>tool-calls.json</c>, <c>tools.json</c>, <c>permissions.json</c> and
    ''' <c>session.json</c> into
    ''' <c>&lt;workspace&gt;\<see cref="ArtifactFolder"/>\toolcalls\&lt;session&gt;\</c>.
    ''' 
    ''' A host may retune it before the first call (<see cref="Logging.ToolLogOptions"/>)
    ''' and should call <see cref="CloseToolLog"/> when the session ends, which
    ''' is what finishes <c>session.json</c>.
    ''' </summary>
    Public ReadOnly Property ToolLog As Logging.ToolCallLogger
        Get
            Return wsLog
        End Get
    End Property

    ''' <summary>
    ''' False switches the tool call log off; the tools then run exactly as
    ''' they did before the log existed. On by default.
    ''' 
    ''' The switch is read while the tools are registered: set it before
    ''' <see cref="RegisterTo"/> if a session should stay unlogged.
    ''' </summary>
    Public Property ToolLogEnabled As Boolean
        Get
            Return wsLog.Enabled
        End Get
        Set(value As Boolean)
            wsLog.Enabled = value
        End Set
    End Property

    ''' <summary>
    ''' True lets the sandbox put the chat log of the client - every request,
    ''' every reasoning, every answer and every tool call of the session -
    ''' next to its own tool call log, under the same session id. The two logs
    ''' note down where the other one lives; turn this off to log tool calls
    ''' only.
    ''' </summary>
    Public Property LlmSessionLogging As Boolean = True

    ''' <summary>
    ''' Optional host hook called at the beginning of every tool call, so that
    ''' each record can say what the host was doing - the task it is working
    ''' on and the llm round the call belongs to.
    ''' </summary>
    Public Property ToolLogContextProvider As Func(Of Logging.ToolCallContext)
        Get
            Return wsLog.ContextProvider
        End Get
        Set(value As Func(Of Logging.ToolCallContext))
            wsLog.ContextProvider = value
        End Set
    End Property

    ''' <summary>
    ''' Finish the tool call log: write <c>session.json</c> and release the
    ''' file handles. The records stay on disk; the host may keep using the
    ''' sandbox afterwards, though nothing is logged any more.
    ''' </summary>
    Public Sub CloseToolLog()
        Call wsLog.Close()
    End Sub

    ' ========================================================================
    ' Tool registration
    ' ========================================================================

    ''' <summary>
    ''' The tool registry: every entry binds one tool class to the name the
    ''' model calls it by. This is the single place a tool has to be listed
    ''' in, <see cref="RegisterTo"/> walks the table and instantiates each
    ''' tool against this sandbox.
    ''' </summary>
    Private Shared ReadOnly ToolRegistry As (Name As String, Tool As Type)() = {
        ("read", GetType(Tools.ReadTool)),
        ("write", GetType(Tools.WriteTool)),
        ("edit", GetType(Tools.EditTool)),
        ("multi_edit", GetType(Tools.MultiEditTool)),
        ("apply_patch", GetType(Tools.ApplyPatchTool)),
        ("glob", GetType(Tools.GlobTool)),
        ("grep", GetType(Tools.GrepTool)),
        ("ls", GetType(Tools.LsTool)),
        ("delete", GetType(Tools.DeleteTool)),
        ("move", GetType(Tools.MoveTool)),
        ("shell", GetType(Tools.ShellTool)),
        ("ask_user_question", GetType(Tools.AskUserQuestionTool)),
        ("job_start", GetType(Tools.JobStartTool)),
        ("job_kill", GetType(Tools.JobKillTool)),
        ("job_list", GetType(Tools.JobListTool)),
        ("job_output", GetType(Tools.JobOutputTool))
    }

    ''' <summary>
    ''' The exported name of every tool this sandbox can register, in registry
    ''' order.
    ''' </summary>
    Public ReadOnly Property ToolNames As String()
        Get
            Dim names(ToolRegistry.Length - 1) As String

            For i As Integer = 0 To ToolRegistry.Length - 1
                names(i) = ToolRegistry(i).Name
            Next

            Return names
        End Get
    End Property

    ''' <summary>
    ''' Register every tool backed by this sandbox into an Ollama client, so
    ''' that the model can call them by their exported names (see
    ''' <see cref="ToolNames"/>). Each tool instance is bound to this sandbox
    ''' and therefore shares its workspace and security policy.
    ''' 
    ''' A new tool class only needs one more entry in
    ''' <see cref="ToolRegistry"/> to become visible to the model.
    ''' </summary>
    ''' <param name="client">The client that should expose the tools.</param>
    Public Function RegisterTo(client As LLMClient) As Sandbox
        If client Is Nothing Then
            Throw New ArgumentNullException(NameOf(client))
        End If

        ' put the chat log of the client next to the tool call log of the
        ' sandbox and let both of them run under the same session id, so the
        ' two can be read side by side afterwards
        Call LinkSessionLog(client)

        ' AddFunction(Of T) looks the tool method up through GetType(T), i.e.
        ' through the static type of its argument, so the generic helper below
        ' is closed over the concrete tool type of every registry entry
        ' instead of being called with an Object.
        ' A background task job runs the same LLM client the session talks
        ' to, so the registry is handed the client while the tools are
        ' registered.
        Call Jobs.AttachClient(client)

        ' the mcp servers declared in the workspace configuration are bridged in
        ' first: an unreachable server only drops its own tools and never blocks
        ' the local tool registration below
        Call RegisterMcpServers(client)

        Dim factory As Reflection.MethodInfo = GetType(Sandbox).GetMethod(
            NameOf(RegisterTool),
            Reflection.BindingFlags.Instance Or Reflection.BindingFlags.NonPublic)

        For Each entry In ToolRegistry
            Try
                Call factory.MakeGenericMethod(entry.Tool).Invoke(Me, New Object() {client, entry.Name})
            Catch ex As Reflection.TargetInvocationException
                ' a broken tool definition must not be reported as a
                ' reflection failure. Rethrowing through
                ' ExceptionDispatchInfo keeps the stack trace of the inner
                ' exception instead of restarting it at this line.
                Call ExceptionDispatchInfo.Capture(ex.InnerException).Throw()
            End Try
        Next

        Return Me
    End Function

    ''' <summary>
    ''' Align the chat log of the client with the tool call log of this
    ''' sandbox: both run under the same session id and each of them notes
    ''' down where the other one lives, so a reviewer who opens one of them
    ''' finds the other.
    ''' 
    ''' The client only logs when it was told to - either by the host handing
    ''' a log folder to its constructor, or by this sandbox enabling it here.
    ''' </summary>
    ''' <param name="client">The client whose chat log should be linked.</param>
    Private Sub LinkSessionLog(client As LLMClient)
        If Not LlmSessionLogging Then
            Return
        End If

        If client.SessionLog Is Nothing Then
            Dim folder As String = IO.Path.Combine(IO.Path.Combine(wsRoot, ArtifactFolder), "llm-sessions")

            Call client.EnableSessionLog(folder, sessionId:=wsLog.SessionId)
        Else
            ' the host enabled it already: only the id can still be aligned,
            ' and only while nothing was written yet
            client.SessionLog.SessionId = wsLog.SessionId
        End If

        If client.SessionLog IsNot Nothing Then
            client.SessionLog.HarnessLogFolder = wsLog.Folder
            wsLog.LlmLogFolder = client.SessionLog.Folder
        End If
    End Sub

    ''' <summary>
    ''' Create one tool of the given type, bound to this sandbox, and register
    ''' it with <paramref name="client"/> under <paramref name="name"/>. This
    ''' is the generic half of the table driven registration.
    ''' </summary>
    ''' <typeparam name="T">The tool class, which must take a <see cref="Sandbox"/> constructor argument.</typeparam>
    ''' <param name="client">The client that should expose the tool.</param>
    ''' <param name="name">The name the model calls the tool by.</param>
    Private Sub RegisterTool(Of T As Class)(client As LLMClient, name As String)
        Dim instance As Object = Activator.CreateInstance(GetType(T), New Object() {Me})
        Dim tool As T = DirectCast(instance, T)

        ' The logging wrapper needs the backend delegate of the tool, and the
        ' ollama helper that builds it (CLRFunction) is internal to that
        ' project - which this one leaves untouched. So the registration runs
        ' in two steps:
        '
        '   1. the original AddFunction(Of T) runs unchanged: ollama builds
        '      the schema of the tool, appends it to client.tools and
        '      registers its own backend under the name of the tool.
        '   2. for every schema that step appended the backend is rebuilt by
        '      ToolBackend, wrapped by wsLog and registered again under the
        '      same name, which replaces the backend of step 1. The schema
        '      the second registration appended duplicates the first one and
        '      is dropped again, so the model still sees every tool exactly
        '      once and with exactly the schema ollama built for it.
        Dim targets As Reflection.MethodInfo() = Logging.ToolBackend.GetTargets(GetType(T), name)
        Dim before As Integer = If(client.tools Is Nothing, 0, client.tools.Count)

        Call client.AddFunction(tool, name)

        If client.tools Is Nothing OrElse Not wsLog.Enabled Then
            Return
        End If

        Dim added As Integer = client.tools.Count - before

        For k As Integer = added - 1 To 0 Step -1
            Dim meta As FunctionModel = client.tools(before + k).function

            If meta Is Nothing Then
                Continue For
            End If

            Dim handle As Reflection.MethodInfo = If(k < targets.Length, targets(k), Nothing)

            If handle Is Nothing Then
                Continue For
            End If

            Dim backend As Func(Of FunctionCall, String) = Logging.ToolBackend.CreateCaller(tool, handle)
            Dim logged As Func(Of FunctionCall, String) = wsLog.Wrap(
                meta, Logging.ToolLogText.SourceSandbox, backend, originalName:=name)

            Call client.AddFunction(meta, logged)

            ' drop the duplicate schema the second registration appended
            Call client.tools.RemoveAt(client.tools.Count - 1)
        Next
    End Sub

    ' ========================================================================
    ' MCP servers
    ' ========================================================================

    ''' <summary>
    ''' The MCP bridge of the last <see cref="RegisterMcpServers"/> call, or
    ''' Nothing when the workspace declares no MCP server.
    ''' </summary>
    Public ReadOnly Property McpBridge As MCP.McpToolBridge
        Get
            Return wsMcp
        End Get
    End Property

    ''' <summary>
    ''' Connect to the MCP servers declared in the workspace configuration
    ''' (<c>&lt;workspace&gt;\.harness\mcp.json</c>) and register their tools,
    ''' resources and prompts on <paramref name="client"/>.
    ''' 
    ''' This never throws: a missing configuration file simply yields no
    ''' server, a malformed entry is reported on the diagnostics stream, and a
    ''' server that cannot start is recorded on
    ''' <see cref="MCP.McpToolBridge.Failures"/> - the rest of the session keeps
    ''' working without it.
    ''' </summary>
    ''' <param name="client">The client that should expose the MCP primitives.</param>
    ''' <param name="configPath">Overrides the default configuration path.</param>
    Public Function RegisterMcpServers(client As LLMClient, Optional configPath As String = Nothing) As Sandbox
        If client Is Nothing Then
            Throw New ArgumentNullException(NameOf(client))
        End If

        Dim path As String = If(String.IsNullOrEmpty(configPath), MCP.McpConfig.DefaultPath(wsRoot), configPath)
        Dim errors() As String = Nothing
        Dim servers As MCP.McpServerDescriptor() = MCP.McpConfig.Load(path, wsRoot, errors)

        If errors.Length > 0 Then
            For Each problem In errors
                Console.Error.WriteLine("[MCP] " & problem)
            Next
        End If

        If servers.Length = 0 Then
            Return Me
        End If

        Dim bridge As New MCP.McpToolBridge(servers)

        ' the bridge registers its own primitives, so it needs the same log
        ' to wrap them with
        bridge.ToolLog = wsLog

        Try
            ' the bridge awaits with ConfigureAwait(False) throughout, so a
            ' synchronous wait here cannot deadlock on a ui thread
            Call bridge.ConnectAllAsync().GetAwaiter().GetResult()
            Call bridge.Attach(client)

            For Each failure In bridge.Failures
                Console.Error.WriteLine("[MCP] " & failure)
            Next
        Catch ex As Exception
            Console.Error.WriteLine("[MCP] unable to initialise the mcp servers: " & ex.Message)
        End Try

        Call CloseMcpServers()
        wsMcp = bridge

        Return Me
    End Function

    ''' <summary>
    ''' Close every MCP server process / session opened by this sandbox. The
    ''' host should call it when the session ends.
    ''' </summary>
    Public Sub CloseMcpServers()
        If wsMcp IsNot Nothing Then
            Try
                wsMcp.Dispose()
            Catch ex As Exception
                Console.Error.WriteLine("[MCP] close error: " & ex.Message)
            End Try
            wsMcp = Nothing
        End If
    End Sub

    ' ========================================================================
    ' Security
    ' ========================================================================

    ''' <summary>
    ''' Gate an operation through the active <see cref="SecurityPolicy"/>.
    ''' Returns Nothing when the operation is permitted, otherwise a
    ''' message that can be handed straight back to the LLM.
    ''' </summary>
    ''' <param name="operation">The operation the calling tool wants to perform.</param>
    ''' <param name="target">The resolved absolute path the operation targets.</param>
    ''' <param name="insideWorkspace">True when <paramref name="target"/> is the workspace root itself or a descendant of it.</param>
    Public Function Authorize(operation As SandboxOperation, target As String, insideWorkspace As Boolean) As String
        Select Case wsSecurity
            Case SecurityPolicy.FullControls
                Return Nothing

            Case SecurityPolicy.[Readonly]
                If Not insideWorkspace Then
                    Call wsLog.LogPermission(operation.ToString(), target, Nothing, Nothing, False,
                                             "the 'Readonly' policy does not permit escaping the workspace", insideWorkspace)
                    Return $"Error: '{target}' is outside the sandbox workspace '{wsRoot}', and the 'Readonly' security policy does not permit escaping it."
                End If

                If operation = SandboxOperation.Read Then
                    Return Nothing
                End If

                Call wsLog.LogPermission(operation.ToString(), target, Nothing, Nothing, False,
                                         "the 'Readonly' policy only permits reading", insideWorkspace)
                Return $"Error: security policy 'Readonly' only permits reading, but '{operation}' was requested on '{target}'."

            Case Else
                ' SecurityPolicy.AskPermission
                If insideWorkspace AndAlso operation = SandboxOperation.Read Then
                    Return Nothing
                End If

                Return AskPermission(operation, target)
        End Select
    End Function

    ''' <summary>
    ''' Raise <see cref="RequestPermission"/> and let the host decide
    ''' whether the pending operation may proceed.
    ''' </summary>
    Private Function AskPermission(operation As SandboxOperation, target As String) As String
        Dim e As New PermissionEventArg With {
            .Operation = operation,
            .Target = target
        }

        RaiseEvent RequestPermission(Me, e)

        Dim granted As Boolean = e.PermissionGranted

        Call wsLog.LogPermission(operation.ToString(), target, Nothing, Nothing, granted,
                                 If(granted, "the host granted the request under the 'AskPermission' policy",
                                    "the host denied the request under the 'AskPermission' policy"),
                                 IsInsideWorkspace(target))

        If granted Then
            Return Nothing
        End If

        Return $"Error: permission denied by the user for '{operation}' on '{target}'."
    End Function

    ''' <summary>
    ''' Raise <see cref="AskUser"/> and let the host show the pending
    ''' questions to the user. Returns False when no host subscribed, so
    ''' that the calling tool can report the missing channel instead of
    ''' waiting for an answer that can never arrive.
    ''' </summary>
    Friend Function RaiseAskUser(e As Tools.QuestionEventArg) As Boolean
        If AskUserEvent Is Nothing Then
            Return False
        End If

        RaiseEvent AskUser(Me, e)

        Return True
    End Function

    ''' <summary>
    ''' The narrowest sandbox mode accepted by the one-shot escalation
    ''' parameter: the single denied operation may be retried, but only
    ''' against a target inside the workspace.
    ''' </summary>
    Public Const PermissionWorkspaceWrite As String = "workspace-write"

    ''' <summary>
    ''' The widest sandbox mode accepted by the one-shot escalation
    ''' parameter: the single denied operation may be retried against any
    ''' target, including one outside the workspace.
    ''' </summary>
    Public Const PermissionDangerFullAccess As String = "danger-full-access"

    ''' <summary>
    ''' Ask the user to allow a single operation that the active policy just
    ''' denied, without changing that policy. The caller passes the mode the
    ''' model asked for; the narrowest sufficient mode is enforced, and the
    ''' <see cref="RequestPermission"/> event carries the requested mode and
    ''' the model's justification so the host can render a meaningful prompt.
    ''' 
    ''' Returns Nothing when the user approved, otherwise a message that can
    ''' be handed straight back to the LLM.
    ''' </summary>
    ''' <param name="operation">The operation that was denied.</param>
    ''' <param name="target">The resolved absolute target of that operation.</param>
    ''' <param name="permission">"workspace-write" or "danger-full-access".</param>
    ''' <param name="justification">The model supplied reason for wanting the wider access.</param>
    Public Function AuthorizeEscalation(operation As SandboxOperation,
                                        target As String,
                                        permission As String,
                                        justification As String) As String

        If String.IsNullOrWhiteSpace(justification) Then
            Call wsLog.LogPermission(operation.ToString(), target, permission, justification, False,
                                     "the escalation carried no justification", IsInsideWorkspace(target))
            Return "Error: 'justification' is required together with 'sandbox_permissions'."
        End If

        Dim mode As String = permission.Trim().ToLowerInvariant()
        Dim inside As Boolean = IsInsideWorkspace(target)

        Select Case mode
            Case PermissionWorkspaceWrite
                If Not inside Then
                    Call wsLog.LogPermission(operation.ToString(), target, mode, justification, False,
                                             "'workspace-write' does not cover a target outside the workspace", inside)
                    Return $"Error: '{PermissionWorkspaceWrite}' only covers targets inside the sandbox workspace, but '{target}' is outside it. Use '{PermissionDangerFullAccess}'."
                End If

            Case PermissionDangerFullAccess
                If inside Then
                    Call wsLog.LogPermission(operation.ToString(), target, mode, justification, False,
                                             "'workspace-write' is the narrowest sufficient mode for a target inside the workspace", inside)
                    Return $"Error: '{target}' is inside the sandbox workspace, so '{PermissionWorkspaceWrite}' is the narrowest sufficient sandbox mode."
                End If

            Case Else
                Call wsLog.LogPermission(operation.ToString(), target, permission, justification, False,
                                         "unknown sandbox_permissions mode", inside)
                Return $"Error: unknown sandbox_permissions '{permission}'. Expected '{PermissionWorkspaceWrite}' or '{PermissionDangerFullAccess}'."
        End Select

        Dim e As New PermissionEventArg With {
            .Operation = operation,
            .Target = target,
            .RequestedPermission = mode,
            .Justification = justification
        }

        RaiseEvent RequestPermission(Me, e)

        Dim granted As Boolean = e.PermissionGranted

        Call wsLog.LogPermission(operation.ToString(), target, mode, justification, granted,
                                 If(granted, $"the host granted the one-shot '{mode}' escalation",
                                    $"the host denied the one-shot '{mode}' escalation"),
                                 inside)

        If granted Then
            Return Nothing
        End If

        Return $"Error: the user denied the one-shot '{mode}' escalation for '{operation}' on '{target}'."
    End Function

    ' ========================================================================
    ' Path resolution
    ' ========================================================================

    ''' <summary>
    ''' Resolve a user supplied path into an absolute path. Relative paths
    ''' are resolved against the workspace root. No sandbox constraint is
    ''' applied here - <see cref="Authorize"/> decides whether the result is
    ''' reachable.
    ''' </summary>
    Public Function ResolvePath(path As String) As String
        If String.IsNullOrWhiteSpace(path) Then
            Return wsRoot
        End If

        Dim sep As Char = IO.Path.DirectorySeparatorChar
        Dim text As String = path.Trim().Replace("/"c, sep)
        Dim fullPath As String

        If text = "." Then
            fullPath = wsRoot
        ElseIf IO.Path.IsPathRooted(text) Then
            fullPath = IO.Path.GetFullPath(text)
        Else
            fullPath = IO.Path.GetFullPath(IO.Path.Combine(wsRoot, text))
        End If

        Dim trimmed As String = fullPath.TrimEnd(sep, IO.Path.AltDirectorySeparatorChar)

        ' keep the trailing separator of a drive root such as "C:\"
        If trimmed.Length > 2 Then
            fullPath = trimmed
        End If

        Return fullPath
    End Function

    ''' <summary>
    ''' True when the resolved path is the workspace root itself or a
    ''' descendant of it. The separator is part of the prefix comparison so
    ''' that a sibling folder sharing a name prefix (e.g. "C:\abc" versus
    ''' "C:\abcd") cannot slip through.
    ''' </summary>
    Public Function IsInsideWorkspace(fullPath As String) As Boolean
        If fullPath.Equals(wsRoot, StringComparison.OrdinalIgnoreCase) Then
            Return True
        End If

        Return fullPath.StartsWith(wsRoot & IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
    End Function

    ''' <summary>
    ''' Resolve <paramref name="path"/> and decide whether the resulting
    ''' target is reachable under the active security policy, handing the
    ''' absolute path back through <paramref name="fullPath"/> when it is.
    ''' Returns Nothing when access is granted, otherwise the denial message.
    ''' 
    ''' <paramref name="fullPath"/> also receives the resolved target when
    ''' access is denied, so that the caller can offer a one-shot escalation
    ''' through <see cref="AuthorizeEscalation"/>.
    ''' </summary>
    Public Function ResolveAndAuthorize(path As String, operation As SandboxOperation, ByRef fullPath As String) As String
        fullPath = Nothing

        Dim resolved As String

        Try
            resolved = ResolvePath(path)
        Catch ex As Exception
            Return $"Error: invalid path '{path}': {ex.Message}"
        End Try

        fullPath = resolved

        Return Authorize(operation, resolved, IsInsideWorkspace(resolved))
    End Function

    ''' <summary>
    ''' Express <paramref name="fullPath"/> the way a tool result reports
    ''' it: relative to the workspace root when it lives inside, otherwise
    ''' the absolute path.
    ''' </summary>
    Public Function WorkspaceRelative(fullPath As String) As String
        If fullPath.StartsWith(wsRoot, StringComparison.OrdinalIgnoreCase) Then
            Return fullPath.Substring(wsRoot.Length).TrimStart(IO.Path.DirectorySeparatorChar, IO.Path.AltDirectorySeparatorChar)
        End If

        Return fullPath
    End Function

    ''' <summary>
    ''' Absolute path of an artifact a tool wants to leave behind, for
    ''' example the complete match list of a truncated grep result. The
    ''' folder is <b>not</b> created here: the caller creates it only after
    ''' the write has been authorized.
    ''' </summary>
    ''' <param name="folder">Tool specific sub folder name, e.g. "grep".</param>
    ''' <param name="name">The file name of the artifact.</param>
    Public Function ArtifactPath(folder As String, name As String) As String
        Return IO.Path.Combine(IO.Path.Combine(wsRoot, ArtifactFolder), folder, name)
    End Function

End Class
