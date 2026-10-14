Imports System.ComponentModel
Imports System.IO
Imports Microsoft.VisualBasic.CommandLine.Reflection

Namespace Tools

    ''' <summary>
    ''' Tool "move": move or rename a file, or a whole directory.
    ''' 
    ''' The tool holds a reference to the <see cref="Sandbox"/> that owns it
    ''' and resolves both paths through it, so the source and the destination
    ''' are each checked against the workspace and the security policy. An
    ''' existing destination is always an error: this tool never overwrites
    ''' something the model did not ask about.
    ''' </summary>
    Public Class MoveTool

        Private ReadOnly _sandbox As Sandbox

        ''' <param name="sandbox">The sandbox that resolves the paths and enforces the security policy.</param>
        Public Sub New(sandbox As Sandbox)
            If sandbox Is Nothing Then
                Throw New ArgumentNullException(NameOf(sandbox))
            End If

            _sandbox = sandbox
        End Sub

        ''' <summary>
        ''' Move or rename a file or a directory.
        ''' </summary>
        ''' <param name="source">Existing file or directory to move, resolved by the filesystem backend.</param>
        ''' <param name="destination">New path, resolved by the filesystem backend. It must not exist yet.</param>
        ''' <param name="sandbox_permissions">The narrowest wider sandbox mode for a one-shot retry of the exact operation the sandbox just denied.</param>
        ''' <param name="justification">Required with sandbox_permissions: one sentence for the user explaining why this exact file operation needs the wider access.</param>
        <Description("Move or rename a file or a directory. Both the source and the destination are resolved by the filesystem backend, and an existing destination is refused instead of being overwritten. A directory crossing volumes is copied and then deleted, because the platform cannot move it in one step.")>
        <ExportAPI("move")>
        Public Function move(
            <Argument("source", Description:="Existing file or directory to move, resolved by the filesystem backend.")> source As String,
            <Argument("destination", Description:="New path for the file or directory, resolved by the filesystem backend. It must not exist yet.")> destination As String,
            <Argument("sandbox_permissions", Description:="The narrowest wider sandbox mode for a one-shot retry of the exact operation the sandbox just denied; the retry asks the user for approval. One of ""workspace-write"" (target inside the workspace) or ""danger-full-access"" (target outside it). Leave empty unless the sandbox denied the operation.")> Optional sandbox_permissions As String = Nothing,
            <Argument("justification", Description:="Required with sandbox_permissions: one sentence for the user explaining why this exact file operation needs the wider access. Use the language of the user's current request.")> Optional justification As String = Nothing
        ) As String
            Dim sourcePath As String = Nothing
            Dim denied As String = _sandbox.ResolveAndAuthorize(source, SandboxOperation.Write, sourcePath)

            If denied IsNot Nothing Then
                If String.IsNullOrWhiteSpace(sandbox_permissions) OrElse sourcePath Is Nothing Then
                    Return denied
                End If

                Dim escalated As String = _sandbox.AuthorizeEscalation(SandboxOperation.Write, sourcePath, sandbox_permissions, justification)

                If escalated IsNot Nothing Then
                    Return escalated
                End If
            End If

            Dim destinationPath As String = Nothing
            Dim refused As String = _sandbox.ResolveAndAuthorize(destination, SandboxOperation.Write, destinationPath)

            If refused IsNot Nothing Then
                If String.IsNullOrWhiteSpace(sandbox_permissions) OrElse destinationPath Is Nothing Then
                    Return refused
                End If

                Dim escalated As String = _sandbox.AuthorizeEscalation(SandboxOperation.Write, destinationPath, sandbox_permissions, justification)

                If escalated IsNot Nothing Then
                    Return escalated
                End If
            End If

            If sourcePath.Equals(destinationPath, StringComparison.OrdinalIgnoreCase) Then
                Return $"Error: the source and the destination are the same path: {sourcePath}"
            End If

            If File.Exists(destinationPath) OrElse Directory.Exists(destinationPath) Then
                Return $"Error: the destination '{destinationPath}' already exists; this tool does not overwrite an existing entry."
            End If

            Dim failure As String = MoveEntry(sourcePath, destinationPath)

            If failure IsNot Nothing Then
                Return failure
            End If

            Return $"Moved {sourcePath} to {destinationPath}."
        End Function

        ''' <summary>
        ''' Move a file, or a whole directory, without touching the security
        ''' policy: the callers resolve and authorize both paths themselves.
        ''' Returns Nothing when the move succeeded, otherwise a plain text
        ''' error message.
        ''' </summary>
        ''' <param name="source">The absolute path to move.</param>
        ''' <param name="destination">The absolute target path, which must not exist.</param>
        Friend Shared Function MoveEntry(source As String, destination As String) As String
            Try
                Dim folder As String = IO.Path.GetDirectoryName(destination)

                If Not String.IsNullOrEmpty(folder) AndAlso Not Directory.Exists(folder) Then
                    Call Directory.CreateDirectory(folder)
                End If

                If File.Exists(source) Then
                    Call File.Move(source, destination)

                    Return Nothing
                End If

                If Not Directory.Exists(source) Then
                    Return $"Error: no such file or directory: {source}"
                End If

                Try
                    Call Directory.Move(source, destination)
                Catch ex As IOException
                    ' The platform cannot move a directory to another volume in
                    ' one step, so copy the whole tree and then remove the
                    ' source.
                    Return CopyThenDelete(source, destination, ex)
                End Try

                Return Nothing
            Catch ex As Exception
                Return $"Error: cannot move '{source}' to '{destination}': {ex.Message}"
            End Try
        End Function

        ''' <summary>
        ''' Fallback for a directory move that the platform refused: copy the
        ''' tree first and delete the source only after the copy succeeded.
        ''' </summary>
        Private Shared Function CopyThenDelete(source As String, destination As String, cause As Exception) As String
            Try
                Call CopyTree(source, destination)
                Call Directory.Delete(source, recursive:=True)
            Catch ex As Exception
                Return $"Error: cannot move '{source}' to '{destination}': {cause.Message} The copy fallback also failed: {ex.Message}"
            End Try

            Return Nothing
        End Function

        Private Shared Sub CopyTree(source As String, destination As String)
            Call Directory.CreateDirectory(destination)

            For Each file As String In Directory.GetFiles(source)
                Call IO.File.Copy(file, IO.Path.Combine(destination, IO.Path.GetFileName(file)), overwrite:=False)
            Next

            For Each folder As String In Directory.GetDirectories(source)
                Call CopyTree(folder, IO.Path.Combine(destination, IO.Path.GetFileName(folder)))
            Next
        End Sub

    End Class

End Namespace
