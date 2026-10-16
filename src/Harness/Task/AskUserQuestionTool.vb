Imports System.ComponentModel
Imports System.Collections.Generic
Imports System.Linq
Imports System.Text
Imports System.Threading.Tasks
Imports Microsoft.VisualBasic.CommandLine.Reflection
Imports Microsoft.VisualBasic.MIME.application.json.Javascript
Imports Microsoft.VisualBasic.MIME.application.json.LenientJson

Namespace Tools

    ''' <summary>
    ''' One choice offered to the user inside a <see cref="UserQuestion"/>.
    ''' </summary>
    Public Class QuestionOption

        ''' <summary>Short user facing label; this is what the answer reports back.</summary>
        Public Property Label As String

        ''' <summary>One sentence explaining the tradeoff or impact of this choice.</summary>
        Public Property Description As String

    End Class

    ''' <summary>
    ''' One question asked to the user through the "ask_user_question" tool.
    ''' </summary>
    Public Class UserQuestion

        ''' <summary>Stable id of this question; echoed in the answer.</summary>
        Public Property Id As String

        ''' <summary>The specific question to ask the user.</summary>
        Public Property Question As String

        ''' <summary>Optional short heading, such as "Confirm" or "Choose Mode".</summary>
        Public Property Header As String

        ''' <summary>
        ''' The choices to show the user, or Nothing for an open question
        ''' that is answered with free text. A recommended option is
        ''' expected first, with "(Recommended)" appended to its label.
        ''' </summary>
        Public Property Options As List(Of QuestionOption)

        ''' <summary>Whether the user may select more than one option. Defaults to False.</summary>
        Public Property MultiSelect As Boolean

    End Class

    ''' <summary>
    ''' What the user answered: the response of every question, keyed by its
    ''' <see cref="UserQuestion.Id"/>, or the declined flag when the user
    ''' dismissed the question without answering.
    ''' </summary>
    Public Class UserAnswer

        ''' <summary>
        ''' The response of every answered question, keyed by its id: the
        ''' chosen option label, the comma joined labels of a multi-select
        ''' question, or the free text of an open question. Empty when the
        ''' user declined.
        ''' </summary>
        Public Property Responses As New Dictionary(Of String, String)()

        ''' <summary>True when the user dismissed the question without answering.</summary>
        Public Property Declined As Boolean

    End Class

    ''' <summary>
    ''' Tool "ask_user_question": ask the user a concise question when a
    ''' confirmation, a choice, or missing information is needed before the
    ''' task can go on, and hand the answer back to the model.
    '''
    ''' The tool does not touch the filesystem, so it needs no path and no
    ''' sandbox authorization: the questions travel over the
    ''' <see cref="Sandbox.AskUser"/> event to the host, which shows them to
    ''' the user and answers through <see cref="QuestionEventArg"/>.
    '''
    ''' Blocking is the original behaviour: the call waits until the user
    ''' answered, however long that takes. With <c>mode</c> "timed" the call
    ''' waits at most <c>timeout</c> milliseconds and then returns a pending
    ''' result while the question remains answerable - the model continues
    ''' with other work and collects the answer by calling the tool again
    ''' with the same question id. A <c>timeout</c> of -1 keeps even a timed
    ''' call blocking indefinitely, and 0 returns the pending result at
    ''' once.
    '''
    ''' The tool instance is created once per
    ''' <see cref="Sandbox.RegisterTo"/> call and every call of the model
    ''' goes through it, so the pending registry of the instance is what
    ''' lets a timed call hand an unanswered question over to a later one.
    ''' </summary>
    Public Class AskUserQuestionTool

        ''' <summary>Foreground wait of the "timed" mode when the model gives no timeout, in milliseconds.</summary>
        Private Const DefaultTimeoutMs As Double = 300000

        Private Const ModeBlocking As String = "blocking"
        Private Const ModeTimed As String = "timed"

        Private ReadOnly _sandbox As Sandbox

        ''' <summary>
        ''' Every question batch of this session that is still waiting for -
        ''' or already carries - the answer of the user, keyed by question
        ''' id. Answered batches are kept, so that a repeated call with the
        ''' same id reports the recorded answer instead of asking the user
        ''' a second time.
        ''' </summary>
        Private ReadOnly _pending As New Dictionary(Of String, AskBatch)(StringComparer.OrdinalIgnoreCase)
        Private ReadOnly _registryLock As New Object()

        ''' <param name="sandbox">The sandbox that owns this session and raises the <see cref="Sandbox.AskUser"/> event.</param>
        Public Sub New(sandbox As Sandbox)
            If sandbox Is Nothing Then
                Throw New ArgumentNullException(NameOf(sandbox))
            End If

            _sandbox = sandbox
        End Sub

        ''' <summary>
        ''' Ask the user one or several questions and return their answers.
        ''' </summary>
        ''' <param name="questions">A JSON array of question objects, given as a JSON encoded string.</param>
        ''' <param name="mode">"blocking" (the default) waits until the user answers; "timed" waits at most <paramref name="timeout"/> milliseconds and then returns a pending result while the question stays answerable.</param>
        ''' <param name="timeout">Maximum wait of the "timed" mode, in milliseconds. Defaults to 300000 (5 minutes). -1 waits indefinitely, 0 returns the pending result at once. Ignored in "blocking" mode.</param>
        <Description("Ask the user a concise question when you need a confirmation, a choice, or missing information before proceeding. One call may ask several questions at once; each question may offer options (single or multi select) or be open ended. The call blocks until the user answers; with mode ""timed"" it waits at most ""timeout"" milliseconds and then returns a pending result while the question remains answerable - call ask_user_question again with the same question id to collect the answer later. Use it before an irreversible or ambiguous step, never for something you can determine yourself.")>
        <ExportAPI("ask_user_question")>
        Public Function ask_user_question(
            <Argument("questions", Description:="A JSON array of question objects, given as a JSON encoded string. Each object has: ""id"" (required, a stable id echoed in the answer), ""question"" (required, the specific question to ask), ""header"" (optional short heading such as ""Confirm"" or ""Choose Mode""), ""options"" (optional array of choice objects, each with a required ""label"" and an optional ""description"" of one sentence explaining the tradeoff; if you recommend one, put it first and append ""(Recommended)"" to its label) and ""multi_select"" (optional boolean, defaults to false; true lets the user select more than one option). A question without options is answered with free text. Example: [{""id"":""mode"",""question"":""Which mode should I use?"",""header"":""Choose Mode"",""options"":[{""label"":""Fast (Recommended)"",""description"":""Lower latency, slightly less accurate.""},{""label"":""Thorough"",""description"":""Slower, more accurate.""}]}]")> questions As String,
            <Argument("mode", Description:="""blocking"" (the default when omitted) waits until the user answers, however long that takes; ""timed"" waits at most ""timeout"" milliseconds and then returns a pending result while the question remains answerable - call ask_user_question again with the same question id to collect the answer.")> Optional mode As String = Nothing,
            <Argument("timeout", Description:="Maximum time to wait for the answer in ""timed"" mode, in milliseconds. Defaults to 300000 (5 minutes). Use -1 to wait indefinitely (same as the blocking mode), or 0 to return the pending result immediately. Ignored in ""blocking"" mode.")> Optional timeout As Double = DefaultTimeoutMs
        ) As String
            If String.IsNullOrWhiteSpace(questions) Then
                Return "Error: 'questions' is empty. Pass a JSON array of question objects such as " &
                      "[{""id"":""mode"",""question"":""Which mode should I use?""}]."
            End If

            Dim timed As Boolean = False
            Dim failure As String = Nothing

            If Not ParseMode(mode, timed, failure) Then
                Return failure
            End If

            Dim waitMs As Double = Double.PositiveInfinity

            If timed AndAlso Not ParseTimeout(timeout, waitMs, failure) Then
                Return failure
            End If

            Dim specs As List(Of UserQuestion) = Nothing

            If Not ParseQuestions(questions, specs, failure) Then
                Return failure
            End If

            Return Ask(specs, waitMs)
        End Function

        ''' <summary>
        ''' Show the still unanswered questions to the user and wait for the
        ''' answer according to the requested mode. Questions an earlier
        ''' call already answered are reported from the registry instead of
        ''' being asked again, and questions an earlier timed call left
        ''' pending are waited on without raising the event a second time.
        ''' </summary>
        ''' <param name="specs">The questions of this call, in the order the model asked them.</param>
        ''' <param name="waitMs">How long to wait: infinite for the blocking mode, the timeout of the timed mode otherwise.</param>
        Private Function Ask(specs As List(Of UserQuestion), waitMs As Double) As String
            Dim answers As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
            Dim waiting As New List(Of AskBatch)()
            Dim fresh As New List(Of UserQuestion)()

            For Each spec In specs
                Dim batch As AskBatch = Nothing

                SyncLock _registryLock
                    _pending.TryGetValue(spec.Id, batch)
                End SyncLock

                If batch Is Nothing Then
                    fresh.Add(spec)
                ElseIf batch.Arg.AnswerTask.IsCompleted Then
                    ' answered (or declined) by an earlier call: report the
                    ' recorded answer instead of asking the user again
                    Collect(batch, answers)
                ElseIf Not waiting.Contains(batch) Then
                    waiting.Add(batch)
                End If
            Next

            If fresh.Count > 0 Then
                Dim batch As New AskBatch(fresh, New QuestionEventArg(fresh))

                SyncLock _registryLock
                    For Each spec In fresh
                        _pending(spec.Id) = batch
                    Next
                End SyncLock

                Dim raised As Boolean = False
                Dim problem As String = Nothing

                Try
                    raised = _sandbox.RaiseAskUser(batch.Arg)
                Catch ex As Exception
                    problem = $"Error: the host failed to show the question: {ex.Message}"
                End Try

                If raised Then
                    waiting.Add(batch)
                Else
                    ' no host can ever answer this batch, so take it out of
                    ' the registry again instead of leaving it pending
                    SyncLock _registryLock
                        For Each spec In fresh
                            _pending.Remove(spec.Id)
                        Next
                    End SyncLock

                    Return If(problem, "Error: this session has no user channel; the host did not subscribe to the question event, so the question cannot be asked.")
                End If
            End If

            If waiting.Count = 0 Then
                ' every question of this call was answered before
                Return RenderAnswers(specs, answers, New HashSet(Of String)(StringComparer.OrdinalIgnoreCase))
            End If

            Dim all As Task = Task.WhenAll(waiting.Select(Function(b) b.Arg.AnswerTask).ToArray())
            Dim completed As Boolean

            If Double.IsPositiveInfinity(waitMs) Then
                Try
                    all.Wait()
                    completed = True
                Catch
                    ' a faulted host answer ends the wait as well; the
                    ' responses of a broken batch are simply missing
                    completed = True
                End Try
            Else
                completed = all.Wait(ToMilliseconds(waitMs))
            End If

            For Each batch In waiting
                Collect(batch, answers)
            Next

            If completed Then
                Dim declined As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)

                For Each batch In waiting
                    If batch.IsDeclined Then
                        For Each spec In batch.Questions
                            declined.Add(spec.Id)
                        Next
                    End If
                Next

                Return RenderAnswers(specs, answers, declined)
            End If

            Return RenderPending(specs, waiting)
        End Function

        ''' <summary>
        ''' Merge the recorded answer of a batch into the answer map. A
        ''' batch the user declined - or a host answer that broke - simply
        ''' contributes nothing.
        ''' </summary>
        Private Shared Sub Collect(batch As AskBatch, answers As Dictionary(Of String, String))
            If Not batch.Arg.AnswerTask.IsCompleted Then
                Return
            End If

            Try
                Dim answer As UserAnswer = batch.Arg.AnswerTask.Result

                If answer Is Nothing OrElse answer.Declined OrElse answer.Responses Is Nothing Then
                    Return
                End If

                For Each pair In answer.Responses
                    answers(pair.Key) = pair.Value
                Next
            Catch
                ' a broken host answer must not break the tool call
            End Try
        End Sub

        ''' <summary>
        ''' Render the answers the tool hands back to the model: one block
        ''' per question of the call, in the order the model asked them.
        ''' </summary>
        Private Shared Function RenderAnswers(specs As List(Of UserQuestion),
                                              answers As Dictionary(Of String, String),
                                              declined As HashSet(Of String)) As String
            Dim sb As New StringBuilder()

            sb.AppendLine("<answers>")

            For Each spec In specs
                Dim response As String = Nothing

                sb.AppendLine($"<answer id=""{Escape(spec.Id)}"">")
                sb.AppendLine($"<question>{Escape(spec.Question)}</question>")

                If answers.TryGetValue(spec.Id, response) Then
                    sb.AppendLine($"<response>{Escape(response)}</response>")
                ElseIf declined.Contains(spec.Id) Then
                    sb.AppendLine("<response>the user declined to answer this question</response>")
                Else
                    sb.AppendLine("<response>no answer was given for this question</response>")
                End If

                sb.AppendLine("</answer>")
            Next

            sb.AppendLine()
            sb.Append("The user has answered; continue with these answers and do not ask the same question again.")

            Return sb.ToString()
        End Function

        ''' <summary>
        ''' Render the pending result of a timed call that ran out of time:
        ''' the questions stay open and answerable, and the model collects
        ''' the answer by calling the tool again with the same id.
        ''' </summary>
        Private Shared Function RenderPending(specs As List(Of UserQuestion), waiting As List(Of AskBatch)) As String
            Dim wanted As New HashSet(Of String)(specs.Select(Function(s) s.Id), StringComparer.OrdinalIgnoreCase)
            Dim open As New List(Of String)()

            For Each batch In waiting
                For Each spec In batch.Questions
                    If wanted.Contains(spec.Id) AndAlso Not open.Contains(spec.Id) Then
                        open.Add(spec.Id)
                    End If
                Next
            Next

            Dim sb As New StringBuilder()

            sb.AppendLine("<status>pending</status>")
            sb.AppendLine($"<question_ids>{Escape(String.Join(", ", open))}</question_ids>")
            sb.AppendLine()
            sb.Append("The user has not answered yet, but the question remains open and answerable. " &
                      "Continue with other work if you can, then call ask_user_question again with the same question id(s) to collect the answer.")

            Return sb.ToString()
        End Function

        ''' <summary>
        ''' Accept the two modes of the tool: omitted or "blocking" waits
        ''' until the user answered, "timed" applies the foreground timeout.
        ''' </summary>
        Private Shared Function ParseMode(mode As String, ByRef timed As Boolean, ByRef failure As String) As Boolean
            timed = False

            Select Case If(mode, "").Trim().ToLowerInvariant()
                Case "", ModeBlocking
                    Return True

                Case ModeTimed
                    timed = True
                    Return True

                Case Else
                    failure = $"Error: unknown mode '{mode}'. Expected '{ModeBlocking}' (wait until the user answers, the default) or '{ModeTimed}' (wait at most 'timeout' milliseconds, then return a pending result)."
                    Return False
            End Select
        End Function

        ''' <summary>
        ''' Coerce the timeout of the timed mode into a wait in
        ''' milliseconds, where infinity means "wait forever". A missing
        ''' timeout falls back to <see cref="DefaultTimeoutMs"/>; -1 is the
        ''' explicit infinite wait and 0 the non blocking poll.
        ''' </summary>
        Private Shared Function ParseTimeout(timeout As Double, ByRef waitMs As Double, ByRef failure As String) As Boolean
            If Double.IsNaN(timeout) Then
                waitMs = DefaultTimeoutMs
                Return True
            End If

            If Double.IsInfinity(timeout) Then
                If timeout > 0 Then
                    waitMs = Double.PositiveInfinity
                    Return True
                End If

                failure = ErrorTimeout(timeout)
                Return False
            End If

            If timeout = -1 Then
                waitMs = Double.PositiveInfinity
                Return True
            End If

            If timeout < 0 Then
                failure = ErrorTimeout(timeout)
                Return False
            End If

            waitMs = timeout
            Return True
        End Function

        Private Shared Function ErrorTimeout(timeout As Double) As String
            Return $"Error: 'timeout' must be -1 (wait indefinitely), 0 (return the pending result at once) or a positive number of milliseconds, got {timeout}."
        End Function

        ''' <summary>
        ''' Clamp a wait in milliseconds into the range
        ''' <see cref="Task.Wait(Integer)"/> accepts, so that an absurd
        ''' timeout cannot overflow the wait.
        ''' </summary>
        Private Shared Function ToMilliseconds(value As Double) As Integer
            If value >= Integer.MaxValue Then
                Return Integer.MaxValue
            End If

            Return CInt(Math.Ceiling(value))
        End Function

        ''' <summary>
        ''' Parse the question list the model sent. The value is a JSON
        ''' encoded string because the tool bridge only carries string
        ''' arguments; a single object is accepted as a one entry list, and
        ''' a double encoded JSON string is unwrapped.
        ''' </summary>
        Private Shared Function ParseQuestions(json As String, ByRef specs As List(Of UserQuestion), ByRef failure As String) As Boolean
            specs = Nothing

            Dim root As JsonElement

            Try
                root = LenientJsonParser.ParseJSON(json)
            Catch ex As Exception
                failure = $"Error: 'questions' is not valid JSON: {ex.Message}"
                Return False
            End Try

            ' the model sometimes sends the array as a JSON string
            If TypeOf root Is JsonValue Then
                Dim inner As Object = DirectCast(root, JsonValue).Literal()

                If TypeOf inner Is String AndAlso Not String.IsNullOrWhiteSpace(CStr(inner)) Then
                    Try
                        root = LenientJsonParser.ParseJSON(CStr(inner))
                    Catch ex As Exception
                        failure = $"Error: 'questions' is not valid JSON: {ex.Message}"
                        Return False
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
                        failure = "Error: every entry of 'questions' must be a JSON object with an 'id' and a 'question'."
                        Return False
                    End If

                    items.Add(item)
                Next
            Else
                failure = "Error: 'questions' must be a JSON array of question objects such as " &
                          "[{""id"":""mode"",""question"":""Which mode should I use?""}], given as a JSON encoded string."
                Return False
            End If

            If items.Count = 0 Then
                failure = "Error: 'questions' contains no question."
                Return False
            End If

            Dim parsed As New List(Of UserQuestion)()
            Dim seen As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)

            For i As Integer = 0 To items.Count - 1
                Dim item As JsonObject = items(i)
                Dim id As String = Member(item, "id", "question_id", "questionId")
                Dim text As String = Member(item, "question", "prompt")
                Dim header As String = Member(item, "header", "title")

                If String.IsNullOrWhiteSpace(id) Then
                    failure = $"Error: question {i + 1} of {items.Count} is missing its 'id'."
                    Return False
                End If

                If String.IsNullOrWhiteSpace(text) Then
                    failure = $"Error: question '{id}' is missing its 'question' text."
                    Return False
                End If

                If Not seen.Add(id) Then
                    failure = $"Error: question id '{id}' appears more than once in the same call; every question needs a unique id."
                    Return False
                End If

                Dim options As List(Of QuestionOption) = Nothing
                Dim optionsElement As JsonElement = Nothing

                If item.HasObjectKey("options") Then
                    optionsElement = item("options")
                End If

                If optionsElement IsNot Nothing AndAlso Not IsJsonNull(optionsElement) Then
                    options = ParseOptions(optionsElement, id, failure)

                    If options Is Nothing Then
                        Return False
                    End If
                End If

                parsed.Add(New UserQuestion With {
                    .Id = id,
                    .Question = text,
                    .Header = header,
                    .Options = options,
                    .MultiSelect = FlagMember(item, "multi_select", "multiSelect", "multiple")
                })
            Next

            specs = parsed
            Return True
        End Function

        ''' <summary>
        ''' Parse the option list of one question. An empty list is an open
        ''' question that is answered with free text; Nothing reports a
        ''' malformed list through <paramref name="failure"/>.
        ''' </summary>
        Private Shared Function ParseOptions(element As JsonElement, id As String, ByRef failure As String) As List(Of QuestionOption)
            Dim entries As New List(Of JsonObject)()

            If TypeOf element Is JsonObject Then
                entries.Add(DirectCast(element, JsonObject))
            ElseIf TypeOf element Is JsonArray Then
                For Each entry As JsonElement In DirectCast(element, JsonArray)
                    Dim item As JsonObject = TryCast(entry, JsonObject)

                    If item Is Nothing Then
                        failure = $"Error: every option of question '{id}' must be a JSON object with a 'label'."
                        Return Nothing
                    End If

                    entries.Add(item)
                Next
            Else
                failure = $"Error: 'options' of question '{id}' must be an array of objects with a 'label'."
                Return Nothing
            End If

            Dim options As New List(Of QuestionOption)()

            For Each item In entries
                Dim label As String = Member(item, "label")

                If String.IsNullOrWhiteSpace(label) Then
                    failure = $"Error: an option of question '{id}' is missing its 'label'."
                    Return Nothing
                End If

                options.Add(New QuestionOption With {
                    .Label = label,
                    .Description = Member(item, "description")
                })
            Next

            Return options
        End Function

        ''' <summary>
        ''' Read a string member, accepting the usual spellings a model may
        ''' use for it. Returns Nothing when no candidate is present.
        ''' </summary>
        Private Shared Function Member(item As JsonObject, ParamArray names As String()) As String
            For Each name In names
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

        Private Shared Function IsJsonNull(element As JsonElement) As Boolean
            Dim value As JsonValue = TryCast(element, JsonValue)
            Return value IsNot Nothing AndAlso value.IsLiteralNull
        End Function

        ''' <summary>
        ''' Escape the three XML metacharacters of a rendered value, so that
        ''' a question or an answer containing them cannot break the
        ''' tag structure the model reads.
        ''' </summary>
        Private Shared Function Escape(text As String) As String
            If String.IsNullOrEmpty(text) Then
                Return ""
            End If

            Return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
        End Function

        ''' <summary>
        ''' One batch of questions asked together: every id of the batch
        ''' maps to it in the registry, and the event argument carries the
        ''' answer of the whole batch.
        ''' </summary>
        Private NotInheritable Class AskBatch

            Public ReadOnly Property Questions As List(Of UserQuestion)
            Public ReadOnly Property Arg As QuestionEventArg

            Public Sub New(questions As List(Of UserQuestion), arg As QuestionEventArg)
                Me.Questions = questions
                Me.Arg = arg
            End Sub

            ''' <summary>True once the user dismissed this batch without answering.</summary>
            Public ReadOnly Property IsDeclined As Boolean
                Get
                    If Not Arg.AnswerTask.IsCompleted Then
                        Return False
                    End If

                    Try
                        Dim answer As UserAnswer = Arg.AnswerTask.Result
                        Return answer IsNot Nothing AndAlso answer.Declined
                    Catch
                        Return False
                    End Try
                End Get
            End Property

        End Class

    End Class

End Namespace
