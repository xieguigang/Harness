Imports System.IO
Imports Ollama

''' <summary>
''' The kind of filesystem operation that a sandbox tool is trying to
''' perform. Every tool implementation must be checked against the
''' active <see cref="SecurityPolicy"/> through <see cref="Sandbox.Authorize"/>
''' before it touches the disk.
''' </summary>
Public Enum SandboxOperation

    ''' <summary>
    ''' Read a file or list a directory. Always allowed inside the
    ''' workspace; a target outside the workspace follows the active
    ''' <see cref="SecurityPolicy"/>.
    ''' </summary>
    Read
    ''' <summary>Create, overwrite or delete a file inside the workspace.</summary>
    Write
    ''' <summary>Launch a child process, e.g. to run a script or a build.</summary>
    Execute

End Enum

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
    ''' Raised when a write or execute operation needs the user's consent.
    ''' Set <see cref="PermissionEventArg.PermissionGranted"/> to True inside
    ''' the handler to let the operation proceed.
    ''' </summary>
    Public Event RequestPermission(sender As Object, e As PermissionEventArg)

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

    ' ========================================================================
    ' Tool registration
    ' ========================================================================

    ''' <summary>
    ''' Register every tool backed by this sandbox into an Ollama client, so
    ''' that the model can call them by their exported names (currently
    ''' "read", "grep", "glob", "edit" and "write"). Each tool instance is
    ''' bound to this sandbox and therefore shares its workspace and security
    ''' policy.
    ''' 
    ''' This method is the registry: a new tool class only needs one more
    ''' entry here to become visible to the model.
    ''' </summary>
    ''' <param name="client">The client that should expose the tools.</param>
    Public Function RegisterTo(client As LLMClient) As Sandbox
        If client Is Nothing Then
            Throw New ArgumentNullException(NameOf(client))
        End If

        Dim reader As New Tools.ReadTool(Me)
        Dim searcher As New Tools.GrepTool(Me)
        Dim finder As New Tools.GlobTool(Me)
        Dim editor As New Tools.EditTool(Me)
        Dim writer As New Tools.WriteTool(Me)

        Call client.AddFunction(reader, "read")
        Call client.AddFunction(searcher, "grep")
        Call client.AddFunction(finder, "glob")
        Call client.AddFunction(editor, "edit")
        Call client.AddFunction(writer, "write")

        Return Me
    End Function

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
                    Return $"Error: '{target}' is outside the sandbox workspace '{wsRoot}', and the 'Readonly' security policy does not permit escaping it."
                End If

                If operation = SandboxOperation.Read Then
                    Return Nothing
                End If

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

        If e.PermissionGranted Then
            Return Nothing
        End If

        Return $"Error: permission denied by the user for '{operation}' on '{target}'."
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
            Return "Error: 'justification' is required together with 'sandbox_permissions'."
        End If

        Dim mode As String = permission.Trim().ToLowerInvariant()
        Dim inside As Boolean = IsInsideWorkspace(target)

        Select Case mode
            Case PermissionWorkspaceWrite
                If Not inside Then
                    Return $"Error: '{PermissionWorkspaceWrite}' only covers targets inside the sandbox workspace, but '{target}' is outside it. Use '{PermissionDangerFullAccess}'."
                End If

            Case PermissionDangerFullAccess
                If inside Then
                    Return $"Error: '{target}' is inside the sandbox workspace, so '{PermissionWorkspaceWrite}' is the narrowest sufficient sandbox mode."
                End If

            Case Else
                Return $"Error: unknown sandbox_permissions '{permission}'. Expected '{PermissionWorkspaceWrite}' or '{PermissionDangerFullAccess}'."
        End Select

        Dim e As New PermissionEventArg With {
            .Operation = operation,
            .Target = target,
            .RequestedPermission = mode,
            .Justification = justification
        }

        RaiseEvent RequestPermission(Me, e)

        If e.PermissionGranted Then
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
