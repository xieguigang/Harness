' ---------------------------------------------------------------------------
' ChatTemplate —— 对话消息 → token 序列 + 损失掩码
'
' 这是 function calling 链路里的第一环："把工具定义与对话历史翻译成模型见过的 token 格式"。
' 真实框架里这一步由随模型发布的 Jinja2 模板完成；这里用 VB 复刻同一套语义，
' 规则与 tokenizer_config.json 中的 chat_template 逐条对齐：
'
'     {BOS}{system}\n\n{User}{用户}{Assistant}{回复}{EOS}
'
'     assistant 带工具调用时：
'     {Assistant}{content}{tool_calls_begin}
'         {tool_call_begin}function{sep}{name}\n```json\n{args}\n```{tool_call_end}
'     {tool_calls_end}{EOS}
'
'     tool 结果：
'     {tool_outputs_begin}{tool_output_begin}{content}{tool_output_end}{tool_outputs_end}
'
' 关键设计是<b>损失掩码</b>：
'   * 只对 assistant 自己产出的 token（回复内容 / 工具调用片段 / 其后的 EOS）计损失；
'   * system、user、tool 结果一律不计 —— 模型要学的是"该怎么回应"，
'     而不是"复述用户说了什么"或"预测工具会返回什么"。
'
' 掩码是按<b>片段</b>对齐的：文本被切成若干段（段落边界正好落在保留 token 上），
' 每段单独编码后拼接，因此掩码与 token 一一对应，不需要事后做字符串位置回溯。
' ---------------------------------------------------------------------------

Imports Microsoft.VisualBasic.DeepLearning.LLM.Agent.ToolCalls
Imports Microsoft.VisualBasic.DeepLearning.LLM.Trainer

''' <summary>一条对话消息。</summary>
Public Class ChatMessage

    ''' <summary><c>system</c> / <c>user</c> / <c>assistant</c> / <c>tool</c>。</summary>
    Public Property Role As String

    ''' <summary>消息正文。</summary>
    Public Property Content As String

    ''' <summary>assistant 消息携带的工具调用（仅 <c>assistant</c> 角色有意义）。</summary>
    Public Property ToolCalls As List(Of ToolCall)

    Public Sub New()
    End Sub

    Public Sub New(role As String, content As String, Optional toolCalls As List(Of ToolCall) = Nothing)
        Me.Role = role
        Me.Content = content
        Me.ToolCalls = toolCalls
    End Sub

    Public Shared Function AsSystem(content As String) As ChatMessage
        Return New ChatMessage("system", content)
    End Function

    Public Shared Function AsUser(content As String) As ChatMessage
        Return New ChatMessage("user", content)
    End Function

    Public Shared Function AsAssistant(content As String, Optional toolCalls As List(Of ToolCall) = Nothing) As ChatMessage
        Return New ChatMessage("assistant", content, toolCalls)
    End Function

    Public Shared Function AsTool(content As String) As ChatMessage
        Return New ChatMessage("tool", content)
    End Function

End Class

