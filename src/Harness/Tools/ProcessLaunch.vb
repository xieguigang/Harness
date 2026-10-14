Imports System
Imports System.Diagnostics
Imports System.Runtime.InteropServices
Imports System.Text

Namespace Tools

    ''' <summary>
    ''' The one place that knows how a command is handed to the platform
    ''' shell. The synchronous "shell" tool and the background job runner both
    ''' run commands through here, so a command behaves the same whether it is
    ''' run in the foreground or in the background: same interpreter, same
    ''' encoding fixes, same output rendering helpers.
    ''' </summary>
    Friend Module ProcessLaunch

        ''' <summary>Maximum number of characters reported per output stream.</summary>
        Friend Const MaxOutputChars As Integer = 20000

        ''' <summary>Wait used when the caller does not ask for a specific timeout.</summary>
        Friend Const DefaultTimeoutMs As Integer = 120000

        ''' <summary>Upper bound on the wait, so a runaway process cannot hang the agent.</summary>
        Friend Const MaxTimeoutMs As Integer = 600000

        ''' <summary>
        ''' Build the launch settings for the platform shell. The command
        ''' string is handed over as a single argument so that quoting and
        ''' redirection stay intact: bash receives it with "-c", and
        ''' PowerShell receives it base64 encoded through "-EncodedCommand",
        ''' which avoids every quoting pitfall of "-Command".
        ''' </summary>
        Friend Function CreateShellStartInfo(command As String, workDir As String) As ProcessStartInfo
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

        ''' <summary>Coerce a requested timeout into the allowed band.</summary>
        Friend Function ClampTimeout(value As Double) As Integer
            Return Clamp(value, DefaultTimeoutMs, MaxTimeoutMs)
        End Function

        ''' <summary>
        ''' Coerce a wait into the band [fallback, cap]; a missing or absurd
        ''' value falls back and an oversized one is capped.
        ''' </summary>
        Friend Function Clamp(value As Double, fallback As Integer, cap As Integer) As Integer
            If Double.IsNaN(value) OrElse Double.IsInfinity(value) OrElse value <= 0 Then
                Return fallback
            End If

            If value >= cap Then
                Return cap
            End If

            Return CInt(Math.Floor(value))
        End Function

        ''' <summary>
        ''' Trim trailing whitespace and cap the text so that a chatty
        ''' command cannot flood the model context.
        ''' </summary>
        Friend Function Limit(text As String, ByRef truncated As Boolean) As String
            Dim value As String = If(text, "").TrimEnd()

            If value.Length <= MaxOutputChars Then
                Return value
            End If

            truncated = True
            Return value.Substring(0, MaxOutputChars)
        End Function

        ''' <summary>Render one output stream as a tag block.</summary>
        Friend Sub AppendStream(sb As StringBuilder, tag As String, text As String, truncated As Boolean)
            sb.AppendLine($"<{tag}>")

            If text.Length > 0 Then
                sb.AppendLine(text)
            End If

            If truncated Then
                sb.AppendLine($"[output truncated at {MaxOutputChars} characters]")
            End If

            sb.AppendLine($"</{tag}>")
        End Sub

    End Module

End Namespace
