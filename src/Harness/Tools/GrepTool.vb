Imports System.ComponentModel
Imports System.IO
Imports System.Text
Imports System.Text.RegularExpressions
Imports Microsoft.VisualBasic.CommandLine.Reflection

Namespace Tools

    ''' <summary>
    ''' Tool "grep": search file contents with a ripgrep style regular
    ''' expression and report the matching lines grouped by file.
    ''' 
    ''' The tool holds a reference to the <see cref="Sandbox"/> that owns it,
    ''' so path resolution, the workspace boundary and the permission checks
    ''' (including the write of an oversized result dump) always use the
    ''' workspace and the security policy of that sandbox.
    ''' </summary>
    Public Class GrepTool

        ''' <summary>Maximum number of matches a single grep result reports.</summary>
        Private Const MaxMatches As Integer = 250

        ''' <summary>Files larger than this are skipped instead of loaded into memory.</summary>
        Private Const MaxFileBytes As Long = 32L * 1024L * 1024L

        Private ReadOnly _sandbox As Sandbox

        ''' <param name="sandbox">The sandbox that resolves the paths and enforces the security policy.</param>
        Public Sub New(sandbox As Sandbox)
            If sandbox Is Nothing Then
                Throw New ArgumentNullException(NameOf(sandbox))
            End If

            _sandbox = sandbox
        End Sub

        ''' <summary>
        ''' Search file contents with a ripgrep style regular expression.
        ''' </summary>
        ''' <param name="pattern">Regular expression to search for (ripgrep syntax).</param>
        ''' <param name="path">File or directory to search. Defaults to the session workspace; a relative path resolves against it.</param>
        ''' <param name="include">One glob filter for which files to search (e.g. "*.ts", "*.{js,jsx}"). Not a list; negation is not supported.</param>
        <Description("Search file contents with a ripgrep regular expression. Returns matching lines with line numbers, grouped by file. Returns up to 250 matches; a larger result reports where the complete match list was saved.")>
        <ExportAPI("grep")>
        Public Function grep(
            <Argument("pattern", Description:="Regular expression to search for (ripgrep syntax).")> pattern As String,
            <Argument("path", Description:="File or directory to search. Defaults to the session workspace; a relative path resolves against it.")> Optional path As String = Nothing,
            <Argument("include", Description:="One glob filter for which files to search (e.g. ""*.ts"", ""*.{js,jsx}""). Not a list; negation is not supported.")> Optional include As String = Nothing
        ) As String
            If String.IsNullOrWhiteSpace(pattern) Then
                Return "Error: pattern is empty."
            End If

            Dim regex As Regex

            Try
                regex = New Regex(pattern, RegexOptions.CultureInvariant)
            Catch ex As Exception
                Return $"Error: invalid pattern '{pattern}': {ex.Message}"
            End Try

            Dim root As String = Nothing
            Dim denied As String = _sandbox.ResolveAndAuthorize(path, SandboxOperation.Read, root)

            If denied IsNot Nothing Then
                Return denied
            End If

            If Not File.Exists(root) AndAlso Not Directory.Exists(root) Then
                Return $"Error: no such file or directory: {root}"
            End If

            Dim matches As New List(Of GrepMatch)()

            For Each file As String In CollectSearchFiles(root, include)
                Call CollectMatches(file, regex, matches)
            Next

            Dim report As String = FormatMatches(matches, MaxMatches)

            If matches.Count > MaxMatches Then
                report &= vbCrLf & BuildOverflowNote(matches) & vbCrLf
            End If

            Return report
        End Function

        ''' <summary>
        ''' Enumerate the files that a grep call should read. A file target is
        ''' returned as is; a directory target is walked recursively while
        ''' skipping the usual noise folders (hidden entries, bin, obj,
        ''' node_modules) and applying the single <paramref name="include"/>
        ''' glob. The result is sorted so that the report is deterministic.
        ''' </summary>
        Private Function CollectSearchFiles(root As String, include As String) As List(Of String)
            Dim files As New List(Of String)()
            Dim matcher As GlobPattern = GlobPattern.Create(include)

            If File.Exists(root) Then
                If matcher Is Nothing OrElse matcher.IsMatch(_sandbox.WorkspaceRelative(root)) Then
                    files.Add(root)
                End If

                Return files
            End If

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
                    If Not IsSkippedDirectory(IO.Path.GetFileName(subDir)) Then
                        pending.Push(subDir)
                    End If
                Next

                Try
                    entries = Directory.GetFiles(dir)
                Catch
                    entries = New String() {}
                End Try

                For Each file As String In entries
                    If matcher Is Nothing OrElse matcher.IsMatch(_sandbox.WorkspaceRelative(file)) Then
                        files.Add(file)
                    End If
                Next
            End While

            files.Sort(StringComparer.OrdinalIgnoreCase)

            Return files
        End Function

        ''' <summary>
        ''' Collect every line of <paramref name="target"/> that the pattern
        ''' matches. Binary and oversized files are skipped, and a file that
        ''' cannot be read is ignored instead of aborting the whole search.
        ''' </summary>
        Private Sub CollectMatches(target As String, regex As Regex, matches As List(Of GrepMatch))
            Dim lines() As String

            Try
                Dim info As New FileInfo(target)

                If info.Length > MaxFileBytes OrElse IsBinaryFile(target) Then
                    Return
                End If

                lines = File.ReadAllLines(target, Encoding.UTF8)
            Catch
                Return
            End Try

            Dim rel As String = _sandbox.WorkspaceRelative(target)

            For i As Integer = 0 To lines.Length - 1
                If regex.IsMatch(lines(i)) Then
                    matches.Add(New GrepMatch With {
                        .File = rel,
                        .Line = i + 1,
                        .Text = lines(i)
                    })
                End If
            Next
        End Sub

        ''' <summary>
        ''' Render the match list as "Found N matches" followed by one block per
        ''' file, each block listing "Line n: ..." entries. At most
        ''' <paramref name="limit"/> matches are written out, but the header
        ''' always reports the total number of matches found.
        ''' </summary>
        Private Shared Function FormatMatches(matches As List(Of GrepMatch), limit As Integer) As String
            Dim sb As New StringBuilder()
            sb.AppendLine($"Found {matches.Count} matches")

            Dim shown As Integer = 0
            Dim index As Integer = 0

            While index < matches.Count AndAlso shown < limit
                Dim file As String = matches(index).File
                sb.AppendLine()
                sb.AppendLine(file)

                While index < matches.Count AndAlso matches(index).File = file AndAlso shown < limit
                    sb.AppendLine($"Line {matches(index).Line}: {matches(index).Text}")
                    index += 1
                    shown += 1
                End While
            End While

            Return sb.ToString()
        End Function

        ''' <summary>
        ''' Save the complete match list inside the workspace and report where
        ''' it went. The dump is a write operation, so it is gated by the active
        ''' security policy just like any other write.
        ''' </summary>
        Private Function BuildOverflowNote(matches As List(Of GrepMatch)) As String
            Dim target As String = _sandbox.ArtifactPath("grep", $"grep-{DateTime.Now.ToString("yyyyMMdd-HHmmss")}.txt")
            Dim denied As String = _sandbox.Authorize(SandboxOperation.Write, target, insideWorkspace:=True)

            If denied IsNot Nothing Then
                Return $"(Showing the first {MaxMatches} of {matches.Count} matches. The complete match list could not be saved under the current security policy.)"
            End If

            Try
                Call Directory.CreateDirectory(IO.Path.GetDirectoryName(target))
                Call File.WriteAllText(target, FormatMatches(matches, Integer.MaxValue), Encoding.UTF8)
            Catch ex As Exception
                Return $"(Showing the first {MaxMatches} of {matches.Count} matches. The complete match list could not be saved: {ex.Message})"
            End Try

            Return $"(Showing the first {MaxMatches} of {matches.Count} matches. Complete match list saved to: {_sandbox.WorkspaceRelative(target)})"
        End Function

        Private Shared Function IsSkippedDirectory(name As String) As Boolean
            If String.IsNullOrEmpty(name) OrElse name.StartsWith(".") Then
                Return True
            End If

            Select Case name.ToLowerInvariant()
                Case "bin", "obj", "node_modules"
                    Return True
                Case Else
                    Return False
            End Select
        End Function

        ''' <summary>
        ''' A cheap binary sniff: a NUL byte inside the first few kilobytes
        ''' means the file is not text and must not be grepped.
        ''' </summary>
        Private Shared Function IsBinaryFile(target As String) As Boolean
            Using stream As FileStream = File.OpenRead(target)
                Dim buffer(4095) As Byte
                Dim read As Integer = stream.Read(buffer, 0, buffer.Length)

                For i As Integer = 0 To read - 1
                    If buffer(i) = 0 Then
                        Return True
                    End If
                Next
            End Using

            Return False
        End Function

        Private NotInheritable Class GrepMatch

            Public Property File As String
            Public Property Line As Integer
            Public Property Text As String

        End Class

    End Class

End Namespace
