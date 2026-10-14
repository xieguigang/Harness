Imports System.ComponentModel
Imports System.Diagnostics
Imports System.IO
Imports System.Text
Imports Microsoft.VisualBasic.CommandLine.Reflection

Namespace Tools

    ''' <summary>
    ''' Tool "shell": run an external process through .NET
    ''' <see cref="Process"/> and hand its standard output and standard error
    ''' back to the agent, so that the model can drive its own analysis
    ''' scripts (python, dotnet, R, ...).
    ''' 
    ''' The tool holds a reference to the <see cref="Sandbox"/> that owns it.
    ''' The command runs in the <c>workdir</c> directory, which is resolved
    ''' and authorized like any other path - so the sandbox decides whether
    ''' the agent may execute at all, and where. Like the other tools it
    ''' supports a one-shot escalation when the sandbox denies the call.
    ''' </summary>
    Public Class ShellTool

        Private ReadOnly _sandbox As Sandbox

        ''' <param name="sandbox">The sandbox that resolves the paths and enforces the security policy.</param>
        Public Sub New(sandbox As Sandbox)
            If sandbox Is Nothing Then
                Throw New ArgumentNullException(NameOf(sandbox))
            End If

            _sandbox = sandbox
        End Sub

        ''' <summary>
        ''' Run a shell command and return its exit code, standard output and
        ''' standard error.
        ''' </summary>
        ''' <param name="command">The command line to run. It is executed by the platform shell: PowerShell on Windows, bash elsewhere.</param>
        ''' <param name="workdir">Working directory for the command. Defaults to the session workspace; a relative path resolves against it.</param>
        ''' <param name="timeout">Maximum time to wait, in milliseconds. Defaults to 120000 (2 minutes), capped at 600000 (10 minutes).</param>
        ''' <param name="sandbox_permissions">The narrowest wider sandbox mode for a one-shot retry of the exact operation the sandbox just denied; the retry asks the user for approval.</param>
        ''' <param name="justification">Required with sandbox_permissions: one sentence for the user explaining why this exact operation needs the wider access. Use the language of the user's current request.</param>
        <Description("Run a shell command through an external process and return its exit code, standard output and standard error. Use it to run analysis scripts, tests or build tools. The command runs in the session workspace unless a workdir is given, and is killed when it exceeds the timeout.")>
        <ExportAPI("shell")>
        Public Function shell(
            <Argument("command", Description:="The command line to run. It is executed by the platform shell: PowerShell on Windows, bash elsewhere.")> command As String,
            <Argument("workdir", Description:="Working directory for the command. Defaults to the session workspace; a relative path resolves against it.")> Optional workdir As String = Nothing,
            <Argument("timeout", Description:="Maximum time to wait, in milliseconds. Defaults to 120000 (2 minutes), capped at 600000 (10 minutes).")> Optional timeout As Double = 0,
            <Argument("sandbox_permissions", Description:="The narrowest wider sandbox mode for a one-shot retry of the exact operation the sandbox just denied; the retry asks the user for approval. One of ""workspace-write"" (target inside the workspace) or ""danger-full-access"" (target outside it). Leave empty unless the sandbox denied the operation.")> Optional sandbox_permissions As String = Nothing,
            <Argument("justification", Description:="Required with sandbox_permissions: one sentence for the user explaining why this exact operation needs the wider access. Use the language of the user's current request.")> Optional justification As String = Nothing
        ) As String
            If String.IsNullOrWhiteSpace(command) Then
                Return "Error: command is empty."
            End If

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

            Dim wait As Integer = ProcessLaunch.ClampTimeout(timeout)

            Dim psi As ProcessStartInfo

            Try
                psi = ProcessLaunch.CreateShellStartInfo(command, cwd)
            Catch ex As Exception
                Return $"Error: cannot prepare the command: {ex.Message}"
            End Try

            Dim exitCode As Integer
            Dim stdout As String
            Dim stderr As String
            Dim killed As Boolean

            Try
                Using child As New Process()
                    child.StartInfo = psi
                    Call child.Start()

                    ' No interactive input; closing the stream gives the child
                    ' an EOF instead of a hang.
                    Call child.StandardInput.Close()

                    Dim stdoutTask = child.StandardOutput.ReadToEndAsync()
                    Dim stderrTask = child.StandardError.ReadToEndAsync()

                    killed = Not child.WaitForExit(wait)

                    If killed Then
                        Try
                            Call child.Kill(entireProcessTree:=True)
                        Catch
                            ' already gone
                        End Try

                        Call child.WaitForExit()
                    End If

                    stdout = stdoutTask.Result
                    stderr = stderrTask.Result
                    exitCode = If(killed, -1, child.ExitCode)
                End Using
            Catch ex As Exception
                Return $"Error: failed to run the command: {ex.Message}"
            End Try

            If killed Then
                Dim note As String = $"[the process was killed after exceeding the {wait} ms timeout]"
                stderr = If(String.IsNullOrWhiteSpace(stderr), note, stderr.TrimEnd() & vbCrLf & note)
            End If

            Dim stdoutCut As Boolean = False
            Dim stderrCut As Boolean = False
            Dim stdoutText As String = ProcessLaunch.Limit(stdout, stdoutCut)
            Dim stderrText As String = ProcessLaunch.Limit(stderr, stderrCut)

            Dim sb As New StringBuilder()
            sb.AppendLine($"<exit_code>{exitCode}</exit_code>")
            Call ProcessLaunch.AppendStream(sb, "stdout", stdoutText, stdoutCut)
            Call ProcessLaunch.AppendStream(sb, "stderr", stderrText, stderrCut)

            Return sb.ToString()
        End Function

    End Class

End Namespace
