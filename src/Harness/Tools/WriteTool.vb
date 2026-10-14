Imports System.ComponentModel
Imports System.IO
Imports System.Text
Imports Microsoft.VisualBasic.CommandLine.Reflection

Namespace Tools

    ''' <summary>
    ''' Tool "write": create or fully replace a UTF-8 text file.
    ''' 
    ''' The tool holds a reference to the <see cref="Sandbox"/> that owns it,
    ''' so the path is resolved and the write permission is checked against
    ''' the workspace and the security policy of that sandbox.
    ''' 
    ''' When the sandbox denies the write, the model may issue one retry that
    ''' carries a <c>sandbox_permissions</c> mode and a justification. That
    ''' retry does not change the policy: it raises the sandbox permission
    ''' event and, if the user approves, performs this single operation.
    ''' </summary>
    Public Class WriteTool

        Private ReadOnly _sandbox As Sandbox

        ''' <param name="sandbox">The sandbox that resolves the paths and enforces the security policy.</param>
        Public Sub New(sandbox As Sandbox)
            If sandbox Is Nothing Then
                Throw New ArgumentNullException(NameOf(sandbox))
            End If

            _sandbox = sandbox
        End Sub

        ''' <summary>
        ''' Create or fully replace a UTF-8 text file.
        ''' </summary>
        ''' <param name="file_path">Path to write, resolved by the filesystem backend.</param>
        ''' <param name="content">Full UTF-8 text content to write.</param>
        ''' <param name="sandbox_permissions">
        ''' The narrowest wider sandbox mode for a one-shot retry of the exact
        ''' operation the sandbox just denied; the retry asks the user for
        ''' approval. Either "workspace-write" (target inside the workspace) or
        ''' "danger-full-access" (target outside it). Leave empty unless the
        ''' sandbox denied the operation.
        ''' </param>
        ''' <param name="justification">
        ''' Required with sandbox_permissions: one sentence for the user
        ''' explaining why this exact file operation needs the wider access.
        ''' Use the language of the user current request.
        ''' </param>
        <Description("Create or fully replace a UTF-8 text file.")>
        <ExportAPI("write")>
        Public Function write(
            <Argument("file_path", Description:="Path to write, resolved by the filesystem backend.")> file_path As String,
            <Argument("content", Description:="Full UTF-8 text content to write.")> content As String,
            <Argument("sandbox_permissions", Description:="The narrowest wider sandbox mode for a one-shot retry of the exact operation the sandbox just denied; the retry asks the user for approval. One of ""workspace-write"" (target inside the workspace) or ""danger-full-access"" (target outside it). Leave empty unless the sandbox denied the operation.")> Optional sandbox_permissions As String = Nothing,
            <Argument("justification", Description:="Required with sandbox_permissions: one sentence for the user explaining why this exact file operation needs the wider access. Use the language of the user's current request.")> Optional justification As String = Nothing
        ) As String
            Dim fullPath As String = Nothing
            Dim denied As String = _sandbox.ResolveAndAuthorize(file_path, SandboxOperation.Write, fullPath)

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
                Dim escalated As String = _sandbox.AuthorizeEscalation(SandboxOperation.Write, fullPath, sandbox_permissions, justification)

                If escalated IsNot Nothing Then
                    Return escalated
                End If
            End If

            Dim existed As Boolean = File.Exists(fullPath)

            Try
                Dim folder As String = IO.Path.GetDirectoryName(fullPath)

                If Not String.IsNullOrEmpty(folder) AndAlso Not Directory.Exists(folder) Then
                    Call Directory.CreateDirectory(folder)
                End If

                ' Keep the encoding shape of an existing file; a brand new
                ' file is written without a byte order mark.
                Dim isBom As Boolean = existed AndAlso TextFileUtils.HasUtf8Bom(fullPath)

                Call TextFileUtils.WriteAllText(fullPath, content, isBom)
            Catch ex As Exception
                Return $"Error writing file '{fullPath}': {ex.Message}"
            End Try

            Dim sb As New StringBuilder()
            sb.AppendLine($"<path>{fullPath}</path>")
            sb.AppendLine("<type>file</type>")
            sb.AppendLine("<content>")
            sb.AppendLine(If(existed, "Replaced file", "Created file"))
            sb.Append("</content>")

            Return sb.ToString()
        End Function

    End Class

End Namespace
