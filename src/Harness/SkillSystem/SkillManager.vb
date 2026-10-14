Imports System.IO
Imports System.Text
Imports Microsoft.VisualBasic.MIME.text.yaml

Namespace SkillSystem

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
    '''     reads the entire SKILL.md, parses it into sections, loads the
    '''     optional manifest.yaml and preloads its always_load files.
    ''' 
    ''' Layer 3 (loaded on demand):
    '''     During execution, the LLM may request a script to be run via the
    '''     execute_skill_script function tool. The manager delegates to
    '''     <see cref="SkillScriptExecutor"/> which runs the script in a
    '''     child process. Reference and asset files can also be loaded
    '''     on demand via <see cref="LoadResourceContent"/>.
    ''' </summary>
    Public Class SkillManager

        ''' <summary>safety cap of the front matter block read in layer 1.</summary>
        Private Const MaxFrontMatterChars As Integer = 256 * 1024

        ''' <summary>largest single file preloaded from the manifest always_load table.</summary>
        Public Const PreloadSingleFileLimit As Integer = 30000

        ''' <summary>total budget of the always_load files preloaded for one skill.</summary>
        Public Const PreloadTotalBudget As Integer = 120000

        ''' <summary>largest file read_skill_file is willing to return.</summary>
        Public Const MaxReadableFileBytes As Integer = 256 * 1024

        ''' <summary>hard cap of the files a single resource enumeration may report.</summary>
        Public Const MaxEnumeratedResources As Integer = 2000

        ''' <summary>
        ''' Folders that never carry resources meant for the model; the enumeration
        ''' skips them.
        ''' </summary>
        Private Shared ReadOnly ExcludedResourceFolders As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {
            "tests", "test", "__pycache__", ".git", ".github", ".venv", "node_modules", "dist", "build"
        }

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
        ''' The absolute path of the skills root folder. Sibling skill packages
        ''' (nature-shared, for example) live directly inside it.
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
            ' Read the front matter up to its closing delimiter, not up to a fixed byte
            ' budget: a long description (a yaml block scalar) used to be cut in the
            ' middle, which silently produced a different string.
            Dim frontMatter As String = ReadFrontMatter(skillMdPath)

            Dim yaml As Dictionary(Of String, String) = YamlFrontMatterParser.Parse(frontMatter)

            Dim metadata As New SkillMetadata()
            metadata.FolderPath = Path.GetDirectoryName(skillMdPath)

            If yaml.ContainsKey("name") Then metadata.Name = yaml("name").Trim()
            If yaml.ContainsKey("description") Then metadata.Description = yaml("description")
            If yaml.ContainsKey("version") Then metadata.Version = yaml("version").Trim()
            If yaml.ContainsKey("author") Then metadata.Author = yaml("author").Trim()
            If yaml.ContainsKey("tags") Then metadata.Tags = SkillMetadata.ParseTags(yaml("tags"))

            ' shared support packages are scanned (other skills reference them) but they
            ' must never be offered by the router as a selectable workflow
            metadata.IsSharedPackage = SkillMetadata.DetectSharedPackage(metadata.Name, metadata.Description)

            Return metadata
        End Function

        ''' <summary>
        ''' Read the YAML front matter block of a skill file: the leading "---" line,
        ''' every line up to the closing delimiter, and the delimiter itself. Reading
        ''' stops at the delimiter rather than at a fixed byte budget, so a long block
        ''' scalar description survives; <see cref="MaxFrontMatterChars"/> is only a
        ''' safety net for a file that never closes its block.
        ''' 
        ''' A file without front matter returns its first line, keeping the previous
        ''' behaviour of parsing whatever head the file happens to have.
        ''' </summary>
        Private Function ReadFrontMatter(skillMdPath As String) As String
            Dim sb As New StringBuilder()
            Dim total As Integer = 0

            Using reader As New StreamReader(skillMdPath, Encoding.UTF8, detectEncodingFromByteOrderMarks:=True)
                Dim firstLine As String = reader.ReadLine()
                If firstLine Is Nothing Then Return ""

                If firstLine.Trim() <> "---" Then
                    Return firstLine
                End If

                sb.Append(firstLine).Append(vbLf)

                Dim line As String
                Do
                    line = reader.ReadLine()
                    If line Is Nothing Then Exit Do

                    sb.Append(line).Append(vbLf)
                    total += line.Length

                    If line.Trim() = "---" Then Exit Do

                    If total >= MaxFrontMatterChars Then
                        System.Diagnostics.Debug.WriteLine($"the front matter of '{skillMdPath}' was capped at {MaxFrontMatterChars} characters.")
                        Exit Do
                    End If
                Loop
            End Using

            Return sb.ToString()
        End Function

        ''' <summary>
        ''' Return all cached metadata objects. This is the snapshot that
        ''' <see cref="SkillAgent"/> injects into the task-matching prompt.
        ''' </summary>
        Public Function GetAllMetadata() As IReadOnlyList(Of SkillMetadata)
            Return _metadataCache.Values.ToList()
        End Function

        ''' <summary>
        ''' The metadata the router may offer: the internal shared packages are excluded,
        ''' because they are dependencies of other skills rather than workflows the user
        ''' can invoke on their own.
        ''' </summary>
        Public Function GetRoutableMetadata() As IReadOnlyList(Of SkillMetadata)
            Return _metadataCache.Values.Where(Function(m) Not m.IsSharedPackage).ToList()
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
        ''' skill, parses it into structured sections, loads the optional
        ''' manifest.yaml and returns a <see cref="SkillDefinition"/> ready
        ''' for context injection.
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

            Dim rawContent As String = File.ReadAllText(skillMdPath, Encoding.UTF8)
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

            ' the optional manifest declares what must always be injected and what may be
            ' loaded on demand; its always_load files are read here, inside the budget
            definition.Manifest = SkillManifest.Load(metadata.FolderPath)
            PreloadAlwaysLoadFiles(definition)

            _definitionCache(skillName) = definition
            Return definition
        End Function

        ''' <summary>
        ''' The manifest of an installed skill, or Nothing when the skill is unknown.
        ''' A skill without a manifest yields an empty (but usable) manifest object.
        ''' </summary>
        Public Function LoadManifest(skillName As String) As SkillManifest
            Dim metadata As SkillMetadata = GetMetadata(skillName)
            If metadata Is Nothing Then Return Nothing
            Return SkillManifest.Load(metadata.FolderPath)
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

        ''' <summary>
        ''' Read the files declared in the manifest <c>always_load</c> table into the
        ''' definition, inside the context budget. A file that does not fit is not read
        ''' at all: it is listed as deferred, so the model can request it with
        ''' read_skill_file instead of blowing up the prompt.
        ''' </summary>
        Private Sub PreloadAlwaysLoadFiles(definition As SkillDefinition)
            Dim manifest As SkillManifest = definition.Manifest
            If manifest Is Nothing OrElse Not manifest.IsLoaded Then Return

            Dim total As Integer = 0

            For Each relativePath As String In manifest.AlwaysLoad
                Dim resolved As String = ResolveSkillRelativePath(manifest.FilePath, relativePath)
                Dim info As FileInfo = If(resolved Is Nothing, Nothing, New FileInfo(resolved))

                If info Is Nothing OrElse Not info.Exists Then
                    definition.DeferredResources.Add(relativePath)
                    Continue For
                End If

                If info.Length > PreloadSingleFileLimit OrElse total + info.Length > PreloadTotalBudget Then
                    definition.DeferredResources.Add(relativePath)
                    Continue For
                End If

                Try
                    Dim text As String = File.ReadAllText(resolved, Encoding.UTF8)

                    definition.PreloadedFiles.Add(New PreloadedSkillFile With {
                        .RelativePath = relativePath,
                        .AbsolutePath = resolved,
                        .Content = text
                    })
                    total += text.Length
                Catch ex As Exception
                    System.Diagnostics.Debug.WriteLine($"unable to preload '{resolved}': {ex.Message}")
                    definition.DeferredResources.Add(relativePath)
                End Try
            Next

            If definition.DeferredResources.Count > 0 Then
                System.Diagnostics.Debug.WriteLine(
                    $"{definition.Metadata.Name}: {definition.DeferredResources.Count} always_load file(s) were deferred (preload budget).")
            End If
        End Sub

        ''' <summary>
        ''' Resolve a path declared inside a skill (or inside its manifest) against the
        ''' folder of the declaring file. A sibling package reference such as
        ''' <c>"../nature-shared/core/x.md"</c> is allowed, because sibling skill packages
        ''' live next to each other - but the resolved path must stay inside the skills
        ''' root. Anything escaping it is rejected and Nothing is returned.
        ''' </summary>
        Public Function ResolveSkillRelativePath(baseFilePath As String, relativePath As String) As String
            If String.IsNullOrWhiteSpace(relativePath) Then Return Nothing

            Dim baseFolder As String = Path.GetDirectoryName(baseFilePath)
            If String.IsNullOrWhiteSpace(baseFolder) Then baseFolder = _skillsBaseDir

            Dim normalized As String = relativePath.Replace("/"c, Path.DirectorySeparatorChar).Replace("\"c, Path.DirectorySeparatorChar)
            Dim full As String

            Try
                full = Path.GetFullPath(Path.Combine(baseFolder, normalized))
            Catch ex As Exception
                Return Nothing
            End Try

            If IsInsideSkillsRoot(full) Then Return full

            System.Diagnostics.Debug.WriteLine($"rejected a skill path escaping the skills root: {relativePath}")
            Return Nothing
        End Function

        ''' <summary>true when the path is the skills root itself or a descendant of it.</summary>
        ''' <remarks>
        ''' the parameter must NOT be called "path": vb is case insensitive, so a local
        ''' named path would shadow the System.IO.Path type and turn Path.Combine into a
        ''' member lookup on a string.
        ''' </remarks>
        Private Function IsInsideSkillsRoot(candidatePath As String) As Boolean
            Dim separators As Char() = {Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar}
            Dim root As String = _skillsBaseDir.TrimEnd(separators)
            Dim candidate As String = candidatePath.TrimEnd(separators)

            If candidate.Equals(root, StringComparison.OrdinalIgnoreCase) Then Return True

            Return candidate.StartsWith(root & Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
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

            ' the script may live in a sibling shared package
            ' ("../nature-shared/scripts/x.py"), so the path is resolved and validated
            ' first; the executor then runs it from its own folder
            Dim resolved As String = ResolveSkillRelativePath(Path.Combine(metadata.FolderPath, "SKILL.md"), scriptRelativePath)

            If resolved Is Nothing Then
                Return New ScriptExecutionResult() With {
                    .Success = False,
                    .ExitCode = -1,
                    .Stderr = $"Rejected script path (it escapes the skills root): {scriptRelativePath}"
                }
            End If

            Return _executor.Execute(Path.GetDirectoryName(resolved), Path.GetFileName(resolved), args)
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

            Return File.ReadAllText(fullPath, Encoding.UTF8)
        End Function

        ''' <summary>
        ''' Read a text file that belongs to a skill, or to a sibling shared package the
        ''' skill references. The path is resolved and then validated: ".." itself is
        ''' allowed (the nature skills reference ../nature-shared/...), but escaping the
        ''' skills root is rejected.
        ''' </summary>
        ''' <param name="skillName">The skill whose file should be read.</param>
        ''' <param name="relativePath">The path as written in the skill (forward or backward slashes).</param>
        Public Function ReadSkillFile(skillName As String, relativePath As String) As String
            Dim metadata As SkillMetadata = GetMetadata(skillName)
            If metadata Is Nothing Then Return $"[error] Unknown skill: {skillName}"

            If String.IsNullOrWhiteSpace(relativePath) Then
                Return "[error] The relative path must not be empty."
            End If

            Dim resolved As String = ResolveSkillRelativePath(Path.Combine(metadata.FolderPath, "SKILL.md"), relativePath)

            If resolved Is Nothing Then
                Return $"[error] Rejected path (it escapes the skills root): {relativePath}"
            End If

            If Not File.Exists(resolved) Then
                Return $"[error] File not found: {relativePath}"
            End If

            Try
                Dim info As New FileInfo(resolved)

                If info.Length > MaxReadableFileBytes Then
                    Return $"[error] The file is too large to read ({info.Length} bytes, limit {MaxReadableFileBytes})."
                End If

                Return File.ReadAllText(resolved, Encoding.UTF8)
            Catch ex As Exception
                Return $"[error] Unable to read '{relativePath}': {ex.Message}"
            End Try
        End Function

        ''' <summary>
        ''' Enumerate the resource files of a skill folder. Every sub folder is walked
        ''' (the nature skills keep their content in folders such as static, core,
        ''' journal-formats or agents, not only in scripts / references / assets), except
        ''' the folders that never carry model resources. The result is capped.
        ''' </summary>
        Public Function EnumerateResources(skillName As String) As List(Of SkillResource)
            Dim result As New List(Of SkillResource)()
            Dim metadata As SkillMetadata = GetMetadata(skillName)
            If metadata Is Nothing Then Return result

            If Not Directory.Exists(metadata.FolderPath) Then Return result

            For Each subfolderPath As String In Directory.GetDirectories(metadata.FolderPath)
                Dim subfolder As String = Path.GetFileName(subfolderPath)
                If ExcludedResourceFolders.Contains(subfolder) Then Continue For

                Dim kind As SkillResource.ResourceKind = ResourceKindOf(subfolder)

                For Each filePath As String In Directory.GetFiles(subfolderPath, "*", SearchOption.AllDirectories)
                    Dim info As New FileInfo(filePath)
                    Dim effectiveKind As SkillResource.ResourceKind = kind

                    If kind = SkillResource.ResourceKind.Asset AndAlso IsScriptExtension(info.Extension) Then
                        effectiveKind = SkillResource.ResourceKind.Script
                    End If

                    Dim rel As String = filePath.Substring(metadata.FolderPath.Length).TrimStart("\"c, "/"c).Replace("\"c, "/"c)

                    result.Add(New SkillResource() With {
                        .RelativePath = rel,
                        .AbsolutePath = filePath,
                        .Kind = effectiveKind,
                        .Extension = info.Extension,
                        .SizeBytes = info.Length
                    })

                    If result.Count >= MaxEnumeratedResources Then
                        System.Diagnostics.Debug.WriteLine(
                            $"{skillName}: the resource enumeration stopped at the {MaxEnumeratedResources} file cap.")
                        Return result
                    End If
                Next
            Next

            Return result
        End Function

        ''' <summary>map a sub folder name to the kind of resource it holds.</summary>
        Private Shared Function ResourceKindOf(subfolder As String) As SkillResource.ResourceKind
            Select Case subfolder.ToLowerInvariant()
                Case "scripts", "script"
                    Return SkillResource.ResourceKind.Script
                Case "references", "reference", "docs", "doc"
                    Return SkillResource.ResourceKind.Reference
                Case Else
                    Return SkillResource.ResourceKind.Asset
            End Select
        End Function

        Private Shared Function IsScriptExtension(extension As String) As Boolean
            Select Case If(extension Is Nothing, "", extension).ToLowerInvariant()
                Case ".py", ".pyw", ".sh", ".ps1", ".bat", ".cmd", ".js", ".mjs", ".cjs", ".r", ".rb"
                    Return True
                Case Else
                    Return False
            End Select
        End Function

    End Class
End Namespace
