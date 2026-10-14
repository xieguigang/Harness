Imports System.IO
Imports System.Text

Namespace SkillSystem

    ''' <summary>
    ''' Layer 3: Skill Resource
    ''' 
    ''' Represents a single resource file inside a skill folder. Resources
    ''' are NEVER injected into the LLM context window - they are loaded
    ''' and executed directly by the runtime environment. This is the key
    ''' design decision that keeps token consumption bounded: the model
    ''' decides WHAT to invoke, the sandbox decides HOW to run it.
    ''' </summary>
    Public Class SkillResource

        ''' <summary>
        ''' The category of the resource, derived from its parent folder.
        ''' </summary>
        Public Enum ResourceKind
            ''' <summary>Executable script under scripts/</summary>
            Script
            ''' <summary>Reference document under references/</summary>
            Reference
            ''' <summary>Static asset under assets/ (templates, schemas, samples)</summary>
            Asset
        End Enum

        ''' <summary>
        ''' The relative path of the resource inside the skill folder,
        ''' e.g. "scripts/extract_text.py".
        ''' </summary>
        Public Property RelativePath As String

        ''' <summary>
        ''' The absolute path on disk.
        ''' </summary>
        Public Property AbsolutePath As String

        ''' <summary>
        ''' The resource category.
        ''' </summary>
        Public Property Kind As ResourceKind

        ''' <summary>
        ''' The file extension (e.g. ".py", ".sh", ".md", ".json") used to
        ''' determine the execution strategy for scripts.
        ''' </summary>
        Public Property Extension As String

        ''' <summary>
        ''' File size in bytes, populated when the resource is enumerated.
        ''' </summary>
        Public Property SizeBytes As Long

        Public Overrides Function ToString() As String
            Return $"{Kind}::{RelativePath} ({SizeBytes} bytes)"
        End Function

    End Class

    ''' <summary>
    ''' A sandboxed script executor for Layer 3 resources.
    ''' 
    ''' The executor resolves a script by its relative path inside the skill
    ''' folder, picks the correct interpreter based on file extension, and
    ''' runs the script in a child process. The standard output and standard
    ''' error are captured and returned to the caller (typically the LLM via
    ''' a function-call tool).
    ''' 
    ''' Security note: the executor restricts the working directory to the
    ''' skill folder and rejects any path that attempts to escape via ".."
    ''' traversal. A real production deployment should add additional
    ''' sandboxing (container, seccomp, etc.) on top of this baseline.
    ''' </summary>
    Public Class SkillScriptExecutor

        ''' <summary>
        ''' Optional timeout in milliseconds for script execution. Defaults
        ''' to 60 seconds. Set to 0 or a negative value to disable the
        ''' timeout (not recommended for untrusted scripts).
        ''' </summary>
        Public Property TimeoutMs As Integer = 60000

        ''' <summary>
        ''' Hard cap of the captured stdout / stderr of a single script run, so a runaway
        ''' script cannot flood the model context.
        ''' </summary>
        Public Const MaxCapturedChars As Integer = 60000

        ''' <summary>
        ''' Execute a script identified by its relative path inside the given skill
        ''' folder. The argument string is split shell-style into separate arguments
        ''' (quote aware), so <c>--fast --limit 10</c> reaches the script as three
        ''' arguments instead of one quoted blob.
        ''' </summary>
        ''' <param name="skillFolderPath">Absolute path to the folder that contains the script.</param>
        ''' <param name="scriptRelativePath">Relative path like "scripts/run.py".</param>
        ''' <param name="args">Argument string forwarded to the script.</param>
        ''' <returns>A <see cref="ScriptExecutionResult"/> capturing stdout, stderr, and exit code.</returns>
        Public Function Execute(skillFolderPath As String,
                            scriptRelativePath As String,
                            Optional args As String = "") As ScriptExecutionResult

            Dim result As New ScriptExecutionResult()

            ' Normalize and validate the path to prevent directory traversal
            Dim normalizedRel As String = scriptRelativePath.Replace("/"c, "\"c).TrimStart("\"c)
            If normalizedRel.Contains("..") Then
                result.Success = False
                result.ExitCode = -1
                result.Stderr = $"Rejected path traversal attempt: {scriptRelativePath}"
                Return result
            End If

            Dim scriptPath As String = Path.Combine(skillFolderPath, normalizedRel)
            If Not File.Exists(scriptPath) Then
                result.Success = False
                result.ExitCode = -1
                result.Stderr = $"Script not found: {scriptRelativePath}"
                Return result
            End If

            Dim extension As String = Path.GetExtension(scriptPath).ToLowerInvariant()
            Dim psi As New ProcessStartInfo()

            psi.WorkingDirectory = skillFolderPath
            psi.UseShellExecute = False
            psi.RedirectStandardOutput = True
            psi.RedirectStandardError = True
            psi.RedirectStandardInput = True
            psi.CreateNoWindow = True

            ' the scripts are expected to speak utf-8: decoding their output with the
            ' legacy console code page turns every non ascii character into noise
            psi.StandardOutputEncoding = Encoding.UTF8
            psi.StandardErrorEncoding = Encoding.UTF8

            ' ArgumentList quotes every argument correctly on its own, including the
            ' script path and each user supplied argument
            Select Case extension
                Case ".py", ".pyw"
                    psi.FileName = "python"
                    psi.ArgumentList.Add(scriptPath)

                Case ".sh"
                    psi.FileName = "/bin/bash"
                    psi.ArgumentList.Add(scriptPath)

                Case ".bat", ".cmd"
                    psi.FileName = "cmd.exe"
                    psi.ArgumentList.Add("/c")
                    psi.ArgumentList.Add(scriptPath)

                Case ".ps1"
                    psi.FileName = "powershell.exe"
                    psi.ArgumentList.Add("-NoProfile")
                    psi.ArgumentList.Add("-ExecutionPolicy")
                    psi.ArgumentList.Add("Bypass")
                    psi.ArgumentList.Add("-File")
                    psi.ArgumentList.Add(scriptPath)

                Case ".js", ".mjs", ".cjs"
                    psi.FileName = "node"
                    psi.ArgumentList.Add(scriptPath)

                Case Else
                    result.Success = False
                    result.ExitCode = -1
                    result.Stderr = $"Unsupported script extension: {extension}" & vbCrLf &
                                    "supported extensions: .py .pyw .sh .bat .cmd .ps1 .js .mjs .cjs"
                    Return result
            End Select

            For Each argument As String In SplitArguments(args)
                psi.ArgumentList.Add(argument)
            Next

            Try
                Using proc As New Process()
                    proc.StartInfo = psi
                    proc.Start()

                    ' read both pipes asynchronously: reading synchronously would deadlock
                    ' as soon as one of them fills its buffer
                    Dim stdoutTask As Task(Of String) = proc.StandardOutput.ReadToEndAsync()
                    Dim stderrTask As Task(Of String) = proc.StandardError.ReadToEndAsync()

                    ' nothing is fed to the script: closing stdin lets a script that reads
                    ' stdin finish instead of waiting forever
                    Try
                        proc.StandardInput.Close()
                    Catch
                    End Try

                    Dim exited As Boolean = proc.WaitForExit(If(TimeoutMs > 0, TimeoutMs, Integer.MaxValue))

                    If Not exited Then
                        Try
                            ' a launcher such as uv or npx leaves grand children behind, so
                            ' the whole tree has to go
                            proc.Kill(entireProcessTree:=True)
                        Catch
                        End Try

                        Try
                            proc.WaitForExit(2000)
                        Catch
                        End Try

                        result.Success = False
                        result.ExitCode = -1
                        result.Stdout = Captured(stdoutTask)
                        result.Stderr = $"Script timed out after {TimeoutMs} ms." & vbCrLf & Captured(stderrTask)
                        Return result
                    End If

                    result.Stdout = Captured(stdoutTask)
                    result.Stderr = Captured(stderrTask)
                    result.ExitCode = proc.ExitCode
                    result.Success = (proc.ExitCode = 0)
                End Using
            Catch ex As Exception
                result.Success = False
                result.ExitCode = -1
                result.Stderr = $"Failed to launch script: {ex.Message}"
            End Try

            Return result
        End Function

        ''' <summary>
        ''' Split an argument string the way a shell does: whitespace separates the
        ''' arguments unless it sits inside a pair of single or double quotes. A blank
        ''' string yields no argument at all.
        ''' </summary>
        Friend Shared Function SplitArguments(text As String) As String()
            If String.IsNullOrWhiteSpace(text) Then Return New String() {}

            Dim args As New List(Of String)()
            Dim sb As New StringBuilder()
            Dim quote As Char = ChrW(0)
            Dim hasContent As Boolean = False

            For Each c As Char In text
                If quote <> ChrW(0) Then
                    If c = quote Then
                        quote = ChrW(0)
                    Else
                        sb.Append(c)
                        hasContent = True
                    End If
                ElseIf c = """"c OrElse c = "'"c Then
                    quote = c
                    hasContent = True
                ElseIf Char.IsWhiteSpace(c) Then
                    If hasContent Then
                        args.Add(sb.ToString())
                        sb.Clear()
                        hasContent = False
                    End If
                Else
                    sb.Append(c)
                    hasContent = True
                End If
            Next

            If hasContent Then args.Add(sb.ToString())

            Return args.ToArray()
        End Function

        ''' <summary>
        ''' Wait for a reader task and return what it captured, capped so a runaway
        ''' script cannot flood the model context.
        ''' </summary>
        Private Shared Function Captured(task As Task(Of String)) As String
            Dim text As String = ""

            Try
                If task.Wait(2000) Then text = If(task.Result Is Nothing, "", task.Result)
            Catch
            End Try

            If text.Length > MaxCapturedChars Then
                Return text.Substring(0, MaxCapturedChars) & vbCrLf &
                       $"... [{text.Length - MaxCapturedChars} more characters truncated]"
            End If

            Return text
        End Function

    End Class

    ''' <summary>
    ''' The result of executing a skill script.
    ''' </summary>
    Public Class ScriptExecutionResult

        ''' <summary>True if the script exited with code 0.</summary>
        Public Property Success As Boolean

        ''' <summary>The process exit code.</summary>
        Public Property ExitCode As Integer

        ''' <summary>Captured standard output.</summary>
        Public Property Stdout As String = ""

        ''' <summary>Captured standard error.</summary>
        Public Property Stderr As String = ""

        Public Overrides Function ToString() As String
            If Success Then
                Return Stdout
            Else
                Return $"[exit {ExitCode}] {Stderr}{vbCrLf}{Stdout}"
            End If
        End Function

    End Class
End Namespace