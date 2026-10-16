Imports System.Text
Imports System.Threading
Imports Harness.SkillSystem
Imports Ollama

Namespace Tools

    ''' <summary>
    ''' The background job registry of one session: it starts jobs, keeps
    ''' their state, and hands snapshots and output back to the job_* tools.
    ''' 
    ''' One manager belongs to one <see cref="Sandbox"/> (see
    ''' <see cref="Sandbox.Jobs"/>), so background work lives exactly as long
    ''' as the session that started it: jobs are process local and are never
    ''' persisted.
    ''' 
    ''' Two kinds of work can be started:
    ''' 
    '''   - <see cref="StartShellJob"/> a command running in a child process,
    '''     reported as a stream shaped job;
    '''   - <see cref="StartAgentJob"/> a task routed through the skill
    '''     pipeline in the background, reported as a result shaped job.
    '''     <see cref="StartResultJob"/> is the delegate based entry point
    '''     behind it, so hosts and tests can run their own work as a
    '''     background job without an LLM.
    ''' </summary>
    Public Class BackgroundJobManager

        ''' <summary>
        ''' How long a cancelled job may keep running before its process tree
        ''' is terminated.
        ''' </summary>
        Public Const KillGraceMs As Integer = 5000

        ''' <summary>Wait applied by job_output(wait:=True) when none is asked for.</summary>
        Public Const DefaultWaitMs As Integer = ProcessLaunch.DefaultTimeoutMs

        ''' <summary>Upper bound on that wait.</summary>
        Public Const MaxWaitMs As Integer = ProcessLaunch.MaxTimeoutMs

        Private ReadOnly _sync As New Object()
        Private ReadOnly _jobs As New List(Of BackgroundJob)()
        Private ReadOnly _byId As New Dictionary(Of String, BackgroundJob)(StringComparer.OrdinalIgnoreCase)

        Private _nextId As Integer
        Private _client As LLMClient
        Private _agent As SkillAgent

        Friend Sub New(workspace As String)
            Me.Workspace = workspace
            Me.SkillsDirectory = IO.Path.Combine(workspace, Sandbox.ArtifactFolder, "skills")
        End Sub

        ''' <summary>The workspace the jobs are started for.</summary>
        Public ReadOnly Property Workspace As String

        ''' <summary>
        ''' Root folder of the skills a background task may use; it is also
        ''' what the sub agent scans for its skill catalog. Defaults to
        ''' "&lt;workspace&gt;\.harness\skills", and must be set before the
        ''' first task job is started (the agent is created once and cached).
        ''' </summary>
        Public Property SkillsDirectory As String

        ''' <summary>Number of jobs started in this session.</summary>
        Public ReadOnly Property Count As Integer
            Get
                SyncLock _sync
                    Return _jobs.Count
                End SyncLock
            End Get
        End Property

        ''' <summary>
        ''' Hand the session LLM client over. Background task jobs need it;
        ''' <see cref="Sandbox.RegisterTo"/> does this while it registers the
        ''' tools, so that a job started through job_start can use the very
        ''' same client the session talks to.
        ''' </summary>
        Public Sub AttachClient(client As LLMClient)
            SyncLock _sync
                _client = client
            End SyncLock
        End Sub

        ''' <summary>True when a background task (sub agent) job can be started.</summary>
        Public ReadOnly Property ClientAttached As Boolean
            Get
                SyncLock _sync
                    Return _client IsNot Nothing
                End SyncLock
            End Get
        End Property

        ' ====================================================================
        ' Starting work
        ' ====================================================================

        ''' <summary>
        ''' Start a command in the background and return the job id. The
        ''' working directory must already have been authorized by the
        ''' caller (the job_start tool does that, exactly like the shell
        ''' tool does for a foreground command).
        ''' </summary>
        Public Function StartShellJob(command As String, workDir As String) As String
            Dim job As BackgroundJob = Register(JobKinds.Shell, JobShape.Stream, JobText.Summarise(command))

            Call ShellJobRunner.Start(job, command, workDir)

            Return job.Id
        End Function

        ''' <summary>
        ''' Start a piece of work that produces one result and return the job
        ''' id. The delegate is the extension point of the engine: the "task"
        ''' kind hands the sub agent in, a host can hand anything else in.
        ''' The work receives the job token, so it can stop when the job is
        ''' cancelled.
        ''' </summary>
        Public Function StartResultJob(description As String, work As Func(Of CancellationToken, Task(Of Object))) As String
            Dim job As BackgroundJob = Register(JobKinds.Agent, JobShape.FinalOutput, JobText.Summarise(description))

            ' The work runs on its own thread pool task: nothing here blocks
            ' the tool call, and the job completes when the work does.
            Call Task.Run(Function() RunResultJobAsync(job, work))

            Return job.Id
        End Function

        Private Shared Async Function RunResultJobAsync(job As BackgroundJob, work As Func(Of CancellationToken, Task(Of Object))) As Task
            Try
                Dim value As Object = Await work(job.Cancellation.Token)

                Call job.SetResult(value)
                Call job.MarkFinished(JobStatus.Succeeded, 0)
            Catch ex As OperationCanceledException
                Call job.MarkFinished(JobStatus.Cancelled, Nothing)
            Catch ex As Exception
                Call job.AppendNote($"the job failed: {ex.Message}")
                Call job.MarkFinished(JobStatus.Failed, -1)
            End Try
        End Function

        ''' <summary>
        ''' Start a background task: the text is routed through the skill
        ''' pipeline (which may run scripts of the matched skill) and the
        ''' final answer becomes the job result. Returns Nothing when the
        ''' session cannot run a task job, with the reason in
        ''' <paramref name="failure"/>.
        ''' </summary>
        Public Function StartAgentJob(task As String, ByRef failure As String) As String
            failure = Nothing

            Dim agent As SkillAgent = GetAgent(failure)

            If agent Is Nothing Then
                Return Nothing
            End If

            Return StartResultJob(task, Function(token) RunAgentAsync(agent, task, token))
        End Function

        Private Shared Async Function RunAgentAsync(agent As SkillAgent, task As String, token As CancellationToken) As Task(Of Object)
            Dim result As SkillExecutionResult = Await agent.RunTask(task, token)

            Return RenderAgentAnswer(result)
        End Function

        ''' <summary>
        ''' The answer of a task job, prefixed with the skill that was matched
        ''' so that the reader can tell how the task was routed.
        ''' </summary>
        Private Shared Function RenderAgentAnswer(result As SkillExecutionResult) As String
            If result Is Nothing Then
                Return "the task produced no answer."
            End If

            Dim sb As New StringBuilder()

            If Not String.IsNullOrEmpty(result.MatchedSkillName) Then
                Call sb.AppendLine($"matched skill: {result.MatchedSkillName}")
                Call sb.AppendLine()
            End If

            Call sb.Append(If(result.Answer, "").Trim())

            Return sb.ToString()
        End Function

        ''' <summary>
        ''' The cached sub agent of this session, or Nothing when the session
        ''' has no LLM client or no skills folder to route tasks to.
        ''' </summary>
        Private Function GetAgent(ByRef failure As String) As SkillAgent
            SyncLock _sync
                If _agent IsNot Nothing Then
                    Return _agent
                End If

                If _client Is Nothing Then
                    failure = "Error: this session has no LLM client, so a background task cannot be started. Start a background command instead."
                    Return Nothing
                End If

                Dim folder As String = SkillsDirectory

                If String.IsNullOrWhiteSpace(folder) OrElse Not IO.Directory.Exists(folder) Then
                    failure = $"Error: no skills folder was found at '{folder}', so a background task cannot be started. Start a background command instead."
                    Return Nothing
                End If

                ' Constructing the agent scans the skill catalog and registers
                ' the script tool on the client, so it happens once per
                ' session and only when a task job is actually asked for.
                _agent = New SkillAgent(_client, folder)

                Return _agent
            End SyncLock
        End Function

        ' ====================================================================
        ' Reading the registry
        ' ====================================================================

        ''' <summary>Every job of the session, running and finished, in start order.</summary>
        Public Function List() As IReadOnlyList(Of JobSummary)
            Dim jobs As New List(Of JobSummary)

            For Each job As BackgroundJob In Snapshot()
                Call jobs.Add(job.Snapshot())
            Next

            Return jobs
        End Function

        ''' <summary>The snapshot of one job, or Nothing for an unknown id.</summary>
        Public Function GetSummary(jobId As String) As JobSummary
            Dim job As BackgroundJob = Find(jobId)

            If job Is Nothing Then
                Return Nothing
            End If

            Return job.Snapshot()
        End Function

        ''' <summary>
        ''' Read a job: wait for it first when asked (a wait that runs out
        ''' leaves the job running), then hand the raw material back for
        ''' rendering. Returns Nothing for an unknown id.
        ''' </summary>
        Public Function Output(jobId As String, wait As Boolean, timeoutMs As Integer) As JobOutput
            Dim job As BackgroundJob = Find(jobId)

            If job Is Nothing Then
                Return Nothing
            End If

            Dim budget As Integer = 0
            Dim timedOut As Boolean = False

            If wait Then
                budget = JobText.ClampWait(timeoutMs)
                timedOut = Not job.WaitForCompletion(budget)
            End If

            Return New JobOutput(job.Snapshot(), job.ReadNewOutput(), job.ReadResult(), timedOut, budget)
        End Function

        ' ====================================================================
        ' Cancelling
        ' ====================================================================

        ''' <summary>
        ''' Request cancellation of one job: the reason is recorded in the job
        ''' log and reaches its work through the job token. Returns Nothing
        ''' when the request was accepted, otherwise the message to report.
        ''' </summary>
        Public Function Cancel(jobId As String, reason As String) As String
            Dim job As BackgroundJob = Find(jobId)

            If job Is Nothing Then
                Return $"Error: no such background job: {jobId}"
            End If

            If Not job.BeginCancelling(reason) Then
                Dim summary As JobSummary = job.Snapshot()

                Return $"Error: background job '{summary.Id}' has already finished ({summary.StatusText})."
            End If

            ' Watchdog: a job that ignores the request is terminated after the
            ' grace period. It runs as a thread pool task rather than a timer
            ' so that no thread is kept alive per job.
            Call Task.Run(Sub() TerminateAfterGrace(job))

            Return Nothing
        End Function

        Private Shared Sub TerminateAfterGrace(job As BackgroundJob)
            If job.WaitForCompletion(KillGraceMs) Then
                Return
            End If

            Call job.AppendNote($"the job did not stop within {KillGraceMs} ms, so it was terminated.")

            Call job.ForceKillProcessTree()

            ' Give the runner a moment to report the exit code the kill
            ' produced: it is more useful than "ended without one".
            If Not job.WaitForCompletion(1000) Then
                Call job.MarkFinished(JobStatus.Cancelled, Nothing)
            End If
        End Sub

        ''' <summary>
        ''' Stop everything: the host calls this before it exits. Each running
        ''' job is asked to stop and then given the grace period, so a job
        ''' that reacts to the request never has to be terminated.
        ''' </summary>
        Public Sub ShutdownAll()
            For Each job As BackgroundJob In Snapshot()
                If Not job.IsFinished Then
                    Call Cancel(job.Id, "the host session is shutting down.")
                End If
            Next

            For Each job As BackgroundJob In Snapshot()
                Call job.WaitForCompletion(KillGraceMs)
            Next
        End Sub

        ' ====================================================================
        ' Internals
        ' ====================================================================

        Private Function Register(kind As String, shape As JobShape, description As String) As BackgroundJob
            SyncLock _sync
                _nextId += 1

                Dim job As New BackgroundJob($"job-{_nextId}", kind, shape, description)

                Call _jobs.Add(job)
                Call _byId.Add(job.Id, job)

                Return job
            End SyncLock
        End Function

        ''' <summary>The job behind an id, or Nothing when there is none.</summary>
        Friend Function Find(jobId As String) As BackgroundJob
            If String.IsNullOrWhiteSpace(jobId) Then
                Return Nothing
            End If

            SyncLock _sync
                Return _byId.TryGetValue(jobId.Trim())
            End SyncLock
        End Function

        ''' <summary>
        ''' The job objects themselves, taken under the lock so that the list
        ''' cannot change while a caller walks it.
        ''' </summary>
        Private Function Snapshot() As BackgroundJob()
            SyncLock _sync
                Return _jobs.ToArray()
            End SyncLock
        End Function

    End Class

End Namespace
