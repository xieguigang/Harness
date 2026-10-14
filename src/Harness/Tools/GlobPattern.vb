Imports System.Text
Imports System.Text.RegularExpressions

Namespace Tools

    ''' <summary>
    ''' A glob pattern translated into a regular expression, shared by the
    ''' tools that need path filtering (the grep "include" filter and the
    ''' glob search pattern).
    ''' 
    ''' Supported syntax: "*", "?", "**" and "{a,b}" alternation. Negation is
    ''' deliberately not supported.
    ''' 
    ''' Anchoring follows the ripgrep convention: a pattern without a path
    ''' separator is matched against the file name at any depth, so "*.vb"
    ''' searches the whole tree; a pattern that contains a separator is
    ''' matched against the whole relative path it is handed, which anchors
    ''' the depth.
    ''' </summary>
    Friend NotInheritable Class GlobPattern

        Private ReadOnly _patterns As List(Of Regex)
        Private ReadOnly _onPath As List(Of Boolean)

        Private Sub New()
            _patterns = New List(Of Regex)()
            _onPath = New List(Of Boolean)()
        End Sub

        ''' <summary>
        ''' Build a matcher, or Nothing when <paramref name="glob"/> is empty
        ''' (meaning "no filter" for the callers).
        ''' </summary>
        Public Shared Function Create(glob As String) As GlobPattern
            If String.IsNullOrWhiteSpace(glob) Then
                Return Nothing
            End If

            Dim matcher As New GlobPattern()

            For Each token As String In ExpandBraces(glob.Trim().Replace("\"c, "/"c))
                Dim value As String = token.Trim()

                If value.Length = 0 Then
                    Continue For
                End If

                matcher._patterns.Add(New Regex("^" & ToRegex(value) & "$", RegexOptions.IgnoreCase Or RegexOptions.CultureInvariant))
                matcher._onPath.Add(value.Contains("/"))
            Next

            If matcher._patterns.Count = 0 Then
                Return Nothing
            End If

            Return matcher
        End Function

        ''' <summary>
        ''' Match a relative path, which may use either path separator.
        ''' </summary>
        Public Function IsMatch(relativePath As String) As Boolean
            Dim path As String = relativePath.Replace("\"c, "/"c)
            Dim leaf As String = path.Substring(path.LastIndexOf("/"c) + 1)

            For i As Integer = 0 To _patterns.Count - 1
                If _onPath(i) Then
                    If _patterns(i).IsMatch(path) Then
                        Return True
                    End If
                ElseIf _patterns(i).IsMatch(leaf) Then
                    Return True
                End If
            Next

            Return False
        End Function

        Private Shared Function ExpandBraces(glob As String) As String()
            Dim open As Integer = glob.IndexOf("{"c)

            If open < 0 Then
                Return {glob}
            End If

            Dim close As Integer = glob.IndexOf("}"c, open)

            If close < 0 Then
                Return {glob}
            End If

            Dim prefix As String = glob.Substring(0, open)
            Dim suffix As String = glob.Substring(close + 1)
            Dim list As New List(Of String)()

            For Each part As String In glob.Substring(open + 1, close - open - 1).Split(","c)
                For Each branch As String In ExpandBraces(prefix & part.Trim() & suffix)
                    list.Add(branch)
                Next
            Next

            Return list.ToArray()
        End Function

        Private Shared Function ToRegex(glob As String) As String
            Dim sb As New StringBuilder()
            Dim i As Integer = 0

            While i < glob.Length
                Dim c As Char = glob(i)

                Select Case c
                    Case "*"c
                        If i + 1 < glob.Length AndAlso glob(i + 1) = "*"c Then
                            ' "**/" spans zero or more directories, so that
                            ' "src/**/*.vb" also matches "src/a.vb". A bare
                            ' "**" spans anything at all.
                            If i + 2 < glob.Length AndAlso glob(i + 2) = "/"c Then
                                sb.Append("(?:.*/)?")
                                i += 2
                            Else
                                sb.Append(".*")
                                i += 1
                            End If
                        Else
                            sb.Append("[^/]*")
                        End If
                    Case "?"c
                        sb.Append("[^/]")
                    Case Else
                        sb.Append(Regex.Escape(c.ToString()))
                End Select

                i += 1
            End While

            Return sb.ToString()
        End Function

    End Class

End Namespace
