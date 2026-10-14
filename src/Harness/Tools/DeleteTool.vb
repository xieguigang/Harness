Imports System.ComponentModel
Imports System.IO
Imports Microsoft.VisualBasic.CommandLine.Reflection

Namespace Tools

    ''' <summary>
    ''' Tool "delete": remove a file, or a directory together with its whole
    ''' content.
    ''' 
    ''' The tool holds a reference to the <see cref="Sandbox"/> that owns it,
    ''' so the path is resolved and the permission is checked against the
    ''' workspace and the security policy of that sandbox. The workspace root
    ''' itself can never be deleted, and a directory has to be unlocked
    ''' explicitly through <c>recursive</c>, so that a wrong guess cannot wipe
    ''' a whole folder.
    ''' </summary>
    Public Class DeleteTool

        Private ReadOnly _sandbox As Sandbox

        ''' <param name="sandbox">The sandbox that resolves the paths and enforces the security policy.</param>
        Public Sub New(sandbox As Sandbox)
            If sandbox Is Nothing Then
                Throw New ArgumentNullException(NameOf(sandbox))
            End If

            _sandbox = sandbox
        End Sub

        ''' <summary>
        ''' Delete a file, or a directory together with its content.
        ''' </summary>
        ''' <param name="path">File or directory to delete, resolved by the filesystem backend.</param>
        ''' <param name="recursive">Required to delete a directory that is not empty.</param>
        ''' <param name="sandbox_permissions">The narrowest wider sandbox mode for a one-shot retry of the exact operation the sandbox just denied.</param>
        ''' <param name="justification">Required with sandbox_permissions: one sentence for the user explaining why this exact file operation needs the wider access.</param>
        <Description("Delete a file, or a directory together with everything it holds. Deleting a directory that is not empty requires recursive=true. The sandbox workspace root itself can never be deleted.")>
        <ExportAPI("delete")>
        Public Function delete(
            <Argument("path", Description:="File or directory to delete, resolved by the filesystem backend.")> path As String,
            <Argument("recursive", Description:="Set to true to delete a directory together with its whole content. Defaults to false, which refuses a directory that is not empty.")> Optional recursive As Boolean = False,
            <Argument("sandbox_permissions", Description:="The narrowest wider sandbox mode for a one-shot retry of the exact operation the sandbox just denied; the retry asks the user for approval. One of ""workspace-write"" (target inside the workspace) or ""danger-full-access"" (target outside it). Leave empty unless the sandbox denied the operation.")> Optional sandbox_permissions As String = Nothing,
            <Argument("justification", Description:="Required with sandbox_permissions: one sentence for the user explaining why this exact file operation needs the wider access. Use the language of the user's current request.")> Optional justification As String = Nothing
        ) As String
            Dim fullPath As String = Nothing
            Dim denied As String = _sandbox.ResolveAndAuthorize(path, SandboxOperation.Write, fullPath)

            If denied IsNot Nothing Then
                If String.IsNullOrWhiteSpace(sandbox_permissions) OrElse fullPath Is Nothing Then
                    Return denied
                End If

                Dim escalated As String = _sandbox.AuthorizeEscalation(SandboxOperation.Write, fullPath, sandbox_permissions, justification)

                If escalated IsNot Nothing Then
                    Return escalated
                End If
            End If

            If fullPath.Equals(_sandbox.Workspace, StringComparison.OrdinalIgnoreCase) Then
                Return $"Error: '{fullPath}' is the sandbox workspace itself, it must not be deleted."
            End If

            If File.Exists(fullPath) Then
                Try
                    File.Delete(fullPath)
                Catch ex As Exception
                    Return $"Error deleting file '{fullPath}': {ex.Message}"
                End Try

                Return $"Deleted file {fullPath}."
            End If

            If Not Directory.Exists(fullPath) Then
                Return $"Error: no such file or directory: {fullPath}"
            End If

            Dim entries() As String

            Try
                entries = Directory.GetFileSystemEntries(fullPath)
            Catch ex As Exception
                Return $"Error reading directory '{fullPath}': {ex.Message}"
            End Try

            If entries.Length > 0 AndAlso Not recursive Then
                Return $"Error: '{fullPath}' is a directory holding {entries.Length} entries and recursive is false. " &
                       "Set recursive to true to delete the directory together with its content."
            End If

            Try
                Directory.Delete(fullPath, recursive:=recursive)
            Catch ex As Exception
                Return $"Error deleting directory '{fullPath}': {ex.Message}"
            End Try

            Return $"Deleted directory {fullPath} and all of its content."
        End Function

    End Class

End Namespace
