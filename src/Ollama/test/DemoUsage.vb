' ============================================================================
' DemoUsage.vb - Example usage of the SkillAgent module
'
' This file demonstrates how to wire up the SkillSystem module with the
' existing Ollama client and run a user task through the three-layer
' progressive disclosure pipeline.
'
' To use this demo:
'   1. Make sure Ollama is running locally (default: http://localhost:11434)
'   2. Make sure a model is pulled, e.g.: ollama pull qwen2.5:7b
'   3. Place the skills/ folder next to your executable
'   4. Call DemoUsage.Run() from your application entry point
' ============================================================================

Imports System.IO
Imports Harness
Imports Ollama

Public Class DemoUsage

    ' ---------------------------------------------------------------------
    ' Entry point: run a complete demo of the skill system
    ' ---------------------------------------------------------------------
    Public Shared Async Function Run() As Task
        Console.WriteLine("=== Skill System Demo ===")
        Console.WriteLine()

        ' 1. Create the Ollama client (assumes the existing class)
        Dim ollama As New LLMClient(New OllamaProvider(""), "deepseek-r1:671b")

        ' 2. Locate the skills folder. In a real app, this would come
        '    from configuration. Here we use a path relative to the
        '    current working directory.
        Dim skillsDir As String = Path.Combine(Environment.CurrentDirectory, "skills")

        If Not Directory.Exists(skillsDir) Then
            Console.WriteLine($"ERROR: skills folder not found at: {skillsDir}")
            Console.WriteLine("Please copy the 'skills' folder next to the executable.")
            Return
        End If

        ' 3. Create the SkillAgent. The constructor performs Layer 1
        '    scanning immediately - it reads only the YAML front-matter
        '    of every SKILL.md and caches the metadata.
        Console.WriteLine($"Initializing SkillAgent with skills folder: {skillsDir}")
        Dim agent As New SkillAgent(ollama, skillsDir)
        Console.WriteLine($"Layer 1 scan complete. {agent.Manager.GetAllMetadata().Count} skill(s) loaded.")
        Console.WriteLine()

        ' Print the Layer 1 metadata catalog (this is what the LLM sees
        ' during task matching - just names and descriptions, no full
        ' instructions).
        Console.WriteLine("--- Layer 1: Metadata Catalog (always loaded) ---")
        For Each m In agent.Manager.GetAllMetadata()
            Console.WriteLine($"  {m.ToSummaryLine()}")
            Console.WriteLine($"    version={m.Version}, tags=[{String.Join(", ", m.Tags)}]")
        Next
        Console.WriteLine()

        ' 4. Demo: enumerate resources for the pdf_extract_text skill.
        '    This shows what Layer 3 resources are available without
        '    actually loading their content into the LLM context.
        Console.WriteLine("--- Layer 3: Resource Catalog (not loaded into context) ---")
        Dim resources = agent.Manager.EnumerateResources("pdf_extract_text")
        For Each r In resources
            Console.WriteLine($"  [{r.Kind}] {r.RelativePath} ({r.SizeBytes} bytes)")
        Next
        Console.WriteLine()

        ' 5. Run a real user task through the full pipeline.
        Console.WriteLine("--- Running User Task ---")
        Dim userTask As String = "请帮我提取 /home/user/documents/sample.pdf 里的所有文字内容"
        Console.WriteLine($"User task: {userTask}")
        Console.WriteLine()

        Dim result = Await agent.RunTask(userTask)

        Console.WriteLine($"Matched skill: {result.MatchedSkillName}")
        Console.WriteLine($"Success: {result.Success}")
        Console.WriteLine()
        Console.WriteLine("=== LLM Answer ===")
        Console.WriteLine(result.Answer)
        If Not String.IsNullOrEmpty(result.ThinkText) Then
            Console.WriteLine()
            Console.WriteLine("=== Thinking (chain-of-thought) ===")
            Console.WriteLine(result.ThinkText)
        End If
    End Function

    ' ---------------------------------------------------------------------
    ' Lower-level demo: invoke each layer individually
    ' ---------------------------------------------------------------------
    Public Shared Async Function RunLayerByLayerDemo() As Task
        Console.WriteLine("=== Layer-by-Layer Demo ===")

        Dim ollama As New LLMClient(New OllamaProvider(""), "deepseek-r1:671b")
        Dim skillsDir As String = Path.Combine(Environment.CurrentDirectory, "skills")
        Dim agent As New SkillAgent(ollama, skillsDir)

        ' Layer 1 is already done by the constructor.
        Console.WriteLine("Layer 1 (metadata) - done by constructor.")
        Console.WriteLine()

        ' Layer 2 trigger: ask the LLM to match a task to a skill.
        ' Only the metadata summaries are sent to the LLM here.
        Console.WriteLine("Layer 2 trigger: matching task to skill...")
        Dim task As String = "我有一份扫描的合同需要提取文字"
        Dim matched = Await agent.MatchTaskToSkill(task)

        If matched IsNot Nothing Then
            Console.WriteLine($"  Matched: {matched.Name}")
            Console.WriteLine($"  Description: {matched.Description}")
            Console.WriteLine()

            ' Layer 2 load: pull the full SKILL.md.
            Console.WriteLine("Layer 2 load: loading full SKILL.md...")
            Dim definition = agent.Manager.LoadFullSkill(matched.Name)
            Console.WriteLine($"  Body length: {definition.BodyMarkdown.Length} chars")
            Console.WriteLine($"  Execution flow section: {definition.ExecutionFlow.Length} chars")
            Console.WriteLine($"  Resource references found: {definition.ResourceReferences.Count}")
            For Each r In definition.ResourceReferences
                Console.WriteLine($"    - {r}")
            Next
            Console.WriteLine()

            ' Layer 3: directly execute a script (bypassing the LLM).
            ' This is useful for testing scripts in isolation.
            Console.WriteLine("Layer 3: directly executing extract_text.py...")
            Dim argsJson As String = "{""pdf_path"": ""/home/user/test.pdf"", ""mode"": ""auto""}"
            Dim execResult = agent.Manager.ExecuteScript(matched.Name, "scripts/extract_text.py", argsJson)
            Console.WriteLine($"  Success: {execResult.Success}")
            Console.WriteLine($"  Exit code: {execResult.ExitCode}")
            Console.WriteLine($"  Stdout: {execResult.Stdout}")
            If Not String.IsNullOrEmpty(execResult.Stderr) Then
                Console.WriteLine($"  Stderr: {execResult.Stderr}")
            End If
        Else
            Console.WriteLine("  No skill matched the task.")
        End If
    End Function

    ' ---------------------------------------------------------------------
    ' Standalone SkillManager demo (no Ollama required)
    ' ---------------------------------------------------------------------
    Public Shared Sub RunManagerOnlyDemo()
        Console.WriteLine("=== SkillManager-Only Demo (no LLM) ===")

        Dim skillsDir As String = Path.Combine(Environment.CurrentDirectory, "skills")
        Dim manager As New SkillManager(skillsDir)

        ' Layer 1
        Dim count As Integer = manager.ScanSkills()
        Console.WriteLine($"Layer 1: scanned {count} skill(s).")
        Console.WriteLine()

        ' Show metadata
        For Each m In manager.GetAllMetadata()
            Console.WriteLine($"  Name: {m.Name}")
            Console.WriteLine($"  Description: {m.Description}")
            Console.WriteLine($"  Version: {m.Version}")
            Console.WriteLine($"  Tags: {String.Join(", ", m.Tags)}")
            Console.WriteLine($"  Folder: {m.FolderPath}")
            Console.WriteLine()
        Next

        ' Layer 2: load full definition for pdf_extract_text
        Console.WriteLine("Layer 2: loading full definition for 'pdf_extract_text'...")
        Dim def = manager.LoadFullSkill("pdf_extract_text")
        If def IsNot Nothing Then
            Console.WriteLine($"  Body markdown length: {def.BodyMarkdown.Length}")
            Console.WriteLine($"  Execution flow length: {def.ExecutionFlow.Length}")
            Console.WriteLine($"  Input spec length: {def.InputSpec.Length}")
            Console.WriteLine($"  Output spec length: {def.OutputSpec.Length}")
            Console.WriteLine($"  Constraints length: {def.Constraints.Length}")
            Console.WriteLine($"  Resource references: {def.ResourceReferences.Count}")
            Console.WriteLine()
            Console.WriteLine("--- Prompt fragment that would be injected into LLM context ---")
            Console.WriteLine(def.BuildPromptFragment())
        End If

        ' Layer 3: enumerate resources
        Console.WriteLine("Layer 3: enumerating resources...")
        Dim resources = manager.EnumerateResources("pdf_extract_text")
        For Each r In resources
            Console.WriteLine($"  {r}")
        Next
        Console.WriteLine()

        ' Layer 3: load a reference file content
        Console.WriteLine("Layer 3: loading references/API.md content...")
        Dim apiContent = manager.LoadResourceContent("pdf_extract_text", "references/API.md")
        Console.WriteLine($"  Loaded {apiContent.Length} chars.")
        Console.WriteLine("  First 200 chars:")
        Console.WriteLine(apiContent.Substring(0, Math.Min(200, apiContent.Length)))
        Console.WriteLine("...")
    End Sub

End Class

' ============================================================================
' Integration notes
' ============================================================================
'
' 1. The SkillAgent constructor calls Ollama.AddFunction() to register
'    the "execute_skill_script" tool. This tool is available to the LLM
'    for the lifetime of the agent. The LLM can call it whenever a
'    skill's instructions reference a script under scripts/.
'
' 2. The three-layer loading flow is automatic:
'    - Layer 1: happens in the SkillAgent constructor (ScanSkills).
'    - Layer 2: triggered by MatchTaskToSkill, then LoadFullSkill.
'    - Layer 3: triggered on-demand when the LLM calls the
'      execute_skill_script function tool.
'
' 3. To add a new skill, simply create a new folder under skills/ with
'    a SKILL.md file. The next time the agent starts, Layer 1 scanning
'    will pick it up automatically. No code changes required.
'
' 4. The SkillScriptExecutor supports .py, .sh, .bat/.cmd, and .ps1
'    scripts. To add support for other script types, extend the
'    Select Case block in SkillResource.vb.
'
' 5. Security: the executor rejects paths containing ".." to prevent
'    directory traversal. For production use, consider running scripts
'    inside a container or sandbox with restricted permissions.
' ============================================================================
