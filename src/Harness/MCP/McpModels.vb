Imports Microsoft.VisualBasic.MIME.application.json
Imports Microsoft.VisualBasic.MIME.application.json.Javascript
Imports jsobj = Microsoft.VisualBasic.MIME.application.json.Javascript.JsonObject

Namespace MCP

    Public Class McpTool
        Public Property Name As String
        Public Property Title As String
        Public Property Description As String
        Public Property InputSchema As JsonObject
        Public Property Annotations As JsonObject

        Public Shared Function FromJson(o As JsonObject) As McpTool
            If o Is Nothing Then Return Nothing
            Return New McpTool With {
                .Name = JsonRpc.Str(o, "name"),
                .Title = JsonRpc.Str(o, "title"),
                .Description = JsonRpc.Str(o, "description"),
                .InputSchema = TryCast(o("inputSchema"), JsonObject),
                .Annotations = TryCast(o("annotations"), JsonObject)
            }
        End Function
    End Class

    Public Class McpPromptArgument
        Public Property Name As String
        Public Property Description As String
        Public Property Required As Boolean

        Public Shared Function FromJson(o As JsonObject) As McpPromptArgument
            If o Is Nothing Then Return Nothing
            Return New McpPromptArgument With {
                .Name = JsonRpc.Str(o, "name"),
                .Description = JsonRpc.Str(o, "description"),
                .Required = JsonRpc.Str(o, "required").ParseBoolean
            }
        End Function
    End Class

    Public Class McpPrompt
        Public Property Name As String
        Public Property Title As String
        Public Property Description As String
        Public Property Arguments As McpPromptArgument()

        Public Shared Function FromJson(o As JsonObject) As McpPrompt
            If o Is Nothing Then Return Nothing
            Dim p As New McpPrompt With {
                .Name = JsonRpc.Str(o, "name"),
                .Title = JsonRpc.Str(o, "title"),
                .Description = JsonRpc.Str(o, "description")
            }
            Dim arr = TryCast(o("arguments"), JsonArray)
            If arr Is Nothing Then
                p.Arguments = New McpPromptArgument() {}
            Else
                Dim list As New List(Of McpPromptArgument)
                For Each item As JsonElement In arr
                    Dim jo = TryCast(item, JsonObject)
                    If jo IsNot Nothing Then list.Add(McpPromptArgument.FromJson(jo))
                Next
                p.Arguments = list.ToArray()
            End If
            Return p
        End Function
    End Class

    Public Class McpResource
        Public Property Uri As String
        Public Property Name As String
        Public Property Description As String
        Public Property MimeType As String
        Public Property Size As Integer

        Public Shared Function FromJson(o As JsonObject) As McpResource
            If o Is Nothing Then Return Nothing
            Return New McpResource With {
                .Uri = JsonRpc.Str(o, "uri"),
                .Name = JsonRpc.Str(o, "name"),
                .Description = JsonRpc.Str(o, "description"),
                .MimeType = JsonRpc.Str(o, "mimeType"),
                .Size = JsonRpc.Int(o, "size")
            }
        End Function
    End Class

    Public Class McpContent
        Public Property Type As String
        Public Property Text As String
        Public Property MimeType As String
        Public Property Uri As String
        Public Property Data As String
        Public Property Raw As JsonObject

        Public Shared Function FromJson(o As JsonObject) As McpContent
            If o Is Nothing Then Return Nothing
            Return New McpContent With {
                .Type = JsonRpc.Str(o, "type"),
                .Text = JsonRpc.Str(o, "text"),
                .MimeType = JsonRpc.Str(o, "mimeType"),
                .Uri = JsonRpc.Str(o, "uri"),
                .Data = JsonRpc.Str(o, "data"),
                .Raw = o
            }
        End Function

        Public Function ToText() As String
            If Not String.IsNullOrEmpty(Text) Then Return Text
            If Not String.IsNullOrEmpty(Data) Then Return $"[{Type}] {Uri} ({MimeType}, {Data.Length} chars of base64)"
            Return ""
        End Function
    End Class

    Public Class McpCallToolResult
        Public Property Content As List(Of McpContent)
        Public Property IsError As Boolean
        Public Property StructuredContent As JsonObject

        Public Shared Function FromJson(o As JsonObject) As McpCallToolResult
            Dim r As New McpCallToolResult With {
                .IsError = JsonRpc.Str(o, "isError").ParseBoolean,
                .StructuredContent = TryCast(o("structuredContent"), JsonObject)
            }
            r.Content = New List(Of McpContent)
            Dim arr = TryCast(o("content"), JsonArray)
            If arr IsNot Nothing Then
                For Each item As JsonElement In arr
                    Dim co = TryCast(item, JsonObject)
                    If co IsNot Nothing Then r.Content.Add(McpContent.FromJson(co))
                Next
            End If
            Return r
        End Function

        Public Function ToText() As String
            If Content Is Nothing OrElse Content.Count = 0 Then Return ""
            Dim lines As New List(Of String)
            For Each c In Content
                lines.Add(c.ToText())
            Next
            Return String.Join(vbCrLf, lines)
        End Function
    End Class

    Public Class McpResourceContent
        Public Property Uri As String
        Public Property MimeType As String
        Public Property Text As String
        Public Property Blob As String
        Public Property Raw As JsonObject

        Public Shared Function FromJson(o As JsonObject) As McpResourceContent
            If o Is Nothing Then Return Nothing
            Return New McpResourceContent With {
                .Uri = JsonRpc.Str(o, "uri"),
                .MimeType = JsonRpc.Str(o, "mimeType"),
                .Text = JsonRpc.Str(o, "text"),
                .Blob = JsonRpc.Str(o, "blob"),
                .Raw = o
            }
        End Function

        Public Function ToText() As String
            If Not String.IsNullOrEmpty(Text) Then Return Text
            If Not String.IsNullOrEmpty(Blob) Then Return $"[resource {Uri}] ({MimeType}, {Blob.Length} chars of base64)"
            Return ""
        End Function
    End Class

    Public Class McpReadResourceResult
        Public Property Contents As List(Of McpResourceContent)

        Public Shared Function FromJson(o As JsonObject) As McpReadResourceResult
            Dim r As New McpReadResourceResult()
            r.Contents = New List(Of McpResourceContent)
            Dim arr = TryCast(o("contents"), JsonArray)
            If arr IsNot Nothing Then
                For Each item As JsonElement In arr
                    Dim co = TryCast(item, JsonObject)
                    If co IsNot Nothing Then r.Contents.Add(McpResourceContent.FromJson(co))
                Next
            End If
            Return r
        End Function

        Public Function ToText() As String
            If Contents Is Nothing OrElse Contents.Count = 0 Then Return ""
            Dim lines As New List(Of String)
            For Each c In Contents
                lines.Add(c.ToText())
            Next
            Return String.Join(vbCrLf, lines)
        End Function
    End Class

    Public Class McpPromptMessage
        Public Property Role As String
        Public Property Content As McpContent

        Public Shared Function FromJson(o As JsonObject) As McpPromptMessage
            If o Is Nothing Then Return Nothing
            Return New McpPromptMessage With {
                .Role = JsonRpc.Str(o, "role"),
                .Content = McpContent.FromJson(TryCast(o("content"), JsonObject))
            }
        End Function
    End Class

    Public Class McpGetPromptResult
        Public Property Description As String
        Public Property Messages As List(Of McpPromptMessage)

        Public Shared Function FromJson(o As JsonObject) As McpGetPromptResult
            Dim r As New McpGetPromptResult()
            r.Description = JsonRpc.Str(o, "description")
            r.Messages = New List(Of McpPromptMessage)
            Dim arr = TryCast(o("messages"), JsonArray)
            If arr IsNot Nothing Then
                For Each item As JsonElement In arr
                    Dim co = TryCast(item, JsonObject)
                    If co IsNot Nothing Then r.Messages.Add(McpPromptMessage.FromJson(co))
                Next
            End If
            Return r
        End Function

        Public Function ToText() As String
            If Messages Is Nothing OrElse Messages.Count = 0 Then Return Description
            Dim sb As New StringBuilder()
            For Each m In Messages
                sb.AppendLine($"[{m.Role}] {m.Content.ToText()}")
            Next
            Return sb.ToString().TrimEnd()
        End Function
    End Class

    Public Class McpServerCapabilities
        Public Property Tools As Boolean
        Public Property Resources As Boolean
        Public Property Prompts As Boolean
        Public Property ToolsListChanged As Boolean
        Public Property ResourcesListChanged As Boolean
        Public Property PromptsListChanged As Boolean

        Public Shared Function FromJson(caps As JsonObject) As McpServerCapabilities
            Dim c As New McpServerCapabilities()
            If caps Is Nothing Then Return c
            c.Tools = caps.HasObjectKey("tools")
            c.Resources = caps.HasObjectKey("resources")
            c.Prompts = caps.HasObjectKey("prompts")
            c.ToolsListChanged = HasFlag(caps, "tools", "listChanged")
            c.ResourcesListChanged = HasFlag(caps, "resources", "listChanged")
            c.PromptsListChanged = HasFlag(caps, "prompts", "listChanged")
            Return c
        End Function

        Private Shared Function HasFlag(caps As JsonObject, key As String, flag As String) As Boolean
            Dim subObj = TryCast(caps(key), JsonObject)
            If subObj Is Nothing Then Return False
            Return subObj.HasObjectKey(flag)
        End Function
    End Class

    Public Class McpInitializeResult
        Public Property ProtocolVersion As String
        Public Property Capabilities As McpServerCapabilities
        Public Property ServerName As String
        Public Property ServerVersion As String
        Public Property Instructions As String

        Public Shared Function FromJson(o As JsonObject) As McpInitializeResult
            If o Is Nothing Then Return Nothing
            Return New McpInitializeResult With {
                .ProtocolVersion = JsonRpc.Str(o, "protocolVersion"),
                .ServerName = JsonRpc.Str(o, "serverInfo", "name"),
                .ServerVersion = JsonRpc.Str(o, "serverInfo", "version"),
                .Instructions = JsonRpc.Str(o, "instructions"),
                .Capabilities = McpServerCapabilities.FromJson(TryCast(o("capabilities"), JsonObject))
            }
        End Function
    End Class

End Namespace
