Imports System.ComponentModel
Imports System.IO
Imports Microsoft.VisualBasic.CommandLine.Reflection

Namespace Tools

    ''' <summary>
    ''' Tool "edit": edit an existing UTF-8 text file by replacing literal
    ''' text.
    ''' 
    ''' The tool holds a reference to the <see cref="Sandbox"/> that owns it,
    ''' so the path is resolved and the write permission is checked against
    ''' the workspace and the security policy of that sandbox.
    ''' </summary>
    Public Class EditTool

        Private ReadOnly _sandbox As Sandbox

        ''' <param name="sandbox">The sandbox that resolves the paths and enforces the security policy.</param>
        Public Sub New(sandbox As Sandbox)
            If sandbox Is Nothing Then
                Throw New ArgumentNullException(NameOf(sandbox))
            End If

            _sandbox = sandbox
        End Sub

        ''' <summary>
        ''' Edit an existing UTF-8 text file by replacing literal text.
        ''' </summary>
        ''' <param name="file_path">Path to edit, resolved by the filesystem backend.</param>
        ''' <param name="old_string">Literal text to replace.</param>
        ''' <param name="new_string">Literal replacement text. Use an empty string to delete the match.</param>
        ''' <param name="replace_all">Replace all matches. Defaults to false; when false, old_string must appear exactly once.</param>
        <Description("Edit an existing UTF-8 text file by replacing literal text.")>
        <ExportAPI("edit")>
        Public Function edit(
            <Argument("file_path", Description:="Path to edit, resolved by the filesystem backend.")> file_path As String,
            <Argument("old_string", Description:="Literal text to replace.")> old_string As String,
            <Argument("new_string", Description:="Literal replacement text. Use an empty string to delete the match.")> new_string As String,
            <Argument("replace_all", Description:="Replace all matches. Defaults to false; when false, old_string must appear exactly once.")> Optional replace_all As Boolean = False
        ) As String
            If String.IsNullOrEmpty(old_string) Then
                Return "Error: old_string must not be empty."
            End If

            If old_string = new_string Then
                Return "Error: old_string and new_string are identical, so the edit would not change the file."
            End If

            Dim fullPath As String = Nothing
            Dim denied As String = _sandbox.ResolveAndAuthorize(file_path, SandboxOperation.Write, fullPath)

            If denied IsNot Nothing Then
                Return denied
            End If

            If Not File.Exists(fullPath) Then
                Return $"Error: File not found: {fullPath}"
            End If

            Dim isBom As Boolean

            Try
                isBom = TextFileUtils.HasUtf8Bom(fullPath)
            Catch ex As Exception
                Return $"Error reading file '{fullPath}': {ex.Message}"
            End Try

            Dim text As String

            Try
                text = TextFileUtils.ReadAllText(fullPath)
            Catch ex As Exception
                Return $"Error reading file '{fullPath}': {ex.Message}"
            End Try

            ' Prefer the literal text exactly as given. Only when that does
            ' not occur - typically because the model wrote "\n" while the
            ' file on disk uses CRLF - fall back to the newline normalised
            ' variants and keep the replacement in the file's own style.
            Dim needle As String = old_string
            Dim replacement As String = new_string

            If Not text.Contains(old_string) Then
                needle = TextFileUtils.ResolveNeedle(text, old_string)

                If needle Is Nothing Then
                    Return $"Error: old_string was not found in {fullPath}."
                End If

                replacement = TextFileUtils.NormaliseNewlines(new_string, TextFileUtils.DetectNewline(text))
            End If

            Dim count As Integer = TextFileUtils.CountOccurrences(text, needle)

            If count > 1 AndAlso Not replace_all Then
                Return $"Error: old_string occurs {count} times in {fullPath}. " &
                       "Provide more surrounding context to identify a single match, or set replace_all to true."
            End If

            Dim updated As String

            If replace_all Then
                updated = text.Replace(needle, replacement)
            Else
                Dim index As Integer = text.IndexOf(needle, StringComparison.Ordinal)
                updated = text.Substring(0, index) & replacement & text.Substring(index + needle.Length)
            End If

            Try
                TextFileUtils.WriteAllText(fullPath, updated, isBom)
            Catch ex As Exception
                Return $"Error writing file '{fullPath}': {ex.Message}"
            End Try

            Return $"The file {fullPath} has been updated successfully."
        End Function

    End Class

End Namespace
