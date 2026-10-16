Imports System.ComponentModel
Imports System.IO
Imports System.Text
Imports Microsoft.VisualBasic.CommandLine.Reflection

Namespace Tools

    ''' <summary>
    ''' Tool "job_start": hand work over to the background and get a job id
    ''' back at once, so that a command that runs for minutes does not block
    ''' the conversation.
    ''' 
    ''' The tool starts exactly one of two things:
    ''' 
    '''   - <c>command</c>: a shell command, run in a child process and
    '''     reported as a stream shaped job - read it with job_output to see
    '''     what it printed since the previous read;
    '''   - <c>task</c>: a natural language task, run in the background through
    '''     the skill pipeline and reported as a result shaped job - read it
    '''     with job_output once it finished.
    ''' 
    ''' A command is authorized against the sandbox like the "shell" tool is:
    ''' its working directory is resolved and checked for the Execute
    ''' operation, and a denied call may be retried once with
    ''' <c>sandbox_permissions</c> plus a justification.
    ''' 
    ''' A background command has no automatic timeout - being in the
    ''' background is what replaces the timeout of the foreground tool. It
    ''' runs until it ends on its own or until job_kill stops it, and the
    ''' elapsed time reported by job_list is what keeps a forgotten job
    ''' visible.
    ''' </summary>
    Public Class JobStartTool

        Private ReadOnly _sandbox As Sandbox

        ''' <param name="sandbox">The sandbox that resolves the paths and enforces the security policy.</param>
        Public Sub New(sandbox As Sandbox)
            If sandbox Is Nothing Then
                Throw New ArgumentNullException(NameOf(sandbox))
            End If

            _sandbox = sandbox
        End Sub

        ''' <summary>
        ''' Start a background command or a background task and return its job
        ''' id.
        ''' </summary>
        ''' <param name="command">The command line to run in the background. Give this or <paramref name="task"/>, never both.</param>
        ''' <param name="task">The natural language task to run in the background. Give this or <paramref name="command"/>, never both.</param>
        ''' <param name="kind">Optional consistency check: "shell" for a background command, "agent" for a background task.</param>
        ''' <param name="workdir">Working directory for a background command. Defaults to the session workspace; a relative path resolves against it.</param>
        ''' <param name="sandbox_permissions">
        ''' The narrowest wider sandbox mode for a one-shot retry of the exact
        ''' operation the sandbox just denied; the retry asks the user for
        ''' approval. Either "workspace-write" (target inside the workspace) or
        ''' "danger-full-access" (target outside it). Leave empty unless the
        ''' sandbox denied the operation.
        ''' </param>
        ''' <param name="justification">
        ''' Required with sandbox_permissions: one sentence for the user
        ''' explaining why this exact operation needs the wider access. Use the
        ''' language of the user current request.
        ''' </param>
        <Description("Start background work and return a job id immediately, so that a long running command or task does not block the conversation. Give either ""command"" (a shell command: its output is read as a stream) or ""task"" (a natural language task run through the skills: it produces one final result), never both. Read the job with job_output, list the jobs with job_list and stop one with job_kill.")>
        <ExportAPI("job_start")>
        Public Function job_start(
            <Argument("command", Description:="The command line to run in the background. It is executed by the platform shell: PowerShell on Windows, bash elsewhere. Its output is read incrementally with job_output. Give this or ""task"", never both.")> Optional command As String = Nothing,
            <Argument("task", Description:="A natural language task to run in the background through the skill pipeline; it produces one final result, read with job_output once the job finished. Give this or ""command"", never both.")> Optional task As String = Nothing,
            <Argument("kind", Description:="Optional consistency check of what is being started: ""shell"" for a background command, ""agent"" for a background task. Omit it, or repeat the kind that matches the argument you gave.")> Optional kind As String = Nothing,
            <Argument("workdir", Description:="Working directory for a background command. Defaults to the session workspace; a relative path resolves against it.")> Optional workdir As String = Nothing,
            <Argument("sandbox_permissions", Description:="The narrowest wider sandbox mode for a one-shot retry of the exact operation the sandbox just denied; the retry asks the user for approval. One of ""workspace-write"" (target inside the workspace) or ""danger-full-access"" (target outside it). Leave empty unless the sandbox denied the operation.")> Optional sandbox_permissions As String = Nothing,
            <Argument("justification", Description:="Required with sandbox_permissions: one sentence for the user explaining why this exact operation needs the wider access. Use the language of the user's current request.")> Optional justification As String = Nothing
        ) As String
            Dim hasCommand As Boolean = Not String.IsNullOrWhiteSpace(command)
            Dim hasTask As Boolean = Not String.IsNullOrWhiteSpace(task)

            If Not hasCommand AndAlso Not hasTask Then
                Return "Error: job_start needs either 'command' (a background shell command) or 'task' (a background task), and neither was given."
            End If

            If hasCommand AndAlso hasTask Then
                Return "Error: 'command' and 'task' are mutually exclusive; give exactly one of them."
            End If

            Dim expected As String = If(hasCommand, JobKinds.Shell, JobKinds.Agent)
            Dim requested As String = If(String.IsNullOrWhiteSpace(kind), expected, kind.Trim().ToLowerInvariant())

            If requested <> JobKinds.Shell AndAlso requested <> JobKinds.Agent Then
                Return $"Error: unknown kind '{kind}'. Expected '{JobKinds.Shell}' or '{JobKinds.Agent}'."
            End If

            If requested <> expected Then
                Dim given As String = If(hasCommand, "command", "task")

                Return $"Error: kind '{kind}' does not match the argument: '{given}' starts a '{expected}' job."
            End If

            If hasTask Then
                Return StartTask(task.Trim())
            End If

            Return StartCommand(command, workdir, sandbox_permissions, justification)
        End Function

        ''' <summary>
        ''' Start a background task. The engine owns the sub agent (it needs
        ''' the session LLM client and the skills folder), and reports why the
        ''' session cannot run one when it cannot.
        ''' </summary>
        Private Function StartTask(task As String) As String
            Dim failure As String = Nothing
            Dim jobId As String = _sandbox.Jobs.StartAgentJob(task, failure)

            If jobId Is Nothing Then
                Return failure
            End If

            Return RenderStarted(_sandbox.Jobs.GetSummary(jobId))
        End Function

        ''' <summary>
        ''' Start a background command, authorized exactly like the "shell"
        ''' tool authorizes a foreground one.
        ''' </summary>
        Private Function StartCommand(command As String, workdir As String, sandbox_permissions As String, justification As String) As String
            Dim cwd As String = Nothing
            Dim denied As String = _sandbox.ResolveAndAuthorize(workdir, SandboxOperation.Execute, cwd)

            If denied IsNot Nothing Then
                ' No escalation asked for (or no path to escalate: an
                ' unresolvable workdir leaves cwd empty): report the denial
                ' as it is.
                If String.IsNullOrWhiteSpace(sandbox_permissions) OrElse cwd Is Nothing Then
                    Return denied
                End If

                Dim escalated As String = _sandbox.AuthorizeEscalation(SandboxOperation.Execute, cwd, sandbox_permissions, justification)

                If escalated IsNot Nothing Then
                    Return escalated
                End If
            End If

            If Not Directory.Exists(cwd) Then
                Return $"Error: working directory not found: {cwd}"
            End If

            Dim jobId As String = _sandbox.Jobs.StartShellJob(command, cwd)

            Return RenderStarted(_sandbox.Jobs.GetSummary(jobId))
        End Function

        ''' <summary>
        ''' Report the job that was just started, together with the tools that
        ''' work on it.
        ''' </summary>
        Private Shared Function RenderStarted(summary As JobSummary) As String
            Dim sb As New StringBuilder()

            sb.AppendLine($"<job_id>{summary.Id}</job_id>")
            sb.AppendLine($"<kind>{summary.Kind}</kind>")
            sb.AppendLine($"<status>{summary.StatusText}</status>")
            sb.AppendLine($"<description>{summary.Description}</description>")
            sb.AppendLine()
            sb.Append($"The job runs in the background. Read it with job_output, list every job with job_list, stop it with job_kill.")

            Return sb.ToString()
        End Function

    End Class

End Namespace
