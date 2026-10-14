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
    ''' 
    ''' Three report shapes are supported: the matching lines with their line
    ''' numbers (optionally with a few lines of context around each match),
    ''' just the paths of the files that matched, and the per file match
    ''' counts.
    ''' 
    ''' When the sandbox denies the read, the model may issue one retry that
    ''' carries a <c>sandbox_permissions</c> mode and a justification. That
    ''' retry does not change the policy: it raises the sandbox permission
    ''' event and, if the user approves, performs this single operation.
    ''' </summary>
    Public Class GrepTool

        ''' <summary>Maximum number of matches (or of files) a single grep result reports.</summary>
        Private Const MaxMatches As Integer = 250

        ''' <summary>
        ''' Safety valve on the number of reportable lines held in memory, so
        ''' that a very large file combined with a large context request
        ''' cannot exhaust the process memory.
        ''' </summary>
        Private Const MaxHitLines As Integer = 20000

        ''' <summary>Files larger than this are skipped instead of loaded into memory.</summary>
        Private Const MaxFileBytes As Long = 32L * 1024L * 1024L

        ''' <summary>Upper bound on the context lines requested for one side of a match.</summary>
        Private Const MaxContextLines As Integer = 50

        ''' <summary>output_mode value: report the matching lines.</summary>
        Private Const ModeContent As String = "content"

        ''' <summary>output_mode value: report only the paths of the files that matched.</summary>
        Private Const ModeFiles As String = "files_with_matches"

        ''' <summary>output_mode value: report one "path: count" line per matched file.</summary>
        Private Const ModeCount As String = "count"

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
        ''' <param name="include">Comma separated glob filters for which files to search; a leading "!" excludes a pattern.</param>
        ''' <param name="context">Lines of context to show on both sides of every match. Defaults to 0.</param>
        ''' <param name="before">Lines of context before every match; the effective value is max(context, before). Defaults to 0.</param>
        ''' <param name="after">Lines of context after every match; the effective value is max(context, after). Defaults to 0.</param>
        ''' <param name="output_mode">"content" (default), "files_with_matches" or "count".</param>
        ''' <param name="ignore_case">Match case insensitively. Defaults to false.</param>
        ''' <param name="sandbox_permissions">
        ''' The narrowest wider sandbox mode for a one-shot retry of the exact
        ''' operation the sandbox just denied; the retry asks the user for
        ''' approval. Either "workspace-write" (target inside the workspace) or
        ''' "danger-full-access" (target outside it). Leave empty unless the
        ''' sandbox denied the operation.
        ''' </param>
        ''' <param name="justification">
        ''' Required with sandbox_permissions: one sentence for the user
        ''' explaining why this exact search needs the wider access. Use the
        ''' language of the user current request.
        ''' </param>
        <Description("Search file contents with a ripgrep regular expression. By default returns the matching lines with line numbers, grouped by file, and with the surrounding context lines when context/before/after are given (context lines are marked ""Line n- ""). output_mode=""files_with_matches"" returns the paths of the matched files only, output_mode=""count"" returns one ""path: count"" line per file. Returns up to 250 matches (or 250 files for the two list modes); a larger result reports where the match list was saved.")>
        <ExportAPI("grep")>
        Public Function grep(
            <Argument("pattern", Description:="Regular expression to search for (ripgrep syntax).")> pattern As String,
            <Argument("path", Description:="File or directory to search. Defaults to the session workspace; a relative path resolves against it.")> Optional path As String = Nothing,
            <Argument("include", Description:="Comma separated glob filters for which files to search, e.g. ""*.vb,*.vbproj"" or ""*.{js,jsx}"". A leading ""!"" excludes a pattern, e.g. ""*.vb,!*Tests.vb"". A pattern without a ""/"" is matched against the file name at any depth. Omitted: every text file.")> Optional include As String = Nothing,
            <Argument("context", Description:="Lines of context to show on both sides of every match. Defaults to 0 (only the matching lines); capped at 50.")> Optional context As Double = 0,
            <Argument("before", Description:="Lines of context before every match. Defaults to 0; the effective value is max(context, before).")> Optional before As Double = 0,
            <Argument("after", Description:="Lines of context after every match. Defaults to 0; the effective value is max(context, after).")> Optional after As Double = 0,
            <Argument("output_mode", Description:="What to report: ""content"" (default) returns the matching lines, ""files_with_matches"" returns only the paths of the matching files, ""count"" returns one ""path: count"" line per matching file.")> Optional output_mode As String = Nothing,
            <Argument("ignore_case", Description:="Match case insensitively. Defaults to false.")> Optional ignore_case As Boolean = False,
            <Argument("sandbox_permissions", Description:="The narrowest wider sandbox mode for a one-shot retry of the exact operation the sandbox just denied; the retry asks the user for approval. One of ""workspace-write"" (target inside the workspace) or ""danger-full-access"" (target outside it). Leave empty unless the sandbox denied the operation.")> Optional sandbox_permissions As String = Nothing,
            <Argument("justification", Description:="Required with sandbox_permissions: one sentence for the user explaining why this exact search needs the wider access. Use the language of the user's current request.")> Optional justification As String = Nothing
        ) As String
            If String.IsNullOrWhiteSpace(pattern) Then
                Return "Error: pattern is empty."
            End If

            Dim mode As String = If(String.IsNullOrWhiteSpace(output_mode), ModeContent, output_mode.Trim().ToLowerInvariant())

            Select Case mode
                Case ModeContent, ModeFiles, ModeCount
                    ' supported
                Case Else
                    Return $"Error: unknown output_mode '{output_mode}'. Expected '{ModeContent}', '{ModeFiles}' or '{ModeCount}'."
            End Select

            Dim options As RegexOptions = RegexOptions.CultureInvariant

            If ignore_case Then
                options = options Or RegexOptions.IgnoreCase
            End If

            Dim regex As Regex

            Try
                regex = New Regex(pattern, options)
            Catch ex As Exception
                Return $"Error: invalid pattern '{pattern}': {ex.Message}"
            End Try

            Dim root As String = Nothing
            Dim denied As String = _sandbox.ResolveAndAuthorize(path, SandboxOperation.Read, root)

            If denied IsNot Nothing Then
                ' No escalation asked for (or no path to escalate: an
                ' unresolvable path leaves root empty): report the denial as
                ' it is.
                If String.IsNullOrWhiteSpace(sandbox_permissions) OrElse root Is Nothing Then
                    Return denied
                End If

                ' One-shot retry: ask the user for the narrowest mode that
                ' would cover this exact operation. The policy itself is not
                ' changed.
                Dim escalated As String = _sandbox.AuthorizeEscalation(SandboxOperation.Read, root, sandbox_permissions, justification)

                If escalated IsNot Nothing Then
                    Return escalated
                End If
            End If

            If Not File.Exists(root) AndAlso Not Directory.Exists(root) Then
                Return $"Error: no such file or directory: {root}"
            End If

            Dim lead As Integer = ContextLines(context, before)
            Dim trail As Integer = ContextLines(context, after)
            Dim hits As New List(Of GrepHit)()
            Dim files As Integer = 0
            Dim matches As Integer = 0
            Dim capped As Boolean = False

            For Each candidate As String In CollectSearchFiles(root, include)
                Call CollectMatches(candidate, regex, hits, lead, trail, files, matches, capped)
            Next

            If files = 0 Then
                ' One wording for an empty result, whatever the report shape.
                Return "No matches found."
            End If

            Dim listed As Integer = If(mode = ModeContent, matches, files)

            ' The "--" chunk separator is only meaningful when context lines
            ' are reported: without them every line of the report is a match
            ' and the chunks are the plain historical match list.
            Dim chunked As Boolean = lead > 0 OrElse trail > 0
            Dim report As String = RenderReport(hits, mode, MaxMatches, matches, chunked)

            If listed > MaxMatches Then
                Dim noun As String = If(mode = ModeContent, "matches", "files")

                report &= vbCrLf & BuildOverflowNote(listed, noun, RenderReport(hits, mode, Integer.MaxValue, matches, chunked), capped) & vbCrLf
            End If

            Return report
        End Function

        ''' <summary>
        ''' Enumerate the files that a grep call should read. A file target is
        ''' returned as is; a directory target is walked recursively while
        ''' skipping the usual noise folders (hidden entries, bin, obj,
        ''' node_modules) and applying the <paramref name="include"/> glob set
        ''' (several comma separated entries, "!" prefixed entries exclude).
        ''' The result is sorted so that the report is deterministic.
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
        ''' Collect the reportable lines of <paramref name="target"/>: every
        ''' line the pattern matches, plus up to <paramref name="before"/> and
        ''' <paramref name="after"/> lines of context around each match, with
        ''' touching or overlapping regions merged into a single chunk.
        ''' Binary and oversized files are skipped, and a file that cannot be
        ''' read is ignored instead of aborting the whole search.
        ''' </summary>
        ''' <param name="fileCount">Receives 1 when the file holds at least one match.</param>
        ''' <param name="matchCount">Receives the number of matching lines of the file.</param>
        ''' <param name="capped">Set to True when the in memory list reached <see cref="MaxHitLines"/>, so that the reportable lines are no longer all of them.</param>
        Private Sub CollectMatches(candidate As String,
                                   regex As Regex,
                                   hits As List(Of GrepHit),
                                   before As Integer,
                                   after As Integer,
                                   ByRef fileCount As Integer,
                                   ByRef matchCount As Integer,
                                   ByRef capped As Boolean)
            Dim lines() As String

            Try
                Dim info As New FileInfo(candidate)

                If info.Length > MaxFileBytes OrElse IsBinaryFile(candidate) Then
                    Return
                End If

                lines = File.ReadAllLines(candidate, Encoding.UTF8)
            Catch
                Return
            End Try

            Dim matched As New List(Of Integer)()

            For i As Integer = 0 To lines.Length - 1
                If regex.IsMatch(lines(i)) Then
                    matched.Add(i)
                End If
            Next

            If matched.Count = 0 Then
                Return
            End If

            fileCount += 1
            matchCount += matched.Count

            If hits.Count >= MaxHitLines Then
                capped = True
                Return
            End If

            Dim rel As String = _sandbox.WorkspaceRelative(candidate)

            ' Merge the reportable regions: the regions of two matches that
            ' touch or overlap form one chunk and are emitted once.
            Dim regions As New List(Of Integer())()
            Dim lastLine As Integer = -2

            For Each index As Integer In matched
                Dim from As Integer = Math.Max(0, index - before)
                Dim [finish] As Integer = Math.Min(lines.Length - 1, index + after)

                If from > lastLine + 1 Then
                    regions.Add(New Integer() {from, [finish]})
                ElseIf [finish] > lastLine Then
                    regions(regions.Count - 1)(1) = [finish]
                End If

                If [finish] > lastLine Then
                    lastLine = [finish]
                End If
            Next

            ' Walk the merged regions and the ascending match list together so
            ' that every emitted line knows whether it is a match or context.
            Dim cursor As Integer = 0

            For Each region As Integer() In regions
                Dim first As Boolean = True

                For n As Integer = region(0) To region(1)
                    If hits.Count >= MaxHitLines Then
                        capped = True
                        Return
                    End If

                    While cursor < matched.Count AndAlso matched(cursor) < n
                        cursor += 1
                    End While

                    Dim isMatch As Boolean = cursor < matched.Count AndAlso matched(cursor) = n

                    hits.Add(New GrepHit With {
                        .File = rel,
                        .Line = n + 1,
                        .Text = lines(n),
                        .IsMatch = isMatch,
                        .NewChunk = first
                    })

                    first = False
                Next
            Next
        End Sub

        ''' <summary>
        ''' Render the report for the requested shape, honouring the match (or
        ''' file) limit.
        ''' </summary>
        ''' <param name="chunked">True when context lines are reported, so that the chunks of one file are separated by "--".</param>
        Private Shared Function RenderReport(hits As List(Of GrepHit), mode As String, limit As Integer, matches As Integer, chunked As Boolean) As String
            Select Case mode
                Case ModeFiles
                    Return RenderFiles(hits, limit)
                Case ModeCount
                    Return RenderCounts(hits, limit)
                Case Else
                    Return $"Found {matches} matches" & vbCrLf & RenderContent(hits, limit, chunked)
            End Select
        End Function

        ''' <summary>
        ''' Render the match list as one block per file, each block listing
        ''' "Line n: ..." entries, with "Line n- ..." context lines around
        ''' them and a "--" separator between the chunks of one file. At most
        ''' <paramref name="limit"/> matches are written out.
        ''' </summary>
        Private Shared Function RenderContent(hits As List(Of GrepHit), limit As Integer, chunked As Boolean) As String
            Dim sb As New StringBuilder()
            Dim shown As Integer = 0
            Dim current As String = Nothing
            Dim i As Integer = 0

            While i < hits.Count AndAlso shown < limit
                Dim hit As GrepHit = hits(i)

                If Not String.Equals(hit.File, current, StringComparison.Ordinal) Then
                    sb.AppendLine()
                    sb.AppendLine(hit.File)
                    current = hit.File
                ElseIf chunked AndAlso hit.NewChunk Then
                    sb.AppendLine("--")
                End If

                If hit.IsMatch Then
                    shown += 1
                    sb.AppendLine($"Line {hit.Line}: {hit.Text}")
                Else
                    sb.AppendLine($"Line {hit.Line}- {hit.Text}")
                End If

                i += 1
            End While

            Return sb.ToString()
        End Function

        ''' <summary>
        ''' Render one workspace relative path per matched file, in the order
        ''' the search found them, at most <paramref name="limit"/> of them.
        ''' </summary>
        Private Shared Function RenderFiles(hits As List(Of GrepHit), limit As Integer) As String
            Dim sb As New StringBuilder()
            Dim listed As Integer = 0
            Dim i As Integer = 0

            While i < hits.Count AndAlso listed < limit
                sb.AppendLine(hits(i).File)
                listed += 1

                ' skip the remaining lines of this file
                Dim file As String = hits(i).File

                While i < hits.Count AndAlso String.Equals(hits(i).File, file, StringComparison.Ordinal)
                    i += 1
                End While
            End While

            Return sb.ToString()
        End Function

        ''' <summary>
        ''' Render one "path: count" line per matched file, at most
        ''' <paramref name="limit"/> files.
        ''' </summary>
        Private Shared Function RenderCounts(hits As List(Of GrepHit), limit As Integer) As String
            Dim sb As New StringBuilder()
            Dim listed As Integer = 0
            Dim i As Integer = 0

            While i < hits.Count AndAlso listed < limit
                Dim file As String = hits(i).File
                Dim count As Integer = 0

                While i < hits.Count AndAlso String.Equals(hits(i).File, file, StringComparison.Ordinal)
                    If hits(i).IsMatch Then
                        count += 1
                    End If

                    i += 1
                End While

                sb.AppendLine($"{file}: {count}")
                listed += 1
            End While

            Return sb.ToString()
        End Function

        ''' <summary>
        ''' Save the report inside the workspace and report where it went. The
        ''' dump is a write operation, so it is gated by the active security
        ''' policy just like any other write.
        ''' </summary>
        ''' <param name="listed">Total number of matches (or files) that were found.</param>
        ''' <param name="noun">"matches" or "files", for the report text.</param>
        ''' <param name="full">The report to save.</param>
        ''' <param name="capped">True when the in memory report stopped at <see cref="MaxHitLines"/>, so that the saved list holds fewer entries than <paramref name="listed"/>.</param>
        Private Function BuildOverflowNote(listed As Integer, noun As String, full As String, capped As Boolean) As String
            Dim target As String = _sandbox.ArtifactPath("grep", $"grep-{DateTime.Now.ToString("yyyyMMdd-HHmmss")}.txt")
            Dim denied As String = _sandbox.Authorize(SandboxOperation.Write, target, insideWorkspace:=True)

            ' The scan stops growing the in memory list at MaxHitLines, so the
            ' dump is only the complete list while that limit was not reached.
            ' The total counts stay exact either way.
            Dim subject As String = If(capped,
                $"The match list (truncated after {MaxHitLines} report lines)",
                "The complete match list")

            If denied IsNot Nothing Then
                Return $"(Showing the first {MaxMatches} of {listed} {noun}. {subject} could not be saved under the current security policy.)"
            End If

            Try
                Call Directory.CreateDirectory(IO.Path.GetDirectoryName(target))
                Call File.WriteAllText(target, full, Encoding.UTF8)
            Catch ex As Exception
                Return $"(Showing the first {MaxMatches} of {listed} {noun}. {subject} could not be saved: {ex.Message})"
            End Try

            Return $"(Showing the first {MaxMatches} of {listed} {noun}. {subject} saved to: {_sandbox.WorkspaceRelative(target)})"
        End Function

        ''' <summary>
        ''' The number of context lines to report on one side of a match: the
        ''' larger of the shared "context" value and the side specific
        ''' override, clamped so that a malformed or absurd request cannot
        ''' flood the result.
        ''' </summary>
        Private Shared Function ContextLines(sharedValue As Double, sideValue As Double) As Integer
            Return CInt(Math.Max(LineCount(sharedValue), LineCount(sideValue)))
        End Function

        Private Shared Function LineCount(value As Double) As Double
            If Double.IsNaN(value) OrElse Double.IsInfinity(value) OrElse value < 1 Then
                Return 0
            End If

            If value >= MaxContextLines Then
                Return MaxContextLines
            End If

            Return Math.Floor(value)
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

        ''' <summary>
        ''' One reportable line of one file: either a matching line or a
        ''' context line around a match.
        ''' </summary>
        Private NotInheritable Class GrepHit

            ''' <summary>The workspace relative path of the file.</summary>
            Public Property File As String

            ''' <summary>The 1-based line number.</summary>
            Public Property Line As Integer

            ''' <summary>The text of that line.</summary>
            Public Property Text As String

            ''' <summary>True for a matching line, False for a context line.</summary>
            Public Property IsMatch As Boolean

            ''' <summary>True when this line starts a new chunk of its file.</summary>
            Public Property NewChunk As Boolean

        End Class

    End Class

End Namespace
