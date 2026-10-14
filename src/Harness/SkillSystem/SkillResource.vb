Imports System.IO

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
    ''' Execute a script identified by its relative path inside the
    ''' given skill folder. Arguments are passed as a single string
    ''' (typically JSON) and forwarded to the script as-is.
    ''' </summary>
    ''' <param name="skillFolderPath">Absolute path to the skill folder.</param>
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
        psi.CreateNoWindow = True

        Select Case extension
            Case ".py"
                psi.FileName = "python"
                psi.Arguments = """" & scriptPath & """"
                If Not String.IsNullOrEmpty(args) Then
                    psi.Arguments &= " """ & args & """"
                End If

            Case ".sh"
                psi.FileName = "/bin/bash"
                psi.Arguments = """" & scriptPath & """"
                If Not String.IsNullOrEmpty(args) Then
                    psi.Arguments &= " """ & args & """"
                End If

            Case ".bat", ".cmd"
                psi.FileName = "cmd.exe"
                psi.Arguments = "/c """ & scriptPath & """"
                If Not String.IsNullOrEmpty(args) Then
                    psi.Arguments &= " """ & args & """"
                End If

            Case ".ps1"
                psi.FileName = "powershell.exe"
                psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -File """ & scriptPath & """"
                If Not String.IsNullOrEmpty(args) Then
                    psi.Arguments &= " """ & args & """"
                End If

            Case Else
                result.Success = False
                result.ExitCode = -1
                result.Stderr = $"Unsupported script extension: {extension}"
                Return result
        End Select

        Try
            Using proc As New Process()
                proc.StartInfo = psi
                proc.Start()

                ' Read synchronously - for long-running scripts a real
                ' implementation should read async to avoid deadlocks
                ' when the stdout buffer fills up.
                Dim stdoutTask = proc.StandardOutput.ReadToEndAsync()
                Dim stderrTask = proc.StandardError.ReadToEndAsync()

                Dim exited As Boolean = proc.WaitForExit(If(TimeoutMs > 0, TimeoutMs, Integer.MaxValue))
                If Not exited Then
                    Try
                        proc.Kill()
                    Catch
                    End Try
                    result.Success = False
                    result.ExitCode = -1
                    result.Stdout = stdoutTask.Result
                    result.Stderr = $"Script timed out after {TimeoutMs} ms." & vbCrLf & stderrTask.Result
                    Return result
                End If

                result.Stdout = stdoutTask.Result
                result.Stderr = stderrTask.Result
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
