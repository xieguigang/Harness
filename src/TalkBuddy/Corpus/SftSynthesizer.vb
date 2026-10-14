' ---------------------------------------------------------------------------
' SftSynthesizer —— 程序化合成"指令 → 回复"样本（训练阶段 2）
'
' readme 里的第二阶段是"用人工标注的高质量指令-回复对继续训练"。标注我们没有，
' 但可以用模板程序化地合成出一批结构规整的指令-回复对 —— 数据量小、质量可控，
' 恰好对应 SFT 阶段"数据量小、质量高"的特征。
'
' 这一阶段的训练目标与预训练完全相同（仍然是下一个 token 预测），差别只在两点：
'   1. 数据带上了对话结构（角色标记），让模型学会"轮到我说话"的边界；
'   2. 损失<b>只计在 assistant 的 token 上</b> —— 学会"该怎么回答"，
'      而不是"复述用户问了什么"。
' ---------------------------------------------------------------------------

Public Class SftSynthesizer

    Private Structure QaPair
        Public Question As String
        Public Answer As String
    End Structure

    Private ReadOnly _codec As DeepSeekTokenizerAdapter
    Private ReadOnly _template As ChatTemplate
    Private ReadOnly _random As Random

    ''' <summary>SFT 阶段使用的系统提示（尽量短，把上下文预算留给对话本身）。</summary>
    Public Const SystemPrompt As String = "You are a small language model that explains LLM concepts."

    Public Sub New(codec As DeepSeekTokenizerAdapter, template As ChatTemplate, Optional seed As Integer = 0)
        _codec = codec
        _template = template
        _random = New Random(seed)
    End Sub

    ''' <summary>合成 <paramref name="count"/> 条指令样本。</summary>
    Public Function CreateSamples(count As Integer) As List(Of TemplatedSample)
        Dim allPairs = Pairs()
        Dim samples As New List(Of TemplatedSample)()

        For i As Integer = 0 To count - 1
            Dim pair = allPairs(i Mod allPairs.Length)

            Dim messages As New List(Of ChatMessage) From {
                ChatMessage.AsSystem(SystemPrompt),
                ChatMessage.AsUser(pair.Question),
                ChatMessage.AsAssistant(pair.Answer)
            }

            Call samples.Add(_template.Render(messages, addGenerationPrompt:=False))
        Next

        ' 打乱顺序，避免训练时同一批里全是同主题样本
        Return Shuffle(samples)
    End Function

    Private Function Shuffle(samples As List(Of TemplatedSample)) As List(Of TemplatedSample)
        For i As Integer = samples.Count - 1 To 1 Step -1
            Dim j = _random.Next(i + 1)
            Dim swap = samples(i)
            samples(i) = samples(j)
            samples(j) = swap
        Next

        Return samples
    End Function

    ''' <summary>
    ''' 内置的指令-回复对。
    ''' </summary>
    ''' <remarks>
    ''' 内容全部围绕本 demo 涉及的概念，回答刻意写得很短 ——
    ''' 小模型的注意力预算有限，长回答只会让它学到"胡言乱语也能算对"。
    ''' </remarks>
    Private Shared Function Pairs() As QaPair()
        Return New QaPair() {
            New QaPair With {.Question = "你好", .Answer = "你好！我是一个用于演示的语言模型。"},
            New QaPair With {.Question = "你是谁", .Answer = "我是一个 decoder-only 的语言模型演示。"},
            New QaPair With {.Question = "什么是注意力", .Answer = "注意力按相关性加权聚合其他位置的信息。"},
            New QaPair With {.Question = "为什么要多头", .Answer = "多头让模型在不同子空间捕捉不同类型的依赖。"},
            New QaPair With {.Question = "什么是因果掩码", .Answer = "因果掩码屏蔽未来位置，保证只能看到前文。"},
            New QaPair With {.Question = "什么是位置编码", .Answer = "位置编码把词序信息注入到表示里。"},
            New QaPair With {.Question = "什么是 RoPE", .Answer = "RoPE 对查询和键按位置旋转，使点积只依赖相对距离。"},
            New QaPair With {.Question = "什么是 RMSNorm", .Answer = "RMSNorm 只按均方根缩放，计算量比 LayerNorm 更小。"},
            New QaPair With {.Question = "什么是前置归一化", .Answer = "前置归一化先归一化再进子层，让梯度有一条直连通路。"},
            New QaPair With {.Question = "什么是 SwiGLU", .Answer = "SwiGLU 用 SiLU 做门控，比 ReLU 表达能力更强。"},
            New QaPair With {.Question = "什么是 MoE", .Answer = "MoE 把前馈网络拆成多个专家，每个 token 只激活少数几个。"},
            New QaPair With {.Question = "MoE 有什么好处", .Answer = "MoE 让总参数量很大而每个 token 的计算量很小。"},
            New QaPair With {.Question = "什么是细粒度专家", .Answer = "细粒度专家把大专家切成更多小专家，组合数指数增长。"},
            New QaPair With {.Question = "什么是共享专家", .Answer = "共享专家被每个 token 无条件激活，承载通用知识。"},
            New QaPair With {.Question = "负载均衡为什么重要", .Answer = "负载不均会造成计算热点，甚至导致专家塌缩。"},
            New QaPair With {.Question = "什么是 KV Cache", .Answer = "KV Cache 复用已生成 token 的键和值，把单步开销从平方降到线性。"},
            New QaPair With {.Question = "KV Cache 的代价是什么", .Answer = "缓存占用随序列长度线性增长。"},
            New QaPair With {.Question = "什么是温度", .Answer = "温度缩放 logits，越高分布越平坦、生成越随机。"},
            New QaPair With {.Question = "什么是 Top-p 采样", .Answer = "Top-p 只在累计概率达到 p 的最小集合里采样。"},
            New QaPair With {.Question = "什么是权重绑定", .Answer = "权重绑定让输出层与词嵌入共享同一份权重。"},
            New QaPair With {.Question = "什么是 SFT", .Answer = "SFT 用指令-回复对继续训练，教会模型听懂指令。"},
            New QaPair With {.Question = "为什么只对助手算损失", .Answer = "模型要学的是该怎么回答，而不是复述用户说了什么。"},
            New QaPair With {.Question = "什么是工具调用", .Answer = "工具调用是模型生成特殊标记，框架解析并执行真实函数。"},
            New QaPair With {.Question = "模型会真的调用函数吗", .Answer = "不会。它只是生成符合约定的标记序列，执行由框架完成。"},
            New QaPair With {.Question = "什么是约束解码", .Answer = "约束解码把非法 token 的概率置零，使结构必然合法。"},
            New QaPair With {.Question = "约束解码能保证语义正确吗", .Answer = "不能，它只保证语法合法，参数是否合理仍取决于模型。"},
            New QaPair With {.Question = "什么是 Agent Loop", .Answer = "Agent Loop 循环执行决策、执行、回填，直到不再调用工具。"},
            New QaPair With {.Question = "谢谢", .Answer = "不客气，希望这些解释对你有帮助。"}
        }
    End Function

End Class
