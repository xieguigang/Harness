' ---------------------------------------------------------------------------
' ToolCallSynthesizer —— 程序化合成工具调用轨迹（训练阶段 3）
'
' 工具调用能力不是预训练自带的，而是 SFT 阶段显式注入的。readme 描述的工业做法
' 是"大规模合成"：收集工具清单 → 自动合成单步与多步调用任务 → 生成调用轨迹 →
' 转成多轮对话。这里把这条流水线压缩成一个模板合成器。
'
' 合成两类样本：
'
'   A. 单步调用轨迹（含系统提示 + 工具清单）
'      {system(工具清单)}{User}{问题}{Assistant}{工具调用}
'      教模型"看到这类问题就输出这样的调用片段"。
'
'   B. 完整闭环轨迹（省略系统提示以节省上下文预算）
'      {User}{问题}{Assistant}{工具调用}{tool}{工具结果}{Assistant}{简短回答}
'      这一类的关键作用是<b>展示损失掩码</b>：工具结果那一段由框架回填，
'      不计入损失（模型不需要学习"预测工具会返回什么"），
'      只有 assistant 的两个片段被训练。
'
' 另外合成少量"失败 → 自我修正"样本：先调用一个不存在的工具，框架返回可用清单，
' 模型改用正确的工具。这对应 readme 里列举的"幻觉工具名"失败模式。
'
' 参数 JSON 一律由 <see cref="JsonSchema.BuildJson"/> 生成，从而保证
' "训练时喂给模型的 JSON" 与 "约束解码能够生成的 JSON" 在键顺序与形态上完全一致。
' ---------------------------------------------------------------------------

Imports Microsoft.VisualBasic.DeepLearning.LLM.Agent.ToolCalls

Public Class ToolCallSynthesizer

    Private ReadOnly _codec As DeepSeekTokenizerAdapter
    Private ReadOnly _template As ChatTemplate
    Private ReadOnly _registry As ToolRegistry
    Private ReadOnly _random As Random

    Public Sub New(codec As DeepSeekTokenizerAdapter, template As ChatTemplate,
                   registry As ToolRegistry, Optional seed As Integer = 0)

        _codec = codec
        _template = template
        _registry = registry
        _random = New Random(seed)
    End Sub

    ''' <summary>
    ''' 合成 <paramref name="count"/> 条工具调用样本（A / B / 纠错三类按固定比例混合）。
    ''' </summary>
    Public Function CreateSamples(count As Integer) As List(Of TemplatedSample)
        Dim samples As New List(Of TemplatedSample)()
        Dim tools = _registry.Tools

        For i As Integer = 0 To count - 1
            Dim tool = tools(_random.Next(tools.Count))
            Dim args = RandomArguments(tool)
            Dim json = tool.Schema.BuildJson(args)
            Dim toolCall As New ToolCall With {
                .Name = tool.Name,
                .ArgumentsJson = json,
                .Arguments = args
            }

            Dim question = QuestionFor(tool, args)
            Dim result = _registry.Invoke(toolCall)

            Select Case i Mod 5
                Case 3
                    ' 完整闭环：演示工具结果的损失掩码
                    Call samples.Add(RenderFullTrajectory(question, toolCall, result))
                Case 4
                    ' 失败 → 自我修正：演示幻觉工具名的处理
                    Call samples.Add(RenderCorrection(question, tool, toolCall))
                Case Else
                    ' 单步调用
                    Call samples.Add(RenderSingleCall(question, toolCall))
            End Select
        Next

        Return Shuffle(samples)
    End Function

    ''' <summary>把工具清单渲染成系统提示。</summary>
    Public Function BuildSystemPrompt() As String
        Return _registry.RenderCompactCatalog()
    End Function

