Namespace Tools

    ''' <summary>
    ''' The shape of a job's output, which is what decides how job_output
    ''' reads it.
    ''' </summary>
    Public Enum JobShape

        ''' <summary>
        ''' A job that keeps producing output while it runs: every read
        ''' consumes what arrived since the previous read.
        ''' </summary>
        Stream

        ''' <summary>
        ''' A job that produces one result when it finishes: every read
        ''' returns that result again.
        ''' </summary>
        FinalOutput

    End Enum

    ''' <summary>The lifecycle state of a background job.</summary>
    Public Enum JobStatus

        ''' <summary>The job is doing its work.</summary>
        Running

        ''' <summary>
        ''' Cancellation was requested; the job has the grace period to stop
        ''' on its own before it is terminated.
        ''' </summary>
        Cancelling

        ''' <summary>The job stopped because it was cancelled.</summary>
        Cancelled

        ''' <summary>The job finished its work.</summary>
        Succeeded

        ''' <summary>The job stopped because of an error.</summary>
        Failed

    End Enum

    ''' <summary>
    ''' The kind labels job_start accepts and job_list reports: a background
    ''' command, or a background sub agent task.
    ''' </summary>
    Public Module JobKinds

        ''' <summary>A command running in the background shell.</summary>
        Public Const Shell As String = "shell"

        ''' <summary>A task running in the background through the skill pipeline.</summary>
        Public Const Agent As String = "agent"

    End Module

    ''' <summary>
    ''' An immutable view of one background job - what the job_* tools render.
    ''' Taking a snapshot instead of handing the job object out keeps the
    ''' mutable state (buffers, process handle, token) inside the engine.
    ''' </summary>
    Public Class JobSummary

        Friend Sub New(id As String, kind As String, shape As JobShape, status As JobStatus, description As String,
                       startedAt As DateTime, finishedAt As DateTime?, exitCode As Integer?, note As String)
            Me.Id = id
            Me.Kind = kind
            Me.Shape = shape
            Me.Status = status
            Me.Description = description
            Me.StartedAt = startedAt
            Me.FinishedAt = finishedAt
            Me.ExitCode = exitCode
            Me.Note = note
        End Sub

        ''' <summary>The id the model passes back to job_kill and job_output.</summary>
        Public ReadOnly Property Id As String

        ''' <summary>Either "shell" or "agent".</summary>
        Public ReadOnly Property Kind As String

        ''' <summary>Whether the output is streamed or produced once at the end.</summary>
        Public ReadOnly Property Shape As JobShape

        ''' <summary>The current state of the job.</summary>
        Public ReadOnly Property Status As JobStatus

        ''' <summary>One line description of the work: the command, or the task.</summary>
        Public ReadOnly Property Description As String

        ''' <summary>When the job was started.</summary>
        Public ReadOnly Property StartedAt As DateTime

        ''' <summary>When the job reached its final state, or Nothing while it runs.</summary>
        Public ReadOnly Property FinishedAt As DateTime?

        ''' <summary>Exit code of a command job, or Nothing when there is none.</summary>
        Public ReadOnly Property ExitCode As Integer?

        ''' <summary>Cancellation reason and other notes recorded for the job.</summary>
        Public ReadOnly Property Note As String

        ''' <summary>How long the job has been - or had been - running.</summary>
        Public ReadOnly Property Elapsed As TimeSpan
            Get
                If FinishedAt.HasValue Then
                    Return FinishedAt.Value - StartedAt
                End If

                Return DateTime.Now - StartedAt
            End Get
        End Property

        ''' <summary>True once the job reached a final state.</summary>
        Public ReadOnly Property IsFinished As Boolean
            Get
                Return Status = JobStatus.Cancelled OrElse Status = JobStatus.Succeeded OrElse Status = JobStatus.Failed
            End Get
        End Property

        ''' <summary>The status as it is shown to the model.</summary>
        Public ReadOnly Property StatusText As String
            Get
                Return JobText.StatusText(Status)
            End Get
        End Property

    End Class

    ''' <summary>
    ''' The output a stream shaped job produced since the previous read.
    ''' Reading is consuming, so the text handed back here is never handed
    ''' back twice; <see cref="Dropped"/> reports how many characters were
    ''' discarded before the reader got to them, which is the only hole the
    ''' bounded buffer may leave.
    ''' </summary>
    Public Class JobOutputChunk

        Friend Sub New(stdout As String, stderr As String, dropped As Long)
            Me.Stdout = stdout
            Me.Stderr = stderr
            Me.Dropped = dropped
        End Sub

        ''' <summary>Standard output produced since the previous read.</summary>
        Public ReadOnly Property Stdout As String

        ''' <summary>Standard error produced since the previous read.</summary>
        Public ReadOnly Property Stderr As String

        ''' <summary>Characters the bounded buffer had to drop before this read.</summary>
        Public ReadOnly Property Dropped As Long

        ''' <summary>True when there is nothing new to report.</summary>
        Public ReadOnly Property IsEmpty As Boolean
            Get
                Return String.IsNullOrEmpty(Stdout) AndAlso String.IsNullOrEmpty(Stderr) AndAlso Dropped = 0
            End Get
        End Property

    End Class

    ''' <summary>
    ''' The raw material job_output renders: the job snapshot, the output
    ''' produced since the previous read, the (idempotent) result of a result
    ''' shaped job, and whether an asked for wait timed out.
    ''' </summary>
    Public Class JobOutput

        Friend Sub New(summary As JobSummary, chunk As JobOutputChunk, result As String, timedOut As Boolean, waitedMs As Integer)
            Me.Summary = summary
            Me.Chunk = chunk
            Me.Result = result
            Me.TimedOut = timedOut
            Me.WaitedMs = waitedMs
        End Sub

        ''' <summary>The state of the job after the optional wait.</summary>
        Public ReadOnly Property Summary As JobSummary

        ''' <summary>New output of a stream shaped job, consumed by this read.</summary>
        Public ReadOnly Property Chunk As JobOutputChunk

        ''' <summary>
        ''' The stored result of a result shaped job, or Nothing while there
        ''' is none yet.
        ''' </summary>
        Public ReadOnly Property Result As String

        ''' <summary>True when wait was asked for and the wait ran out first.</summary>
        Public ReadOnly Property TimedOut As Boolean

        ''' <summary>The wait that was applied, in milliseconds.</summary>
        Public ReadOnly Property WaitedMs As Integer

    End Class

    ''' <summary>
    ''' The small text helpers the job tools and the engine share: status
    ''' labels, elapsed time rendering, description trimming and the wait
    ''' band of job_output.
    ''' </summary>
    Friend Module JobText

        ''' <summary>Longest one line description kept for a job.</summary>
        Friend Const MaxDescriptionChars As Integer = 160

        ''' <summary>Collapse a command or a task into one trimmed, bounded line.</summary>
        Friend Function Summarise(text As String) As String
            Dim value As String = If(text, "").Trim()
            value = value.Replace(vbCrLf, " ").Replace(vbLf, " ").Replace(vbCr, " ").Replace(vbTab, " ")

            While value.Contains("  ")
                value = value.Replace("  ", " ")
            End While

            If value.Length <= MaxDescriptionChars Then
                Return value
            End If

            Return value.Substring(0, MaxDescriptionChars) & "..."
        End Function

        ''' <summary>The status as it is shown to the model.</summary>
        Friend Function StatusText(status As JobStatus) As String
            Select Case status
                Case JobStatus.Running
                    Return "running"
                Case JobStatus.Cancelling
                    Return "cancelling"
                Case JobStatus.Cancelled
                    Return "cancelled"
                Case JobStatus.Succeeded
                    Return "succeeded"
                Case Else
                    Return "failed"
            End Select
        End Function

        ''' <summary>Render a duration the way a status line should read.</summary>
        Friend Function FormatElapsed(value As TimeSpan) As String
            If value.TotalSeconds < 60.0 Then
                Return $"{value.TotalSeconds:F1} s"
            End If

            If value.TotalMinutes < 60.0 Then
                Return $"{Math.Floor(value.TotalMinutes)} min {value.Seconds:D2} s"
            End If

            Return $"{Math.Floor(value.TotalHours)} h {value.Minutes:D2} min"
        End Function

        ''' <summary>
        ''' Coerce the wait asked for by job_output into the configured band:
        ''' a missing or absurd value falls back to the default, and an
        ''' oversized one is capped.
        ''' </summary>
        Friend Function ClampWait(value As Double) As Integer
            If Double.IsNaN(value) OrElse Double.IsInfinity(value) OrElse value <= 0 Then
                Return BackgroundJobManager.DefaultWaitMs
            End If

            If value >= BackgroundJobManager.MaxWaitMs Then
                Return BackgroundJobManager.MaxWaitMs
            End If

            Return CInt(Math.Floor(value))
        End Function

        ''' <summary>Render whatever a result shaped job produced.</summary>
        Friend Function RenderValue(value As Object) As String
            If value Is Nothing Then
                Return "(the job produced no result)"
            End If

            Dim text As String = TryCast(value, String)

            If text IsNot Nothing Then
                Return text.Trim()
            End If

            Return Convert.ToString(value, Globalization.CultureInfo.InvariantCulture)
        End Function

    End Module

End Namespace
