' ---------------------------------------------------------------------------
' TalkBuddy 的 demo 配置集中处
'
' 这里只放"这个 demo 选了什么超参"，不放算法。算法在
' Microsoft.VisualBasic.MachineLearning.LLM 命名空间中。
'
' 关于模型规模：选的是"能在一台普通机器上把三阶段训练跑完"的量级。
' 参数量的绝对大头是词嵌入/输出层（10 万词表 × d_model），这也是为什么
' 输出层与词嵌入做了权重绑定 —— 否则这一项会再翻一倍。
' ---------------------------------------------------------------------------

Imports Microsoft.VisualBasic.DeepLearning.LLM
Imports Microsoft.VisualBasic.DeepLearning.LLM.Trainer

''' <summary>demo 的模型 / 训练 / 分词器配置。</summary>
Public Module DemoConfig

#Region "分词器"

    ''' <summary>
    ''' DeepSeek 分词器模型目录（内含 tokenizer.json 与 tokenizer_config.json）。
    ''' </summary>
    ''' <remarks>
    ''' 可用环境变量 <c>TALKBUDDY_TOKENIZER</c> 覆盖，便于把仓库搬到别的位置。
    ''' </remarks>
    Public Const DefaultTokenizerDirectory As String =
        "E:\codebuddy\GCModeller\src\runtime\sciBASIC#\nlp\hugging_face_tokenizer"

    ''' <summary>解析分词器目录：优先环境变量，其次默认绝对路径。</summary>
    Public Function ResolveTokenizerDirectory() As String
        Dim fromEnv = Environment.GetEnvironmentVariable("TALKBUDDY_TOKENIZER")

        If Not String.IsNullOrEmpty(fromEnv) Then Return fromEnv

        Return DefaultTokenizerDirectory
    End Function

    ''' <summary>
    ''' 词表上限。
    ''' </summary>
    ''' <remarks>
    ''' <c>0</c> 表示使用 DeepSeek 的<b>全量</b>词表（约 10 万），这是本 demo 的默认选择。
    ''' 设成较小的值（如 4096）可以把 LM head 的矩阵乘缩小一个数量级，用于快速冒烟；
    ''' 此时超出上限的 id 会被映射到句尾标记，语义会变差，仅供跑通流程。
    ''' </remarks>
    Public Property VocabularyLimit As Integer = 0

#End Region