#Region "三种样本形态"

    ''' <summary>形态 A：系统提示 + 问题 + 工具调用。</summary>
    Private Function RenderSingleCall(question As String, toolCall As ToolCall) As TemplatedSample
        Dim messages As New List(Of ChatMessage) From {
            ChatMessage.AsSystem(BuildSystemPrompt()),
            ChatMessage.AsUser(question),
            ChatMessage.AsAssistant("", New List(Of ToolCall) From {toolCall})
        }

        Return _template.Render(messages, addGenerationPrompt:=False)
    End Function

    ''' <summary>形态 B：问题 + 调用 + 工具结果 + 简短回答（省略系统提示以节省预算）。</summary>
    Private Function RenderFullTrajectory(question As String, toolCall As ToolCall,
                                          result As String) As TemplatedSample

        Dim messages As New List(Of ChatMessage) From {
            ChatMessage.AsUser(question),
            ChatMessage.AsAssistant("", New List(Of ToolCall) From {toolCall}),
            ChatMessage.AsTool(result),
            ChatMessage.AsAssistant("查询结果：" & result)
        }

        Return _template.Render(messages, addGenerationPrompt:=False)
    End Function

    ''' <summary>形态 C：先调用不存在的工具，收到可用清单后改用正确的工具。</summary>
    Private Function RenderCorrection(question As String, correctTool As ToolDefinition,
                                      toolCall As ToolCall) As TemplatedSample

        ' 故意拼一个词表里不存在的名字，触发框架的"工具名校验失败"分支
        Dim wrongName = "query_" & correctTool.Name
        Dim wrongCall As New ToolCall With {
            .Name = wrongName,
            .ArgumentsJson = toolCall.ArgumentsJson,
            .Arguments = toolCall.Arguments
        }

        Dim failure = _registry.Invoke(wrongCall)

        Dim messages As New List(Of ChatMessage) From {
            ChatMessage.AsUser(question),
            ChatMessage.AsAssistant("", New List(Of ToolCall) From {wrongCall}),
            ChatMessage.AsTool(failure),
            ChatMessage.AsAssistant("", New List(Of ToolCall) From {toolCall})
        }

        Return _template.Render(messages, addGenerationPrompt:=False)
    End Function

#End Region

#Region "随机参数与问题"

    ''' <summary>按 schema 随机生成一组合法参数。</summary>
    Private Function RandomArguments(tool As ToolDefinition) As Dictionary(Of String, String)
        Dim args As New Dictionary(Of String, String)()

        For Each p In tool.Schema.Properties
            Select Case p.Type

                Case "integer", "number"
                    args(p.Name) = _random.Next(1, 20).ToString()

                Case "boolean"
                    args(p.Name) = If(_random.Next(2) = 0, "true", "false")

                Case Else
                    If p.EnumValues IsNot Nothing AndAlso p.EnumValues.Length > 0 Then
                        args(p.Name) = p.EnumValues(_random.Next(p.EnumValues.Length))
                    Else
                        args(p.Name) = StringPool(p.Name)
                    End If

            End Select
        Next

        Return args
    End Function

    ''' <summary>自由字符串参数的取值池。</summary>
    Private Function StringPool(name As String) As String
        If name = "query" Then
            Return DemoTools.KnowledgeBase.Keys.ElementAt(_random.Next(DemoTools.KnowledgeBase.Count))
        End If

        Return "value" & _random.Next(1, 10)
    End Function

    ''' <summary>按工具类型生成一个自然的用户问题。</summary>
    Private Function QuestionFor(tool As ToolDefinition, args As Dictionary(Of String, String)) As String
        Select Case tool.Name

            Case "get_weather"
                Dim variants = New String() {
                    args("city") & " 的天气怎么样？",
                    "帮我查一下 " & args("city") & " 的天气",
                    args("city") & " 现在多少度？"
                }

                Return variants(_random.Next(variants.Length))

            Case "get_current_time"
                Return args("timezone") & " 现在几点？"

            Case "calculate"
                Return args("a") & " " & OperatorSymbol(args("op")) & " " & args("b") & " 等于多少？"

            Case "search_knowledge"
                Return "什么是 " & args("query") & "？"

            Case Else
                Return "请调用 " & tool.Name & " 帮我处理一下。"

        End Select
    End Function

    Private Shared Function OperatorSymbol(op As String) As String
        Select Case op
            Case "add" : Return "+"
            Case "subtract" : Return "-"
            Case "multiply" : Return "*"
            Case "divide" : Return "/"
            Case Else : Return "?"
        End Select
    End Function

#End Region

    Private Function Shuffle(samples As List(Of TemplatedSample)) As List(Of TemplatedSample)
        For i As Integer = samples.Count - 1 To 1 Step -1
            Dim j = _random.Next(i + 1)
            Dim swap = samples(i)
            samples(i) = samples(j)
            samples(j) = swap
        Next

        Return samples
    End Function

End Class
