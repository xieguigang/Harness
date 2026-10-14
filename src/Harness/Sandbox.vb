Imports System.ComponentModel
Imports System.IO
Imports System.Text
Imports Microsoft.VisualBasic.CommandLine.Reflection

''' <summary>
''' The kind of filesystem operation that a sandbox tool is trying to
''' perform. Every tool implementation must be checked against the
''' active <see cref="SecurityPolicy"/> through <c>Authorize</c> before
''' it touches the disk.
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
''' Sandbox - a <see cref="SecurityPolicy"/> gated filesystem backend.
''' 
''' The class exposes LLM function-call tools (see
''' <c>Ollama.LLMClient.AddFunction(Of T)(obj, fun)</c>) whose declarations
''' are written with the <see cref="DescriptionAttribute"/> / 
''' <see cref="ArgumentAttribute"/> annotation style.
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

    ReadOnly wsRoot As String
    ReadOnly wsSecurity As SecurityPolicy

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
    ' Tool: read
    ' ========================================================================

    ''' <summary>
    ''' Read a UTF-8 text file and return its line-numbered content.
    ''' </summary>
    ''' <param name="file_path">Path to read, resolved by the filesystem backend.</param>
    ''' <param name="offset">1-based first line to return. Defaults to 1.</param>
    ''' <param name="limit">Maximum number of lines to return. Defaults to 2000.</param>
    <Description("Read a UTF-8 text file and return line-numbered content.")>
    <ExportAPI("read")>
    Public Function read(
        <Argument("file_path", Description:="Path to read, resolved by the filesystem backend.")> file_path As String,
        <Argument("offset", Description:="1-based first line to return. Defaults to 1.")> Optional offset As Double = 1,
        <Argument("limit", Description:="Maximum number of lines to return. Defaults to 2000.")> Optional limit As Double = 2000
    ) As String
        Dim fullPath As String = Nothing
        Dim denied As String = ResolveAndAuthorize(file_path, SandboxOperation.Read, fullPath)

        If denied IsNot Nothing Then
            Return denied
        End If

        If Not File.Exists(fullPath) Then
            Return $"Error: File not found: {fullPath}"
        End If

        Try
            Dim lines() As String = File.ReadAllLines(fullPath, Encoding.UTF8)
            Return FormatLineNumbered(fullPath, lines, offset, limit)
        Catch ex As Exception
            Return $"Error reading file '{fullPath}': {ex.Message}"
        End Try
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
    Private Function Authorize(operation As SandboxOperation, target As String, insideWorkspace As Boolean) As String
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
    ''' Resolve a user supplied path into an absolute path. Relative paths
    ''' are resolved against the workspace root. No sandbox constraint is
    ''' applied here - <see cref="Authorize"/> decides whether the result is
    ''' reachable.
    ''' </summary>
    Private Function ResolvePath(path As String) As String
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
    Private Function IsInsideWorkspace(fullPath As String) As Boolean
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
    ''' </summary>
    Private Function ResolveAndAuthorize(path As String, operation As SandboxOperation, ByRef fullPath As String) As String
        fullPath = Nothing

        Dim resolved As String

        Try
            resolved = ResolvePath(path)
        Catch ex As Exception
            Return $"Error: invalid path '{path}': {ex.Message}"
        End Try

        Dim denied As String = Authorize(operation, resolved, IsInsideWorkspace(resolved))

        If denied IsNot Nothing Then
            Return denied
        End If

        fullPath = resolved
        Return Nothing
    End Function

    ' ========================================================================
    ' Formatting
    ' ========================================================================

    Private Function FormatLineNumbered(fullPath As String, lines As String(), offset As Double, limit As Double) As String
        Dim total As Integer = lines.Length
        Dim first As Long = ToLineNumber(offset, 1L)
        Dim count As Long = ToLineNumber(limit, 2000L)

        Dim startIndex As Long = Math.Min(first - 1L, CLng(total))
        Dim stopIndex As Long = Math.Min(startIndex + count, CLng(total))

        Dim sb As New StringBuilder()
        sb.AppendLine($"<path>{fullPath}</path>")
        sb.AppendLine("<type>file</type>")
        sb.AppendLine("<content>")

        For i As Long = startIndex To stopIndex - 1L
            sb.AppendLine($"{i + 1L}: {lines(CInt(i))}")
        Next

        sb.AppendLine()

        If stopIndex >= CLng(total) Then
            sb.AppendLine($"(End of file - total {total} lines)")
        Else
            sb.AppendLine($"(Showing lines {startIndex + 1L}-{stopIndex} of {total}. Use offset={stopIndex + 1L} to continue.)")
        End If

        sb.Append("</content>")

        Return sb.ToString()
    End Function

    ''' <summary>
    ''' Coerce a JSON number into a positive line count, clamping NaN,
    ''' infinity and out-of-range values so that a malformed tool call
    ''' can never throw.
    ''' </summary>
    Private Shared Function ToLineNumber(value As Double, fallback As Long) As Long
        If Double.IsNaN(value) OrElse Double.IsInfinity(value) Then
            Return fallback
        End If

        If value >= Long.MaxValue Then
            Return Long.MaxValue
        End If

        Dim n As Long = CLng(Math.Floor(value))

        If n < 1L Then
            n = 1L
        End If

        Return n
    End Function

End Class
