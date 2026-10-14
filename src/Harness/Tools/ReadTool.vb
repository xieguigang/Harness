Imports System.ComponentModel
Imports System.IO
Imports System.Text
Imports Microsoft.VisualBasic.CommandLine.Reflection

Namespace Tools

    ''' <summary>
    ''' Tool "read": read a UTF-8 text file and return its line-numbered
    ''' content.
    ''' 
    ''' The tool holds a reference to the <see cref="Sandbox"/> that owns it,
    ''' so path resolution and permission checks always use the workspace and
    ''' the security policy of that sandbox.
    ''' </summary>
    Public Class ReadTool

        Private ReadOnly _sandbox As Sandbox

        ''' <param name="sandbox">The sandbox that resolves the paths and enforces the security policy.</param>
        Public Sub New(sandbox As Sandbox)
            If sandbox Is Nothing Then
                Throw New ArgumentNullException(NameOf(sandbox))
            End If

            _sandbox = sandbox
        End Sub

        ''' <summary>
        ''' Read a UTF-8 text file and return its line-numbered content.
        ''' </summary>
        ''' <param name="file_path">Path to read, resolved by the filesystem backend.</param>
        ''' <param name="offset">1-based first line to return. Defaults to 1.</param>
        ''' <param name="limit">Maximum number of lines to return. Defaults to 2000.</param>
        ''' <param name="sandbox_permissions">
        ''' The narrowest wider sandbox mode for a one-shot retry of the exact
        ''' operation the sandbox just denied; the retry asks the user for
        ''' approval. Either "workspace-write" (target inside the workspace) or
        ''' "danger-full-access" (target outside it). Leave empty unless the
        ''' sandbox denied the operation.
        ''' </param>
        ''' <param name="justification">
        ''' Required with sandbox_permissions: one sentence for the user
        ''' explaining why this exact read needs the wider access. Use the
        ''' language of the user current request.
        ''' </param>
        <Description("Read a UTF-8 text file and return line-numbered content.")>
        <ExportAPI("read")>
        Public Function read(
            <Argument("file_path", Description:="Path to read, resolved by the filesystem backend.")> file_path As String,
            <Argument("offset", Description:="1-based first line to return. Defaults to 1.")> Optional offset As Double = 1,
            <Argument("limit", Description:="Maximum number of lines to return. Defaults to 2000.")> Optional limit As Double = 2000,
            <Argument("sandbox_permissions", Description:="The narrowest wider sandbox mode for a one-shot retry of the exact operation the sandbox just denied; the retry asks the user for approval. One of ""workspace-write"" (target inside the workspace) or ""danger-full-access"" (target outside it). Leave empty unless the sandbox denied the operation.")> Optional sandbox_permissions As String = Nothing,
            <Argument("justification", Description:="Required with sandbox_permissions: one sentence for the user explaining why this exact read needs the wider access. Use the language of the user's current request.")> Optional justification As String = Nothing
        ) As String
            Dim fullPath As String = Nothing
            Dim denied As String = _sandbox.ResolveAndAuthorize(file_path, SandboxOperation.Read, fullPath)

            If denied IsNot Nothing Then
                ' No escalation asked for (or no path to escalate: an
                ' unresolvable path leaves fullPath empty): report the denial
                ' as it is.
                If String.IsNullOrWhiteSpace(sandbox_permissions) OrElse fullPath Is Nothing Then
                    Return denied
                End If

                ' One-shot retry: ask the user for the narrowest mode that
                ' would cover this exact operation. The policy itself is not
                ' changed.
                Dim escalated As String = _sandbox.AuthorizeEscalation(SandboxOperation.Read, fullPath, sandbox_permissions, justification)

                If escalated IsNot Nothing Then
                    Return escalated
                End If
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

End Namespace
