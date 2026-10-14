Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Text.RegularExpressions

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
    ''' Build the prompt fragment that should be injected into the LLM
    ''' context when executing this skill. Only the operationally
    ''' relevant sections are included; raw markdown boilerplate is
    ''' trimmed to keep token usage low.
    ''' </summary>
    Public Function BuildPromptFragment() As String
        Dim sb As New Text.StringBuilder()

        sb.AppendLine($"# Active Skill: {Metadata.Name}")
        sb.AppendLine($"Description: {Metadata.Description}")
        sb.AppendLine()

        If Not String.IsNullOrWhiteSpace(ExecutionFlow) Then
            sb.AppendLine("## Execution Flow")
            sb.AppendLine(ExecutionFlow.Trim())
            sb.AppendLine()
        End If

        If Not String.IsNullOrWhiteSpace(InputSpec) Then
            sb.AppendLine("## Input Specification")
            sb.AppendLine(InputSpec.Trim())
            sb.AppendLine()
        End If

        If Not String.IsNullOrWhiteSpace(OutputSpec) Then
            sb.AppendLine("## Output Specification")
            sb.AppendLine(OutputSpec.Trim())
            sb.AppendLine()
        End If

        If Not String.IsNullOrWhiteSpace(Constraints) Then
            sb.AppendLine("## Constraints")
            sb.AppendLine(Constraints.Trim())
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

End Class

''' <summary>
''' A simple markdown section extractor that splits a SKILL.md body into
''' named sections based on level-2 headings (## ...). It does not attempt
''' to be a full markdown parser - it only needs to recognize section
''' boundaries so that ExecutionFlow / InputSpec / OutputSpec / Constraints
''' blocks can be pulled out individually.
''' </summary>
Public Module MarkdownSectionParser

    Private ReadOnly HeadingRegex As New Regex(
        "^##\s+(.+?)\s*$",
        RegexOptions.Multiline Or RegexOptions.Compiled)

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
    ''' Scan the markdown body for references to files under the standard
    ''' resource subfolders (scripts/, references/, assets/). Returns a
    ''' de-duplicated list of relative paths in the order they appear.
    ''' </summary>
    Public Function ExtractResourceReferences(markdownBody As String) As List(Of String)
        Dim refs As New List(Of String)
        Dim seen As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)

        If String.IsNullOrWhiteSpace(markdownBody) Then
            Return refs
        End If

        ' Match patterns like: scripts/foo.py, references/bar.md, assets/baz.json
        ' Also matches backtick-wrapped variants: `scripts/foo.py`
        Dim pattern As String = "(?:`)?((?:scripts|references|assets)/[A-Za-z0-9_\-./]+)(?:`)?"
        For Each m As Match In Regex.Matches(markdownBody, pattern)
            Dim path As String = m.Groups(1).Value.TrimEnd("."c, ")"c, "]")
            If seen.Add(path) Then
                refs.Add(path)
            End If
        Next

        Return refs
    End Function

End Module
