Imports System.Text.RegularExpressions

Namespace SkillSystem

    ''' <summary>
    ''' Layer 2: Skill Definition
    ''' 
    ''' Represents the full content of a SKILL.md file, loaded only when the
    ''' agent determines that this skill is highly relevant to the user's task.
    ''' Loading the full definition is what makes the LLM capable of executing
    ''' the skill: it now has access to the step-by-step instructions, the
    ''' input/output specifications, and the constraints.
    ''' 
    ''' The definition also pre-scans the markdown body for resource references
    ''' (paths under scripts/, references/, assets/) so that Layer 3 knows
    ''' which files exist without re-reading the markdown at execution time.
    ''' </summary>
    Public Class SkillDefinition

        ''' <summary>
        ''' The Layer 1 metadata that was used to locate this skill.
        ''' </summary>
        Public Property Metadata As SkillMetadata

        ''' <summary>
        ''' The complete raw markdown content of SKILL.md (including the
        ''' YAML front-matter). Kept for debugging and re-parsing purposes.
        ''' </summary>
        Public Property RawContent As String

        ''' <summary>
        ''' The markdown body with the YAML front-matter stripped off.
        ''' This is the text that gets injected into the LLM's context
        ''' window during skill execution.
        ''' </summary>
        Public Property BodyMarkdown As String

        ''' <summary>
        ''' The "Execution Flow" section parsed out of the markdown body.
        ''' Contains the step-by-step operational instructions.
        ''' </summary>
        Public Property ExecutionFlow As String = ""

        ''' <summary>
        ''' The "Input Specification" section describing the expected input
        ''' format, parameter types, and required fields.
        ''' </summary>
        Public Property InputSpec As String = ""

        ''' <summary>
        ''' The "Output Specification" section describing the structure of
        ''' the result that the skill produces.
        ''' </summary>
        Public Property OutputSpec As String = ""

        ''' <summary>
        ''' The "Constraints" section listing error-handling rules, edge
        ''' cases, and limitations the LLM must respect.
        ''' </summary>
        Public Property Constraints As String = ""

        ''' <summary>
        ''' Pre-scanned list of resource file paths referenced anywhere in
        ''' the markdown body. Each entry is a relative path such as
        ''' "scripts/extract_text.py". Used by Layer 3 to enumerate
        ''' available resources without re-parsing markdown.
        ''' </summary>
        Public Property ResourceReferences As New List(Of String)()

        ''' <summary>
        ''' The optional manifest of the skill folder (Nothing when the skill has none).
        ''' </summary>
        Public Property Manifest As SkillManifest

        ''' <summary>
        ''' The files declared in the manifest always_load table whose content was read
        ''' into memory, inside the preload budget.
        ''' </summary>
        Public Property PreloadedFiles As New List(Of PreloadedSkillFile)()

        ''' <summary>
        ''' The declared files that were NOT preloaded because they exceeded the budget.
        ''' They stay as paths, so the model can fetch them with read_skill_file when it
        ''' really needs them instead of blowing up the prompt.
        ''' </summary>
        Public Property DeferredResources As New List(Of String)()

        ''' <summary>
        ''' maximum characters of the raw body injected when no well known section
        ''' heading matched.
        ''' </summary>
        Public Const BodyInjectionBudget As Integer = 48000

        ''' <summary>
        ''' Build the prompt fragment that should be injected into the LLM
        ''' context when executing this skill. Only the operationally
        ''' relevant sections are included; raw markdown boilerplate is
        ''' trimmed to keep token usage low.
        ''' 
        ''' A skill that does not use the well-known section headings (the nature skills
        ''' describe their workflow with their own headings) would otherwise be injected
        ''' as an empty fragment, so the body itself is injected in that case.
        ''' </summary>
        Public Function BuildPromptFragment() As String
            Dim sb As New Text.StringBuilder()

            sb.AppendLine($"# Active Skill: {Metadata.Name}")
            sb.AppendLine($"Description: {Metadata.Description}")
            sb.AppendLine()

            Dim hasStructuredSection As Boolean = False

            If AppendSection(sb, "Execution Flow", ExecutionFlow) Then hasStructuredSection = True
            If AppendSection(sb, "Input Specification", InputSpec) Then hasStructuredSection = True
            If AppendSection(sb, "Output Specification", OutputSpec) Then hasStructuredSection = True
            If AppendSection(sb, "Constraints", Constraints) Then hasStructuredSection = True

            If Not hasStructuredSection AndAlso Not String.IsNullOrWhiteSpace(BodyMarkdown) Then
                sb.AppendLine("## Skill Instructions")
                sb.AppendLine(SkillText.Truncate(BodyMarkdown.Trim(), BodyInjectionBudget))
                sb.AppendLine()
                sb.AppendLine("(Read any file this skill mentions with read_skill_file before using it.)")
                sb.AppendLine()
            End If

            If Manifest IsNot Nothing AndAlso Manifest.IsLoaded Then
                sb.Append(Manifest.BuildSummary())
            End If

            For Each file In PreloadedFiles
                sb.AppendLine($"## Preloaded skill file: {file.RelativePath}")
                sb.AppendLine(file.Content.Trim())
                sb.AppendLine()
            Next

            If DeferredResources.Count > 0 Then
                sb.AppendLine("## Skill files not preloaded (read with read_skill_file)")
                For Each deferred As String In DeferredResources
                    sb.AppendLine($"- {deferred}")
                Next
                sb.AppendLine()
            End If

            If ResourceReferences.Count > 0 Then
                sb.AppendLine("## Available Resources (call execute_skill_script to invoke)")
                For Each res As String In ResourceReferences
                    sb.AppendLine($"- {res}")
                Next
                sb.AppendLine()
            End If

            Return sb.ToString()
        End Function

        ''' <summary>append one optional section; returns true when something was appended.</summary>
        Private Shared Function AppendSection(sb As Text.StringBuilder, title As String, body As String) As Boolean
            If String.IsNullOrWhiteSpace(body) Then Return False

            sb.AppendLine($"## {title}")
            sb.AppendLine(body.Trim())
            sb.AppendLine()
            Return True
        End Function

    End Class

    ''' <summary>
    ''' One skill file whose content was preloaded into the layer 2 prompt.
    ''' </summary>
    Public Class PreloadedSkillFile

        ''' <summary>the path exactly as declared (it may point into a sibling shared package).</summary>
        Public Property RelativePath As String

        ''' <summary>the resolved absolute path the content was read from.</summary>
        Public Property AbsolutePath As String

        ''' <summary>the file content.</summary>
        Public Property Content As String

    End Class

    ''' <summary>
    ''' A simple markdown section extractor that splits a SKILL.md body into
    ''' named sections based on level-2 headings (## ...). It does not attempt
    ''' to be a full markdown parser - it only needs to recognize section
    ''' boundaries so that ExecutionFlow / InputSpec / OutputSpec / Constraints
    ''' blocks can be pulled out individually.
    ''' </summary>
    Public Module MarkdownSectionParser

        Private ReadOnly HeadingRegex As New Regex("^##\s+(.+?)\s*$", RegexOptions.Multiline Or RegexOptions.Compiled)

        ''' <summary>
        ''' Parse the markdown body into a dictionary keyed by section heading
        ''' (case-insensitive). The value is the raw text of that section,
        ''' excluding the heading line itself.
        ''' </summary>
        Public Function ParseSections(markdownBody As String) As Dictionary(Of String, String)
            Dim sections As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)

            If String.IsNullOrWhiteSpace(markdownBody) Then
                Return sections
            End If

            Dim matches As MatchCollection = HeadingRegex.Matches(markdownBody)

            If matches.Count = 0 Then
                ' No headings - treat entire body as a single "intro" section
                sections("") = markdownBody.Trim()
                Return sections
            End If

            ' Capture any text before the first heading as the preamble
            If matches(0).Index > 0 Then
                sections("__preamble__") = markdownBody.Substring(0, matches(0).Index).Trim()
            End If

            For i As Integer = 0 To matches.Count - 1
                Dim headingMatch As Match = matches(i)
                Dim heading As String = headingMatch.Groups(1).Value.Trim()
                Dim startPos As Integer = headingMatch.Index + headingMatch.Length
                Dim endPos As Integer = If(i < matches.Count - 1,
                                       matches(i + 1).Index,
                                       markdownBody.Length)
                Dim body As String = markdownBody.Substring(startPos, endPos - startPos).Trim()
                sections(heading) = body
            Next

            Return sections
        End Function

        ''' <summary>
        ''' Scan the markdown body for references to skill files. Two shapes are
        ''' recognised: a path inside a known resource folder (scripts/, references/,
        ''' but also the folders the nature skills use, such as static/, core/ or
        ''' journal-formats/, optionally inside a sibling package like
        ''' ../nature-shared/), and any backtick wrapped relative file path.
        ''' Returns a de-duplicated list in the order the references appear.
        ''' </summary>
        Public Function ExtractResourceReferences(markdownBody As String) As List(Of String)
            Dim refs As New List(Of String)
            Dim seen As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)

            If String.IsNullOrWhiteSpace(markdownBody) Then
                Return refs
            End If

            Dim folders As String =
                "scripts|script|references|reference|refs|assets|asset|templates|template|static|core|journal-formats|journal_formats|agents|examples|shared|styles|data"

            ' references/spec.md, static/core/stance.md, ../nature-shared/core/reader-workflow.md
            AddResourceMatches(refs, seen, markdownBody,
                "(?:`)?((?:\.\./)?(?:[A-Za-z0-9_\-]+/)*(?:" & folders & ")/[A-Za-z0-9_\-./]+?\.[A-Za-z0-9]{1,6})(?:`)?")

            ' any backtick wrapped relative path with a known document/script extension
            AddResourceMatches(refs, seen, markdownBody,
                "`((?:\.\./)?[A-Za-z0-9_\-./]+\.(?:md|markdown|txt|rst|json|jsonl|ya?ml|py|pyw|sh|ps1|bat|cmd|js|mjs|cjs|r|rb|docx|xlsx|xml|csv|html|css|bib|tex))`")

            Return refs
        End Function

        Private Sub AddResourceMatches(refs As List(Of String), seen As HashSet(Of String), markdownBody As String, pattern As String)
            For Each m As Match In Regex.Matches(markdownBody, pattern)
                Dim reference As String = m.Groups(1).Value.TrimEnd("."c, ")"c, "]"c, ","c, ";"c, ":"c)

                If reference.Length = 0 Then Continue For
                If seen.Add(reference) Then refs.Add(reference)
            Next
        End Sub

    End Module
End Namespace