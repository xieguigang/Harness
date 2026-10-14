Imports System.Text
Imports System.Threading
Imports Microsoft.VisualBasic.MIME.application.json
Imports Ollama
Imports Ollama.JSON.FunctionCall

Namespace SkillSystem

    ''' <summary>
    ''' SkillAgent - the LLM-facing orchestrator that wires the three-layer
    ''' progressive disclosure mechanism into the existing Ollama client.
    ''' 
    ''' Lifecycle of a single user task:
    ''' 
    '''   1. User submits a natural-language task.
    '''   2. Layer 1 metadata is already in memory (loaded at construction).
    '''   3. SkillAgent asks the LLM "which of these skills matches the task?"
    '''      using only the metadata summaries. (intent classification)
    '''   4. Layer 2: the full SKILL.md of the matched skill is loaded and
    '''      injected into a fresh prompt that asks the LLM to execute it.
    '''   5. Layer 3: during execution, the LLM may invoke the
    '''      "execute_skill_script" function tool to run a script. The
    '''      script's stdout is returned to the LLM as the function result.
    '''   6. The LLM produces the final answer, which SkillAgent returns
    '''      to the caller.
    ''' </summary>
    Public Class SkillAgent

        Private ReadOnly _ollama As LLMClient
        Private ReadOnly _manager As SkillManager
        Private ReadOnly _toolRegistered As Boolean = False

        ''' <summary>
        ''' Create a new SkillAgent. The constructor performs Layer 1
        ''' scanning immediately so that subsequent RunTask calls do not
        ''' pay the scanning cost.
        ''' </summary>
        ''' <param name="ollama">A configured unified LLM client instance (e.g. LLMClient + OllamaProvider).</param>
        ''' <param name="skillsBaseDir">Absolute path to the skills root folder.</param>
        Public Sub New(ollama As LLMClient, skillsBaseDir As String)
            If ollama Is Nothing Then
                Throw New ArgumentNullException(NameOf(ollama))
            End If
            _ollama = ollama
            _manager = New SkillManager(skillsBaseDir)

            ' Layer 1: scan all installed skills and cache their metadata
            Dim count As Integer = _manager.ScanSkills()
            System.Diagnostics.Debug.WriteLine($"SkillAgent: scanned {count} skill(s) from {skillsBaseDir}")

            ' Register the Layer 3 script-execution tool with the unified LLM client.
            ' This makes "execute_skill_script" available to the LLM as a
            ' function-call tool for the lifetime of this agent.
            RegisterScriptExecutionTool()
        End Sub

        ''' <summary>
        ''' Expose the underlying SkillManager for advanced callers that
        ''' want to enumerate skills, peek at metadata, or pre-load
        ''' definitions outside of the normal task flow.
        ''' </summary>
        Public ReadOnly Property Manager As SkillManager
            Get
                Return _manager
            End Get
        End Property

        ' =========================================================================
        ' Layer 2 trigger: task -> skill matching
        ' =========================================================================

        ''' <summary>
        ''' Layer 2 trigger. Asks the LLM to classify the user's task against
        ''' the cached skill metadata and return the name of the best-matching
        ''' skill. Returns Nothing if no skill is relevant or if the LLM
        ''' response cannot be parsed.
        ''' </summary>
        ''' <param name="userTask">The natural-language task from the user.</param>
        ''' <param name="cancellationToken">
        ''' Token that stops the routing call, so that a background task can be
        ''' cancelled while it is still deciding which skill to use.
        ''' </param>
        Public Async Function MatchTaskToSkill(userTask As String,
                                           Optional cancellationToken As CancellationToken = Nothing) As Task(Of SkillMetadata)
            Dim allMetadata As IReadOnlyList(Of SkillMetadata) = _manager.GetAllMetadata()
            If allMetadata.Count = 0 Then
                Return Nothing
            End If

            ' Build the matching prompt: a compact list of skill summaries
            ' followed by an instruction to pick exactly one.
            Dim sb As New StringBuilder()
            sb.AppendLine("You are a skill router. Below is a catalog of available skills.")
            sb.AppendLine("Each skill is listed as '- name: description'.")
            sb.AppendLine()
            sb.AppendLine("## Skill Catalog")
            For Each m As SkillMetadata In allMetadata
                sb.AppendLine(m.ToSummaryLine())
            Next
            sb.AppendLine()
            sb.AppendLine("## User Task")
            sb.AppendLine(userTask)
            sb.AppendLine()
            sb.AppendLine("## Instructions")
            sb.AppendLine("Decide which single skill is most relevant to the user's task.")
            sb.AppendLine("If none of the skills are relevant, reply with the literal word: NONE")
            sb.AppendLine("Otherwise, reply with ONLY the skill name (the part before the colon),")
            sb.AppendLine("with no extra punctuation, no explanation, no markdown.")
            sb.AppendLine()
            sb.AppendLine("Reply now with the skill name:")

            Dim response = Await _ollama.Chat(sb.ToString(), cancellationToken)

            ' Parse the LLM output: trim, strip code fences, take first token
            Dim raw As String = If(response?.output, "").Trim()
            If raw.StartsWith("```") Then
                ' Strip markdown code fences if the model wrapped its answer
                Dim lines() As String = raw.Split(New String() {vbCrLf, vbLf}, StringSplitOptions.None)
                Dim filtered = lines.Where(Function(l) Not l.TrimStart().StartsWith("```"))
                raw = String.Join(" ", filtered).Trim()
            End If

            ' Take the first whitespace-delimited token as the skill name
            Dim firstToken As String = raw.Split(New Char() {" "c, vbTab, vbCrLf, vbLf},
                                              StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()

            If String.IsNullOrEmpty(firstToken) Then Return Nothing
            If firstToken.Equals("NONE", StringComparison.OrdinalIgnoreCase) Then Return Nothing

            ' Look up the metadata by name (case-insensitive)
            Return _manager.GetMetadata(firstToken)
        End Function

        ' =========================================================================
        ' Full task execution: match -> load -> run
        ' =========================================================================

        ''' <summary>
        ''' Run a user task end-to-end through the three-layer pipeline.
        ''' 
        ''' 1. Match the task to a skill (Layer 2 trigger).
        ''' 2. Load the full SKILL.md of the matched skill (Layer 2 load).
        ''' 3. Inject the skill instructions into the LLM context and ask
        '''    it to execute the task. The LLM may call execute_skill_script
        '''    to invoke Layer 3 resources during this step.
        ''' 4. Return the LLM's final answer.
        ''' </summary>
        ''' <param name="userTask">The natural-language task from the user.</param>
        ''' <param name="cancellationToken">
        ''' Token that stops the task while it runs: it reaches the routing call
        ''' and the execution call, which is what lets a background task job be
        ''' cancelled without waiting for the model to finish.
        ''' </param>
        ''' <returns>A <see cref="SkillExecutionResult"/> with the matched skill name and the LLM output.</returns>
        Public Async Function RunTask(userTask As String,
                                  Optional cancellationToken As CancellationToken = Nothing) As Task(Of SkillExecutionResult)
            Dim result As New SkillExecutionResult()
            result.UserTask = userTask

            ' Layer 2 trigger: classify the task
            Dim matched As SkillMetadata = Await MatchTaskToSkill(userTask, cancellationToken)
            If matched Is Nothing Then
                result.Success = False
                result.Answer = "No matching skill was found for this task. " &
                            "Please install an appropriate skill or rephrase the task."
                Return result
            End If
            result.MatchedSkillName = matched.Name

            ' Layer 2 load: pull the full SKILL.md
            Dim definition As SkillDefinition = _manager.LoadFullSkill(matched.Name)
            If definition Is Nothing Then
                result.Success = False
                result.Answer = $"Skill '{matched.Name}' was matched but its definition could not be loaded."
                Return result
            End If

            ' Build the execution prompt: skill instructions + user task
            Dim prompt As String = BuildExecutionPrompt(definition, userTask)

            ' Layer 3 is triggered implicitly: if the LLM decides to call
            ' execute_skill_script, the function tool we registered in the
            ' constructor will run the script and feed its output back to
            ' the LLM as the tool result.
            Dim response = Await _ollama.Chat(prompt, cancellationToken)

            result.Success = True
            result.Answer = If(response?.output, "")
            result.ThinkText = If(response?.think, "")
            Return result
        End Function

        ''' <summary>
        ''' Build the execution prompt that combines the skill's full
        ''' instructions with the user's concrete task. This is the prompt
        ''' that triggers Layer 2 context injection.
        ''' </summary>
        Private Function BuildExecutionPrompt(definition As SkillDefinition,
                                          userTask As String) As String
            Dim sb As New StringBuilder()
            sb.AppendLine("You are now executing a specific skill. Follow the skill's")
            sb.AppendLine("instructions precisely. If the skill references scripts under")
            sb.AppendLine("scripts/, references/, or assets/, you may invoke them by calling")
            sb.AppendLine("the execute_skill_script function tool. The tool will run the")
            sb.AppendLine("script in a sandbox and return its stdout to you.")
            sb.AppendLine()
            sb.AppendLine(definition.BuildPromptFragment())
            sb.AppendLine("## User Task")
            sb.AppendLine(userTask)
            sb.AppendLine()
            sb.AppendLine("## Your Job")
            sb.AppendLine("Execute the skill for the given task. Use execute_skill_script")
            sb.AppendLine("whenever the skill instructs you to run a script. Produce the")
            sb.AppendLine("final result in the format specified by the skill's Output")
            sb.AppendLine("Specification section.")
            Return sb.ToString()
        End Function

        ' =========================================================================
        ' Layer 3 trigger: register the script-execution function tool
        ' =========================================================================

        ''' <summary>
        ''' Register the "execute_skill_script" function tool with the unified
        ''' LLM client. This is the bridge that lets the LLM trigger Layer 3
        ''' resource loading during skill execution.
        ''' 
        ''' The tool takes three parameters:
        '''   - skill_name: which skill's scripts/ folder to look in
        '''   - script_name: the relative path like "scripts/extract_text.py"
        '''   - args: a single string argument forwarded to the script
        ''' 
        ''' The tool returns the script's combined stdout/stderr as a string.
        ''' </summary>
        Private Sub RegisterScriptExecutionTool()
            ' Define the parameter metadata that gets exported to the LLM
            Dim p1 As New ParameterProperties(
            "skill_name",
            "The name of the skill whose scripts/ folder contains the target script.",
            TypeCode.String)

            Dim p2 As New ParameterProperties(
            "script_name",
            "The relative path of the script inside the skill folder, e.g. 'scripts/extract_text.py'.",
            TypeCode.String)

            Dim p3 As New ParameterProperties(
            "args",
            "Optional. A single string argument forwarded to the script. Pass JSON or plain text as needed.",
            TypeCode.String)

            Dim funcModel As New FunctionModel(
            "execute_skill_script",
            "Execute a script located inside a skill's scripts/ directory. " &
            "Use this to run Python, Shell, or PowerShell scripts referenced by the active skill. " &
            "Returns the script's stdout output as a string.",
            p1, p2, p3)

            ' Register with the Ollama client. The lambda is the CLR backend
            ' that actually runs when the LLM invokes this tool.
            Call _ollama.AddFunction(funcModel, f:=AddressOf ScriptExecution)
        End Sub

        Private Function ScriptExecution([call] As FunctionCall) As String
            Dim skillName As String = [call]!skill_name?.ToString()
            Dim scriptName As String = [call]!script_name?.ToString()
            Dim args As String = ""
            If [call]!args IsNot Nothing Then
                args = [call]!args.ToString()
            End If

            If String.IsNullOrEmpty(skillName) OrElse String.IsNullOrEmpty(scriptName) Then
                Return "{""error"": ""skill_name and script_name are required""}"
            End If

            Dim execResult As ScriptExecutionResult =
            _manager.ExecuteScript(skillName, scriptName, args)

            ' Return a compact JSON summary so the LLM can parse it
            Dim stdoutEscaped As String = JSONTextParser.JsonEscape(execResult.Stdout)
            Dim stderrEscaped As String = JSONTextParser.JsonEscape(execResult.Stderr)

            Return $"{{""success"": {execResult.Success.ToString().ToLower()}, " &
               $"""exit_code"": {execResult.ExitCode}, " &
               $"""stdout"": ""{stdoutEscaped}"", " &
               $"""stderr"": ""{stderrEscaped}""}}"
        End Function
    End Class

End Namespace