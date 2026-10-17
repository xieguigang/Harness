Imports System.Collections.Generic
Imports System.IO
Imports System.Text.Json

Namespace Logs

    ''' <summary>
    ''' one log file of a session, with its size in bytes.
    ''' </summary>
    Public Class LogFileInfo

        ''' <summary>the file name inside the session folder, e.g. tool-calls.json.</summary>
        Public Property Name As String

        ''' <summary>size of the file in bytes.</summary>
        Public Property Size As Long

    End Class

    ''' <summary>
    ''' one session directory found under <c>&lt;log folder&gt;\toolcalls</c> or
    ''' <c>&lt;log folder&gt;\llm-sessions</c>.
    ''' </summary>
    Public Class LogSessionInfo

        ''' <summary>"toolcalls" for the harness tool call log, "llm-sessions" for the chat log.</summary>
        Public Property Kind As String

        ''' <summary>the session id, which is also the name of the folder.</summary>
        Public Property SessionId As String

        ''' <summary>the absolute path of the session folder.</summary>
        Public Property Folder As String

        ''' <summary>when the newest file of the session was written.</summary>
        Public Property LastWrite As DateTime

        ''' <summary>the json files of the session with their sizes.</summary>
        Public Property Files As List(Of LogFileInfo)

        ''' <summary>why the session could not be read, when it could not.</summary>
        Public Property [Error] As String

    End Class

    ''' <summary>
    ''' scans a harness log folder (the <c>.harness</c> folder of a workspace)
    ''' for the two kinds of session it holds, and keeps the parsed json
    ''' documents of the session files around so that paging through a 5MB
    ''' turns.json does not parse it again on every page.
    ''' </summary>
    Public Class HarnessLogIndex

        ''' <summary>the folder of the tool call log sessions.</summary>
        Public Const KindToolCalls As String = "toolcalls"
        ''' <summary>the folder of the chat log sessions.</summary>
        Public Const KindLlmSessions As String = "llm-sessions"

        ''' <summary>the json array files of a session that <see cref="HarnessLogReader.Records"/> can page.</summary>
        Public Shared ReadOnly ArrayFiles As String() = {
            "tool-calls.json",
            "tools.json",
            "permissions.json",
            "turns.json",
            "usage.json",
            "messages.json"
        }

        Private ReadOnly _root As String
        Private ReadOnly _cache As New Dictionary(Of String, KeyValuePair(Of JsonDocument, Long))(StringComparer.OrdinalIgnoreCase)
        Private ReadOnly _order As New List(Of String)
        Private ReadOnly _sync As New Object
        Private _cacheBytes As Long
        Private _maxCacheBytes As Long = 48L * 1024 * 1024

        ''' <param name="root">the log folder, i.e. the <c>.harness</c> folder of a workspace. a blank folder reads nothing.</param>
        Public Sub New(root As String)
            If String.IsNullOrWhiteSpace(root) Then
                _root = ""
            Else
                _root = IO.Path.GetFullPath(root)
            End If
        End Sub

        ''' <summary>the log folder this index reads.</summary>
        Public ReadOnly Property Root As String
            Get
                Return _root
            End Get
        End Property

        ''' <summary>True when the log folder exists on disk.</summary>
        Public ReadOnly Property Exists As Boolean
            Get
                Return Directory.Exists(_root)
            End Get
        End Property

        ''' <summary>
        ''' how many parsed json bytes the document cache may hold before it
        ''' starts dropping the least recently used file. the turns.json of a
        ''' long agent session is several megabytes, so the default keeps a
        ''' handful of sessions alive without exhausting memory.
        ''' </summary>
        Public Property MaxCacheBytes As Long
            Get
                Return _maxCacheBytes
            End Get
            Set(value As Long)
                _maxCacheBytes = Math.Max(4L * 1024 * 1024, value)
            End Set
        End Property

        ''' <summary>how many parsed bytes the cache currently holds.</summary>
        Public ReadOnly Property CacheBytes As Long
            Get
                Return _cacheBytes
            End Get
        End Property

        ''' <summary>
        ''' find every session folder under the log root. a folder that cannot
        ''' be read is reported with its <see cref="LogSessionInfo.Error"/>
        ''' instead of breaking the whole scan.
        ''' </summary>
        Public Function Scan() As LogSessionInfo()
            If Not Exists Then
                Return New LogSessionInfo() {}
            End If

            Dim sessions As New List(Of LogSessionInfo)()

            For Each kind As String In {KindToolCalls, KindLlmSessions}
                Dim baseDir As String = IO.Path.Combine(_root, kind)

                If Not Directory.Exists(baseDir) Then
                    Continue For
                End If

                For Each folder As String In Directory.GetDirectories(baseDir)
                    sessions.Add(ReadSession(kind, folder))
                Next
            Next

            ' newest first: the session a reviewer wants is almost always the last one
            Return sessions _
                .OrderByDescending(Function(s) s.LastWrite) _
                .ThenBy(Function(s) s.SessionId, StringComparer.Ordinal) _
                .ToArray()
        End Function

        Private Shared Function ReadSession(kind As String, folder As String) As LogSessionInfo
            Dim info As New LogSessionInfo With {
                .Kind = kind,
                .SessionId = New DirectoryInfo(folder).Name,
                .Folder = folder,
                .Files = New List(Of LogFileInfo)()
            }

            Try
                Dim newest As DateTime = DateTime.MinValue

                For Each path As String In Directory.GetFiles(folder, "*.json")
                    Dim item As New FileInfo(path)
                    info.Files.Add(New LogFileInfo With {.Name = item.Name, .Size = item.Length})

                    If item.LastWriteTime > newest Then
                        newest = item.LastWriteTime
                    End If
                Next

                info.Files.Sort(Function(a, b) String.Compare(a.Name, b.Name, StringComparison.Ordinal))
                info.LastWrite = newest
            Catch ex As Exception
                info.Error = ex.Message
            End Try

            Return info
        End Function

        ''' <summary>
        ''' the folder of one session, or Nothing when the id is unknown. the
        ''' id is only ever a directory name, so a hostile value cannot escape
        ''' the log root.
        ''' </summary>
        Public Function SessionFolder(kind As String, sessionId As String) As String
            If String.IsNullOrWhiteSpace(sessionId) Then
                Return Nothing
            End If

            If sessionId.IndexOfAny({IO.Path.DirectorySeparatorChar, IO.Path.AltDirectorySeparatorChar}) >= 0 Then
                Return Nothing
            End If

            Dim folder As String = IO.Path.Combine(IO.Path.Combine(_root, kind), sessionId)
            Dim baseDir As String = IO.Path.GetFullPath(IO.Path.Combine(_root, kind))

            If Not IO.Path.GetFullPath(folder).StartsWith(baseDir & IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) Then
                Return Nothing
            End If

            If Not Directory.Exists(folder) Then
                Return Nothing
            End If

            Return folder
        End Function

        ''' <summary>
        ''' the text log files of the log root (the <c>rscript</c> folder holds
        ''' the plain text logs of a background job), as relative names.
        ''' </summary>
        Public Function TextFiles() As LogFileInfo()
            If Not Exists Then
                Return New LogFileInfo() {}
            End If

            Dim files As New List(Of LogFileInfo)()

            Try
                For Each path As String In Directory.GetFiles(_root, "*.log", SearchOption.AllDirectories)
                    Dim item As New FileInfo(path)
                    Dim relative As String = path.Substring(_root.Length).TrimStart(IO.Path.DirectorySeparatorChar)

                    files.Add(New LogFileInfo With {.Name = relative, .Size = item.Length})
                Next
            Catch
                ' an unreadable sub folder must not break the file list
            End Try

            files.Sort(Function(a, b) String.Compare(a.Name, b.Name, StringComparison.Ordinal))

            Return files.ToArray()
        End Function

        ''' <summary>
        ''' resolve a relative name inside the log root to a full path,
        ''' refusing anything that would leave the root.
        ''' </summary>
        Public Function ResolveTextFile(relative As String) As String
            If String.IsNullOrWhiteSpace(relative) Then
                Return Nothing
            End If

            Dim norm As String = relative.Replace("/"c, IO.Path.DirectorySeparatorChar).Replace("\"c, IO.Path.DirectorySeparatorChar)

            If norm.Contains("..") OrElse IO.Path.IsPathRooted(norm) Then
                Return Nothing
            End If

            Dim full As String = IO.Path.GetFullPath(IO.Path.Combine(_root, norm))

            If Not full.StartsWith(_root & IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) Then
                Return Nothing
            End If

            If Not File.Exists(full) Then
                Return Nothing
            End If

            Return full
        End Function

        ''' <summary>
        ''' get the parsed json document of a file, parsing it once and keeping
        ''' it until the cache runs out of bytes.
        ''' </summary>
        ''' <param name="path">the absolute path of the json file.</param>
        Public Function Document(path As String) As JsonDocument
            If String.IsNullOrEmpty(path) Then
                Return Nothing
            End If

            Dim key As String = IO.Path.GetFullPath(path)

            SyncLock _sync
                Dim hit As New KeyValuePair(Of JsonDocument, Long)(Nothing, 0L)

                If _cache.TryGetValue(key, hit) Then
                    ' refresh the lru position
                    _order.Remove(key)
                    _order.Add(key)

                    Return hit.Key
                End If
            End SyncLock

            If Not File.Exists(path) Then
                Return Nothing
            End If

            Dim parsed As JsonDocument = Nothing

            Try
                Using stream As FileStream = File.OpenRead(path)
                    parsed = JsonDocument.Parse(stream)
                End Using
            Catch ex As Exception
                Return Nothing
            End Try

            Dim bytes As Long = New FileInfo(path).Length

            SyncLock _sync
                Dim existing As New KeyValuePair(Of JsonDocument, Long)(Nothing, 0L)

                If _cache.TryGetValue(key, existing) Then
                    ' another thread got there first
                    parsed.Dispose()
                    Return existing.Key
                End If

                _cache(key) = New KeyValuePair(Of JsonDocument, Long)(parsed, bytes)
                _order.Add(key)
                _cacheBytes += bytes

                While _cacheBytes > _maxCacheBytes AndAlso _order.Count > 0
                    Dim oldest As String = _order(0)
                    _order.RemoveAt(0)

                    Dim evicted As New KeyValuePair(Of JsonDocument, Long)(Nothing, 0L)

                    If _cache.TryGetValue(oldest, evicted) Then
                        _cache.Remove(oldest)
                        _cacheBytes -= evicted.Value
                        evicted.Key.Dispose()
                    End If
                End While
            End SyncLock

            Return parsed
        End Function

        ''' <summary>drop every cached document.</summary>
        Public Sub ClearCache()
            SyncLock _sync
                For Each pair In _cache.Values
                    pair.Key.Dispose()
                Next

                _cache.Clear()
                _order.Clear()
                _cacheBytes = 0
            End SyncLock
        End Sub

    End Class

End Namespace
