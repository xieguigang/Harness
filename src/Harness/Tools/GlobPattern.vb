Imports System.Text
Imports System.Text.RegularExpressions

Namespace Tools

    ''' <summary>
    ''' A glob pattern - or a set of glob patterns - translated into regular
    ''' expressions, shared by the tools that need path filtering (the grep
    ''' "include" filter and the glob search pattern).
    ''' 
    ''' Supported syntax: "*", "?", "**" and "{a,b}" alternation.
    ''' 
    ''' A pattern string may hold several comma separated entries; an entry
    ''' that starts with "!" is an exclusion. A path matches when at least one
    ''' inclusion entry matches (or when there is no inclusion entry at all)
    ''' and no exclusion entry matches. Commas inside "{a,b}" braces belong to
    ''' the alternation and do not separate entries.
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
        Private ReadOnly _excludes As List(Of Regex)
        Private ReadOnly _excludeOnPath As List(Of Boolean)

        Private Sub New()
            _patterns = New List(Of Regex)()
            _onPath = New List(Of Boolean)()
            _excludes = New List(Of Regex)()
            _excludeOnPath = New List(Of Boolean)()
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

            For Each entry As String In SplitEntries(glob)
                Dim value As String = entry.Trim()

                If value.Length = 0 Then
                    Continue For
                End If

                Dim excluded As Boolean = value.StartsWith("!")

                If excluded Then
                    value = value.Substring(1).Trim()
                End If

                For Each token As String In ExpandBraces(value.Replace("\"c, "/"c))
                    Dim part As String = token.Trim()

                    If part.Length = 0 Then
                        Continue For
                    End If

                    Dim regex As New Regex("^" & ToRegex(part) & "$", RegexOptions.IgnoreCase Or RegexOptions.CultureInvariant)
                    Dim onPath As Boolean = part.Contains("/")

                    If excluded Then
                        matcher._excludes.Add(regex)
                        matcher._excludeOnPath.Add(onPath)
                    Else
                        matcher._patterns.Add(regex)
                        matcher._onPath.Add(onPath)
                    End If
                Next
            Next

            If matcher._patterns.Count = 0 AndAlso matcher._excludes.Count = 0 Then
                Return Nothing
            End If

            Return matcher
        End Function

        ''' <summary>
        ''' Match a relative path, which may use either path separator. The
        ''' exclusions win over the inclusions; a matcher that only carries
        ''' exclusions accepts everything else.
        ''' </summary>
        Public Function IsMatch(relativePath As String) As Boolean
            Dim path As String = relativePath.Replace("\"c, "/"c)
            Dim leaf As String = path.Substring(path.LastIndexOf("/"c) + 1)

            If _patterns.Count > 0 AndAlso Not MatchesAny(_patterns, _onPath, path, leaf) Then
                Return False
            End If

            Return Not MatchesAny(_excludes, _excludeOnPath, path, leaf)
        End Function

        ''' <summary>
        ''' Split a pattern string on the commas that separate entries, i.e.
        ''' the ones outside "{...}" alternation braces.
        ''' </summary>
        Private Shared Function SplitEntries(text As String) As List(Of String)
            Dim entries As New List(Of String)()
            Dim depth As Integer = 0
            Dim start As Integer = 0

            For i As Integer = 0 To text.Length - 1
                Select Case text(i)
                    Case "{"c
                        depth += 1
                    Case "}"c
                        If depth > 0 Then
                            depth -= 1
                        End If
                    Case ","c
                        If depth = 0 Then
                            entries.Add(text.Substring(start, i - start))
                            start = i + 1
                        End If
                End Select
            Next

            entries.Add(text.Substring(start))

            Return entries
        End Function

        Private Shared Function MatchesAny(patterns As List(Of Regex), onPath As List(Of Boolean), path As String, leaf As String) As Boolean
            For i As Integer = 0 To patterns.Count - 1
                If onPath(i) Then
                    If patterns(i).IsMatch(path) Then
                        Return True
                    End If
                ElseIf patterns(i).IsMatch(leaf) Then
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
