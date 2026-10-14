Imports System.ComponentModel
Imports System.IO
Imports System.Text
Imports Microsoft.VisualBasic.CommandLine.Reflection

Namespace Tools

    ''' <summary>
    ''' Tool "ls": list the children of a directory, sub directories included.
    ''' 
    ''' This closes a real hole in the tool set: glob only reports files, and
    ''' under the <c>Readonly</c> policy the shell tool is refused, so without
    ''' this tool the agent cannot see the directory structure at all.
    ''' 
    ''' The tool holds a reference to the <see cref="Sandbox"/> that owns it,
    ''' so the listed directory is resolved and authorized like any other
    ''' path. Hidden entries are listed: a ".git" or ".codebuddy" folder is
    ''' something the agent should be able to see.
    ''' </summary>
    Public Class LsTool

        ''' <summary>Maximum number of entries a single listing reports.</summary>
        Private Const MaxEntries As Integer = 1000

        ''' <summary>Deepest level the walk descends to.</summary>
        Private Const MaxDepth As Integer = 4

        ''' <summary>Safety valve: stop walking once this many entries were seen.</summary>
        Private Const ScanLimit As Integer = 20000

        ''' <summary>Indentation added per extra level.</summary>
        Private Const IndentUnit As String = "  "

        Private ReadOnly _sandbox As Sandbox

        ''' <param name="sandbox">The sandbox that resolves the paths and enforces the security policy.</param>
        Public Sub New(sandbox As Sandbox)
            If sandbox Is Nothing Then
                Throw New ArgumentNullException(NameOf(sandbox))
            End If

            _sandbox = sandbox
        End Sub

        ''' <summary>
        ''' List a directory: sub directories marked "[DIR]" and files marked
        ''' "[FILE]", one entry per line.
        ''' </summary>
        ''' <param name="path">Directory to list. Defaults to the session workspace; a relative path resolves against it.</param>
        ''' <param name="depth">How many directory levels to descend, 1 meaning only the direct children. Defaults to 1, capped at 4.</param>
        <Description("List the children of a directory: sub directories and files, one entry per line, hidden entries included. Sub directories are marked [DIR] and files [FILE]. Set depth above 1 to walk several levels; the tree is then shown indented below its parent.")>
        <ExportAPI("ls")>
        Public Function ls(
            <Argument("path", Description:="Directory to list. Defaults to the session workspace; a relative path resolves against it.")> Optional path As String = Nothing,
            <Argument("depth", Description:="How many directory levels to descend, 1 meaning only the direct children. Defaults to 1, capped at 4.")> Optional depth As Double = 1
        ) As String
            Dim fullPath As String = Nothing
            Dim denied As String = _sandbox.ResolveAndAuthorize(path, SandboxOperation.Read, fullPath)

            If denied IsNot Nothing Then
                Return denied
            End If

            If File.Exists(fullPath) Then
                Return $"Error: not a directory: {fullPath}"
            End If

            If Not Directory.Exists(fullPath) Then
                Return $"Error: no such directory: {fullPath}"
            End If

            Dim entries As New List(Of String)()
            Dim exhausted As Boolean = False

            Call Collect(fullPath, ClampDepth(depth), 1, entries, exhausted)

            If entries.Count = 0 Then
                Return "(empty directory)"
            End If

            Dim sb As New StringBuilder()

            For i As Integer = 0 To Math.Min(MaxEntries, entries.Count) - 1
                sb.AppendLine(entries(i))
            Next

            If entries.Count > MaxEntries OrElse exhausted Then
                sb.AppendLine()
                sb.AppendLine(BuildOverflowNote(entries, exhausted))
            End If

            Return sb.ToString()
        End Function

        ''' <summary>
        ''' Walk the tree depth first, appending the marker line of every
        ''' directory before the lines of its content so that the report reads
        ''' as a tree. Directories come before files at each level, and each
        ''' group is sorted ordinally so the output is deterministic.
        ''' </summary>
        Private Shared Sub Collect(dir As String, maxLevel As Integer, level As Integer, entries As List(Of String), ByRef exhausted As Boolean)
            If level > maxLevel OrElse entries.Count >= ScanLimit Then
                exhausted = exhausted OrElse entries.Count >= ScanLimit
                Return
            End If

            Dim subDirs() As String
            Dim files() As String

            Try
                subDirs = Directory.GetDirectories(dir)
            Catch
                subDirs = New String() {}
            End Try

            Try
                files = Directory.GetFiles(dir)
            Catch
                files = New String() {}
            End Try

            Array.Sort(subDirs, StringComparer.OrdinalIgnoreCase)
            Array.Sort(files, StringComparer.OrdinalIgnoreCase)

            Dim indent As String = New String(" "c, (level - 1) * IndentUnit.Length)

            For Each subDir As String In subDirs
                entries.Add($"[DIR]  {indent}{IO.Path.GetFileName(subDir)}/")

                If level < maxLevel Then
                    Call Collect(subDir, maxLevel, level + 1, entries, exhausted)

                    If entries.Count >= ScanLimit Then
                        exhausted = True
                        Return
                    End If
                End If
            Next

            For Each item As String In files
                entries.Add($"[FILE] {indent}{IO.Path.GetFileName(item)}")
            Next
        End Sub

        ''' <summary>
        ''' Coerce the JSON number into a level count, clamping NaN, infinity
        ''' and out-of-range values so a malformed call can never throw.
        ''' </summary>
        Private Shared Function ClampDepth(value As Double) As Integer
            If Double.IsNaN(value) OrElse Double.IsInfinity(value) OrElse value < 1 Then
                Return 1
            End If

            If value >= MaxDepth Then
                Return MaxDepth
            End If

            Return CInt(Math.Floor(value))
        End Function

        ''' <summary>
        ''' Save the complete listing inside the workspace and report where it
        ''' went. The dump is a write operation, so it is gated by the active
        ''' security policy just like any other write.
        ''' </summary>
        Private Function BuildOverflowNote(entries As List(Of String), exhausted As Boolean) As String
            Dim reason As String

            If entries.Count > MaxEntries Then
                reason = $"(Showing the first {MaxEntries} of {entries.Count} entries."
            Else
                reason = $"(Showing all {entries.Count} entries."
            End If

            If exhausted Then
                reason &= $" The walk stopped after {ScanLimit} entries.)"
                Return reason
            End If

            Dim target As String = _sandbox.ArtifactPath("ls", $"ls-{DateTime.Now.ToString("yyyyMMdd-HHmmss")}.txt")
            Dim denied As String = _sandbox.Authorize(SandboxOperation.Write, target, insideWorkspace:=True)

            If denied IsNot Nothing Then
                Return reason & " The complete listing could not be saved under the current security policy.)"
            End If

            Try
                Call Directory.CreateDirectory(IO.Path.GetDirectoryName(target))
                Call File.WriteAllText(target, String.Join(vbCrLf, entries) & vbCrLf, Encoding.UTF8)
            Catch ex As Exception
                Return reason & $" The complete listing could not be saved: {ex.Message})"
            End Try

            Return reason & $" Complete listing saved to: {_sandbox.WorkspaceRelative(target)})"
        End Function

    End Class

End Namespace
