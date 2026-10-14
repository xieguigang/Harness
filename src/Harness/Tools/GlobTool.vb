Imports System.ComponentModel
Imports System.IO
Imports System.Text
Imports Microsoft.VisualBasic.CommandLine.Reflection

Namespace Tools

    ''' <summary>
    ''' Tool "glob": find files, not directories, whose paths match a glob
    ''' pattern. Hidden and ignored files are included, i.e. unlike grep this
    ''' tool does not skip dot folders or bin/obj.
    ''' 
    ''' The tool holds a reference to the <see cref="Sandbox"/> that owns it,
    ''' so path resolution and permission checks always use the workspace and
    ''' the security policy of that sandbox. Reported paths are workspace
    ''' relative, like the other tools.
    ''' </summary>
    Public Class GlobTool

        ''' <summary>Maximum number of paths a single glob result reports.</summary>
        Private Const MaxPaths As Integer = 100

        Private ReadOnly _sandbox As Sandbox

        ''' <param name="sandbox">The sandbox that resolves the paths and enforces the security policy.</param>
        Public Sub New(sandbox As Sandbox)
            If sandbox Is Nothing Then
                Throw New ArgumentNullException(NameOf(sandbox))
            End If

            _sandbox = sandbox
        End Sub

        ''' <summary>
        ''' Find files whose paths match a glob pattern.
        ''' </summary>
        ''' <param name="pattern">Glob pattern to match file paths against (e.g. "**/*.ts", "src/**/*.test.js"). A pattern with no "/" matches the basename at any depth, so "*" and "*.ts" both search the whole tree; include a separator to anchor the depth.</param>
        ''' <param name="path">Directory to search in. Defaults to the session workspace; a relative path resolves against it.</param>
        <Description("Find files, not directories, whose paths match a glob pattern, including hidden and ignored files. Returns up to 100 paths in modification-time order; a larger result keeps the first paths and reports where the complete list was saved.")>
        <ExportAPI("glob")>
        Public Function glob(
            <Argument("pattern", Description:="Glob pattern to match file paths against (e.g. ""**/*.ts"", ""src/**/*.test.js""). A pattern with no ""/"" matches the basename at any depth, so ""*"" and ""*.ts"" both search the whole tree; include a separator to anchor the depth.")> pattern As String,
            <Argument("path", Description:="Directory to search in. Defaults to the session workspace; a relative path resolves against it.")> Optional path As String = Nothing
        ) As String
            If String.IsNullOrWhiteSpace(pattern) Then
                Return "Error: pattern is empty."
            End If

            Dim matcher As GlobPattern = GlobPattern.Create(pattern)

            If matcher Is Nothing Then
                Return "Error: pattern is empty."
            End If

            Dim root As String = Nothing
            Dim denied As String = _sandbox.ResolveAndAuthorize(path, SandboxOperation.Read, root)

            If denied IsNot Nothing Then
                Return denied
            End If

            If Not File.Exists(root) AndAlso Not Directory.Exists(root) Then
                Return $"Error: no such file or directory: {root}"
            End If

            Dim matches As New List(Of String)()

            If File.Exists(root) Then
                If MatchesFile(matcher, root, root) Then
                    matches.Add(root)
                End If
            Else
                Call CollectFiles(root, matcher, matches)
            End If

            If matches.Count = 0 Then
                Return "No files matched."
            End If

            Call SortByModificationTime(matches)

            Dim sb As New StringBuilder()

            For i As Integer = 0 To Math.Min(MaxPaths, matches.Count) - 1
                sb.AppendLine(_sandbox.WorkspaceRelative(matches(i)))
            Next

            If matches.Count > MaxPaths Then
                sb.AppendLine()
                sb.AppendLine(BuildOverflowNote(matches))
            End If

            Return sb.ToString()
        End Function

        ''' <summary>
        ''' Test one candidate file. The pattern is anchored against the path
        ''' relative to the search root; when the search root sits below the
        ''' workspace the workspace relative path is tried as well, so that
        ''' both "**/*.vb" and "src/**/*.vb" work with path="src".
        ''' </summary>
        Private Function MatchesFile(matcher As GlobPattern, root As String, file As String) As Boolean
            Dim anchored As String = RelativeTo(root, file)

            If matcher.IsMatch(anchored) Then
                Return True
            End If

            Dim relative As String = _sandbox.WorkspaceRelative(file)

            If relative = file OrElse relative = anchored Then
                Return False
            End If

            Return matcher.IsMatch(relative)
        End Function

        ''' <summary>
        ''' Walk the whole tree below <paramref name="root"/>, collecting
        ''' every file the matcher accepts. Hidden folders and build output
        ''' are deliberately not skipped: this tool is documented to include
        ''' hidden and ignored files.
        ''' </summary>
        Private Sub CollectFiles(root As String, matcher As GlobPattern, matches As List(Of String))
            Dim pending As New Stack(Of String)()
            pending.Push(root)

            While pending.Count > 0
                Dim dir As String = pending.Pop()
                Dim subDirs() As String
                Dim entries() As String

                Try
                    subDirs = Directory.GetDirectories(dir)
                Catch
                    subDirs = New String() {}
                End Try

                For Each subDir As String In subDirs
                    pending.Push(subDir)
                Next

                Try
                    entries = Directory.GetFiles(dir)
                Catch
                    entries = New String() {}
                End Try

                For Each file As String In entries
                    If MatchesFile(matcher, root, file) Then
                        matches.Add(file)
                    End If
                Next
            End While
        End Sub

        ''' <summary>
        ''' <paramref name="file"/> expressed relative to
        ''' <paramref name="root"/>, using the platform separator. A single
        ''' file target is expressed as its own file name.
        ''' </summary>
        Private Shared Function RelativeTo(root As String, file As String) As String
            If file.Equals(root, StringComparison.OrdinalIgnoreCase) Then
                Return IO.Path.GetFileName(file)
            End If

            Dim prefix As String = root & IO.Path.DirectorySeparatorChar

            If file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) Then
                Return file.Substring(prefix.Length)
            End If

            Return file
        End Function

        ''' <summary>
        ''' Order the matches by modification time, oldest first, falling back
        ''' to the path so that equal timestamps stay deterministic.
        ''' </summary>
        Private Shared Sub SortByModificationTime(files As List(Of String))
            Dim stamped As New List(Of KeyValuePair(Of DateTime, String))(files.Count)

            For Each target As String In files
                Dim written As DateTime

                Try
                    written = File.GetLastWriteTime(target)
                Catch
                    written = DateTime.MinValue
                End Try

                stamped.Add(New KeyValuePair(Of DateTime, String)(written, target))
            Next

            stamped.Sort(Function(a, b)
                             Dim order As Integer = a.Key.CompareTo(b.Key)

                             If order <> 0 Then
                                 Return order
                             End If

                             Return StringComparer.OrdinalIgnoreCase.Compare(a.Value, b.Value)
                         End Function)

            files.Clear()

            For Each item As KeyValuePair(Of DateTime, String) In stamped
                files.Add(item.Value)
            Next
        End Sub

        ''' <summary>
        ''' Save the complete list inside the workspace and report where it
        ''' went. The dump is a write operation, so it is gated by the active
        ''' security policy just like any other write.
        ''' </summary>
        Private Function BuildOverflowNote(matches As List(Of String)) As String
            Dim target As String = _sandbox.ArtifactPath("glob", $"glob-{DateTime.Now.ToString("yyyyMMdd-HHmmss")}.txt")
            Dim denied As String = _sandbox.Authorize(SandboxOperation.Write, target, insideWorkspace:=True)

            If denied IsNot Nothing Then
                Return $"(Showing the first {MaxPaths} of {matches.Count} paths. The complete file list could not be saved under the current security policy.)"
            End If

            Try
                Dim sb As New StringBuilder()

                For Each file As String In matches
                    sb.AppendLine(_sandbox.WorkspaceRelative(file))
                Next

                Call Directory.CreateDirectory(IO.Path.GetDirectoryName(target))
                Call File.WriteAllText(target, sb.ToString(), Encoding.UTF8)
            Catch ex As Exception
                Return $"(Showing the first {MaxPaths} of {matches.Count} paths. The complete file list could not be saved: {ex.Message})"
            End Try

            Return $"(Showing the first {MaxPaths} of {matches.Count} paths. Complete file list saved to: {_sandbox.WorkspaceRelative(target)})"
        End Function

    End Class

End Namespace
