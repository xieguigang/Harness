Imports System.Collections.Generic
Imports System.Text
Imports Microsoft.VisualBasic.MIME.application.json
Imports Microsoft.VisualBasic.MIME.application.json.Javascript
Imports Microsoft.VisualBasic.MIME.text.yaml
Imports jsobj = Microsoft.VisualBasic.MIME.application.json.Javascript.JsonObject
Imports jsVal = Microsoft.VisualBasic.MIME.application.json.Javascript.JsonValue

Namespace SkillSystem

    ''' <summary>
    ''' small text helpers shared by the skill metadata and the skill manifest.
    ''' </summary>
    Friend Module SkillText

        ''' <summary>
        ''' fold a possibly multi-line value (a yaml block scalar, for example) into a
        ''' single line with collapsed whitespace. the skill catalog keeps one line per
        ''' skill, so neither the router prompt nor the summary line may be broken by a
        ''' newline inside a description.
        ''' </summary>
        Friend Function CollapseToSingleLine(text As String) As String
            If String.IsNullOrEmpty(text) Then Return ""

            Dim sb As New StringBuilder(text.Length)
            Dim pendingSpace As Boolean = False

            For Each c As Char In text
                If Char.IsWhiteSpace(c) Then
                    pendingSpace = sb.Length > 0
                Else
                    If pendingSpace Then sb.Append(" "c)
                    sb.Append(c)
                    pendingSpace = False
                End If
            Next

            Return sb.ToString()
        End Function

        ''' <summary>truncate a text to at most <paramref name="maxLength"/> characters.</summary>
        Friend Function Truncate(text As String, maxLength As Integer) As String
            If String.IsNullOrEmpty(text) OrElse text.Length <= maxLength Then Return text
            Return text.Substring(0, maxLength).TrimEnd() & "..."
        End Function
    End Module

    ''' <summary>
    ''' one <c>on_demand</c> entry of a skill manifest: a file the skill asks for plus
    ''' the condition under which it should be loaded.
    ''' </summary>
    Public Class SkillManifestReference

        ''' <summary>the manifest block the entry belongs to (references / templates / scripts / core / ...).</summary>
        Public Property Category As String

        ''' <summary>the skill relative path of the file to load.</summary>
        Public Property Path As String

        ''' <summary>the condition that tells the model when this file is needed.</summary>
        Public Property Condition As String

        Public Overrides Function ToString() As String
            Return Path
        End Function
    End Class

    ''' <summary>
    ''' one selectable axis of a skill manifest (<c>axes.&lt;name&gt;</c>): the values it may
    ''' take, the file each value loads and how the model should pick one.
    ''' </summary>
    Public Class SkillManifestAxis
        Public Property Name As String
        Public Property Detect As String
        Public Property DefaultValue As String
        Public Property Multi As Boolean
        Public Property Values As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
    End Class

    ''' <summary>
    ''' the optional <c>manifest.yaml</c> of a skill folder. nature skills use it as a
    ''' declarative router: <c>always_load</c> lists the files that are always injected,
    ''' every other block (<c>references</c>, <c>templates</c>, <c>scripts</c>, <c>core</c>,
    ''' <c>journal_formats</c>, ...) lists <c>on_demand</c> files with the condition that
    ''' selects them, and <c>axes</c> declares the optional variants of the workflow.
    '''
    ''' A skill without a manifest is not an error: <see cref="IsLoaded"/> stays false and
    ''' every list is simply empty, so the loader keeps working with plain SKILL.md skills.
    ''' </summary>
    Public Class SkillManifest

        Public Const ManifestFileName As String = "manifest.yaml"

        Public Property Name As String = ""
        Public Property Version As String = ""
        Public Property Description As String = ""
        Public Property FilePath As String = ""

        ''' <summary>true when a manifest file was found and parsed.</summary>
        Public Property IsLoaded As Boolean

        ''' <summary>the files the skill always needs, in manifest order.</summary>
        Public Property AlwaysLoad As New List(Of String)()

        ''' <summary>the flattened <c>on_demand</c> table of every manifest block.</summary>
        Public Property OnDemand As New List(Of SkillManifestReference)()

        ''' <summary>the declared optional axes.</summary>
        Public Property Axes As New List(Of SkillManifestAxis)()

        ''' <summary>scalar settings such as <c>quality_tools.consistency_script</c>.</summary>
        Public Property Scalars As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)

        ''' <summary>non fatal problems found while loading (the manifest is still usable).</summary>
        Public Property Errors As New List(Of String)()

        ''' <summary>the manifest path of a skill folder, or Nothing when there is none.</summary>
        Public Shared Function Locate(skillFolder As String) As String
            If String.IsNullOrWhiteSpace(skillFolder) Then Return Nothing

            Dim path As String = IO.Path.Combine(skillFolder, ManifestFileName)
            If IO.File.Exists(path) Then Return path

            Dim yml As String = IO.Path.Combine(skillFolder, "manifest.yml")
            If IO.File.Exists(yml) Then Return yml

            Return Nothing
        End Function

        ''' <summary>load the manifest of a skill folder; a skill without manifest yields an empty model.</summary>
        Public Shared Function Load(skillFolder As String) As SkillManifest
            Dim path As String = Locate(skillFolder)
            If path Is Nothing Then Return New SkillManifest()
            Return LoadFile(path)
        End Function

        Public Shared Function LoadFile(path As String) As SkillManifest
            Dim manifest As New SkillManifest With {.FilePath = If(path, "")}

            If String.IsNullOrWhiteSpace(path) OrElse Not IO.File.Exists(path) Then
                manifest.Errors.Add($"the manifest '{path}' does not exist.")
                Return manifest
            End If

            Dim text As String

            Try
                text = IO.File.ReadAllText(path, Encoding.UTF8)
            Catch ex As Exception
                manifest.Errors.Add($"unable to read the manifest '{path}': {ex.Message}")
                Return manifest
            End Try

            Dim parsed As SkillManifest = Parse(text, path)
            parsed.FilePath = path
            Return parsed
        End Function

        ''' <summary>
        ''' parse manifest yaml text. never throws: a malformed document is reported
        ''' through <see cref="Errors"/> and leaves an empty (but usable) manifest.
        ''' </summary>
        Public Shared Function Parse(yamlText As String, Optional filePath As String = Nothing) As SkillManifest
            Dim manifest As New SkillManifest With {.FilePath = If(filePath, "")}
            Dim root As JsonObject

            Try
                root = TryCast(YamlParser.Parse(yamlText), JsonObject)
            Catch ex As Exception
                manifest.Errors.Add($"unable to parse the manifest: {ex.Message}")
                Return manifest
            End Try

            ' an empty document parses into an empty mapping: it is not a manifest
            If root Is Nothing OrElse Not root.Any() Then
                manifest.Errors.Add("the manifest must be a non empty yaml mapping.")
                Return manifest
            End If

            manifest.IsLoaded = True
            manifest.Name = JsonRpc.Str(root, "name")
            manifest.Version = JsonRpc.Str(root, "version")
            manifest.Description = SkillText.CollapseToSingleLine(JsonRpc.Str(root, "description"))

            manifest.AlwaysLoad.AddRange(ReadStringList(root("always_load")))

            ' every remaining block is either an "on_demand" table or a plain setting map
            For Each block In root
                Dim category As String = block.Name

                If category = "always_load" OrElse category = "axes" OrElse
                   category = "name" OrElse category = "version" OrElse category = "description" Then
                    Continue For
                End If

                Dim blockObj As JsonObject = TryCast(block.Value, JsonObject)

                If blockObj Is Nothing Then
                    Dim scalar As JsonElement = block.Value
                    If scalar IsNot Nothing Then manifest.Scalars(category) = scalar.ToString()
                    Continue For
                End If

                Dim onDemand = TryCast(blockObj("on_demand"), JsonArray)

                If onDemand IsNot Nothing Then
                    For Each item As JsonElement In onDemand
                        Dim entry As JsonObject = TryCast(item, JsonObject)
                        If entry Is Nothing Then Continue For

                        manifest.OnDemand.Add(New SkillManifestReference With {
                            .Category = category,
                            .Path = JsonRpc.Str(entry, "path"),
                            .Condition = SkillText.CollapseToSingleLine(JsonRpc.Str(entry, "condition"))
                        })
                    Next
                End If

                ' scalar members of the block, e.g. quality_tools.consistency_script
                For Each field In blockObj
                    Dim fieldValue As JsonValue = TryCast(field.Value, JsonValue)
                    If fieldValue IsNot Nothing Then
                        manifest.Scalars(category & "." & field.Name) = fieldValue.ToString()
                    End If
                Next
            Next

            Dim axes As JsonObject = TryCast(root("axes"), JsonObject)

            If axes IsNot Nothing Then
                For Each axis In axes
                    Dim axisObj As JsonObject = TryCast(axis.Value, JsonObject)
                    If axisObj Is Nothing Then Continue For

                    Dim parsed As New SkillManifestAxis With {
                        .Name = axis.Name,
                        .Detect = SkillText.CollapseToSingleLine(JsonRpc.Str(axisObj, "detect")),
                        .DefaultValue = JsonRpc.Str(axisObj, "default"),
                        .Multi = JsonRpc.Str(axisObj, "multi").ParseBoolean
                    }

                    Dim values As JsonObject = TryCast(axisObj("values"), JsonObject)

                    If values IsNot Nothing Then
                        For Each v In values
                            parsed.Values(v.Name) = v.Value.ToString()
                        Next
                    End If

                    manifest.Axes.Add(parsed)
                Next
            End If

            Return manifest
        End Function

        ''' <summary>
        ''' the manifest summary injected into the layer 2 prompt: the optional axes and
        ''' the on-demand table, so the model knows which files exist and when to read them.
        ''' </summary>
        Public Function BuildSummary(Optional conditionLength As Integer = 160) As String
            If Not IsLoaded Then Return ""

            Dim sb As New StringBuilder()

            If Axes.Count > 0 Then
                sb.AppendLine("## Skill Manifest: Optional Axes")
                For Each axis In Axes
                    sb.AppendLine($"- {axis.Name} (default: {axis.DefaultValue}{(If(axis.Multi, ", multi", ""))})")

                    If Not String.IsNullOrWhiteSpace(axis.Detect) Then
                        sb.AppendLine($"  when to switch: {axis.Detect}")
                    End If

                    For Each v In axis.Values
                        sb.AppendLine($"  - {v.Key} -> {v.Value}")
                    Next
                Next
                sb.AppendLine()
            End If

            If OnDemand.Count > 0 Then
                sb.AppendLine("## On-demand files (read one with read_skill_file)")
                For Each reference In OnDemand
                    sb.AppendLine($"- {reference.Path} [{reference.Category}] {SkillText.Truncate(reference.Condition, conditionLength)}")
                Next
                sb.AppendLine()
            End If

            Return sb.ToString()
        End Function

        Private Shared Function ReadStringList(node As JsonElement) As List(Of String)
            Dim list As New List(Of String)()
            If node Is Nothing Then Return list

            Dim array = TryCast(node, JsonArray)

            If array IsNot Nothing Then
                For Each item As JsonElement In array
                    Dim text As String = item.ToString()
                    If Not String.IsNullOrWhiteSpace(text) Then list.Add(text.Trim())
                Next
            Else
                Dim text As String = node.ToString()
                If Not String.IsNullOrWhiteSpace(text) Then list.Add(text.Trim())
            End If

            Return list
        End Function

    End Class

End Namespace
