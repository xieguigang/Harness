Imports System.Collections.Generic
Imports System.ComponentModel
Imports System.Text
Imports Microsoft.VisualBasic.CommandLine.Reflection

Namespace Tools

    ''' <summary>
    ''' Tool "job_list": the background jobs of this session, running and
    ''' finished, in the order they were started.
    ''' 
    ''' One line per job with the id job_output and job_kill need, the kind of
    ''' work, the status, how long it ran and what it was asked to do.
    ''' </summary>
    Public Class JobListTool

        Private ReadOnly _sandbox As Sandbox

        ''' <param name="sandbox">The sandbox that owns the background jobs.</param>
        Public Sub New(sandbox As Sandbox)
            If sandbox Is Nothing Then
                Throw New ArgumentNullException(NameOf(sandbox))
            End If

            _sandbox = sandbox
        End Sub

        ''' <summary>List the background jobs of this session.</summary>
        <Description("List your background jobs, running and finished, with their ids, kinds and statuses: one line per job with the id used by job_output and job_kill, the kind (shell for a background command, agent for a background task), the status, how long the job ran, its exit code and what it was asked to do.")>
        <ExportAPI("job_list")>
        Public Function job_list() As String
            Dim jobs As IReadOnlyList(Of JobSummary) = _sandbox.Jobs.List()

            If jobs.Count = 0 Then
                Return "No background jobs have been started in this session."
            End If

            Dim sb As New StringBuilder()

            sb.AppendLine($"<jobs count=""{jobs.Count}"">")

            For Each job As JobSummary In jobs
                Dim exitCode As String = If(job.ExitCode.HasValue, $"exit {job.ExitCode.Value}", "-")

                sb.AppendLine($"{job.Id}  {job.Kind}  {job.StatusText}  {JobText.FormatElapsed(job.Elapsed)}  {exitCode}  {job.Description}")

                If Not String.IsNullOrWhiteSpace(job.Note) Then
                    Call AppendNotes(sb, job.Note)
                End If
            Next

            sb.Append("</jobs>")
            sb.AppendLine()
            sb.Append("Read a job with job_output, stop a running one with job_kill.")

            Return sb.ToString()
        End Function

        ''' <summary>
        ''' Indent the recorded notes of a job below its line, one line each,
        ''' trimmed so that a job list stays readable.
        ''' </summary>
        Private Shared Sub AppendNotes(sb As StringBuilder, note As String)
            Dim lines As String() = note.Split(New String() {vbCrLf, vbLf}, StringSplitOptions.RemoveEmptyEntries)

            For Each line As String In lines
                sb.AppendLine($"    {JobText.Summarise(line)}")
            Next
        End Sub

    End Class

End Namespace
