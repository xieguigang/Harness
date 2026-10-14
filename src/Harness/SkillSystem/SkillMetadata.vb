Namespace SkillSystem

    ''' <summary>
    ''' Layer 1: Skill Metadata
    ''' 
    ''' Represents the lightweight metadata that is always loaded at agent startup.
    ''' Only the YAML front matter at the top of SKILL.md is parsed to construct
    ''' this object. This keeps the agent's baseline context footprint minimal
    ''' regardless of how many skills are installed.
    ''' </summary>
    Public Class SkillMetadata

        Private _description As String = ""
        Private _rawDescription As String = ""

        ''' <summary>
        ''' The unique identifier name of the skill, e.g. "pdf_extract_text".
        ''' This value is used as the lookup key when the agent needs to load
        ''' the full skill definition in Layer 2.
        ''' </summary>
        ''' <returns>The skill name string, never null or empty.</returns>
        Public Property Name As String

        ''' <summary>
        ''' A one-line human readable description of what the skill does.
        ''' This is the primary text used by the LLM to perform intent
        ''' classification during task matching.
        ''' 
        ''' Setting it folds the value into a single line: skill descriptions are
        ''' frequently yaml block scalars (or multi-line folded quotes), and a
        ''' newline inside a description would break the one-line-per-skill catalog
        ''' the router prompt is built from.
        ''' </summary>
        ''' <returns>The description wrapped into a single line.</returns>
        Public Property Description As String
            Get
                Return _description
            End Get
            Set(value As String)
                RawDescription = value
            End Set
        End Property

        ''' <summary>
        ''' The description exactly as written in the front matter, line breaks
        ''' included. Kept for diagnostics and for callers that want the original
        ''' wording.
        ''' </summary>
        ''' <returns>The unmodified description.</returns>
        Public Property RawDescription As String
            Get
                Return _rawDescription
            End Get
            Set(value As String)
                _rawDescription = If(value, "")
                _description = SkillText.CollapseToSingleLine(_rawDescription)
            End Set
        End Property

        ''' <summary>
        ''' The absolute path to the skill folder on disk. Used by Layer 2
        ''' and Layer 3 to locate the SKILL.md file and resource subfolders.
        ''' </summary>
        ''' <returns>The absolute directory path.</returns>
        Public Property FolderPath As String

        ''' <summary>
        ''' Optional semantic version string, defaults to "1.0.0" when the
        ''' SKILL.md does not declare a version field.
        ''' </summary>
        ''' <returns>The version string.</returns>
        Public Property Version As String = "1.0.0"

        ''' <summary>
        ''' Optional list of category tags that can be used for filtering
        ''' or grouping skills in management UIs.
        ''' </summary>
        ''' <returns>A list of tag strings, possibly empty.</returns>
        Public Property Tags As New List(Of String)()

        ''' <summary>
        ''' Optional author attribution string.
        ''' </summary>
        Public Property Author As String = ""

        ''' <summary>
        ''' True when this folder is an internal shared package (such as nature-shared)
        ''' that other skills reference but that must never be offered to the model as a
        ''' selectable workflow.
        ''' </summary>
        Public Property IsSharedPackage As Boolean

        Public Overrides Function ToString() As String
            Return $"[{Name}] {Description}"
        End Function

        ''' <summary>
        ''' Build a compact one-line summary suitable for inclusion in the
        ''' LLM's task-matching prompt. The format is intentionally terse
        ''' to minimize token consumption when many skills are installed.
        ''' </summary>
        ''' <param name="maxDescriptionLength">
        ''' The description is truncated to this many characters, because a single
        ''' verbose skill must not dominate the catalog.
        ''' </param>
        Public Function ToSummaryLine(Optional maxDescriptionLength As Integer = 320) As String
            Return $"- {Name}: {SkillText.Truncate(Description, maxDescriptionLength)}"
        End Function

        ''' <summary>
        ''' Parse a front matter tag value. The lightweight front matter parser hands a
        ''' flow sequence over as raw text (<c>[a, b, c]</c>), so the brackets and the
        ''' quotes have to be trimmed here.
        ''' </summary>
        Friend Shared Function ParseTags(text As String) As List(Of String)
            Dim tags As New List(Of String)()
            If String.IsNullOrWhiteSpace(text) Then Return tags

            Dim cleaned As String = text.Trim()

            If cleaned.StartsWith("[") Then cleaned = cleaned.Substring(1)
            If cleaned.EndsWith("]") Then cleaned = cleaned.Substring(0, cleaned.Length - 1)

            For Each part As String In cleaned.Split(New Char() {","c, ";"c, ControlChars.Cr, ControlChars.Lf})
                Dim tag As String = part.Trim().Trim(New Char() {ChrW(34), ChrW(39)})

                If tag.Length > 0 AndAlso Not tags.Contains(tag) Then
                    tags.Add(tag)
                End If
            Next

            Return tags
        End Function

        ''' <summary>
        ''' Decide whether a skill is an internal shared package rather than a user
        ''' invocable workflow. nature skills state this in prose ("internal shared
        ''' reference support package", "do not invoke it as a standalone"), so both the
        ''' name and the description are inspected.
        ''' </summary>
        Friend Shared Function DetectSharedPackage(name As String, description As String) As Boolean
            Dim skillName As String = If(name, "").ToLowerInvariant()
            Dim text As String = If(description, "").ToLowerInvariant()

            If skillName.EndsWith("-shared") OrElse skillName.EndsWith("_shared") Then Return True

            If text.Contains("internal shared") Then Return True
            If text.Contains("shared-reference support package") Then Return True
            If text.Contains("not a standalone") Then Return True
            If text.Contains("do not invoke it as a standalone") Then Return True

            Return False
        End Function

    End Class
End Namespace
