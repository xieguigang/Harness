Imports System.IO
Imports Microsoft.VisualBasic.MIME.text.yaml

''' <summary>
''' SkillManager - the central coordinator for the three-layer progressive
''' disclosure loading mechanism.
''' 
''' Layer 1 (always loaded):
'''     Call <see cref="ScanSkills"/> at agent startup. The manager walks
'''     the skills base directory, reads only the YAML front-matter of
'''     each SKILL.md, and caches <see cref="SkillMetadata"/> objects.
'''     This is cheap and keeps the agent's baseline context small.
''' 
''' Layer 2 (loaded on task match):
'''     Once the agent (via <see cref="SkillAgent"/>) has identified the
'''     relevant skill, call <see cref="LoadFullSkill"/>. The manager
'''     reads the entire SKILL.md, parses it into sections, and returns
'''     a <see cref="SkillDefinition"/> ready for context injection.
''' 
''' Layer 3 (loaded on demand):
'''     During execution, the LLM may request a script to be run via the
'''     execute_skill_script function tool. The manager delegates to
'''     <see cref="SkillScriptExecutor"/> which runs the script in a
'''     child process. Reference and asset files can also be loaded
'''     on demand via <see cref="LoadResourceContent"/>.
''' </summary>
Public Class SkillManager

    Private ReadOnly _skillsBaseDir As String
    Private ReadOnly _executor As New SkillScriptExecutor()

    ' Layer 1 cache: skill name -> metadata
    Private ReadOnly _metadataCache As New Dictionary(Of String, SkillMetadata)(StringComparer.OrdinalIgnoreCase)

    ' Layer 2 cache: skill name -> full definition (loaded lazily)
    Private ReadOnly _definitionCache As New Dictionary(Of String, SkillDefinition)(StringComparer.OrdinalIgnoreCase)

    ''' <summary>
    ''' Create a new SkillManager rooted at the given base directory.
    ''' The directory should contain one subfolder per skill, each with
    ''' its own SKILL.md file.
    ''' </summary>
    ''' <param name="skillsBaseDir">Absolute path to the skills root folder.</param>
    Public Sub New(skillsBaseDir As String)
        If String.IsNullOrWhiteSpace(skillsBaseDir) Then
            Throw New ArgumentException("skillsBaseDir must not be empty", NameOf(skillsBaseDir))
        End If
        _skillsBaseDir = Path.GetFullPath(skillsBaseDir)
    End Sub

    ''' <summary>
    ''' The absolute path of the skills root folder.
    ''' </summary>
    Public ReadOnly Property SkillsBaseDirectory As String
        Get
            Return _skillsBaseDir
        End Get
    End Property

    ''' <summary>
    ''' The script executor instance used by Layer 3. Exposed so callers
    ''' can tweak properties like <see cref="SkillScriptExecutor.TimeoutMs"/>.
    ''' </summary>
    Public ReadOnly Property Executor As SkillScriptExecutor
        Get
            Return _executor
        End Get
    End Property

    ' =========================================================================
    ' Layer 1: Metadata scanning
    ' =========================================================================

    ''' <summary>
    ''' Layer 1 entry point. Walks the skills base directory, reads the
    ''' YAML front-matter of every SKILL.md found, and caches the
    ''' resulting <see cref="SkillMetadata"/> objects. Call this once
    ''' at agent startup.
    ''' </summary>
    ''' <returns>The number of skills successfully scanned.</returns>
    Public Function ScanSkills() As Integer
        _metadataCache.Clear()
        _definitionCache.Clear()

        If Not Directory.Exists(_skillsBaseDir) Then
            Return 0
        End If

        Dim count As Integer = 0
        For Each skillDir As String In Directory.GetDirectories(_skillsBaseDir)
            Dim skillMdPath As String = Path.Combine(skillDir, "SKILL.md")
            If Not File.Exists(skillMdPath) Then
                Continue For
            End If

            Try
                Dim metadata As SkillMetadata = ParseMetadataOnly(skillMdPath)
                If metadata IsNot Nothing AndAlso Not String.IsNullOrEmpty(metadata.Name) Then
                    _metadataCache(metadata.Name) = metadata
                    count += 1
                End If
            Catch ex As Exception
                ' A malformed SKILL.md should not crash the whole scan.
                ' In production, log this to a diagnostics sink.
                System.Diagnostics.Debug.WriteLine($"Failed to scan {skillDir}: {ex.Message}")
            End Try
        Next

        Return count
    End Function

    ''' <summary>
    ''' Read only the YAML front-matter from a SKILL.md file and build
    ''' a <see cref="SkillMetadata"/>. The rest of the file is NOT read
    ''' into memory - this is the whole point of Layer 1.
    ''' </summary>
    Private Function ParseMetadataOnly(skillMdPath As String) As SkillMetadata
        ' Read only the first 4KB - metadata blocks are tiny, so we
        ' avoid loading multi-megabyte SKILL.md files at startup.
        Dim preview As String = ReadHead(skillMdPath, 4096)

        Dim yaml As Dictionary(Of String, String) = YamlFrontMatterParser.Parse(preview)

        Dim metadata As New SkillMetadata()
        metadata.FolderPath = Path.GetDirectoryName(skillMdPath)

        If yaml.ContainsKey("name") Then metadata.Name = yaml("name")
        If yaml.ContainsKey("description") Then metadata.Description = yaml("description")
        If yaml.ContainsKey("version") Then metadata.Version = yaml("version")
        If yaml.ContainsKey("author") Then metadata.Author = yaml("author")
        If yaml.ContainsKey("tags") Then
            metadata.Tags = yaml("tags").
                Split(New String() {","}, StringSplitOptions.RemoveEmptyEntries).
                Select(Function(s) s.Trim()).
                ToList()
        End If

        Return metadata
    End Function

    ''' <summary>
    ''' Read up to maxBytes from the beginning of a text file. Used by
    ''' Layer 1 to peek at the YAML front-matter without loading the
    ''' entire file.
    ''' </summary>
    Private Function ReadHead(path As String, maxBytes As Integer) As String
        Using fs As New FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
            Dim buffer(maxBytes - 1) As Byte
            Dim read As Integer = fs.Read(buffer, 0, buffer.Length)
            Return System.Text.Encoding.UTF8.GetString(buffer, 0, read)
        End Using
    End Function

    ''' <summary>
    ''' Return all cached metadata objects. This is the snapshot that
    ''' <see cref="SkillAgent"/> injects into the task-matching prompt.
    ''' </summary>
    Public Function GetAllMetadata() As IReadOnlyList(Of SkillMetadata)
        Return _metadataCache.Values.ToList()
    End Function

    ''' <summary>
    ''' Look up a single metadata object by skill name (case-insensitive).
    ''' Returns Nothing if no skill with that name is installed.
    ''' </summary>
    Public Function GetMetadata(skillName As String) As SkillMetadata
        If skillName Is Nothing Then Return Nothing
        Dim result As SkillMetadata = Nothing
        If _metadataCache.TryGetValue(skillName, result) Then
            Return result
        End If
        Return Nothing
    End Function

    ' =========================================================================
    ' Layer 2: Full skill loading
    ' =========================================================================

    ''' <summary>
    ''' Layer 2 entry point. Loads the complete SKILL.md for the named
    ''' skill, parses it into structured sections, and returns a
    ''' <see cref="SkillDefinition"/> ready for context injection.
    ''' 
    ''' Results are cached, so calling this multiple times for the same
    ''' skill does not re-read the file.
    ''' </summary>
    ''' <param name="skillName">The skill name (must exist in the Layer 1 cache).</param>
    ''' <returns>The full skill definition, or Nothing if the skill is not installed.</returns>
    Public Function LoadFullSkill(skillName As String) As SkillDefinition
        If String.IsNullOrEmpty(skillName) Then Return Nothing

        ' Return cached definition if we already loaded it
        Dim cached As SkillDefinition = Nothing
        If _definitionCache.TryGetValue(skillName, cached) Then
            Return cached
        End If

        ' Need the metadata to know where the folder is
        Dim metadata As SkillMetadata = GetMetadata(skillName)
        If metadata Is Nothing Then Return Nothing

        Dim skillMdPath As String = Path.Combine(metadata.FolderPath, "SKILL.md")
        If Not File.Exists(skillMdPath) Then Return Nothing

        Dim rawContent As String = File.ReadAllText(skillMdPath, System.Text.Encoding.UTF8)
        Dim body As String = YamlFrontMatterParser.StripFrontMatter(rawContent)
        Dim sections As Dictionary(Of String, String) = MarkdownSectionParser.ParseSections(body)
        Dim resourceRefs As List(Of String) = MarkdownSectionParser.ExtractResourceReferences(body)

        Dim definition As New SkillDefinition()
        definition.Metadata = metadata
        definition.RawContent = rawContent
        definition.BodyMarkdown = body
        definition.ResourceReferences = resourceRefs

        ' Map well-known section headings to the structured fields.
        ' We try several aliases for each field so skill authors can
        ' use whichever heading style they prefer.
        definition.ExecutionFlow = PickSection(sections,
            "Execution Flow", "执行流程", "Steps", "操作步骤", "How To Run")
        definition.InputSpec = PickSection(sections,
            "Input Specification", "输入规范", "Input", "输入", "Parameters")
        definition.OutputSpec = PickSection(sections,
            "Output Specification", "输出规范", "Output", "输出", "Result")
        definition.Constraints = PickSection(sections,
            "Constraints", "约束条件", "注意事项", "Notes", "Caveats")

        _definitionCache(skillName) = definition
        Return definition
    End Function

    ''' <summary>
    ''' Helper that returns the first non-empty section matching any of
    ''' the candidate heading names (case-insensitive).
    ''' </summary>
    Private Function PickSection(sections As Dictionary(Of String, String),
                                 ParamArray candidates As String()) As String
        For Each c As String In candidates
            If sections.ContainsKey(c) AndAlso Not String.IsNullOrWhiteSpace(sections(c)) Then
                Return sections(c)
            End If
        Next
        Return ""
    End Function

    ' =========================================================================
    ' Layer 3: Resource access
    ' =========================================================================

    ''' <summary>
    ''' Layer 3 entry point for scripts. Executes a script located at
    ''' <paramref name="scriptRelativePath"/> inside the named skill's
    ''' folder. The script's stdout/stderr are captured and returned.
    ''' </summary>
    Public Function ExecuteScript(skillName As String,
                                  scriptRelativePath As String,
                                  Optional args As String = "") As ScriptExecutionResult
        Dim metadata As SkillMetadata = GetMetadata(skillName)
        If metadata Is Nothing Then
            Return New ScriptExecutionResult() With {
                .Success = False,
                .ExitCode = -1,
                .Stderr = $"Unknown skill: {skillName}"
            }
        End If

        Return _executor.Execute(metadata.FolderPath, scriptRelativePath, args)
    End Function

    ''' <summary>
    ''' Layer 3 entry point for reference and asset files. Reads the
    ''' full text content of a resource file inside the named skill's
    ''' folder. Use this when the LLM explicitly asks to consult a
    ''' reference document or template.
    ''' </summary>
    Public Function LoadResourceContent(skillName As String,
                                        resourceRelativePath As String) As String
        Dim metadata As SkillMetadata = GetMetadata(skillName)
        If metadata Is Nothing Then
            Return $"[error] Unknown skill: {skillName}"
        End If

        Dim normalizedRel As String = resourceRelativePath.Replace("/"c, "\"c).TrimStart("\"c)
        If normalizedRel.Contains("..") Then
            Return $"[error] Rejected path traversal: {resourceRelativePath}"
        End If

        Dim fullPath As String = Path.Combine(metadata.FolderPath, normalizedRel)
        If Not File.Exists(fullPath) Then
            Return $"[error] Resource not found: {resourceRelativePath}"
        End If

        Return File.ReadAllText(fullPath, System.Text.Encoding.UTF8)
    End Function

    ''' <summary>
    ''' Enumerate all resource files inside a skill folder. Returns a
    ''' flat list including scripts/, references/, and assets/ contents.
    ''' Useful for management UIs or for building a resource catalog
    ''' that the LLM can browse.
    ''' </summary>
    Public Function EnumerateResources(skillName As String) As List(Of SkillResource)
        Dim result As New List(Of SkillResource)()
        Dim metadata As SkillMetadata = GetMetadata(skillName)
        If metadata Is Nothing Then Return result

        For Each subfolder As String In New String() {"scripts", "references", "assets"}
            Dim subfolderPath As String = Path.Combine(metadata.FolderPath, subfolder)
            If Not Directory.Exists(subfolderPath) Then Continue For

            Dim kind As SkillResource.ResourceKind
            Select Case subfolder
                Case "scripts" : kind = SkillResource.ResourceKind.Script
                Case "references" : kind = SkillResource.ResourceKind.Reference
                Case Else : kind = SkillResource.ResourceKind.Asset
            End Select

            For Each filePath As String In Directory.GetFiles(subfolderPath, "*", SearchOption.AllDirectories)
                Dim rel As String = filePath.Substring(metadata.FolderPath.Length).TrimStart("\"c, "/"c).Replace("\"c, "/"c)
                Dim info As New FileInfo(filePath)
                result.Add(New SkillResource() With {
                    .RelativePath = rel,
                    .AbsolutePath = filePath,
                    .Kind = kind,
                    .Extension = info.Extension,
                    .SizeBytes = info.Length
                })
            Next
        Next

        Return result
    End Function

End Class
