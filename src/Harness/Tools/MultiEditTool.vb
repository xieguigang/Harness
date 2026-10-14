Imports System.ComponentModel
Imports System.IO
Imports Microsoft.VisualBasic.CommandLine.Reflection
Imports Microsoft.VisualBasic.MIME.application.json.Javascript
Imports Microsoft.VisualBasic.MIME.application.json.LenientJson

Namespace Tools

    ''' <summary>
    ''' Tool "multi_edit": apply several literal replacements to one UTF-8 text
    ''' file in a single atomic step.
    ''' 
    ''' The tool holds a reference to the <see cref="Sandbox"/> that owns it,
    ''' so the path is resolved and the write permission is checked against
    ''' the workspace and the security policy of that sandbox.
    ''' 
    ''' Unlike a sequence of "edit" calls, nothing is written until every
    ''' replacement has been located, so a failure in the middle of a rename
    ''' cannot leave the file half edited.
    ''' </summary>
    Public Class MultiEditTool

        ''' <summary>Files larger than this are refused instead of loaded into memory.</summary>
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
        ''' Apply several literal replacements to one UTF-8 text file, all or
        ''' nothing.
        ''' </summary>
        ''' <param name="file_path">Path to edit, resolved by the filesystem backend.</param>
        ''' <param name="edits">A JSON array of {old_string, new_string, replace_all} objects, passed as a JSON encoded string.</param>
        ''' <param name="sandbox_permissions">The narrowest wider sandbox mode for a one-shot retry of the exact operation the sandbox just denied.</param>
        ''' <param name="justification">Required with sandbox_permissions: one sentence for the user explaining why this exact file operation needs the wider access.</param>
        <Description("Apply several literal replacements to one UTF-8 text file in a single all-or-nothing step. The edits are applied in the given order to the text produced by the previous ones, so a later edit may match text an earlier one inserted. Each edit must be located; if any of them is not found (or matches more than once without replace_all) nothing is written to disk and the failing edit is reported.")>
        <ExportAPI("multi_edit")>
        Public Function multi_edit(
            <Argument("file_path", Description:="Path to edit, resolved by the filesystem backend.")> file_path As String,
            <Argument("edits", Description:="A JSON array of replacement objects, given as a JSON encoded string, for example ""[{\""old_string\"":\""foo\"",\""new_string\"":\""bar\""},{\""old_string\"":\""x\"",\""new_string\"":\""y\"",\""replace_all\"":true}]"". old_string is the literal text to find, new_string the literal replacement (use an empty string to delete the match) and replace_all (default false) replaces every occurrence instead of requiring exactly one. One object may also be given without the surrounding array.")> edits As String,
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

                Dim escalated As String = _sandbox.AuthorizeEscalation(SandboxOperation.Write, fullPath, sandbox_permissions, justification)

                If escalated IsNot Nothing Then
                    Return escalated
                End If
            End If

            If Not File.Exists(fullPath) Then
                Return $"Error: File not found: {fullPath}"
            End If

            Dim failure As String = Nothing
            Dim specs As List(Of EditSpec) = ParseEdits(edits, failure)

            If specs Is Nothing Then
                Return failure
            End If

            ' Reject the edits that cannot be applied at all before reading the
            ' file, so that the report is about the request, not about the file.
            For i As Integer = 0 To specs.Count - 1
                Dim spec As EditSpec = specs(i)

                If String.IsNullOrEmpty(spec.OldText) Then
                    Return $"Error: edit {i + 1} of {specs.Count} has an empty 'old_string'."
                End If

                If spec.OldText = spec.NewText Then
                    Return $"Error: edit {i + 1} of {specs.Count} has identical 'old_string' and 'new_string', so it would not change the file."
                End If
            Next

            Dim info As New FileInfo(fullPath)

            If info.Length > MaxFileBytes Then
                Return $"Error: '{fullPath}' is larger than 32 MB, which is too large to edit safely with this tool."
            End If

            Dim isBom As Boolean

            Try
                isBom = TextFileUtils.HasUtf8Bom(fullPath)
            Catch ex As Exception
                Return $"Error reading file '{fullPath}': {ex.Message}"
            End Try

            Dim original As String

            Try
                original = TextFileUtils.ReadAllText(fullPath)
            Catch ex As Exception
                Return $"Error reading file '{fullPath}': {ex.Message}"
            End Try

            Dim updated As String = original

            For i As Integer = 0 To specs.Count - 1
                Dim spec As EditSpec = specs(i)
                Dim position As Integer = i + 1

                ' Prefer the literal text exactly as given, and only fall back
                ' to the newline normalised variant when the file on disk uses
                ' a different line ending than the model wrote.
                Dim needle As String = spec.OldText
                Dim replacement As String = spec.NewText

                If Not updated.Contains(spec.OldText) Then
                    needle = TextFileUtils.ResolveNeedle(updated, spec.OldText)

                    If needle Is Nothing Then
                        Return $"Error: nothing was written to {fullPath}: edit {position} of {specs.Count} did not match: 'old_string' was not found."
                    End If

                    replacement = TextFileUtils.NormaliseNewlines(spec.NewText, TextFileUtils.DetectNewline(updated))
                End If

                Dim count As Integer = TextFileUtils.CountOccurrences(updated, needle)

                If count > 1 AndAlso Not spec.ReplaceAll Then
                    Return $"Error: nothing was written to {fullPath}: edit {position} of {specs.Count} matches {count} times. " &
                           "Provide more surrounding context to identify a single match, or set replace_all to true."
                End If

                If spec.ReplaceAll Then
                    updated = updated.Replace(needle, replacement)
                Else
                    Dim at As Integer = updated.IndexOf(needle, StringComparison.Ordinal)
                    updated = updated.Substring(0, at) & replacement & updated.Substring(at + needle.Length)
                End If
            Next

            Try
                TextFileUtils.WriteAllText(fullPath, updated, isBom)
            Catch ex As Exception
                Return $"Error writing file '{fullPath}': {ex.Message}"
            End Try

            Return $"The file {fullPath} has been updated successfully. {specs.Count} edit(s) applied."
        End Function

        ''' <summary>
        ''' Parse the replacement list the model sent. The value is a JSON
        ''' encoded string because the tool bridge only carries string
        ''' arguments; a single object is accepted as a one entry list, and a
        ''' double encoded JSON string is unwrapped.
        ''' </summary>
        ''' <param name="json">The raw JSON text of the replacement list.</param>
        ''' <param name="failure">Receives the message to return when parsing fails.</param>
        Private Shared Function ParseEdits(json As String, ByRef failure As String) As List(Of EditSpec)
            If String.IsNullOrWhiteSpace(json) Then
                failure = "Error: 'edits' is empty. Pass a JSON array of replacement objects such as " &
                          "[{""old_string"":""a"",""new_string"":""b""}]."
                Return Nothing
            End If

            Dim root As JsonElement

            Try
                root = LenientJsonParser.ParseJSON(json)
            Catch ex As Exception
                failure = $"Error: 'edits' is not valid JSON: {ex.Message}"
                Return Nothing
            End Try

            ' the model sometimes sends the array as a JSON string
            If TypeOf root Is JsonValue Then
                Dim inner As Object = DirectCast(root, JsonValue).Literal()

                If TypeOf inner Is String AndAlso Not String.IsNullOrWhiteSpace(CStr(inner)) Then
                    Try
                        root = LenientJsonParser.ParseJSON(CStr(inner))
                    Catch ex As Exception
                        failure = $"Error: 'edits' is not valid JSON: {ex.Message}"
                        Return Nothing
                    End Try
                End If
            End If

            Dim items As New List(Of JsonObject)()

            If TypeOf root Is JsonObject Then
                items.Add(DirectCast(root, JsonObject))
            ElseIf TypeOf root Is JsonArray Then
                For Each element As JsonElement In DirectCast(root, JsonArray)
                    Dim item As JsonObject = TryCast(element, JsonObject)

                    If item Is Nothing Then
                        failure = "Error: every entry of 'edits' must be a JSON object with 'old_string' and 'new_string'."
                        Return Nothing
                    End If

                    items.Add(item)
                Next
            Else
                failure = "Error: 'edits' must be a JSON array of replacement objects such as " &
                          "[{""old_string"":""a"",""new_string"":""b""}], given as a JSON encoded string."
                Return Nothing
            End If

            If items.Count = 0 Then
                failure = "Error: 'edits' contains no replacement."
                Return Nothing
            End If

            Dim specs As New List(Of EditSpec)()

            For i As Integer = 0 To items.Count - 1
                Dim item As JsonObject = items(i)
                Dim oldText As String = Member(item, "old_string", "oldString", "old_text", "oldText", "search")
                Dim newText As String = Member(item, "new_string", "newString", "new_text", "newText", "replacement")

                If oldText Is Nothing Then
                    failure = $"Error: edit {i + 1} is missing its 'old_string' member."
                    Return Nothing
                End If

                If newText Is Nothing Then
                    failure = $"Error: edit {i + 1} is missing its 'new_string' member. Use an empty string to delete the match."
                    Return Nothing
                End If

                specs.Add(New EditSpec With {
                    .OldText = oldText,
                    .NewText = newText,
                    .ReplaceAll = FlagMember(item, "replace_all", "replaceAll", "all")
                })
            Next

            Return specs
        End Function

        ''' <summary>
        ''' Read a string member, accepting the usual spellings a model may
        ''' use for it. Returns Nothing when no candidate is present.
        ''' </summary>
        Private Shared Function Member(item As JsonObject, ParamArray names As String()) As String
            For Each name As String In names
                If item.HasObjectKey(name) Then
                    Return item.GetString(name)
                End If
            Next

            Return Nothing
        End Function

        ''' <summary>
        ''' Read a boolean member, tolerating both a JSON boolean and the
        ''' strings "true"/"false".
        ''' </summary>
        Private Shared Function FlagMember(item As JsonObject, ParamArray names As String()) As Boolean
            Dim text As String = Member(item, names)

            If String.IsNullOrEmpty(text) Then
                Return False
            End If

            Dim flag As Boolean

            If Boolean.TryParse(text, flag) Then
                Return flag
            End If

            Return False
        End Function

        ''' <summary>
        ''' One replacement of a "multi_edit" call.
        ''' </summary>
        Private NotInheritable Class EditSpec

            ''' <summary>The literal text to find.</summary>
            Public Property OldText As String

            ''' <summary>The literal replacement text.</summary>
            Public Property NewText As String

            ''' <summary>Replace every occurrence instead of requiring exactly one.</summary>
            Public Property ReplaceAll As Boolean

        End Class

    End Class

End Namespace
