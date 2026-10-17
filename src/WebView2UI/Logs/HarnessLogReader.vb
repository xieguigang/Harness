Imports System.Collections.Generic
Imports System.IO
Imports System.Text
Imports System.Text.Json

Namespace Logs

    ''' <summary>
    ''' the filter a page of records is narrowed down by. every field is
    ''' optional; an empty field matches everything.
    ''' </summary>
    Public Class LogRecordFilter

        ''' <summary>the name of the tool, for the tool call log.</summary>
        Public Property Tool As String

        ''' <summary>"ok", "error", "denied" or "failed".</summary>
        Public Property Status As String

        ''' <summary>"sandbox", "mcp" or "skill" - or "registry", "tool_invoke", "missing" on the chat side.</summary>
        Public Property Source As String

        ''' <summary>a case insensitive substring matched against the text of the record.</summary>
        Public Property Keyword As String

        ''' <summary>only records of this turn, 0 means every turn.</summary>
        Public Property Turn As Integer

        ''' <summary>only records of this round, 0 means every round.</summary>
        Public Property Round As Integer

        ''' <summary>-1 means either answer, 0 means denied, 1 means granted.</summary>
        Public Property Granted As Integer = -1

        ''' <summary>"system", "user", "assistant" or "tool", for the message ledger.</summary>
        Public Property Role As String

        ''' <summary>
        ''' read a filter out of a json object sent by the web page. an
        ''' unreadable or empty filter matches everything.
        ''' </summary>
        Public Shared Function Parse(json As String) As LogRecordFilter
            Dim filter As New LogRecordFilter()

            If String.IsNullOrWhiteSpace(json) Then
                Return filter
            End If

            Try
                Using document As JsonDocument = JsonDocument.Parse(json)
                    Dim root As JsonElement = document.RootElement

                    If root.ValueKind <> JsonValueKind.Object Then
                        Return filter
                    End If

                    filter.Tool = ReadText(root, "tool")
                    filter.Status = ReadText(root, "status")
                    filter.Source = ReadText(root, "source")
                    filter.Keyword = ReadText(root, "keyword")
                    filter.Role = ReadText(root, "role")
                    filter.Turn = ReadInt(root, "turn")
                    filter.Round = ReadInt(root, "round")
                    filter.Granted = ReadInt(root, "granted", -1)
                End Using
            Catch
                ' a broken filter is treated as "no filter": the page still
                ' gets its data, just without the narrowing
            End Try

            Return filter
        End Function

        Private Shared Function ReadText(root As JsonElement, name As String) As String
            For Each pair In root.EnumerateObject()
                If String.Equals(pair.Name, name, StringComparison.OrdinalIgnoreCase) Then
                    If pair.Value.ValueKind = JsonValueKind.String Then
                        Dim text As String = pair.Value.GetString()

                        If String.IsNullOrWhiteSpace(text) Then
                            Return Nothing
                        End If

                        Return text.Trim()
                    End If

                    Return Nothing
                End If
            Next

            Return Nothing
        End Function

        Private Shared Function ReadInt(root As JsonElement, name As String, Optional fallback As Integer = 0) As Integer
            For Each pair In root.EnumerateObject()
                If String.Equals(pair.Name, name, StringComparison.OrdinalIgnoreCase) Then
                    If pair.Value.ValueKind = JsonValueKind.Number Then
                        Dim parsed As Integer

                        If pair.Value.TryGetInt32(parsed) Then
                            Return parsed
                        End If
                    End If

                    Return fallback
                End If
            Next

            Return fallback
        End Function

        ''' <summary>True when the filter narrows nothing down.</summary>
        Public ReadOnly Property IsEmpty As Boolean
            Get
                Return String.IsNullOrEmpty(Tool) AndAlso
                       String.IsNullOrEmpty(Status) AndAlso
                       String.IsNullOrEmpty(Source) AndAlso
                       String.IsNullOrEmpty(Keyword) AndAlso
                       String.IsNullOrEmpty(Role) AndAlso
                       Turn <= 0 AndAlso Round <= 0 AndAlso Granted < 0
            End Get
        End Property

    End Class

    ''' <summary>
    ''' reads the records of one session: pages an array file, narrows it with
    ''' a <see cref="LogRecordFilter"/>, and hands out a single turn or round
    ''' with everything it contains.
        ''' </summary>
    Public Class HarnessLogReader

        Private ReadOnly _index As HarnessLogIndex
        Private _folder As String
        Private _kind As String

        ''' <param name="index">the index the session folders are read from.</param>
        Public Sub New(index As HarnessLogIndex)
            If index Is Nothing Then
                Throw New ArgumentNullException(NameOf(index))
            End If

            _index = index
        End Sub

        ''' <summary>the session this reader currently points at.</summary>
        Public ReadOnly Property Folder As String
            Get
                Return _folder
            End Get
        End Property

        ''' <summary>the kind of the open session, "toolcalls" or "llm-sessions".</summary>
        Public ReadOnly Property Kind As String
            Get
                Return _kind
            End Get
        End Property

        ''' <summary>
        ''' point the reader at one session. a false result means the session
        ''' folder is unknown and every read after it fails with an error.
        ''' </summary>
        ''' <param name="kind">"toolcalls" or "llm-sessions".</param>
        ''' <param name="sessionId">the id of the session, i.e. its folder name.</param>
        Public Function Open(kind As String, sessionId As String) As Boolean
            Dim folder As String = _index.SessionFolder(kind, sessionId)

            If folder Is Nothing Then
                _folder = Nothing
                _kind = Nothing
                Return False
            End If

            _folder = folder
            _kind = kind

            Return True
        End Function

        ''' <summary>
        ''' page one array file of the open session.
        ''' </summary>
        ''' <param name="name">one of <see cref="HarnessLogIndex.ArrayFiles"/>.</param>
        ''' <param name="offset">the first record to return, 0 based.</param>
        ''' <param name="limit">how many records to return at most.</param>
        ''' <param name="filterJson">the filter as a json object, or Nothing.</param>
        ''' <returns>
        ''' <c>{total, offset, limit, items:[...]}</c>; for turns.json the items
        ''' are summaries without their rounds, so a 5MB session stays cheap to
        ''' page through.
        ''' </returns>
        Public Function Records(name As String, offset As Integer, limit As Integer, filterJson As String) As String
            If _folder Is Nothing Then
                Return ErrorJson("no session is open")
            End If

            Dim fileName As String = Nothing

            For Each candidate In HarnessLogIndex.ArrayFiles
                If String.Equals(candidate, name, StringComparison.OrdinalIgnoreCase) Then
                    fileName = candidate
                    Exit For
                End If
            Next

            If fileName Is Nothing Then
                Return ErrorJson($"'{name}' is not a readable array file of a session")
            End If

            Dim document As JsonDocument = _index.Document(IO.Path.Combine(_folder, fileName))

            If document Is Nothing Then
                Return ErrorJson($"cannot read {fileName} of the session")
            End If

            Dim root As JsonElement = document.RootElement

            If root.ValueKind <> JsonValueKind.Array Then
                Return ErrorJson($"{fileName} is not a json array")
            End If

            Dim filter As LogRecordFilter = LogRecordFilter.Parse(filterJson)
            Dim matched As New List(Of JsonElement)()

            For Each item As JsonElement In root.EnumerateArray()
                If item.ValueKind <> JsonValueKind.Object Then
                    Continue For
                End If

                If Not Keep(item, fileName, filter) Then
                    Continue For
                End If

                matched.Add(item)
            Next

            If offset < 0 Then
                offset = 0
            End If
            If limit <= 0 Then
                limit = 30
            End If
            If limit > 200 Then
                limit = 200
            End If

            Dim page As New StringBuilder()
            page.Append("{""total"": ").Append(matched.Count)
            page.Append(", ""offset"": ").Append(offset)
            page.Append(", ""limit"": ").Append(limit)
            page.Append(", ""items"": [")

            Dim first As Boolean = True
            Dim taken As Integer = 0

            For i As Integer = offset To matched.Count - 1
                If taken >= limit Then
                    Exit For
                End If

                If Not first Then
                    page.Append(", ")
                End If

                first = False
                taken += 1

                If String.Equals(fileName, "turns.json", StringComparison.OrdinalIgnoreCase) Then
                    page.Append(TurnSummary(matched(i)))
                Else
                    page.Append(matched(i).GetRawText())
                End If
            Next

            page.Append("]}")

            Return page.ToString()
        End Function

        ''' <summary>
        ''' one complete turn, with its rounds and the messages it added.
        ''' </summary>
        ''' <param name="turnSeq">the sequence number of the turn inside the session.</param>
        Public Function Turn(turnSeq As Integer) As String
            Dim record As JsonElement = FindTurn(turnSeq)

            If record.ValueKind <> JsonValueKind.Object Then
                Return ErrorJson($"turn {turnSeq} is not in this session")
            End If

            Return record.GetRawText()
        End Function

        ''' <summary>
        ''' one round of one turn, with the complete message list of the
        ''' request that was sent to the model.
        ''' </summary>
        Public Function Round(turnSeq As Integer, roundSeq As Integer) As String
            Dim turnRecord As JsonElement = FindTurn(turnSeq)

            If turnRecord.ValueKind <> JsonValueKind.Object Then
                Return ErrorJson($"turn {turnSeq} is not in this session")
            End If

            If Not turnRecord.TryGetProperty("rounds", Nothing) Then
                Return ErrorJson($"turn {turnSeq} has no rounds")
            End If

            Dim rounds As JsonElement = turnRecord.GetProperty("rounds")

            If rounds.ValueKind <> JsonValueKind.Array Then
                Return ErrorJson($"turn {turnSeq} has no rounds")
            End If

            For Each item As JsonElement In rounds.EnumerateArray()
                If item.ValueKind = JsonValueKind.Object AndAlso ReadInt(item, "seq") = roundSeq Then
                    Return item.GetRawText()
                End If
            Next

            Return ErrorJson($"round {roundSeq} is not in turn {turnSeq}")
        End Function

        ''' <summary>
        ''' read a slice of a payload file: the text of a tool result or a
        ''' request message that was too long to inline.
        ''' </summary>
        ''' <param name="relativePath">a name like <c>payloads/0001.txt</c>.</param>
        ''' <param name="offset">the first character to return.</param>
        ''' <param name="limit">how many characters to return at most.</param>
        Public Function Payload(relativePath As String, offset As Integer, limit As Integer) As String
            If _folder Is Nothing Then
                Return ErrorJson("no session is open")
            End If

            Dim full As String = ResolvePayload(relativePath)

            If full Is Nothing Then
                Return ErrorJson("the payload file is not inside this session")
            End If

            If offset < 0 Then
                offset = 0
            End If
            If limit <= 0 Then
                limit = 64 * 1024
            End If
            If limit > 512 * 1024 Then
                limit = 512 * 1024
            End If

            Try
                ' a payload is read as characters, not bytes, so the page the
                ' viewer shows matches the offsets the record describes
                Dim whole As String = File.ReadAllText(full, Encoding.UTF8)
                Dim slice As String

                If offset >= whole.Length Then
                    slice = ""
                Else
                    slice = whole.Substring(offset, Math.Min(limit, whole.Length - offset))
                End If

                Dim sb As New StringBuilder()
                sb.Append("{""offset"": ").Append(offset)
                sb.Append(", ""limit"": ").Append(limit)
                sb.Append(", ""read"": ").Append(slice.Length)
                sb.Append(", ""size"": ").Append(whole.Length)
                sb.Append(", ""text"": ").Append(JsonSerializer.Serialize(slice))
                sb.Append("}")

                Return sb.ToString()
            Catch ex As Exception
                Return ErrorJson("cannot read the payload file: " & ex.Message)
            End Try
        End Function

        ''' <summary>
        ''' the pretty printed raw text of one json file of the session. a file
        ''' longer than <paramref name="maxChars"/> is cut short, and the
        ''' result says so.
        ''' </summary>
        Public Function RawFile(name As String, maxChars As Integer) As String
            If _folder Is Nothing Then
                Return ErrorJson("no session is open")
            End If

            If maxChars <= 0 Then
                maxChars = 512 * 1024
            End If
            If maxChars > 4 * 1024 * 1024 Then
                maxChars = 4 * 1024 * 1024
            End If

            Dim allowed As Boolean = String.Equals(name, "session.json", StringComparison.OrdinalIgnoreCase)

            If Not allowed Then
                For Each candidate In HarnessLogIndex.ArrayFiles
                    If String.Equals(candidate, name, StringComparison.OrdinalIgnoreCase) Then
                        allowed = True
                        Exit For
                    End If
                Next
            End If

            If Not allowed Then
                Return ErrorJson($"'{name}' is not a log file of a session")
            End If

            Dim path As String = IO.Path.Combine(_folder, name)

            Try
                Dim raw As String = File.ReadAllText(path, Encoding.UTF8)

                Dim pretty As String = PrettyJson(raw)
                Dim truncated As Boolean = pretty.Length > maxChars

                If truncated Then
                    pretty = pretty.Substring(0, maxChars)
                End If

                Dim sb As New StringBuilder()
                sb.Append("{""name"": ").Append(JsonSerializer.Serialize(name))
                sb.Append(", ""size"": ").Append(raw.Length)
                sb.Append(", ""truncated"": ").Append(If(truncated, "true", "false"))
                sb.Append(", ""text"": ").Append(JsonSerializer.Serialize(pretty))
                sb.Append("}")

                Return sb.ToString()
            Catch ex As Exception
                Return ErrorJson("cannot read " & name & ": " & ex.Message)
            End Try
        End Function

        ''' <summary>
        ''' a slice of a plain text log of the log root, such as an
        ''' <c>rscript\*.log</c> of a background job.
        ''' </summary>
        Public Function TextFile(name As String, offset As Integer, limit As Integer) As String
            Dim full As String = _index.ResolveTextFile(name)

            If full Is Nothing Then
                Return ErrorJson("the text file is not inside the log folder")
            End If

            If offset < 0 Then
                offset = 0
            End If
            If limit <= 0 Then
                limit = 64 * 1024
            End If
            If limit > 512 * 1024 Then
                limit = 512 * 1024
            End If

            Try
                Dim lines As String() = File.ReadAllLines(full, Encoding.UTF8)
                Dim sb As New StringBuilder()

                sb.Append("{""offset"": ").Append(offset)
                sb.Append(", ""limit"": ").Append(limit)
                sb.Append(", ""lines"": ").Append(lines.Length)
                sb.Append(", ""text"": ").Append(JsonSerializer.Serialize(String.Join(vbCrLf, lines.Skip(offset).Take(limit))))
                sb.Append("}")

                Return sb.ToString()
            Catch ex As Exception
                Return ErrorJson("cannot read " & name & ": " & ex.Message)
            End Try
        End Function

        ' ====================================================================
        ' internals
        ' ====================================================================

        Private Function FindTurn(turnSeq As Integer) As JsonElement
            Dim empty As JsonElement = Nothing

            If _folder Is Nothing Then
                Return empty
            End If

            Dim document As JsonDocument = _index.Document(IO.Path.Combine(_folder, "turns.json"))

            If document Is Nothing OrElse document.RootElement.ValueKind <> JsonValueKind.Array Then
                Return empty
            End If

            For Each turn As JsonElement In document.RootElement.EnumerateArray()
                If turn.ValueKind = JsonValueKind.Object AndAlso ReadInt(turn, "seq") = turnSeq Then
                    Return turn
                End If
            Next

            Return empty
        End Function

        ''' <summary>
        ''' the small version of a turn: everything the list needs, none of the
        ''' rounds and messages that make a session big.
        ''' </summary>
        Private Shared Function TurnSummary(turn As JsonElement) As String
            Dim prompt As String = Nothing
            Dim promptLength As Integer = 0

            If turn.TryGetProperty("prompt", Nothing) Then
                Dim text As JsonElement = turn.GetProperty("prompt")

                prompt = ReadText(text, "text")
                promptLength = ReadInt(text, "length")
            End If

            Dim output As String = Nothing
            Dim outputLength As Integer = 0

            If turn.TryGetProperty("output", Nothing) Then
                Dim text As JsonElement = turn.GetProperty("output")

                output = ReadText(text, "text")
                outputLength = ReadInt(text, "length")
            End If

            Dim sb As New StringBuilder()

            sb.Append("{""seq"": ").Append(ReadInt(turn, "seq"))
            sb.Append(", ""sessionId"": ").Append(JsonSerializer.Serialize(ReadText(turn, "sessionId")))
            sb.Append(", ""timestampUtc"": ").Append(JsonSerializer.Serialize(ReadText(turn, "timestampUtc")))
            sb.Append(", ""model"": ").Append(JsonSerializer.Serialize(ReadText(turn, "model")))
            sb.Append(", ""status"": ").Append(JsonSerializer.Serialize(ReadText(turn, "status")))
            sb.Append(", ""roundCount"": ").Append(ReadInt(turn, "roundCount"))
            sb.Append(", ""toolCallCount"": ").Append(ReadInt(turn, "toolCallCount"))
            sb.Append(", ""promptTokens"": ").Append(ReadLong(turn, "promptTokens"))
            sb.Append(", ""completionTokens"": ").Append(ReadLong(turn, "completionTokens"))
            sb.Append(", ""durationMs"": ").Append(ReadDouble(turn, "durationMs"))
            sb.Append(", ""promptLength"": ").Append(promptLength)
            sb.Append(", ""outputLength"": ").Append(outputLength)

            If prompt IsNot Nothing Then
                sb.Append(", ""promptPreview"": ").Append(JsonSerializer.Serialize(Shorten(prompt, 200)))
            End If
            If output IsNot Nothing Then
                sb.Append(", ""outputPreview"": ").Append(JsonSerializer.Serialize(Shorten(output, 200)))
            End If

            sb.Append("}")

            Return sb.ToString()
        End Function

        ''' <summary>
        ''' whether one record survives the filter of its file. every file has
        ''' its own interesting fields, so the check is written per file.
        ''' </summary>
        Private Shared Function Keep(item As JsonElement, fileName As String, filter As LogRecordFilter) As Boolean
            If filter.IsEmpty Then
                Return True
            End If

            If Not String.IsNullOrEmpty(filter.Tool) Then
                Dim tool As String = ReadText(item, "tool")

                If tool Is Nothing Then
                    tool = ReadText(item, "name")
                End If

                If tool Is Nothing OrElse tool.IndexOf(filter.Tool, StringComparison.OrdinalIgnoreCase) < 0 Then
                    Return False
                End If
            End If

            If Not String.IsNullOrEmpty(filter.Status) Then
                Dim status As String = ReadText(item, "status")

                If status Is Nothing OrElse Not String.Equals(status, filter.Status, StringComparison.OrdinalIgnoreCase) Then
                    Return False
                End If
            End If

            If Not String.IsNullOrEmpty(filter.Source) Then
                Dim source As String = ReadText(item, "source")

                If source Is Nothing OrElse source.IndexOf(filter.Source, StringComparison.OrdinalIgnoreCase) < 0 Then
                    Return False
                End If
            End If

            If Not String.IsNullOrEmpty(filter.Role) Then
                Dim role As String = ReadText(item, "role")

                If role Is Nothing OrElse Not String.Equals(role, filter.Role, StringComparison.OrdinalIgnoreCase) Then
                    Return False
                End If
            End If

            If filter.Turn > 0 Then
                If ReadInt(item, "turnSeq") <> filter.Turn AndAlso ReadInt(item, "seq") <> filter.Turn Then
                    Return False
                End If
            End If

            If filter.Round > 0 Then
                If ReadInt(item, "roundSeq") <> filter.Round Then
                    Return False
                End If
            End If

            If filter.Granted >= 0 Then
                Dim granted As Integer = -1

                If item.TryGetProperty("granted", Nothing) Then
                    Dim value As JsonElement = item.GetProperty("granted")

                    If value.ValueKind = JsonValueKind.True Then
                        granted = 1
                    ElseIf value.ValueKind = JsonValueKind.False Then
                        granted = 0
                    End If
                End If

                If granted <> filter.Granted Then
                    Return False
                End If
            End If

            If Not String.IsNullOrEmpty(filter.Keyword) Then
                If Not HasKeyword(item, fileName, filter.Keyword) Then
                    Return False
                End If
            End If

            Return True
        End Function

        ''' <summary>
        ''' the keyword of a filter is matched against the parts of a record
        ''' that carry the interesting text: the arguments and the result of a
        ''' tool call, the prompt and the answer of a turn, the body of a
        ''' message.
        ''' </summary>
        Private Shared Function HasKeyword(item As JsonElement, fileName As String, keyword As String) As Boolean
            If ContainsKeyword(ReadText(item, "summary"), keyword) Then
                Return True
            End If
            If ContainsKeyword(ReadText(item, "target"), keyword) Then
                Return True
            End If
            If ContainsKeyword(ReadText(item, "justification"), keyword) Then
                Return True
            End If
            If ContainsKeyword(ReadText(item, "reason"), keyword) Then
                Return True
            End If
            If ContainsKeyword(ReadText(item, "name"), keyword) Then
                Return True
            End If

            If item.TryGetProperty("arguments", Nothing) Then
                If ContainsKeyword(item.GetProperty("arguments").GetRawText(), keyword) Then
                    Return True
                End If
            End If

            For Each field In {"prompt", "output", "think", "result", "content"}
                If item.TryGetProperty(field, Nothing) Then
                    If ContainsKeyword(item.GetProperty(field).GetRawText(), keyword) Then
                        Return True
                    End If
                End If
            Next

            If String.Equals(fileName, "turns.json", StringComparison.OrdinalIgnoreCase) Then
                ' the rounds hold the text of a turn, so a keyword has to
                ' reach into them for the turn list to be useful
                If item.TryGetProperty("rounds", Nothing) Then
                    If ContainsKeyword(item.GetProperty("rounds").GetRawText(), keyword) Then
                        Return True
                    End If
                End If
            End If

            Return False
        End Function

        Private Shared Function ContainsKeyword(text As String, keyword As String) As Boolean
            Return text IsNot Nothing AndAlso text.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0
        End Function

        Private Shared Function Shorten(text As String, max As Integer) As String
            If text Is Nothing OrElse text.Length <= max Then
                Return text
            End If

            Return text.Substring(0, max) & "…"
        End Function

        Private Shared Function PrettyJson(raw As String) As String
            Try
                Using document As JsonDocument = JsonDocument.Parse(raw)
                    Return JsonSerializer.Serialize(document.RootElement, New JsonSerializerOptions With {.WriteIndented = True})
                End Using
            Catch
                ' a file that is not valid json is shown as it is
                Return raw
            End Try
        End Function

        Private Function ResolvePayload(relativePath As String) As String
            If String.IsNullOrWhiteSpace(relativePath) Then
                Return Nothing
            End If

            Dim norm As String = relativePath.Replace("/"c, IO.Path.DirectorySeparatorChar)

            If norm.Contains("..") OrElse IO.Path.IsPathRooted(norm) Then
                Return Nothing
            End If

            Dim prefix As String = "payloads" & IO.Path.DirectorySeparatorChar

            If Not norm.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) Then
                Return Nothing
            End If

            Dim name As String = norm.Substring(prefix.Length)

            If name.IndexOfAny({IO.Path.DirectorySeparatorChar, IO.Path.AltDirectorySeparatorChar}) >= 0 Then
                Return Nothing
            End If

            If Not name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) Then
                Return Nothing
            End If

            Dim full As String = IO.Path.Combine(_folder, "payloads", name)

            If Not full.StartsWith(IO.Path.Combine(_folder, "payloads") & IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) Then
                Return Nothing
            End If

            If Not File.Exists(full) Then
                Return Nothing
            End If

            Return full
        End Function

        Private Shared Function ReadText(element As JsonElement, name As String) As String
            If element.ValueKind <> JsonValueKind.Object Then
                Return Nothing
            End If

            Dim value As JsonElement

            If Not element.TryGetProperty(name, value) Then
                Return Nothing
            End If

            If value.ValueKind <> JsonValueKind.String Then
                Return Nothing
            End If

            Return value.GetString()
        End Function

        Private Shared Function ReadInt(element As JsonElement, name As String) As Integer
            If element.ValueKind <> JsonValueKind.Object Then
                Return 0
            End If

            Dim value As JsonElement

            If Not element.TryGetProperty(name, value) Then
                Return 0
            End If

            If value.ValueKind <> JsonValueKind.Number Then
                Return 0
            End If

            Dim number As Integer

            If value.TryGetInt32(number) Then
                Return number
            End If

            Return CInt(value.GetDouble())
        End Function

        Private Shared Function ReadLong(element As JsonElement, name As String) As Long
            If element.ValueKind <> JsonValueKind.Object Then
                Return 0
            End If

            Dim value As JsonElement

            If Not element.TryGetProperty(name, value) Then
                Return 0
            End If

            If value.ValueKind <> JsonValueKind.Number Then
                Return 0
            End If

            Dim number As Long

            If value.TryGetInt64(number) Then
                Return number
            End If

            Return CLng(value.GetDouble())
        End Function

        Private Shared Function ReadDouble(element As JsonElement, name As String) As Double
            If element.ValueKind <> JsonValueKind.Object Then
                Return 0
            End If

            Dim value As JsonElement

            If Not element.TryGetProperty(name, value) Then
                Return 0
            End If

            If value.ValueKind <> JsonValueKind.Number Then
                Return 0
            End If

            Return value.GetDouble()
        End Function

        Private Shared Function ErrorJson(message As String) As String
            Return "{""error"": " & JsonSerializer.Serialize(message) & "}"
        End Function

    End Class

End Namespace
