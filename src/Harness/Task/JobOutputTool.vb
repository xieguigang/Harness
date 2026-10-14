Imports System.ComponentModel
Imports System.Text
Imports Microsoft.VisualBasic.CommandLine.Reflection

Namespace Tools

    ''' <summary>
    ''' Tool "job_output": read a background job.
    ''' 
    ''' What a read returns depends on the shape of the job:
    ''' 
    '''   - a command job is a stream: the read returns what it printed since
    '''     the previous read, and that output is consumed by reading it;
    '''   - a task job produces one result at the end: every read returns the
    '''     same result once it exists.
    ''' 
    ''' With <c>wait</c> the read blocks until the job finishes, up to
    ''' <c>timeout_ms</c>; a wait that runs out reports the job as still
    ''' running instead of ending it.
    ''' </summary>
    Public Class JobOutputTool

        Private ReadOnly _sandbox As Sandbox

        ''' <param name="sandbox">The sandbox that owns the background jobs.</param>
        Public Sub New(sandbox As Sandbox)
            If sandbox Is Nothing Then
                Throw New ArgumentNullException(NameOf(sandbox))
            End If

            _sandbox = sandbox
        End Sub

        ''' <summary>Read a background job, optionally waiting for it to finish.</summary>
        ''' <param name="job_id">Job id returned by the tool that started the background work.</param>
        ''' <param name="wait">Block until the job finishes or the timeout expires; a timed-out wait leaves the job running. Defaults to false.</param>
        ''' <param name="timeout_ms">Max wait in milliseconds with wait: true. Defaults to and is capped by configuration.</param>
        <Description("Read a background job. A background command returns the output produced since the previous read (reading consumes it), a background task returns its final result once it finished - again on every read. Set wait to block until the job finishes, with timeout_ms as the longest wait; a wait that runs out reports the job as still running instead of ending it.")>
        <ExportAPI("job_output")>
        Public Function job_output(
            <Argument("job_id", Description:="Job id returned by the tool that started the background work.")> job_id As String,
            <Argument("wait", Description:="Block until the job finishes or the timeout expires; a timed-out wait leaves the job running. Defaults to false.")> Optional wait As Boolean = False,
            <Argument("timeout_ms", Description:="Max wait in milliseconds with wait: true. Defaults to and is capped by configuration.")> Optional timeout_ms As Double = 0
        ) As String
            If String.IsNullOrWhiteSpace(job_id) Then
                Return "Error: job_id is empty."
            End If

            Dim output As JobOutput = _sandbox.Jobs.Output(job_id.Trim(), wait, timeout_ms)

            If output Is Nothing Then
                Return $"Error: no such background job: {job_id.Trim()}"
            End If

            Return Render(output)
        End Function

        ''' <summary>
        ''' Render one read: the state of the job first, then what this read
        ''' is allowed to hand over.
        ''' </summary>
        Private Shared Function Render(output As JobOutput) As String
            Dim summary As JobSummary = output.Summary
            Dim sb As New StringBuilder()

            sb.AppendLine($"<job_id>{summary.Id}</job_id>")
            sb.AppendLine($"<kind>{summary.Kind}</kind>")
            sb.AppendLine($"<status>{summary.StatusText}</status>")
            sb.AppendLine($"<elapsed>{JobText.FormatElapsed(summary.Elapsed)}</elapsed>")

            If summary.ExitCode.HasValue Then
                sb.AppendLine($"<exit_code>{summary.ExitCode.Value}</exit_code>")
            End If

            If output.TimedOut Then
                sb.AppendLine($"<wait>timed out after {output.WaitedMs} ms; the job is still running</wait>")
            End If

            If summary.Shape = JobShape.Stream Then
                Call AppendStreamOutput(sb, output)
            Else
                Call AppendResult(sb, output)
            End If

            If Not String.IsNullOrWhiteSpace(summary.Note) Then
                sb.AppendLine("<note>")
                sb.AppendLine(summary.Note.Trim())
                sb.Append("</note>")
            End If

            Return sb.ToString().TrimEnd()
        End Function

        ''' <summary>
        ''' Hand over the output of a command job that arrived since the
        ''' previous read. A read with nothing new says so - a stream shaped
        ''' job is read repeatedly to follow it, so "nothing yet" must be an
        ''' answer rather than an empty result.
        ''' </summary>
        Private Shared Sub AppendStreamOutput(sb As StringBuilder, output As JobOutput)
            Dim chunk As JobOutputChunk = output.Chunk
            Dim stdout As String = Nothing
            Dim stderr As String = Nothing
            Dim stdoutCut As Boolean = False
            Dim stderrCut As Boolean = False

            If chunk IsNot Nothing Then
                If chunk.Dropped > 0 Then
                    sb.AppendLine($"[output dropped: {chunk.Dropped} characters]")
                End If

                stdout = ProcessLaunch.Limit(chunk.Stdout, stdoutCut)
                stderr = ProcessLaunch.Limit(chunk.Stderr, stderrCut)
            End If

            If String.IsNullOrEmpty(stdout) AndAlso String.IsNullOrEmpty(stderr) Then
                If Not output.TimedOut Then
                    sb.AppendLine("No new output since the previous read.")
                End If

                Return
            End If

            Call ProcessLaunch.AppendStream(sb, "stdout", stdout, stdoutCut)
            Call ProcessLaunch.AppendStream(sb, "stderr", stderr, stderrCut)
        End Sub

        ''' <summary>
        ''' Hand over the result of a task job. The result is kept by the job,
        ''' so reading it twice hands back the same text.
        ''' </summary>
        Private Shared Sub AppendResult(sb As StringBuilder, output As JobOutput)
            If output.Result Is Nothing Then
                If output.Summary.IsFinished Then
                    sb.AppendLine("The job ended without producing a result; see the note below.")
                Else
                    sb.AppendLine("The job has not produced its result yet.")
                End If

                Return
            End If

            Dim truncated As Boolean = False
            Dim result As String = ProcessLaunch.Limit(output.Result, truncated)

            Call ProcessLaunch.AppendStream(sb, "result", result, truncated)
        End Sub

    End Class

End Namespace
