Imports System.ComponentModel
Imports System.Diagnostics
Imports System.IO
Imports System.Runtime.InteropServices
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

        ''' <summary>Maximum number of characters reported per output stream.</summary>
        Private Const MaxOutputChars As Integer = 20000

        ''' <summary>Wait used when the caller does not ask for a specific timeout.</summary>
        Private Const DefaultTimeoutMs As Integer = 120000

        ''' <summary>Upper bound on the wait, so a runaway process cannot hang the agent.</summary>
        Private Const MaxTimeoutMs As Integer = 600000

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
                If String.IsNullOrWhiteSpace(sandbox_permissions) Then
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

            Dim wait As Integer = ClampTimeout(timeout)

            Dim psi As ProcessStartInfo

            Try
                psi = CreateStartInfo(command, cwd)
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
            Dim stdoutText As String = Limit(stdout, stdoutCut)
            Dim stderrText As String = Limit(stderr, stderrCut)

            Dim sb As New StringBuilder()
            sb.AppendLine($"<exit_code>{exitCode}</exit_code>")
            Call AppendStream(sb, "stdout", stdoutText, stdoutCut)
            Call AppendStream(sb, "stderr", stderrText, stderrCut)

            Return sb.ToString()
        End Function

        ''' <summary>
        ''' Build the launch settings for the platform shell. The command
        ''' string is handed over as a single argument so that quoting and
        ''' redirection stay intact: bash receives it with "-c", and
        ''' PowerShell receives it base64 encoded through "-EncodedCommand",
        ''' which avoids every quoting pitfall of "-Command".
        ''' </summary>
        Private Shared Function CreateStartInfo(command As String, workDir As String) As ProcessStartInfo
            Dim psi As New ProcessStartInfo()
            psi.WorkingDirectory = workDir
            psi.UseShellExecute = False
            psi.CreateNoWindow = True
            psi.RedirectStandardInput = True
            psi.RedirectStandardOutput = True
            psi.RedirectStandardError = True
            psi.StandardOutputEncoding = Encoding.UTF8
            psi.StandardErrorEncoding = Encoding.UTF8

            If RuntimeInformation.IsOSPlatform(OSPlatform.Windows) Then
                ' Two adjustments to the child session:
                '  - force UTF-8 so that non ASCII output survives the pipe;
                '  - silence the progress stream, otherwise PowerShell wraps
                '    stderr in a CLIXML envelope as soon as it emits its
                '    "Preparing modules for first use." record.
                Dim script As String = "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8" & vbLf &
                                       "$ProgressPreference='SilentlyContinue'" & vbLf &
                                       command

                psi.FileName = "powershell.exe"
                Call psi.ArgumentList.Add("-NoProfile")
                Call psi.ArgumentList.Add("-NonInteractive")
                Call psi.ArgumentList.Add("-EncodedCommand")
                Call psi.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)))
            Else
                psi.FileName = "/bin/bash"
                Call psi.ArgumentList.Add("-c")
                Call psi.ArgumentList.Add(command)
            End If

            Return psi
        End Function

        Private Shared Function ClampTimeout(value As Double) As Integer
            If Double.IsNaN(value) OrElse Double.IsInfinity(value) OrElse value <= 0 Then
                Return DefaultTimeoutMs
            End If

            If value >= MaxTimeoutMs Then
                Return MaxTimeoutMs
            End If

            Return CInt(Math.Floor(value))
        End Function

        ''' <summary>
        ''' Trim trailing whitespace and cap the text so that a chatty
        ''' command cannot flood the model context.
        ''' </summary>
        Private Shared Function Limit(text As String, ByRef truncated As Boolean) As String
            Dim value As String = If(text, "").TrimEnd()

            If value.Length <= MaxOutputChars Then
                Return value
            End If

            truncated = True
            Return value.Substring(0, MaxOutputChars)
        End Function

        Private Shared Sub AppendStream(sb As StringBuilder, tag As String, text As String, truncated As Boolean)
            sb.AppendLine($"<{tag}>")

            If text.Length > 0 Then
                sb.AppendLine(text)
            End If

            If truncated Then
                sb.AppendLine($"[output truncated at {MaxOutputChars} characters]")
            End If

            sb.AppendLine($"</{tag}>")
        End Sub

    End Class

End Namespace