#Region "模型结构"

    ''' <summary>
    ''' 模型规模档位。
    ''' </summary>
    ''' <remarks>
    ''' 参数量的大头始终是"词表 × d_model"的词嵌入（12.8 万词表下，d_model=768 就是 9890 万），
    ''' 因此放大规模时必须同步考虑专家中间层宽度 —— 若让它自动推导，
    ''' d_model=768 会得到 768 宽的专家，MoE 部分会直接冲到 3.8 亿，远超预算。
    ''' </remarks>
    Public Enum ModelScale

        ''' <summary>
        ''' 教学小档（约 1800 万参数）：与早先版本一致，用于快速验证流程。
        ''' </summary>
        Tiny = 0

        ''' <summary>
        ''' 约 1.9 亿参数：d_model=768、12 层、16 路由专家 + 2 共享专家。
        ''' </summary>
        ''' <remarks>
        ''' 单精度下设备侧稳定占用约 <c>1.9亿 × 4 × 3 ≈ 2.3 GB</c>（权重 + 一阶矩 + 二阶矩），
        ''' 主机侧因为要保存 <c>Double</c> 主副本，需要约 <c>1.9亿 × 8 × 4 ≈ 6.1 GB</c>。
        ''' </remarks>
        Scale200M = 1

        ''' <summary>
        ''' 约 3.5 亿参数：d_model=1024、12 层、16 路由专家 + 2 共享专家。
        ''' </summary>
        ''' <remarks>
        ''' 设备侧约 <c>4.2 GB</c>（8 GB 卡可用约 7 GB，能放下）；
        ''' 但主机侧需要约 <c>11.2 GB</c> 内存，请确认物理内存充足。
        ''' </remarks>
        Scale400M = 2

    End Enum

    ''' <summary>当前使用的规模档位。</summary>
    Public Property Scale As ModelScale = ModelScale.Scale200M

    ''' <summary>
    ''' demo 模型的超参（按当前规模档位构建）。
    ''' </summary>
    ''' <param name="vocabSize">词表大小（由分词器适配器给出）</param>
    Public Function CreateModelConfig(vocabSize As Integer) As LLMModelConfig
        If Scale = ModelScale.Tiny Then Return CreateTinyModelConfig(vocabSize)

        Dim config As New LLMModelConfig With {
            .VocabSize = vocabSize,
            .MaxSeqLen = 256,
            .RopeTheta = 10000.0,
            .UseMoE = True,
            .MoEStartLayer = 1,
            .NumRoutedExperts = 16,
            .TopKExperts = 2,
            .NumSharedExperts = 2,
            .NodeGroups = 4,
            .MaxNodesPerToken = 2,
            .BalanceBiasRate = BalanceBiasRate
        }

        If Scale = ModelScale.Scale400M Then
            ' d_model=1024：16 头 × 64 = 1024；GQA 4 个 KV 头，KV Cache 缩小 4 倍
            config.DModel = 1024
            config.NumLayers = 12
            config.NumHeads = 16
            config.NumKvHeads = 4
            config.HeadDim = 64
            config.ExpertHidden = 256
            config.DenseFfnHidden = 4096
        Else
            ' d_model=768：12 头 × 64 = 768；GQA 4 个 KV 头
            config.DModel = 768
            config.NumLayers = 12
            config.NumHeads = 12
            config.NumKvHeads = 4
            config.HeadDim = 64
            config.ExpertHidden = 128
            config.DenseFfnHidden = 2048
        End If

        ' MoE 从第 1 层开始：第 0 层保持稠密（DeepSeek 的做法），
        ' 让路由器不必在还很"生"的浅层表示上做选择。
        ' 节点受限路由：16 个路由专家分 4 组，每个 token 的 Top-2 必须落在最多 2 组内。

        Return config
    End Function

    ''' <summary>
    ''' 教学小档：4 层、d_model=128、8 专家，约 1800 万参数。
    ''' </summary>
    ''' <remarks>
    ''' 保留它是因为"规模放大后单步要几秒"，调试代码逻辑时用这个档位才现实。
    ''' </remarks>
    Private Function CreateTinyModelConfig(vocabSize As Integer) As LLMModelConfig
        Return New LLMModelConfig With {
            .VocabSize = vocabSize,
            .DModel = 128,
            .NumLayers = 4,
            .NumHeads = 4,
            .NumKvHeads = 4,
            .HeadDim = 32,
            .MaxSeqLen = 256,
            .RopeTheta = 10000.0,
            .DenseFfnHidden = 0,
            .UseMoE = True,
            .MoEStartLayer = 1,
            .NumRoutedExperts = 8,
            .TopKExperts = 2,
            .NumSharedExperts = 1,
            .ExpertHidden = 0,
            .NodeGroups = 4,
            .MaxNodesPerToken = 2,
            .BalanceBiasRate = BalanceBiasRate
        }
    End Function

    ''' <summary>
    ''' 只用一个批次即可跑通的极小配置（用于验证流程，不用于观察学习效果）。
    ''' </summary>
    Public Function CreateSmokeModelConfig(vocabSize As Integer) As LLMModelConfig
        ' 必须从小档派生：若从当前档位派生，ExpertHidden / DenseFfnHidden 会保留
        ' 大档的取值（128 / 2048），与缩到 d_model=96 的其它字段严重不匹配
        Dim config = CreateTinyModelConfig(vocabSize)

        config.NumLayers = 2
        config.DModel = 96
        config.NumHeads = 4
        config.NumKvHeads = 2       ' GQA：KV Cache 缩小 2 倍
        config.HeadDim = 24
        config.MaxSeqLen = 96
        config.NumRoutedExperts = 4
        config.TopKExperts = 2
        config.NumSharedExperts = 1
        config.ExpertHidden = 64
        config.DenseFfnHidden = 256
        config.NodeGroups = 2
        config.MaxNodesPerToken = 1

        Return config
    End Function

#End Region

#Region "训练超参"

    ' ------------------------------------------------------------------
    ' 训练步数的选择依据（全量 12.8 万词表 + SIMD CPU 后端实测）：
    '
    '   预训练   N =  64 → 约 3.4 s/step
    '   指令 SFT N =  96 → 约 4.1 s/step
    '   工具 SFT N = 256 → 约 10  s/step
    '
    ' 单步耗时的绝对大头是输出层的 [N, d_model] × [d_model, vocab] 矩阵乘 ——
    ' 词表越大、序列越长，它越贵。下面的步数把整次演示控制在 10 分钟量级。
    ' 想更快可以：打开 CUDA（CudaTensor.Register）、减少步数、或临时调小 VocabularyLimit。
    ' ------------------------------------------------------------------

    ''' <summary>预训练步数。</summary>
    Public Property PretrainSteps As Integer = 25

    ''' <summary>指令跟随 SFT 的步数。</summary>
    Public Property InstructionSftSteps As Integer = 25

    ''' <summary>Function Calling SFT 的步数（单步最贵，因此用得更少）。</summary>
    Public Property ToolSftSteps As Integer = 16

    ''' <summary>训练 batch 里放几条样本。</summary>
    Public Property BatchSize As Integer = 2

    ''' <summary>预训练窗口长度（纯文本，窗口可以开得比较小）。</summary>
    Public Property PretrainSequenceLength As Integer = 32

    ''' <summary>指令 SFT 的序列长度（样本很短）。</summary>
    Public Property InstructionSequenceLength As Integer = 48

    ''' <summary>
    ''' 工具调用 SFT 的序列长度。
    ''' </summary>
    ''' <remarks>
    ''' 明显比前两个阶段长，因为一条完整的工具调用轨迹要装下"工具清单 + 问题 +
    ''' 调用片段 + 工具结果 + 回答"。注意训练成本与序列长度成正比（LM head 是
    ''' <c>N × d_model × vocab</c> 级别的矩阵乘），因此这个值不能随意加大。
    ''' </remarks>
    Public Property ToolSequenceLength As Integer = 128

    ''' <summary>解耦权重衰减。</summary>
    Public Property WeightDecay As Double = 0.01

    ''' <summary>峰值学习率（教学小档的取值）。小模型 + 小语料用大一点更容易在几十步内看到 loss 下降。</summary>
    Public Property LearningRate As Double = 0.0015

    ''' <summary>
    ''' 按规模档位给出实际使用的峰值学习率。
    ''' </summary>
    ''' <remarks>
    ''' 0.0015 是围绕 1800 万参数的小档调出来的。参数量放大十倍之后同一个学习率过于激进：
    ''' 实测 2 亿档在工具调用 SFT 阶段出现明显的梯度尖峰（全局梯度范数从 1.8 突增到 1987），
    ''' 紧接着 loss 变成 NaN。这不是设备常驻特有的问题 —— 关掉设备常驻同样会逼近这个尖峰，
    ''' 只是主机路径的更新时机不同因而晚一步暴露。
    ''' <para>
    ''' 大模型对学习率更敏感是普遍规律（同一学习率下参数量越大越容易发散），
    ''' 因此这里按档位下调，而不是把损失归咎于某个具体实现。
    ''' </para>
    ''' </remarks>
    Public ReadOnly Property EffectiveLearningRate As Double
        Get
            Select Case Scale
                Case ModelScale.Tiny
                    Return LearningRate
                Case ModelScale.Scale400M
                    Return 0.0002
                Case Else
                    Return 0.0004
            End Select
        End Get
    End Property

    ''' <summary>
    ''' MoE 负载均衡偏置的步长 u。
    ''' </summary>
    ''' <remarks>
    ''' readme 引用的 DeepSeek 取值是 0.001，但那是配合数十万训练步的尺度。本 demo 的
    ''' 全部训练只有几十步，u 取太小（实测 0.001 / 0.005）时偏置移动量只有百分之几，
    ''' 远小于 sigmoid 打分之差，负载均衡根本来不及起作用 —— 累计最大负载比会停在 1.9x 左右。
    ''' u = 0.05 时偏置在几十步内就能移动到与打分同量级，累计最大负载比降到 1.17x。
    ''' 代价是即时路由会在"集中 / 分散"之间摆动得更明显（见 ShowMoERouting 的说明）——
    ''' 这就是无辅助损失负载均衡里"步长"这个超参本身要做的权衡。
    ''' </remarks>
    Public Property BalanceBiasRate As Double = 0.05

    ''' <summary>学习率 warmup 步数。</summary>
    Public Property WarmupSteps As Integer = 5

    ''' <summary>全局梯度范数裁剪上限。</summary>
    Public Property MaxGradNorm As Double = 1.0

    ''' <summary>
    ''' 各规模档位默认的训练步数。
    ''' </summary>
    ''' <remarks>
    ''' 步数直接由单步耗时倒推：教学小档约 1 s/step，2 亿参数档实测在 10 s/step 量级
    ''' （LM head 随 d_model 线性放大 6 倍、MoE 专家数翻倍），
    ''' 因此步数必须相应压到个位数，才能把整次演示控制在半小时以内。
    ''' </remarks>
    Public Function ScaleDefaults() As (Pretrain As Integer, Instruction As Integer, Tool As Integer)
        Select Case Scale
            Case ModelScale.Tiny : Return (25, 25, 16)
            Case ModelScale.Scale400M : Return (3, 3, 2)
            Case Else : Return (6, 6, 4)
        End Select
    End Function

    ''' <summary>按给定步数为某个阶段构造训练配置。</summary>
    Public Function CreateTrainingConfig(totalSteps As Integer) As TrainingConfig
        Dim peak = EffectiveLearningRate

        Return New TrainingConfig With {
            .LearningRate = peak,
            .MinLearningRate = peak * 0.1,
            .WarmupSteps = WarmupSteps,
            .TotalSteps = totalSteps,
            .MaxGradNorm = MaxGradNorm,
            .UseCosineDecay = True
        }
    End Function

