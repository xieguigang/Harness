Imports System.IO
Imports System.Text

Namespace Tools

    ''' <summary>
    ''' Shared text file helpers for the tools that read and rewrite source
    ''' files: the encoding shape (UTF-8 with or without byte order mark), the
    ''' line ending style, and the literal search that tolerates a mismatch
    ''' between the line endings the model wrote and the ones the file on disk
    ''' actually uses.
    ''' 
    ''' This sits next to the tools instead of inside one of them because
    ''' "edit", "write", "multi_edit" and "apply_patch" all need exactly the
    ''' same behaviour: a rewritten file must keep its byte order mark and its
    ''' own line ending style.
    ''' </summary>
    Friend Module TextFileUtils

        ''' <summary>
        ''' True when the file starts with a UTF-8 byte order mark, so that
        ''' rewriting it keeps the same encoding shape.
        ''' </summary>
        Friend Function HasUtf8Bom(target As String) As Boolean
            Using stream As FileStream = File.OpenRead(target)
                Dim head(2) As Byte

                If stream.Read(head, 0, 3) < 3 Then
                    Return False
                End If

                Return head(0) = &HEF AndAlso head(1) = &HBB AndAlso head(2) = &HBF
            End Using
        End Function

        ''' <summary>
        ''' Read a UTF-8 text file. A byte order mark, if present, is decoded
        ''' away instead of ending up inside the returned text.
        ''' </summary>
        Friend Function ReadAllText(target As String) As String
            Return File.ReadAllText(target, Encoding.UTF8)
        End Function

        ''' <summary>
        ''' Write a text file back, keeping a byte order mark only when the
        ''' file that is being replaced carried one. A brand new file is
        ''' written without a mark.
        ''' </summary>
        Friend Sub WriteAllText(target As String, text As String)
            Call WriteAllText(target, text, File.Exists(target) AndAlso HasUtf8Bom(target))
        End Sub

        ''' <summary>
        ''' Write a text file back with an explicit byte order mark decision,
        ''' for the callers that already inspected the original file.
        ''' </summary>
        Friend Sub WriteAllText(target As String, text As String, hadBom As Boolean)
            Call File.WriteAllText(target, If(text, ""), New UTF8Encoding(hadBom))
        End Sub

        ''' <summary>
        ''' The dominant line ending of a file, used to keep an inserted
        ''' replacement consistent with the rest of the document.
        ''' </summary>
        Friend Function DetectNewline(text As String) As String
            Dim crlf As Integer = CountOccurrences(text, vbCrLf)
            Dim lf As Integer = CountOccurrences(text, vbLf) - crlf

            Return If(crlf >= lf, vbCrLf, vbLf)
        End Function

        ''' <summary>
        ''' Rewrite every line break of <paramref name="text"/> to the given
        ''' line ending, first collapsing CRLF so that no CR is doubled.
        ''' </summary>
        Friend Function NormaliseNewlines(text As String, newline As String) As String
            Return text.Replace(vbCrLf, vbLf).Replace(vbLf, newline)
        End Function

        ''' <summary>
        ''' Work out which literal text to look for. The bytes the model
        ''' writes may not use the same line endings as the file on disk, so
        ''' when the exact literal is absent its CRLF and LF normalised
        ''' variants are tried as well. Returns Nothing when nothing matches.
        ''' </summary>
        Friend Function ResolveNeedle(text As String, old_string As String) As String
            Dim normalized As String = NormaliseNewlines(old_string, vbLf)
            Dim candidates As String() = {NormaliseNewlines(normalized, vbCrLf), normalized}

            For Each candidate As String In candidates
                If candidate <> old_string AndAlso text.Contains(candidate) Then
                    Return candidate
                End If
            Next

            Return Nothing
        End Function

        ''' <summary>
        ''' Number of non overlapping occurrences of <paramref name="needle"/>
        ''' in <paramref name="text"/>.
        ''' </summary>
        Friend Function CountOccurrences(text As String, needle As String) As Integer
            Dim count As Integer = 0
            Dim index As Integer = text.IndexOf(needle, StringComparison.Ordinal)

            While index >= 0
                count += 1
                index = text.IndexOf(needle, index + needle.Length, StringComparison.Ordinal)
            End While

            Return count
        End Function

    End Module

End Namespace
