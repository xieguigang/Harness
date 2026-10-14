Imports System
Imports System.Diagnostics
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks

Namespace Tools

    ''' <summary>
    ''' One background job: its identity, its state machine, its bounded
    ''' output buffer and its completion signal.
    ''' 
    ''' The engine writes to it (the runner appends output, the manager moves
    ''' the status), the tools only read <see cref="Snapshot"/> views of it, so
    ''' the mutable object never leaves the engine.
    ''' 
    ''' Two output shapes share the class:
    ''' 
    '''   - <see cref="JobShape.Stream"/> a command that keeps producing
    '''     output. Reading is consuming: a read hands back everything that
    '''     arrived since the previous read and releases it, so a long running
    '''     job cannot grow the process without bound;
    '''   - <see cref="JobShape.FinalOutput"/> a task that produces one result
    '''     when it is done. The result is kept until the session ends, so
    '''     every read returns it again.
    ''' </summary>
    Friend Class BackgroundJob

        ''' <summary>
        ''' Upper bound on the characters held per stream until the reader
        ''' consumes them. The oldest characters are dropped when a very
        ''' chatty command outruns the reader, and the amount is reported by
        ''' the next read instead of silently losing it.
        ''' </summary>
        Friend Const MaxBufferChars As Integer = 256000

        ''' <summary>
        ''' Upper bound on the note log (cancellation reasons, failure text,
        ''' the kill escalation), which is tiny but must stay bounded too.
        ''' </summary>
        Private Const MaxNoteChars As Integer = 8000

        Private ReadOnly _sync As New Object()
        Private ReadOnly _stdout As New StringBuilder()
        Private ReadOnly _stderr As New StringBuilder()
        Private ReadOnly _notes As New StringBuilder()
        Private ReadOnly _cancellation As New CancellationTokenSource()
        Private ReadOnly _completion As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)

        Private _stdoutDropped As Long
        Private _stderrDropped As Long
        Private _status As JobStatus = JobStatus.Running
        Private _exitCode As Integer?
        Private _finishedAt As DateTime?
        Private _result As String
        Private _hasResult As Boolean
        Private _child As Process

        Friend Sub New(id As String, kind As String, shape As JobShape, description As String)
            Me.Id = id
            Me.Kind = kind
            Me.Shape = shape
            Me.Description = description
            Me.StartedAt = DateTime.Now
        End Sub

        ''' <summary>The job id handed to the model by the starting tool.</summary>
        Friend ReadOnly Property Id As String

        ''' <summary>"shell" or "agent".</summary>
        Friend ReadOnly Property Kind As String

        ''' <summary>Stream or FinalOutput.</summary>
        Friend ReadOnly Property Shape As JobShape

        ''' <summary>One line description of the work.</summary>
        Friend ReadOnly Property Description As String

        ''' <summary>When the job was started.</summary>
        Friend ReadOnly Property StartedAt As DateTime

        ''' <summary>
        ''' The token source the job hands to its work, so that a cancellation
        ''' request reaches the work itself and not only the job bookkeeping.
        ''' </summary>
        Friend ReadOnly Property Cancellation As CancellationTokenSource
            Get
                Return _cancellation
            End Get
        End Property

        ''' <summary>Completes once the job reached a final status.</summary>
        Friend ReadOnly Property Completion As Task
            Get
                Return _completion.Task
            End Get
        End Property

        ''' <summary>The current status, read under the job lock.</summary>
        Friend ReadOnly Property Status As JobStatus
            Get
                SyncLock _sync
                    Return _status
                End SyncLock
            End Get
        End Property

        ''' <summary>True once the job reached a final status.</summary>
        Friend ReadOnly Property IsFinished As Boolean
            Get
                SyncLock _sync
                    Return IsFinal(_status)
                End SyncLock
            End Get
        End Property

        Private Shared Function IsFinal(value As JobStatus) As Boolean
            Return value = JobStatus.Cancelled OrElse value = JobStatus.Succeeded OrElse value = JobStatus.Failed
        End Function

        ' ====================================================================
        ' Output
        ' ====================================================================

        ''' <summary>Append one line of standard output.</summary>
        Friend Sub AppendStdout(line As String)
            SyncLock _sync
                Call Append(0, line)
            End SyncLock
        End Sub

        ''' <summary>Append one line of standard error.</summary>
        Friend Sub AppendStderr(line As String)
            SyncLock _sync
                Call Append(1, line)
            End SyncLock
        End Sub

        ''' <summary>
        ''' Append one line to the given stream, dropping the oldest characters
        ''' of that stream when the buffer is full. Called on the output reader
        ''' threads, so it only locks, appends and trims.
        ''' </summary>
        Private Sub Append(stream As Integer, line As String)
            Dim buffer As StringBuilder = If(stream = 0, _stdout, _stderr)

            Call buffer.AppendLine(If(line, ""))

            Dim drop As Integer = buffer.Length - MaxBufferChars

            If drop <= 0 Then
                Return
            End If

            ' The reader never saw these characters: drop them and remember
            ' how many, so that the next read can report the hole.
            Call buffer.Remove(0, drop)

            If stream = 0 Then
                _stdoutDropped += drop
            Else
                _stderrDropped += drop
            End If
        End Sub

        ''' <summary>
        ''' Record a note for the job: a cancellation reason, the kill
        ''' escalation, or the text of a failure.
        ''' </summary>
        Friend Sub AppendNote(text As String)
            If String.IsNullOrWhiteSpace(text) Then
                Return
            End If

            SyncLock _sync
                If _notes.Length > 0 Then
                    Call _notes.AppendLine()
                End If

                Call _notes.Append(text.Trim())

                If _notes.Length > MaxNoteChars Then
                    Call _notes.Remove(0, _notes.Length - MaxNoteChars)
                End If
            End SyncLock
        End Sub

        ''' <summary>
        ''' Hand back every line that arrived since the previous read and
        ''' release it: a stream shaped job is consumed by reading it.
        ''' </summary>
        Friend Function ReadNewOutput() As JobOutputChunk
            SyncLock _sync
                Dim stdout As String = Take(_stdout)
                Dim stderr As String = Take(_stderr)
                Dim dropped As Long = _stdoutDropped + _stderrDropped

                ' The hole is reported once: after this read the reader knows
                ' about it.
                _stdoutDropped = 0
                _stderrDropped = 0

                Return New JobOutputChunk(stdout, stderr, dropped)
            End SyncLock
        End Function

        ''' <summary>
        ''' Take the unread content of one stream and clear the buffer, so the
        ''' memory of a long running job does not accumulate.
        ''' </summary>
        Private Shared Function Take(buffer As StringBuilder) As String
            If buffer.Length = 0 Then
                Return Nothing
            End If

            Dim text As String = buffer.ToString()

            Call buffer.Clear()

            Return text
        End Function

        ''' <summary>Store the result of a result shaped job.</summary>
        Friend Sub SetResult(value As Object)
            SyncLock _sync
                _result = JobText.RenderValue(value)
                _hasResult = True
            End SyncLock
        End Sub

        ''' <summary>
        ''' The stored result, or Nothing while the job produced none. Reading
        ''' it never clears it, so the read is idempotent.
        ''' </summary>
        Friend Function ReadResult() As String
            SyncLock _sync
                Return If(_hasResult, _result, Nothing)
            End SyncLock
        End Function

        ' ====================================================================
        ' Lifecycle
        ' ====================================================================

        ''' <summary>Keep the process the job owns, so it can be terminated.</summary>
        Friend Sub AttachProcess(child As Process)
            SyncLock _sync
                _child = child
            End SyncLock
        End Sub

        ''' <summary>
        ''' Terminate the process tree of the job. A no-op for jobs that do
        ''' not own a process (a sub agent runs inside this process, and its
        ''' own children are out of reach - see the runner).
        ''' </summary>
        Friend Sub ForceKillProcessTree()
            Dim child As Process = Nothing

            SyncLock _sync
                child = _child
            End SyncLock

            If child Is Nothing Then
                Return
            End If

            Try
                If Not child.HasExited Then
                    Call child.Kill(entireProcessTree:=True)
                End If
            Catch
                ' the process tree is already gone
            End Try
        End Sub

        ''' <summary>
        ''' Ask the work of a running job to stop: the status moves to
        ''' cancelling, the reason is recorded in the job log and the token is
        ''' signalled. Returns False when the job cannot be cancelled any more
        ''' (it already finished, or a cancellation is already in flight).
        ''' </summary>
        Friend Function BeginCancelling(reason As String) As Boolean
            SyncLock _sync
                If _status <> JobStatus.Running Then
                    Return False
                End If

                _status = JobStatus.Cancelling
            End SyncLock

            Call AppendNote(If(String.IsNullOrWhiteSpace(reason),
                               $"cancellation requested by the agent at {DateTime.Now.ToString("HH:mm:ss")}.",
                               $"cancellation requested by the agent at {DateTime.Now.ToString("HH:mm:ss")}: {reason.Trim()}"))

            Try
                _cancellation.Cancel()
            Catch ex As ObjectDisposedException
                ' the work finished while the request was in flight
            End Try

            Return True
        End Function

        ''' <summary>
        ''' Move the job to a final status and wake every waiter. Ignored once
        ''' the job already finished, and a job that was asked to stop always
        ''' reports cancelled, whatever its work decided to do afterwards.
        ''' </summary>
        Friend Sub MarkFinished(status As JobStatus, exitCode As Integer?)
            Dim child As Process = Nothing

            SyncLock _sync
                If IsFinal(_status) Then
                    Return
                End If

                If _status = JobStatus.Cancelling AndAlso status <> JobStatus.Cancelled Then
                    status = JobStatus.Cancelled
                End If

                _status = status
                _exitCode = exitCode
                _finishedAt = DateTime.Now
                child = _child
                _child = Nothing
            End SyncLock

            If child IsNot Nothing Then
                Try
                    Call child.Dispose()
                Catch
                    ' nothing left to release
                End Try
            End If

            ' The token source is deliberately not disposed here: the work of
            ' a cancelled job may still be unwinding and reading its token.
            Call _completion.TrySetResult(True)
        End Sub

        ''' <summary>
        ''' Wait for the job to finish. False means the wait ran out and the
        ''' job is still running.
        ''' </summary>
        Friend Function WaitForCompletion(timeoutMs As Integer) As Boolean
            Return _completion.Task.Wait(timeoutMs)
        End Function

        ''' <summary>An immutable view of the job, safe to render.</summary>
        Friend Function Snapshot() As JobSummary
            SyncLock _sync
                Return New JobSummary(Me.Id, Me.Kind, Me.Shape, _status, Me.Description, Me.StartedAt,
                                      _finishedAt, _exitCode, _notes.ToString())
            End SyncLock
        End Function

    End Class

End Namespace
