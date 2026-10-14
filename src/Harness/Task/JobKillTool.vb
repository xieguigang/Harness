Imports System.ComponentModel
Imports System.Text
Imports Microsoft.VisualBasic.CommandLine.Reflection

Namespace Tools

    ''' <summary>
    ''' Tool "job_kill": request cancellation of a running background job.
    ''' 
    ''' The request is polite first and firm afterwards: the job is asked to
    ''' stop through its cancellation token (with the reason recorded in the
    ''' job log), and a job that is still running after the grace period is
    ''' terminated - a background command together with its whole process
    ''' tree.
    ''' </summary>
    Public Class JobKillTool

        Private ReadOnly _sandbox As Sandbox

        ''' <param name="sandbox">The sandbox that owns the background jobs.</param>
        Public Sub New(sandbox As Sandbox)
            If sandbox Is Nothing Then
                Throw New ArgumentNullException(NameOf(sandbox))
            End If

            _sandbox = sandbox
        End Sub

        ''' <summary>Request cancellation of a running background job.</summary>
        ''' <param name="job_id">Job id returned by the tool that started the background work.</param>
        ''' <param name="reason">Optional short reason, recorded in the log and forwarded to the job.</param>
        <Description("Request cancellation of a running background job. The job is asked to stop first; a job that is still running after a short grace period is terminated, a background command together with its whole process tree. Use job_list to find the job ids of this session.")>
        <ExportAPI("job_kill")>
        Public Function job_kill(
            <Argument("job_id", Description:="Job id returned by the tool that started the background work.")> job_id As String,
            <Argument("reason", Description:="Optional short reason, recorded in the log and forwarded to the job.")> Optional reason As String = Nothing
        ) As String
            If String.IsNullOrWhiteSpace(job_id) Then
                Return "Error: job_id is empty."
            End If

            Dim failure As String = _sandbox.Jobs.Cancel(job_id.Trim(), reason)

            If failure IsNot Nothing Then
                Return failure
            End If

            Dim summary As JobSummary = _sandbox.Jobs.GetSummary(job_id.Trim())

            Dim sb As New StringBuilder()
            sb.AppendLine($"<job_id>{summary.Id}</job_id>")
            sb.AppendLine($"<status>{summary.StatusText}</status>")
            sb.AppendLine()
            sb.Append($"The job was asked to stop. It has {BackgroundJobManager.KillGraceMs} ms to exit on its own before it is terminated; read it again with job_output to see how it ended.")

            Return sb.ToString()
        End Function

    End Class

End Namespace
