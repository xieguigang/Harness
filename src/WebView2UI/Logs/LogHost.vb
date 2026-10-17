Imports System.IO
Imports System.Runtime.InteropServices
Imports System.Text
Imports System.Text.Json

Namespace Logs

    ''' <summary>
    ''' the object the web page of <see cref="WebView2HarnessLogViewer"/> talks
    ''' to. it is the only way the page reaches the disk: every method takes and
    ''' returns a string, so nothing but json text crosses the com boundary.
    ''' 
    ''' every method answers with json and reports a failure as
    ''' <c>{"error": "..."}</c> instead of throwing, so a broken log shows up as
    ''' a message in the page rather than a white screen.
    ''' </summary>
    <ClassInterface(ClassInterfaceType.AutoDual)>
    <ComVisible(True)>
    Public Class LogHost

        Private ReadOnly _index As HarnessLogIndex
        Private ReadOnly _reader As HarnessLogReader

        ''' <param name="root">the harness log folder, i.e. the <c>.harness</c> folder of a workspace.</param>
        Public Sub New(root As String)
            _index = New HarnessLogIndex(root)
            _reader = New HarnessLogReader(_index)
        End Sub

        ''' <summary>the harness log folder this host reads.</summary>
        Public ReadOnly Property Root As String
            Get
                Return _index.Root
            End Get
        End Property

        ''' <summary>the index the sessions are read from.</summary>
        Public ReadOnly Property LogIndex As HarnessLogIndex
            Get
                Return _index
            End Get
        End Property

        ''' <summary>
        ''' every session of the log folder, newest first, with the files it
        ''' holds and the plain text logs of the folder.
        ''' </summary>
        Public Function ListSessions() As String
            Try
                Dim sessions As LogSessionInfo() = _index.Scan()
                Dim sb As New StringBuilder()

                sb.Append("{""root"": ").Append(JsonSerializer.Serialize(_index.Root))
                sb.Append(", ""exists"": ").Append(If(_index.Exists, "true", "false"))
                sb.Append(", ""sessions"": [")

                Dim first As Boolean = True

                For Each session In sessions
                    If Not first Then
                        sb.Append(", ")
                    End If

                    first = False

                    sb.Append("{""kind"": ").Append(JsonSerializer.Serialize(session.Kind))
                    sb.Append(", ""sessionId"": ").Append(JsonSerializer.Serialize(session.SessionId))
                    sb.Append(", ""folder"": ").Append(JsonSerializer.Serialize(session.Folder))
                    sb.Append(", ""lastWrite"": ").Append(JsonSerializer.Serialize(session.LastWrite.ToString("o")))
                    sb.Append(", ""error"": ").Append(JsonSerializer.Serialize(session.Error))
                    sb.Append(", ""files"": [")

                    Dim firstFile As Boolean = True

                    For Each file In session.Files
                        If Not firstFile Then
                            sb.Append(", ")
                        End If

                        firstFile = False
                        sb.Append("{""name"": ").Append(JsonSerializer.Serialize(file.Name))
                        sb.Append(", ""size"": ").Append(file.Size).Append("}")
                    Next

                    sb.Append("]")

                    Dim payloads As Integer = 0
                    Dim payloadDir As String = IO.Path.Combine(session.Folder, "payloads")

                    If Directory.Exists(payloadDir) Then
                        Try
                            payloads = Directory.GetFiles(payloadDir, "*.txt").Length
                        Catch
                            payloads = 0
                        End Try
                    End If

                    sb.Append(", ""payloads"": ").Append(payloads)
                    sb.Append("}")
                Next

                sb.Append("], ""textFiles"": [")

                Dim firstText As Boolean = True

                For Each file In _index.TextFiles()
                    If Not firstText Then
                        sb.Append(", ")
                    End If

                    firstText = False
                    sb.Append("{""name"": ").Append(JsonSerializer.Serialize(file.Name))
                    sb.Append(", ""size"": ").Append(file.Size).Append("}")
                Next

                sb.Append("]}")

                Return sb.ToString()
            Catch ex As Exception
                Return ErrorJson("cannot scan the log folder: " & ex.Message)
            End Try
        End Function

        ''' <summary>
        ''' open one session and describe it: its summary, the files it holds
        ''' and the session it is linked to.
        ''' </summary>
        ''' <param name="kind">"toolcalls" or "llm-sessions".</param>
        ''' <param name="sessionId">the id of the session, i.e. its folder name.</param>
        Public Function OpenSession(kind As String, sessionId As String) As String
            Try
                If Not _reader.Open(kind, sessionId) Then
                    Return ErrorJson($"the session '{sessionId}' is not in {kind}")
                End If

                Dim folder As String = _reader.Folder
                Dim summary As String = ReadSummary(folder)
                Dim sb As New StringBuilder()

                sb.Append("{""ok"": true")
                sb.Append(", ""kind"": ").Append(JsonSerializer.Serialize(kind))
                sb.Append(", ""sessionId"": ").Append(JsonSerializer.Serialize(sessionId))
                sb.Append(", ""folder"": ").Append(JsonSerializer.Serialize(folder))
                sb.Append(", ""summary"": ").Append(summary)
                sb.Append(", ""linked"": ").Append(LinkedSession(summary, kind))
                sb.Append(", ""files"": [")

                Dim first As Boolean = True

                For Each path As String In Directory.GetFiles(folder, "*.json")
                    Dim item As New FileInfo(path)

                    If Not first Then
                        sb.Append(", ")
                    End If

                    first = False
                    sb.Append("{""name"": ").Append(JsonSerializer.Serialize(item.Name))
                    sb.Append(", ""size"": ").Append(item.Length).Append("}")
                Next

                sb.Append("]}")

                Return sb.ToString()
            Catch ex As Exception
                Return ErrorJson("cannot open the session: " & ex.Message)
            End Try
        End Function

        ''' <summary>
        ''' page one array file of the open session.
        ''' </summary>
        ''' <param name="name">the file name, e.g. tool-calls.json.</param>
        ''' <param name="offset">the first record to return.</param>
        ''' <param name="limit">how many records to return at most.</param>
        ''' <param name="filterJson">the filter as a json object, or Nothing.</param>
        Public Function Records(name As String, offset As Integer, limit As Integer, filterJson As String) As String
            Try
                Return _reader.Records(name, offset, limit, filterJson)
            Catch ex As Exception
                Return ErrorJson("cannot read " & name & ": " & ex.Message)
            End Try
        End Function

        ''' <summary>one complete turn of the open session, with its rounds.</summary>
        Public Function Turn(turnSeq As Integer) As String
            Try
                Return _reader.Turn(turnSeq)
            Catch ex As Exception
                Return ErrorJson("cannot read turn " & turnSeq & ": " & ex.Message)
            End Try
        End Function

        ''' <summary>one round of one turn, with the complete request it sent.</summary>
        Public Function Round(turnSeq As Integer, roundSeq As Integer) As String
            Try
                Return _reader.Round(turnSeq, roundSeq)
            Catch ex As Exception
                Return ErrorJson("cannot read round " & roundSeq & ": " & ex.Message)
            End Try
        End Function

        ''' <summary>a slice of a payload file of the open session.</summary>
        Public Function Payload(relativePath As String, offset As Integer, limit As Integer) As String
            Try
                Return _reader.Payload(relativePath, offset, limit)
            Catch ex As Exception
                Return ErrorJson("cannot read the payload file: " & ex.Message)
            End Try
        End Function

        ''' <summary>the pretty printed raw text of one json file of the open session.</summary>
        Public Function RawFile(name As String, maxChars As Integer) As String
            Try
                Return _reader.RawFile(name, maxChars)
            Catch ex As Exception
                Return ErrorJson("cannot read " & name & ": " & ex.Message)
            End Try
        End Function

        ''' <summary>a slice of a plain text log of the log folder, e.g. rscript\*.log.</summary>
        Public Function TextFile(name As String, offset As Integer, limit As Integer) As String
            Try
                Return _reader.TextFile(name, offset, limit)
            Catch ex As Exception
                Return ErrorJson("cannot read " & name & ": " & ex.Message)
            End Try
        End Function

        ''' <summary>
        ''' drop the cached json documents, so the next read picks up a log that
        ''' grew while the viewer was open.
        ''' </summary>
        Public Sub Refresh()
            Call _index.ClearCache()
        End Sub

        ' ====================================================================
        ' internals
        ' ====================================================================

        Private Shared Function ReadSummary(folder As String) As String
            Dim path As String = IO.Path.Combine(folder, "session.json")

            If Not File.Exists(path) Then
                Return "null"
            End If

            Try
                Dim raw As String = File.ReadAllText(path, Encoding.UTF8)

                Using document As JsonDocument = JsonDocument.Parse(raw)
                    Return document.RootElement.GetRawText()
                End Using
            Catch ex As Exception
                Return ErrorJson("cannot read session.json: " & ex.Message)
            End Try
        End Function

        ''' <summary>
        ''' the session the given one points at: a tool call log names its chat
        ''' log and a chat log names its tool call log, so the two can be read
        ''' side by side.
        ''' </summary>
        Private Shared Function LinkedSession(summary As String, kind As String) As String
            If String.IsNullOrEmpty(summary) OrElse summary = "null" Then
                Return "null"
            End If

            Try
                Using document As JsonDocument = JsonDocument.Parse(summary)
                    Dim root As JsonElement = document.RootElement
                    Dim field As String = If(kind = HarnessLogIndex.KindToolCalls, "llmLogFolder", "harnessLogFolder")
                    Dim value As JsonElement

                    If Not root.TryGetProperty(field, value) Then
                        Return "null"
                    End If

                    If value.ValueKind <> JsonValueKind.String Then
                        Return "null"
                    End If

                    Dim folder As String = value.GetString()

                    If String.IsNullOrEmpty(folder) OrElse Not Directory.Exists(folder) Then
                        Return "null"
                    End If

                    Dim linked As String = If(kind = HarnessLogIndex.KindToolCalls, HarnessLogIndex.KindLlmSessions, HarnessLogIndex.KindToolCalls)

                    Return "{""kind"": " & JsonSerializer.Serialize(linked) &
                           ", ""sessionId"": " & JsonSerializer.Serialize(New DirectoryInfo(folder).Name) & "}"
                End Using
            Catch
                Return "null"
            End Try
        End Function

        Private Shared Function ErrorJson(message As String) As String
            Return "{""error"": " & JsonSerializer.Serialize(message) & "}"
        End Function

    End Class

End Namespace
