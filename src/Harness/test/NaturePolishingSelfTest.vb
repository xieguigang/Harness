Imports System.IO
Imports System.Linq
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks
Imports Harness.SkillSystem
Imports Ollama

Namespace HarnessSelfTest

    ''' <summary>
    ''' command line options of the polishing end to end test.
    ''' </summary>
    Friend Class PolishOptions

        Public Property File As String
        Public Property SkillsRoot As String
        Public Property Model As String

        Public Sub New(args As String())
            SkillsRoot = "C:\Users\Administrator\Downloads\nature-skills\skills"

            Dim i As Integer = 0

            While args IsNot Nothing AndAlso i < args.Length
                Select Case args(i)
                    Case "--file"
                        i += 1 : File = args(i)
                    Case "--skills"
                        i += 1 : SkillsRoot = args(i)
                    Case "--model"
                        i += 1 : Model = args(i)
                End Select

                i += 1
            End While
        End Sub
    End Class

    ''' <summary>
    ''' A real end to end test of the nature-skills "polishing" skill
    ''' (nature-polishing): the DeepSeek client built by
    ''' <see cref="Program.GetLLMs"/> drives a <see cref="SkillAgent"/>, which routes
    ''' the task, injects the layer 2 context (skill body + manifest + preloaded
    ''' always_load files) and lets the model pull the on-demand fragments through
    ''' the read_skill_file tool.
    ''' 
    ''' The layer 1 / 2 / 3 assertions run locally first, so a failure can be told
    ''' apart from a network or model problem before the real API call is made.
    ''' </summary>
    Friend Module NaturePolishingSelfTest

        ''' <summary>a hard ceiling of the whole run, so a stalled API call cannot hang the test.</summary>
        Private Const RunTimeoutSeconds As Integer = 300

        Friend Async Function RunAsync(args As String()) As Task(Of Integer)
            Dim options As New PolishOptions(args)

            Console.WriteLine("== nature-skills polishing skill (end to end)")
            Console.WriteLine("skills root : " & options.SkillsRoot)
            Console.WriteLine("model       : " & If(String.IsNullOrEmpty(options.Model), "(default from GetLLMs)", options.Model))
            Console.WriteLine("draft       : " & If(options.File Is Nothing, "built in sample", options.File))
            Console.WriteLine()

            If Not Directory.Exists(options.SkillsRoot) Then
                Console.WriteLine("[error] the skills root does not exist: " & options.SkillsRoot)
                Return 1
            End If

            Dim client As LLMClient = CreateClient(options.Model)

            If client Is Nothing Then
                Console.WriteLine("[error] unable to build the LLM client (is the api key readable?)")
                Return 1
            End If

            ' the constructor performs the layer 1 scan and registers
            ' read_skill_file / execute_skill_script on the client
            Dim agent As New SkillAgent(client, options.SkillsRoot)

            If Not ReportLayer1(agent) Then Return 1
            If Not ReportLayer2(agent) Then Return 1
            ReportLayer3(agent)

            Dim draft As String

            Try
                draft = If(options.File Is Nothing, SampleDraft(), File.ReadAllText(options.File, Encoding.UTF8))
            Catch ex As Exception
                Console.WriteLine("[error] unable to read the draft: " & ex.Message)
                Return 1
            End Try

            Dim task As String = BuildTaskText(draft)

            Console.WriteLine()
            Console.WriteLine("== running the polishing task through the real API")
            Console.WriteLine("   (the model output is streamed live, the final answer is repeated below)")
            Console.WriteLine()

            Dim result As SkillExecutionResult

            Try
                Using cts As New CancellationTokenSource(TimeSpan.FromSeconds(RunTimeoutSeconds))
                    result = Await agent.RunTask(task, cts.Token)
                End Using
            Catch ex As OperationCanceledException
                Console.WriteLine()
                Console.WriteLine($"[error] the run was cancelled (timeout after {RunTimeoutSeconds}s).")
                Return 1
            Catch ex As Exception
                Console.WriteLine()
                Console.WriteLine("[error] the skill run failed: " & ex.GetType().Name & ": " & ex.Message)
                Console.WriteLine()
                Console.WriteLine("Hints:")
                Console.WriteLine("  - an authentication error means the api key in the ini file is missing or invalid;")
                Console.WriteLine("  - a model error means the service does not accept the model name, retry with")
                Console.WriteLine("    --model deepseek-chat (or the name your endpoint exposes);")
                Console.WriteLine("  - anything else is usually network or endpoint related.")
                Return 1
            End Try

            Console.WriteLine()
            Console.WriteLine("== result")
            Console.WriteLine("matched skill : " & result.MatchedSkillName)
            Console.WriteLine("success       : " & result.Success)
            Console.WriteLine("answer length : " & result.Answer.Length & " chars")
            Console.WriteLine()
            Console.WriteLine("--- polished text ---")
            Console.WriteLine(result.Answer)
            Console.WriteLine("--- end ---")

            Return 0
        End Function

        ' ====================================================================
        ' diagnostics
        ' ====================================================================

        Private Function ReportLayer1(agent As SkillAgent) As Boolean
            Dim scanned As IReadOnlyList(Of SkillMetadata) = agent.Manager.GetAllMetadata()
            Dim routable As IReadOnlyList(Of SkillMetadata) = agent.Manager.GetRoutableMetadata()

            Console.WriteLine($"layer 1: {scanned.Count} skill(s) scanned, {routable.Count} routable")

            Dim polishing As SkillMetadata = agent.Manager.GetMetadata("nature-polishing")

            If polishing Is Nothing Then
                Console.WriteLine("[error] nature-polishing was not scanned; check the skills root.")
                Return False
            End If

            Dim sharedExcluded As Boolean = routable.All(Function(m) m.Name <> "nature-shared")

            Console.WriteLine("  nature-polishing found        : " & (polishing IsNot Nothing))
            Console.WriteLine("  nature-shared excluded        : " & sharedExcluded)
            Console.WriteLine("  nature-polishing summary      : " & polishing.ToSummaryLine())
            Console.WriteLine("  first routable skills         : " &
                              String.Join(", ", routable.Take(5).Select(Function(m) m.Name)))

            Return True
        End Function

        Private Function ReportLayer2(agent As SkillAgent) As Boolean
            Dim definition As SkillDefinition = agent.Manager.LoadFullSkill("nature-polishing")

            If definition Is Nothing Then
                Console.WriteLine("[error] nature-polishing could not be loaded.")
                Return False
            End If

            Dim manifest As SkillManifest = definition.Manifest

            Console.WriteLine("layer 2: definition loaded")
            Console.WriteLine($"  manifest loaded      : {manifest.IsLoaded} (always_load={manifest.AlwaysLoad.Count}, axes={manifest.Axes.Count}, on_demand={manifest.OnDemand.Count})")

            If manifest.Errors.Count > 0 Then
                Console.WriteLine("  manifest errors      : " & String.Join(" | ", manifest.Errors))
            End If

            For Each file In definition.PreloadedFiles
                Console.WriteLine($"  preloaded            : {file.RelativePath} ({file.Content.Length} chars)")
            Next

            For Each deferred In definition.DeferredResources
                Console.WriteLine($"  deferred (budget)    : {deferred}")
            Next

            Dim fragment As String = definition.BuildPromptFragment()

            Console.WriteLine($"  prompt fragment      : {fragment.Length} chars")

            For Each marker In New String() {
                "## Skill Instructions",
                "## Skill Manifest: Optional Axes",
                "## On-demand files",
                "## Preloaded skill file:",
                "## Skill files not preloaded"
            }
                Console.WriteLine($"    contains '{marker}': {fragment.Contains(marker)}")
            Next

            Return True
        End Function

        Private Sub ReportLayer3(agent As SkillAgent)
            Console.WriteLine("layer 3: local read_skill_file spot checks")

            Dim resultsFragment As String = agent.Manager.ReadSkillFile("nature-polishing", "static/fragments/section/results.md")
            ReportRead("static/fragments/section/results.md", resultsFragment)

            Dim sharedFile As String = agent.Manager.ReadSkillFile("nature-polishing", "../nature-shared/core/reader-workflow.md")
            ReportRead("../nature-shared/core/reader-workflow.md", sharedFile)

            Dim escaped As String = agent.Manager.ReadSkillFile("nature-polishing", "../../../../etc/passwd")
            Console.WriteLine("  traversal rejected   : " & escaped.StartsWith("[error]"))

            Dim resources As List(Of SkillResource) = agent.Manager.EnumerateResources("nature-polishing")
            Dim scriptCount As Integer = resources.Where(Function(r) r.Kind = SkillResource.ResourceKind.Script).Count()
            Console.WriteLine($"  resources enumerated : {resources.Count} (scripts={scriptCount})")
        End Sub

        Private Sub ReportRead(label As String, content As String)
            If content.StartsWith("[error]") Then
                Console.WriteLine($"  read {label} -> {content}")
            Else
                Console.WriteLine($"  read {label} -> {content.Length} chars")
            End If
        End Sub

        ' ====================================================================
        ' helpers
        ' ====================================================================

        ''' <summary>
        ''' The client from <see cref="Program.GetLLMs"/>; when a model override is given
        ''' the same endpoint and key are reused, only the model name changes.
        ''' </summary>
        Private Function CreateClient(model As String) As LLMClient
            If String.IsNullOrEmpty(model) Then
                Return Program.GetLLMs()
            End If

            Try
                Return Program.GetLLMs(model)
            Catch ex As Exception
                Console.WriteLine("[error] unable to build the client for model '" & model & "': " & ex.Message)
                Return Nothing
            End Try
        End Function

        ''' <summary>
        ''' A short English Results paragraph with the usual problems: hedgeless claims,
        ''' a circular explanation, a redundant recap and a speculative closing.
        ''' </summary>
        Private Function SampleDraft() As String
            Return "It was found by us that the model works good. In this paper, " &
                   "a lot of experiments have been done and the results show that " &
                   "the accuracy is higher than the baseline. The reason is because " &
                   "our method can learn better features. We can see from Table 2 " &
                   "that the proposed method gets 94.3% and the baseline gets 91.2%, " &
                   "which proves that our method is the best one. In the future, " &
                   "more and more studies will be conducted to verify it."
        End Function

        Private Function BuildTaskText(draft As String) As String
            Dim sb As New StringBuilder()

            sb.AppendLine("Polish the following academic manuscript paragraph. It belongs to the Results section of a research paper.")
            sb.AppendLine()
            sb.AppendLine("Constraints:")
            sb.AppendLine("- paper_type: research; section: results; language: en; journal: generic.")
            sb.AppendLine("- Preserve every fact, number and term; do not add new claims, results or content.")
            sb.AppendLine("- Return only the polished paragraph.")
            sb.AppendLine()
            sb.AppendLine("Draft to polish:")
            sb.AppendLine(draft)

            Return sb.ToString()
        End Function

    End Module

End Namespace
