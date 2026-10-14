''' <summary>
''' Layer 1: Skill Metadata
''' 
''' Represents the lightweight metadata that is always loaded at agent startup.
''' Only the YAML front matter at the top of SKILL.md is parsed to construct
''' this object. This keeps the agent's baseline context footprint minimal
''' regardless of how many skills are installed.
''' </summary>
Public Class SkillMetadata

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
    ''' </summary>
    ''' <returns>The description string.</returns>
    Public Property Description As String

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
    ''' <returns>The author name, or empty string if not specified.</returns>
    Public Property Author As String = ""

    Public Overrides Function ToString() As String
        Return $"[{Name}] {Description}"
    End Function

    ''' <summary>
    ''' Build a compact one-line summary suitable for inclusion in the
    ''' LLM's task-matching prompt. The format is intentionally terse
    ''' to minimize token consumption when many skills are installed.
    ''' </summary>
    Public Function ToSummaryLine() As String
        Return $"- {Name}: {Description}"
    End Function

End Class
