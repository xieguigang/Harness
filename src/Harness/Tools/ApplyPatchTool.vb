Imports System.ComponentModel
Imports System.IO
Imports System.Text
Imports Microsoft.VisualBasic.CommandLine.Reflection

Namespace Tools

    ''' <summary>
    ''' Tool "apply_patch": apply a self describing patch to one or several
    ''' files in a single all-or-nothing step.
    ''' 
    ''' The patch locates its changes by content, never by line number:
    ''' 
    ''' <code>
    ''' *** Begin Patch
    ''' *** Add File: src/new.txt
    ''' +first line
    ''' *** Update File: src/old.txt
    ''' *** Move to: src/renamed.txt
    ''' @@ some anchor
    '''  unchanged context
    ''' -removed line
    ''' +added line
    ''' *** Delete File: src/gone.txt
    ''' *** End Patch
    ''' </code>
    ''' 
    ''' Every block is parsed and located in memory first; the files are
    ''' written only when the whole patch can be applied, so a patch can never
    ''' leave a half edited tree behind. Every touched path is resolved and
    ''' authorized on its own, through the <see cref="Sandbox"/> that owns this
    ''' tool.
    ''' </summary>
    Public Class ApplyPatchTool

        ''' <summary>Files larger than this are refused instead of loaded into memory.</summary>
        Private Const MaxFileBytes As Long = 32L * 1024L * 1024L

        Private Const DirectiveAdd As String = "*** Add File:"
        Private Const DirectiveUpdate As String = "*** Update File:"
        Private Const DirectiveDelete As String = "*** Delete File:"
        Private Const DirectiveMove As String = "*** Move to:"
        Private Const DirectiveBegin As String = "*** Begin Patch"
        Private Const DirectiveEnd As String = "*** End Patch"
        Private Const DirectiveEndOfFile As String = "*** End of File"

        Private ReadOnly _sandbox As Sandbox

        ''' <param name="sandbox">The sandbox that resolves the paths and enforces the security policy.</param>
        Public Sub New(sandbox As Sandbox)
            If sandbox Is Nothing Then
                Throw New ArgumentNullException(NameOf(sandbox))
            End If

            _sandbox = sandbox
        End Sub

        ''' <summary>
        ''' Apply a self describing patch to one or several files, all or
        ''' nothing.
        ''' </summary>
        ''' <param name="patch">The patch text, wrapped in "*** Begin Patch" and "*** End Patch".</param>
        ''' <param name="sandbox_permissions">The narrowest wider sandbox mode for a one-shot retry of the exact operation the sandbox just denied.</param>
        ''' <param name="justification">Required with sandbox_permissions: one sentence for the user explaining why this exact file operation needs the wider access.</param>
        <Description("Apply a self describing patch to one or several files in a single all-or-nothing step. The patch is wrapped in ""*** Begin Patch"" and ""*** End Patch"" and holds ""*** Add File: <path>"", ""*** Update File: <path>"" (optionally followed by ""*** Move to: <path>"") and ""*** Delete File: <path>"" blocks. Update blocks locate their changes by content, never by line numbers: every hunk line carries a leading marker - one space for unchanged context, ""-"" for a removed line, ""+"" for an added line - and an optional ""@@ anchor"" line may introduce a hunk. If any block does not match, nothing is written and the failing block is reported.")>
        <ExportAPI("apply_patch")>
        Public Function apply_patch(
            <Argument("patch", Description:="The patch text. It must start with ""*** Begin Patch"" and end with ""*** End Patch"". Each block starts with ""*** Add File: <path>"", ""*** Update File: <path>"" or ""*** Delete File: <path>""; an ""*** Move to: <path>"" line right after an update block renames that file. Hunk lines start with a space (unchanged context), ""-"" (removed) or ""+"" (added), and an added file lists its lines with a leading ""+"". Paths are resolved against the workspace.")> patch As String,
            <Argument("sandbox_permissions", Description:="The narrowest wider sandbox mode for a one-shot retry of the exact operation the sandbox just denied; the retry asks the user for approval. One of ""workspace-write"" (target inside the workspace) or ""danger-full-access"" (target outside it). Leave empty unless the sandbox denied the operation.")> Optional sandbox_permissions As String = Nothing,
            <Argument("justification", Description:="Required with sandbox_permissions: one sentence for the user explaining why this exact file operation needs the wider access. Use the language of the user's current request.")> Optional justification As String = Nothing
        ) As String
            If String.IsNullOrWhiteSpace(patch) Then
                Return "Error: the patch is empty."
            End If

            Dim newline As String = TextFileUtils.DetectNewline(patch)
            Dim granted As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            Dim validation As PatchValidation

            Do
                validation = Validate(patch, newline, granted)

                If validation.Denied Is Nothing Then
                    Exit Do
                End If

                If String.IsNullOrWhiteSpace(sandbox_permissions) Then
                    Return validation.Denied
                End If

                Dim escalated As String = _sandbox.AuthorizeEscalation(SandboxOperation.Write, validation.DeniedTarget, sandbox_permissions, justification)

                If escalated IsNot Nothing Then
                    Return escalated
                End If

                Call granted.Add(validation.DeniedTarget)
            Loop

            If validation.Failure IsNot Nothing Then
                Return validation.Failure
            End If

            Return Apply(validation.Blocks)
        End Function

        ' ====================================================================
        ' Phase 1: parse and verify, without touching the disk
        ' ====================================================================

        ''' <summary>
        ''' Parse the patch and locate every change. Nothing is written here:
        ''' this only fills in the new content of every file, so that a failure
        ''' can be reported before any file was modified.
        ''' </summary>
        ''' <param name="patch">The patch text.</param>
        ''' <param name="newline">The line ending of the patch, used for the files it adds.</param>
        ''' <param name="granted">Targets the user already approved through a one-shot escalation.</param>
        Private Function Validate(patch As String, newline As String, granted As HashSet(Of String)) As PatchValidation
            Dim result As New PatchValidation()
            Dim failure As String = Nothing
            Dim blocks As List(Of PatchBlock) = ParseBlocks(patch, newline, failure)

            If blocks Is Nothing Then
                result.Failure = failure
                Return result
            End If

            ' The content of every touched file, so that two blocks that touch
            ' the same file build on each other instead of both starting from
            ' the original text on disk.
            Dim contents As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
            Dim boms As New Dictionary(Of String, Boolean)(StringComparer.OrdinalIgnoreCase)

            For Each block As PatchBlock In blocks
                Dim resolved As String = Nothing
                Dim denied As String = Nothing

                If Not ResolveTarget(block.Path, granted, resolved, denied) Then
                    If resolved Is Nothing Then
                        result.Failure = denied
                    Else
                        result.Denied = denied
                        result.DeniedTarget = resolved
                    End If

                    Return result
                End If

                block.ResolvedPath = resolved

                If Not String.IsNullOrEmpty(block.MoveTo) Then
                    Dim movePath As String = Nothing
                    Dim moveDenied As String = Nothing

                    If Not ResolveTarget(block.MoveTo, granted, movePath, moveDenied) Then
                        If movePath Is Nothing Then
                            result.Failure = moveDenied
                        Else
                            result.Denied = moveDenied
                            result.DeniedTarget = movePath
                        End If

                        Return result
                    End If

                    block.ResolvedMovePath = movePath
                End If

                If Not ValidateBlock(block, contents, boms, result) Then
                    Return result
                End If
            Next

            result.Blocks = blocks

            Return result
        End Function

        ''' <summary>
        ''' Resolve one patch path and check it against the security policy.
        ''' Returns True when the path may be written, with the absolute path
        ''' in <paramref name="resolved"/>; a denied target also hands its
        ''' denial message back through <paramref name="denied"/>.
        ''' </summary>
        Private Function ResolveTarget(path As String, granted As HashSet(Of String), ByRef resolved As String, ByRef denied As String) As Boolean
            Dim fullPath As String = Nothing
            Dim refused As String = _sandbox.ResolveAndAuthorize(path, SandboxOperation.Write, fullPath)

            resolved = fullPath
            denied = Nothing

            If refused Is Nothing Then
                Return True
            End If

            If fullPath IsNot Nothing AndAlso granted.Contains(fullPath) Then
                Return True
            End If

            denied = refused

            Return False
        End Function

        ''' <summary>
        ''' Check one parsed block and work out the content it produces.
        ''' </summary>
        ''' <param name="block">The block to check.</param>
        ''' <param name="contents">The current content of every file already touched by this patch.</param>
        ''' <param name="boms">Whether every touched file carried a byte order mark.</param>
        ''' <param name="result">Receives the failure message.</param>
        Private Shared Function ValidateBlock(block As PatchBlock,
                                              contents As Dictionary(Of String, String),
                                              boms As Dictionary(Of String, Boolean),
                                              result As PatchValidation) As Boolean
            Select Case block.Kind
                Case PatchKind.Add
                    If File.Exists(block.ResolvedPath) OrElse Directory.Exists(block.ResolvedPath) Then
                        result.Failure = $"Error: '*** Add File: {block.Path}' cannot be applied because a file already exists at {block.ResolvedPath}."
                        Return False
                    End If

                    contents(block.ResolvedPath) = block.Content
                    boms(block.ResolvedPath) = False

                Case PatchKind.Delete
                    If Not File.Exists(block.ResolvedPath) AndAlso Not contents.ContainsKey(block.ResolvedPath) Then
                        result.Failure = $"Error: '*** Delete File: {block.Path}' cannot be applied because there is no file at {block.ResolvedPath}."
                        Return False
                    End If

                    Call contents.Remove(block.ResolvedPath)
                    Call boms.Remove(block.ResolvedPath)

                Case Else
                    Dim original As String = Nothing

                    If Not contents.TryGetValue(block.ResolvedPath, original) Then
                        If Not File.Exists(block.ResolvedPath) Then
                            result.Failure = $"Error: '*** Update File: {block.Path}' cannot be applied because there is no file at {block.ResolvedPath}."
                            Return False
                        End If

                        Dim info As New FileInfo(block.ResolvedPath)

                        If info.Length > MaxFileBytes Then
                            result.Failure = $"Error: '{block.ResolvedPath}' is larger than 32 MB, which is too large to patch safely with this tool."
                            Return False
                        End If

                        Try
                            boms(block.ResolvedPath) = TextFileUtils.HasUtf8Bom(block.ResolvedPath)
                            original = TextFileUtils.ReadAllText(block.ResolvedPath)
                        Catch ex As Exception
                            result.Failure = $"Error reading file '{block.ResolvedPath}': {ex.Message}"
                            Return False
                        End Try
                    End If

                    block.HadBom = boms(block.ResolvedPath)

                    If Not ApplyHunks(block, original, result) Then
                        Return False
                    End If

                    contents(block.ResolvedPath) = block.Updated
            End Select

            Return True
        End Function

        ''' <summary>
        ''' Locate every hunk of an update block in the given text and build
        ''' the patched text.
        ''' </summary>
        Private Shared Function ApplyHunks(block As PatchBlock, original As String, result As PatchValidation) As Boolean
            Dim text As String = original
            Dim newline As String = TextFileUtils.DetectNewline(text)

            For i As Integer = 0 To block.Hunks.Count - 1
                Dim hunk As Hunk = block.Hunks(i)
                Dim position As Integer = i + 1
                Dim oldLines As New List(Of String)()
                Dim newLines As New List(Of String)()

                For Each item As HunkLine In hunk.Lines
                    If item.Kind <> "+"c Then
                        oldLines.Add(item.Text)
                    End If

                    If item.Kind <> "-"c Then
                        newLines.Add(item.Text)
                    End If
                Next

                If oldLines.Count = 0 Then
                    result.Failure = $"Error: hunk {position} of '*** Update File: {block.Path}' has no context line and no removed line, so it cannot be located. " &
                                     "Add at least one unchanged context line or a '-' line."
                    Return False
                End If

                Dim needle As String = String.Join(newline, oldLines)
                Dim replacement As String = String.Join(newline, newLines)

                If Not text.Contains(needle) Then
                    Dim located As String = TextFileUtils.ResolveNeedle(text, needle)

                    If located Is Nothing Then
                        result.Failure = $"Error: nothing was written: hunk {position} of '*** Update File: {block.Path}' did not match {block.ResolvedPath}. " &
                                         "Check the context lines against the current content of that file; line numbers are never used."
                        Return False
                    End If

                    needle = located
                    replacement = TextFileUtils.NormaliseNewlines(replacement, newline)
                End If

                Dim count As Integer = TextFileUtils.CountOccurrences(text, needle)

                If count > 1 Then
                    result.Failure = $"Error: nothing was written: hunk {position} of '*** Update File: {block.Path}' matches {count} times in {block.ResolvedPath}. " &
                                     "Add surrounding context lines so that the change can be located uniquely."
                    Return False
                End If

                Dim at As Integer = text.IndexOf(needle, StringComparison.Ordinal)
                text = text.Substring(0, at) & replacement & text.Substring(at + needle.Length)
            Next

            block.Updated = text

            Return True
        End Function

        ' ====================================================================
        ' Phase 2: write the verified change set
        ' ====================================================================

        ''' <summary>
        ''' Execute the verified blocks in patch order, reporting what was
        ''' already done when a file operation fails half way.
        ''' </summary>
        Private Shared Function Apply(blocks As List(Of PatchBlock)) As String
            Dim done As New List(Of String)()

            For i As Integer = 0 To blocks.Count - 1
                Dim block As PatchBlock = blocks(i)
                Dim failure As String = Nothing

                Select Case block.Kind
                    Case PatchKind.Add
                        failure = Write(block.ResolvedPath, block.Content, False)

                        If failure Is Nothing Then
                            done.Add($"Added: {block.Path}")
                        End If

                    Case PatchKind.Delete
                        failure = Delete(block.ResolvedPath)

                        If failure Is Nothing Then
                            done.Add($"Deleted: {block.Path}")
                        End If

                    Case Else
                        failure = Write(block.ResolvedPath, block.Updated, block.HadBom)

                        If failure Is Nothing Then
                            done.Add($"Updated: {block.Path} ({block.Hunks.Count} hunk(s))")
                        End If

                        If failure Is Nothing AndAlso Not String.IsNullOrEmpty(block.MoveTo) Then
                            failure = MoveTool.MoveEntry(block.ResolvedPath, block.ResolvedMovePath)

                            If failure Is Nothing Then
                                done.Add($"Moved: {block.Path} -> {block.MoveTo}")
                            End If
                        End If
                End Select

                If failure IsNot Nothing Then
                    Dim pending As New List(Of String)()

                    For k As Integer = i To blocks.Count - 1
                        pending.Add(Describe(blocks(k)))
                    Next

                    Return $"Error: the patch was applied only partly. {failure} " &
                           $"Already applied: {If(done.Count = 0, "nothing", String.Join(", ", done.ToArray()))}. " &
                           $"Not applied: {String.Join(", ", pending.ToArray())}."
                End If
            Next

            Dim sb As New StringBuilder()

            sb.AppendLine($"Applied patch to {blocks.Count} file(s):")

            For Each item As String In done
                sb.AppendLine("  " & item)
            Next

            Return sb.ToString()
        End Function

        Private Shared Function Write(target As String, text As String, hadBom As Boolean) As String
            Try
                Dim folder As String = IO.Path.GetDirectoryName(target)

                If Not String.IsNullOrEmpty(folder) AndAlso Not Directory.Exists(folder) Then
                    Call Directory.CreateDirectory(folder)
                End If

                TextFileUtils.WriteAllText(target, text, hadBom)
            Catch ex As Exception
                Return $"Failed to write '{target}': {ex.Message}."
            End Try

            Return Nothing
        End Function

        Private Shared Function Delete(target As String) As String
            Try
                File.Delete(target)
            Catch ex As Exception
                Return $"Failed to delete '{target}': {ex.Message}."
            End Try

            Return Nothing
        End Function

        Private Shared Function Describe(block As PatchBlock) As String
            Select Case block.Kind
                Case PatchKind.Add
                    Return block.Path & " (Add)"
                Case PatchKind.Delete
                    Return block.Path & " (Delete)"
                Case Else
                    Return block.Path & " (Update)"
            End Select
        End Function

        ' ====================================================================
        ' Patch parsing
        ' ====================================================================

        ''' <summary>
        ''' Split the patch text into blocks. Returns Nothing when the patch
        ''' cannot be understood, with the reason in <paramref name="failure"/>.
        ''' </summary>
        ''' <param name="patch">The patch text.</param>
        ''' <param name="newline">The line ending of the patch text.</param>
        ''' <param name="failure">Receives the reason a malformed patch was refused.</param>
        Private Shared Function ParseBlocks(patch As String, newline As String, ByRef failure As String) As List(Of PatchBlock)
            Dim lines As String() = patch.Replace(vbCrLf, vbLf).Split(ChrW(10))
            Dim blocks As New List(Of PatchBlock)()
            Dim current As PatchBlock = Nothing
            Dim buffer As New List(Of String)()

            For Each raw As String In lines
                Dim text As String = raw
                Dim trimmed As String = text.Trim()

                If trimmed = DirectiveBegin OrElse trimmed = DirectiveEnd OrElse trimmed = DirectiveEndOfFile Then
                    Continue For
                End If

                If trimmed.StartsWith("*** ") Then
                    ' "*** Move to:" extends the update block that is open right now
                    If trimmed.StartsWith(DirectiveMove) Then
                        If current Is Nothing OrElse current.Kind <> PatchKind.Update Then
                            failure = $"Error: '{DirectiveMove}' must follow an '{DirectiveUpdate}' line."
                            Return Nothing
                        End If

                        current.MoveTo = trimmed.Substring(DirectiveMove.Length).Trim()

                        If String.IsNullOrWhiteSpace(current.MoveTo) Then
                            failure = $"Error: '{trimmed}' is missing the destination path."
                            Return Nothing
                        End If

                        Continue For
                    End If

                    ' a new file block closes the previous one
                    If current IsNot Nothing Then
                        If Not Finalise(current, buffer, newline, failure) Then
                            Return Nothing
                        End If

                        blocks.Add(current)
                        buffer = New List(Of String)()
                    End If

                    current = New PatchBlock()

                    If trimmed.StartsWith(DirectiveAdd) Then
                        current.Kind = PatchKind.Add
                        current.Path = trimmed.Substring(DirectiveAdd.Length).Trim()
                    ElseIf trimmed.StartsWith(DirectiveUpdate) Then
                        current.Kind = PatchKind.Update
                        current.Path = trimmed.Substring(DirectiveUpdate.Length).Trim()
                    ElseIf trimmed.StartsWith(DirectiveDelete) Then
                        current.Kind = PatchKind.Delete
                        current.Path = trimmed.Substring(DirectiveDelete.Length).Trim()
                    Else
                        failure = $"Error: unknown patch directive '{trimmed}'."
                        Return Nothing
                    End If

                    If String.IsNullOrWhiteSpace(current.Path) Then
                        failure = $"Error: '{trimmed}' is missing the file path."
                        Return Nothing
                    End If

                    Continue For
                End If

                If trimmed.StartsWith("***") Then
                    failure = $"Error: unknown patch directive '{trimmed}'."
                    Return Nothing
                End If

                If current Is Nothing Then
                    failure = "Error: the patch holds text outside a file block. Start each block with ""*** Add File:"", ""*** Update File:"" or ""*** Delete File:""."
                    Return Nothing
                End If

                buffer.Add(text)
            Next

            If current IsNot Nothing Then
                If Not Finalise(current, buffer, newline, failure) Then
                    Return Nothing
                End If

                blocks.Add(current)
            End If

            If blocks.Count = 0 Then
                failure = "Error: the patch holds no ""*** Add File:"", ""*** Update File:"" or ""*** Delete File:"" block."
                Return Nothing
            End If

            Return blocks
        End Function

        ''' <summary>
        ''' Turn the raw lines of one block into its hunk list, or into the
        ''' content of a file being added.
        ''' </summary>
        Private Shared Function Finalise(block As PatchBlock, buffer As List(Of String), newline As String, ByRef failure As String) As Boolean
            ' trailing blank lines separate the blocks, they are not content
            While buffer.Count > 0 AndAlso String.IsNullOrWhiteSpace(buffer(buffer.Count - 1))
                buffer.RemoveAt(buffer.Count - 1)
            End While

            Select Case block.Kind
                Case PatchKind.Add
                    Return FinaliseAddedFile(block, buffer, newline, failure)

                Case PatchKind.Update
                    Return FinaliseHunks(block, buffer, failure)

                Case Else
                    Return True
            End Select
        End Function

        Private Shared Function FinaliseAddedFile(block As PatchBlock, buffer As List(Of String), newline As String, ByRef failure As String) As Boolean
            Dim marked As Boolean = False

            For Each text As String In buffer
                If text.StartsWith("+") Then
                    marked = True
                    Exit For
                End If
            Next

            Dim content As New List(Of String)()

            For Each text As String In buffer
                If marked Then
                    If text.StartsWith("+") Then
                        content.Add(text.Substring(1))
                    ElseIf text.Trim().Length = 0 Then
                        content.Add("")
                    Else
                        failure = $"Error: '{block.Path}' is an added file, so every line must start with '+'. Offending line: {text}"
                        Return False
                    End If
                Else
                    content.Add(text)
                End If
            Next

            block.Content = String.Join(newline, content.ToArray())

            Return True
        End Function

        Private Shared Function FinaliseHunks(block As PatchBlock, buffer As List(Of String), ByRef failure As String) As Boolean
            Dim hunks As New List(Of Hunk)()
            Dim current As Hunk = Nothing

            For Each text As String In buffer
                Dim trimmed As String = text.Trim()

                If trimmed.StartsWith("@@") Then
                    current = New Hunk With {.Anchor = trimmed.Substring(2).Trim()}
                    hunks.Add(current)
                    Continue For
                End If

                If text.Length = 0 Then
                    ' an empty line is an empty context line
                    If current Is Nothing Then
                        current = New Hunk()
                        hunks.Add(current)
                    End If

                    current.Lines.Add(New HunkLine With {.Kind = " "c, .Text = ""})
                    Continue For
                End If

                Dim kind As Char = text(0)

                If kind <> " "c AndAlso kind <> "-"c AndAlso kind <> "+"c Then
                    failure = $"Error: every line of '*** Update File: {block.Path}' must start with a space, '-' or '+'. Offending line: {text}"
                    Return False
                End If

                If current Is Nothing Then
                    current = New Hunk()
                    hunks.Add(current)
                End If

                current.Lines.Add(New HunkLine With {.Kind = kind, .Text = text.Substring(1)})
            Next

            block.Hunks = hunks

            Return True
        End Function

        ' ====================================================================
        ' Model
        ' ====================================================================

        Private Enum PatchKind
            Add
            Update
            Delete
        End Enum

        ''' <summary>One "*** ... File:" block of the patch.</summary>
        Private NotInheritable Class PatchBlock

            ''' <summary>What the block does.</summary>
            Public Property Kind As PatchKind

            ''' <summary>The path exactly as the patch writes it.</summary>
            Public Property Path As String

            ''' <summary>The absolute path the block was resolved to.</summary>
            Public Property ResolvedPath As String

            ''' <summary>Rename destination of an update block, or Nothing.</summary>
            Public Property MoveTo As String

            ''' <summary>The absolute rename destination.</summary>
            Public Property ResolvedMovePath As String

            ''' <summary>The content of an added file.</summary>
            Public Property Content As String

            ''' <summary>The hunks of an update block.</summary>
            Public Property Hunks As List(Of Hunk) = New List(Of Hunk)()

            ''' <summary>The text to write for an update block.</summary>
            Public Property Updated As String

            ''' <summary>Whether the updated file carried a byte order mark.</summary>
            Public Property HadBom As Boolean

        End Class

        ''' <summary>One "@@" delimited hunk of an update block.</summary>
        Private NotInheritable Class Hunk

            ''' <summary>The anchor text written after "@@", used for reports only.</summary>
            Public Property Anchor As String

            ''' <summary>The context, removed and added lines of the hunk.</summary>
            Public Property Lines As New List(Of HunkLine)()

        End Class

        ''' <summary>One line of a hunk.</summary>
        Private NotInheritable Class HunkLine

            ''' <summary>" " for context, "-" for removed, "+" for added.</summary>
            Public Property Kind As Char

            ''' <summary>The line content, without its marker character.</summary>
            Public Property Text As String

        End Class

        ''' <summary>The outcome of the parse and verify phase.</summary>
        Private NotInheritable Class PatchValidation

            ''' <summary>The verified blocks, or Nothing when the patch was refused.</summary>
            Public Property Blocks As List(Of PatchBlock)

            ''' <summary>A plain text message describing why the patch was refused.</summary>
            Public Property Failure As String

            ''' <summary>The denial message of the first path the policy refused.</summary>
            Public Property Denied As String

            ''' <summary>The resolved path the denial is about.</summary>
            Public Property DeniedTarget As String

        End Class

    End Class

End Namespace