''' <summary>渲染结果：token 序列 + 逐位置的损失掩码。</summary>
Public Class TemplatedSample

    Public Property Text As String
    Public Property TokenIds As Integer()
    ''' <summary>与 <see cref="TokenIds"/> 等长；True 表示该位置计入损失。</summary>
    Public Property LossMask As Boolean()

    ''' <summary>计入损失的 token 个数。</summary>
    Public ReadOnly Property SupervisedTokens As Integer
        Get
            If LossMask Is Nothing Then Return 0

            Dim count As Integer = 0

            For Each flag In LossMask
                If flag Then count += 1
            Next

            Return count
        End Get
    End Property

    Public Overrides Function ToString() As String
        Return $"tokens={TokenIds.Length}, supervised={SupervisedTokens}"
    End Function

End Class

''' <summary>把消息列表渲染为 token 序列与损失掩码。</summary>
Public Class ChatTemplate

    Private Class Segment
        Public Text As String
        Public Trainable As Boolean
    End Class

    Private ReadOnly _codec As DeepSeekTokenizerAdapter

    Public Sub New(codec As DeepSeekTokenizerAdapter)
        If codec Is Nothing Then Throw New ArgumentNullException(NameOf(codec))
        _codec = codec
    End Sub

    ''' <summary>
    ''' 渲染一段对话。
    ''' </summary>
    ''' <param name="messages">消息列表</param>
    ''' <param name="addGenerationPrompt">
    ''' 是否在末尾追加 <c>&lt;｜Assistant｜&gt;</c>（推理时追加，训练时通常不追加）。
    ''' </param>
    Public Function Render(messages As IEnumerable(Of ChatMessage),
                           Optional addGenerationPrompt As Boolean = False) As TemplatedSample

        Dim segments As New List(Of Segment)

        ' BOS 后面直接跟 system 内容（DeepSeek 模板不给 system 加角色标记）
        Call segments.Add(New Segment With {.Text = ToolCallProtocol.BeginOfSentenceMarker, .Trainable = False})

        Dim systemAdded As Boolean = False

        For Each msg In messages
            Select Case msg.Role

                Case "system"
                    If systemAdded Then
                        Call segments.Add(New Segment With {.Text = vbLf & vbLf, .Trainable = False})
                    End If

                    Call segments.Add(New Segment With {.Text = msg.Content, .Trainable = False})
                    systemAdded = True

                Case "user"
                    Call segments.Add(New Segment With {.Text = ToolCallProtocol.UserMarker, .Trainable = False})
                    Call segments.Add(New Segment With {.Text = msg.Content, .Trainable = False})

                Case "assistant"
                    Call RenderAssistant(segments, msg)

                Case "tool"
                    Call segments.Add(New Segment With {.Text = ToolCallProtocol.OutputsBeginMarker, .Trainable = False})
                    Call segments.Add(New Segment With {.Text = ToolCallProtocol.OutputBeginMarker, .Trainable = False})
                    Call segments.Add(New Segment With {.Text = msg.Content, .Trainable = False})
                    Call segments.Add(New Segment With {.Text = ToolCallProtocol.OutputEndMarker, .Trainable = False})
                    Call segments.Add(New Segment With {.Text = ToolCallProtocol.OutputsEndMarker, .Trainable = False})

                Case Else
                    Throw New ArgumentException($"未知的消息角色 '{msg.Role}'")
            End Select
        Next

        If systemAdded Then
            Call segments.Add(New Segment With {.Text = vbLf & vbLf, .Trainable = False})
        End If

        If addGenerationPrompt Then
            Call segments.Add(New Segment With {.Text = ToolCallProtocol.AssistantMarker, .Trainable = False})
        End If

        Return Compile(segments)
    End Function

    ''' <summary>
    ''' 渲染 assistant 的一条消息：纯文本回复，或一轮工具调用。
    ''' </summary>
    ''' <remarks>
    ''' 工具调用片段本身是<b>要训练</b>的（"调用决策与调用本身计算交叉熵"），
    ''' 被 mask 掉的是随后由框架回填的工具结果。
    ''' </remarks>
    Private Sub RenderAssistant(segments As List(Of Segment), msg As ChatMessage)
        Call segments.Add(New Segment With {.Text = ToolCallProtocol.AssistantMarker, .Trainable = False})

        If Not String.IsNullOrEmpty(msg.Content) Then
            Call segments.Add(New Segment With {.Text = msg.Content, .Trainable = True})
        End If

        If msg.ToolCalls Is Nothing OrElse msg.ToolCalls.Count = 0 Then
            Call segments.Add(New Segment With {.Text = ToolCallProtocol.EndOfSentenceMarker, .Trainable = True})
            Return
        End If

        Call segments.Add(New Segment With {.Text = ToolCallProtocol.CallsBeginMarker, .Trainable = True})

        For i As Integer = 0 To msg.ToolCalls.Count - 1
            Dim toolCall = msg.ToolCalls(i)

            If i > 0 Then
                ' 并行工具调用之间用换行分隔
                Call segments.Add(New Segment With {.Text = vbLf, .Trainable = True})
            End If

            Call segments.Add(New Segment With {
                .Text = ToolCallProtocol.CallBeginMarker &
                        ToolCallProtocol.CallTypeFunction & ToolCallProtocol.SepMarker & toolCall.Name & vbLf &
                        ToolCallProtocol.JsonFenceOpen,
                .Trainable = True
            })

            Call segments.Add(New Segment With {.Text = toolCall.ArgumentsJson, .Trainable = True})

            Call segments.Add(New Segment With {
                .Text = ToolCallProtocol.JsonFenceClose & ToolCallProtocol.CallEndMarker,
                .Trainable = True
            })
        Next

        Call segments.Add(New Segment With {
            .Text = ToolCallProtocol.CallsEndMarker & ToolCallProtocol.EndOfSentenceMarker,
            .Trainable = True
        })
    End Sub

    ''' <summary>把片段列表编码拼接成 token 序列与掩码。</summary>
    Private Function Compile(segments As List(Of Segment)) As TemplatedSample
        Dim ids As New List(Of Integer)
        Dim mask As New List(Of Boolean)
        Dim text As New Text.StringBuilder()

        For Each segment In segments
            Call text.Append(segment.Text)

            Dim segmentIds = _codec.Encode(segment.Text)

            For Each id In segmentIds
                Call ids.Add(id)
                Call mask.Add(segment.Trainable)
            Next
        Next

        Return New TemplatedSample With {
            .Text = text.ToString(),
            .TokenIds = ids.ToArray(),
            .LossMask = mask.ToArray()
        }
    End Function

    ''' <summary>
    ''' 把若干条等长样本整理成一个训练批次（长度不足的用句尾标记补齐并屏蔽损失）。
    ''' </summary>
    ''' <param name="samples">样本列表</param>
    ''' <param name="sequenceLength">目标序列长度</param>
    ''' <param name="batchSize">批次大小</param>
    Public Function CreateBatch(samples As IList(Of TemplatedSample),
                                sequenceLength As Integer,
                                batchSize As Integer) As LMBatch

        If batchSize <= 0 Then Throw New ArgumentException("batchSize 必须为正数")

        Dim tokens(batchSize - 1)() As Integer
        Dim masks(batchSize - 1)() As Boolean

        Dim padId = _codec.TokenIdOf(ToolCallProtocol.EndOfSentenceMarker)

        If padId < 0 Then padId = 0

        For b As Integer = 0 To batchSize - 1
            Dim sample = samples(b Mod samples.Count)

            ' 训练窗口需要 seqLen + 1 个 token 才能产生 seqLen 个预测目标。
            Dim window = sequenceLength + 1
            Dim start As Integer = 0

            If sample.TokenIds.Length > window Then
                ' 样本超窗时取<b>尾部</b>而不是头部：assistant 的内容永远在末尾，
                ' 取头部会把最该学习的部分整个丢掉 —— 损失掩码会全为 False，
                ' 表现为"某个训练步 loss 恒为 0"，而且不会报任何错。
                start = sample.TokenIds.Length - window
            End If

            Dim take = System.Math.Min(sample.TokenIds.Length - start, window)
            Dim row(take - 1) As Integer
            Dim rowMask(take - 1) As Boolean

            Call Array.Copy(sample.TokenIds, start, row, 0, take)
            Call Array.Copy(sample.LossMask, start, rowMask, 0, take)

            If take < sequenceLength + 1 Then
                ReDim Preserve row(sequenceLength)
                ReDim Preserve rowMask(sequenceLength)

                For i As Integer = take To sequenceLength
                    row(i) = padId
                    rowMask(i) = False
                Next
            End If

            tokens(b) = row
            masks(b) = rowMask
        Next

        Return LMBatch.Create(tokens, masks, padId)
    End Function

End Class
