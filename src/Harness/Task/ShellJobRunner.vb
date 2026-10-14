Imports System
Imports System.Diagnostics
Imports System.Threading.Tasks

Namespace Tools

    ''' <summary>
    ''' Runs one command shaped background job: it starts the child process,
    ''' feeds its output into the job buffer line by line and records how the
    ''' command ended.
    ''' 
    ''' The process handle is handed to the job as well, which is what makes
    ''' the escalation of a cancellation request possible (see
    ''' <see cref="BackgroundJob.ForceKillProcessTree"/>).
    ''' </summary>
    Friend Module ShellJobRunner

        ''' <summary>
        ''' Start <paramref name="command"/> in the background for
        ''' <paramref name="job"/>. The working directory must already be
        ''' authorized by the caller. A command that cannot even be started
        ''' fails the job instead of throwing, so that job_output can report
        ''' why.
        ''' </summary>
        Friend Sub Start(job As BackgroundJob, command As String, workDir As String)
            Dim psi As ProcessStartInfo

            Try
                psi = ProcessLaunch.CreateShellStartInfo(command, workDir)
            Catch ex As Exception
                Call Fail(job, $"the command could not be prepared: {ex.Message}")
                Return
            End Try

            Dim child As New Process()

            child.StartInfo = psi
            ' Set before the process starts, otherwise a command that finishes
            ' immediately would never raise its exit event.
            child.EnableRaisingEvents = True

            ' Line by line, so that a job that keeps talking for hours is not
            ' buffered in one piece, and so that output is readable while the
            ' command still runs.
            AddHandler child.OutputDataReceived,
                Sub(sender, e)
                    If e.Data IsNot Nothing Then
                        job.AppendStdout(e.Data)
                    End If
                End Sub
            AddHandler child.ErrorDataReceived,
                Sub(sender, e)
                    If e.Data IsNot Nothing Then
                        job.AppendStderr(e.Data)
                    End If
                End Sub

            ' The exit handler only records the end. Reading ExitCode after a
            ' parameterless WaitForExit() is what flushes the asynchronous
            ' readers, and that wait must not run on the event thread.
            AddHandler child.Exited, Sub(sender, e) Call Task.Run(Sub() Finish(job, child))

            Try
                Call child.Start()
            Catch ex As Exception
                Call Fail(job, $"the command could not be started: {ex.Message}")
                Call DisposeChild(child)

                Return
            End Try

            Call job.AttachProcess(child)

            ' No interactive input in the background either: closing the stream
            ' gives the child an EOF instead of a hang.
            Try
                Call child.StandardInput.Close()
            Catch
                ' the command does not read its standard input
            End Try

            ' A very short command may already be gone by now, in which case
            ' its process object is disposed and there is nothing left to
            ' read: its output was flushed by Finish.
            Try
                Call child.BeginOutputReadLine()
                Call child.BeginErrorReadLine()
            Catch
                ' the command already finished
            End Try
        End Sub

        Private Sub Finish(job As BackgroundJob, child As Process)
            Dim exitCode As Integer = -1

            Try
                ' Flushes whatever the asynchronous readers still hold.
                Call child.WaitForExit()
                exitCode = child.ExitCode
            Catch
                ' the process is already gone
            End Try

            ' A job that was asked to stop reports cancelled, whether the exit
            ' came from a graceful stop or from the kill escalation.
            Dim status As JobStatus = If(job.Status = JobStatus.Cancelling,
                                        JobStatus.Cancelled,
                                        If(exitCode = 0, JobStatus.Succeeded, JobStatus.Failed))

            Call job.MarkFinished(status, exitCode)
        End Sub

        Private Sub Fail(job As BackgroundJob, message As String)
            Call job.AppendNote(message)
            Call job.AppendStderr(message)
            Call job.MarkFinished(JobStatus.Failed, -1)
        End Sub

        Private Sub DisposeChild(child As Process)
            Try
                Call child.Dispose()
            Catch
                ' nothing left to release
            End Try
        End Sub

    End Module

End Namespace