#End Region

#Region "推理默认值"

    ''' <summary>观察"语言建模"效果时的最大新生成 token 数。</summary>
    Public Property MaxNewTokens As Integer = 20

    ''' <summary>
    ''' KV Cache 计时对比的固定生成长度。
    ''' </summary>
    ''' <remarks>
    ''' 取值要够长才能看出 O(t²) 与 O(t) 的差距 —— 序列太短时两者都在毫秒级，
    ''' 测出来的比值基本是噪声。
    ''' </remarks>
    Public Property KvCacheProbeTokens As Integer = 48

    ''' <summary>是否尝试注册 CUDA 后端。</summary>
    Public Property TryCuda As Boolean = True

    ''' <summary>
    ''' 是否打印训练步内部的分阶段耗时（前向 / 损失 / 反向 / 裁剪 / 更新 / MoE 均衡）。
    ''' </summary>
    ''' <remarks>
    ''' 定位"瓶颈到底在哪"只能靠实测：按公式估算算力时，主机侧的类型转换、
    ''' 逐元素循环与数据往返往往被低估，而它们经常才是真正的大头。
    ''' </remarks>
    Public Property ProfileStages As Boolean = False

    ''' <summary>固定随机种子，保证整次运行可复现。</summary>
    Public Property RandomSeed As Integer = 20240919

#End Region

End Module
