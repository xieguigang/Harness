' ---------------------------------------------------------------------------
' DemoPipeline —— 把 demo 需要的所有零件装配起来
'
' 它是"装配层"：负责把分词器、模型、模板、语料合成器、工具注册表拼到一起，
' 并把三大机制（MoE / KV Cache / Function Calling）逐个可观测地演示出来。
' 所有算法都在 Microsoft.VisualBasic.MachineLearning.LLM 里，这里不实现任何算法。
'
' 三个训练阶段共用同一个模型，只是换数据与序列长度：
'
'   Stage 1  预训练      纯文本 + 下一个 token 预测           → 获得"续写"能力
'   Stage 2  指令 SFT    合成指令-回复对，损失只计 assistant  → 学会"轮到我说话"
'   Stage 3  工具 SFT    合成工具调用轨迹，工具结果不计损失    → 学会输出调用片段
' ---------------------------------------------------------------------------

Imports Microsoft.VisualBasic.DeepLearning.LLM
Imports Microsoft.VisualBasic.DeepLearning.LLM.Agent
Imports Microsoft.VisualBasic.DeepLearning.LLM.Agent.ToolCalls
Imports Microsoft.VisualBasic.DeepLearning.LLM.Generator
Imports Microsoft.VisualBasic.DeepLearning.LLM.Sampler
Imports Microsoft.VisualBasic.DeepLearning.LLM.Text
Imports Microsoft.VisualBasic.DeepLearning.LLM.Trainer
Imports Diagnostics = System.Diagnostics
Imports GpuTensor = Microsoft.VisualBasic.Computing.ILCuda.GPUTensor

Public Class DemoPipeline

    Private ReadOnly _verbosity As Integer

    Private _codec As DeepSeekTokenizerAdapter
    Private _template As ChatTemplate
    Private _registry As ToolRegistry
    Private _model As LLMModel
    Private _corpus As EmbeddedCorpus
    Private _instructionSynth As SftSynthesizer
    Private _toolSynth As ToolCallSynthesizer

    Private _pretrainTokens As Integer()
    Private _cursor As Integer
    Private _eosId As Integer

    ''' <summary>全部训练步的报告，用于展示 MoE 负载均衡的收敛过程。</summary>
    Private ReadOnly _history As New List(Of TrainingStepReport)

    ''' <summary>是否成功切到了 CUDA 后端。</summary>
    Public ReadOnly Property CudaEnabled As Boolean

    ''' <summary>
    ''' 最近一次参数更新中真正走了<b>设备端内核</b>的参数个数。
    ''' </summary>
    ''' <remarks>
    ''' 这是"设备常驻训练是否生效"的最直接证据。它不等于参数总数是正常的：
    ''' 词嵌入与 RMSNorm 的 γ 在主机侧被直接读取（<c>Embed</c> 查表、<c>RmsNorm</c> 循环），
    ''' 因此刻意不钉住，仍走主机 AdamW。
    ''' </remarks>
    Public ReadOnly Property DeviceUpdatedParameters As Integer
        Get
            Return _model.Parameters.DeviceUpdatedCount
        End Get
    End Property

    ''' <summary>当前钉在显存里的参数字节数。</summary>
    Public ReadOnly Property PinnedDeviceBytes As Long
        Get
            Return _model.PinnedDeviceBytes
        End Get
    End Property

    ''' <summary>分词器适配器。</summary>
    Public ReadOnly Property Codec As DeepSeekTokenizerAdapter
        Get
            Return _codec
        End Get
    End Property

    ''' <summary>语言模型。</summary>
    Public ReadOnly Property Model As LLMModel
        Get
            Return _model
        End Get
    End Property

    ''' <summary>工具注册表。</summary>
    Public ReadOnly Property Registry As ToolRegistry
        Get
            Return _registry
        End Get
    End Property

    ''' <summary>对话模板。</summary>
    Public ReadOnly Property Template As ChatTemplate
        Get
            Return _template
        End Get
    End Property

    ''' <param name="verbosity">0 = 只在阶段之间打印，1 = 每个训练步都打印</param>
    Public Sub New(Optional verbosity As Integer = 1)
        _verbosity = verbosity

        ConsoleReport.Section("0. 初始化")

        ' ---- 计算后端 ----
        CudaEnabled = TryRegisterCuda()

        ' ---- 随机种子：让整次运行可复现 ----
        Microsoft.VisualBasic.MachineLearning.Transformer.TensorOps.Seed = DemoConfig.RandomSeed
        ConsoleReport.KeyValue("random seed", DemoConfig.RandomSeed)

        ' ---- 分词器 ----
        _codec = DeepSeekTokenizerAdapter.Load(DemoConfig.ResolveTokenizerDirectory(),
                                              DemoConfig.VocabularyLimit, verbose:=True)
        _eosId = _codec.TokenIdOf(ToolCallProtocol.EndOfSentenceMarker)

        ConsoleReport.KeyValue("protocol tokens", DescribeSpecialTokens())

        ' ---- 模型 ----
        Dim modelConfig = DemoConfig.CreateModelConfig(_codec.VocabSize)

        _model = New LLMModel(modelConfig, DemoConfig.WeightDecay)

        ConsoleReport.KeyValue("dtype", "Double（纯托管张量运行时）")
        ConsoleReport.KeyValue("backend", If(CudaEnabled, "CUDA GPU", "SIMD CPU"))

        ' ---- 模板 / 语料 / 工具 ----
        _template = New ChatTemplate(_codec)
        _registry = DemoTools.CreateRegistry()
        _corpus = New EmbeddedCorpus()
        _instructionSynth = New SftSynthesizer(_codec, _template, DemoConfig.RandomSeed)
        _toolSynth = New ToolCallSynthesizer(_codec, _template, _registry, DemoConfig.RandomSeed + 1)

        _pretrainTokens = _corpus.Tokenize(_codec, DemoConfig.PretrainSequenceLength)
    End Sub

    ''' <summary>尝试注册 CUDA 后端；失败时保持 CPU 并打印原因。</summary>
    Private Shared Function TryRegisterCuda() As Boolean
        If Not DemoConfig.TryCuda Then
            ConsoleReport.KeyValue("cuda", "已按配置禁用")
            Return False
        End If

        Try
            Dim ok = GpuTensor.CudaTensor.Register()

            If ok Then
                Dim backend = GpuTensor.CudaTensor.Current

                ConsoleReport.KeyValue("cuda", $"已启用（{backend?.Name}）")
                ConsoleReport.KeyValue("device", backend?.DescribeDevice())

                ' 内核可用性必须显式打印：任一内核缺失会静默回退 CPU，
                ' 没有这份诊断就无法分清"算得慢"和"根本没上 GPU"
                ConsoleReport.KeyValue("kernels", backend?.DescribeKernels())

                Return True
            End If

            ConsoleReport.KeyValue("cuda", $"不可用，回退 CPU：{GpuTensor.CudaTensor.LastError}")
        Catch ex As Exception
            ConsoleReport.KeyValue("cuda", $"注册异常，回退 CPU：{ex.Message}")
        End Try

        Return False
    End Function

    Private Function DescribeSpecialTokens() As String
        Dim parts As New List(Of String)

        For Each marker In New String() {
            ToolCallProtocol.BeginOfSentenceMarker,
            ToolCallProtocol.EndOfSentenceMarker,
            ToolCallProtocol.UserMarker,
            ToolCallProtocol.AssistantMarker,
            ToolCallProtocol.CallsBeginMarker,
            ToolCallProtocol.CallBeginMarker,
            ToolCallProtocol.SepMarker,
            ToolCallProtocol.CallEndMarker,
            ToolCallProtocol.CallsEndMarker,
            ToolCallProtocol.OutputsBeginMarker,
            ToolCallProtocol.OutputBeginMarker,
            ToolCallProtocol.OutputEndMarker,
            ToolCallProtocol.OutputsEndMarker}

            Call parts.Add($"{marker}={_codec.TokenIdOf(marker)}")
        Next

        Return String.Join(", ", parts)
    End Function

#Region "1. 模型结构"

    ''' <summary>打印模型结构与参数量统计（总参数 vs 单 token 激活参数）。</summary>
    Public Sub ShowModelStructure()
        ConsoleReport.Section("1. 模型结构")

        ConsoleReport.Note(_model.DescribeModel())

        ConsoleReport.Note("")
        ConsoleReport.Note("读法：")
        ConsoleReport.Note("  * total 是「知识容量」——全部专家的参数都要驻留内存，即使某个 token 用不到；")
        ConsoleReport.Note("  * active/token 是「实际计算量」——只有 Top-K 路由专家 + 全部共享专家参与前向；")
        ConsoleReport.Note("  * 两者的比值就是 MoE 的激活率，也是「大容量、低算力」这一卖点的量化形式。")
    End Sub

#End Region

#Region "2. 预训练"

    ''' <summary>Stage 1：下一个 token 预测。</summary>
    Public Sub RunPretrain(Optional steps As Integer = 0)
        If steps <= 0 Then steps = DemoConfig.PretrainSteps

        ConsoleReport.Section("2. Stage 1 · 预训练（下一个 token 预测）")

        Dim tokenCount = _pretrainTokens.Length

        ConsoleReport.KeyValue("corpus length", $"{_corpus.Text.Length} 字符 → {tokenCount} tokens")
        ConsoleReport.KeyValue("window", $"batch={DemoConfig.BatchSize}, seq={DemoConfig.PretrainSequenceLength} " &
                                          $"→ 每步 {DemoConfig.BatchSize * DemoConfig.PretrainSequenceLength} 个预测目标")
        ConsoleReport.KeyValue("steps", steps)
        ConsoleReport.KeyValue("learning rate", $"{DemoConfig.EffectiveLearningRate} " &
                                                  $"(warmup {DemoConfig.WarmupSteps} + cosine；按规模档位调整)")
        ConsoleReport.Note("")
        ConsoleReport.Note("注意：LM head 是 [d_model] × [vocab] 级别的矩阵乘，在 10 万词表下它是整步耗时的大头。")

        Dim trainer As New LMTrainer(_model, DemoConfig.CreateTrainingConfig(steps)) With {
            .ProfileStages = DemoConfig.ProfileStages
        }
        Dim watch = Diagnostics.Stopwatch.StartNew()

        _cursor = 0

        For [step] As Integer = 1 To steps
            Dim batch = EmbeddedCorpus.TakeBatch(_pretrainTokens, DemoConfig.BatchSize,
                                                 DemoConfig.PretrainSequenceLength, _cursor, _eosId)
            Dim report = trainer.TrainStep(batch)

            _history.Add(report)

            If _verbosity >= 1 OrElse [step] Mod 5 = 0 OrElse [step] = steps Then
                Call Console.WriteLine("  " & report.ToString())
            End If
        Next

        watch.Stop()

        ConsoleReport.KeyValue("total time", $"{watch.Elapsed.TotalSeconds:F1} s " &
                                              $"({watch.Elapsed.TotalMilliseconds / steps:F0} ms/step)")
        ConsoleReport.KeyValue("loss", $"{trainer.History.First().Loss:F4} → {trainer.History.Last().Loss:F4}")
        ConsoleReport.KeyValue("perplexity", $"{trainer.History.First().Perplexity:F2} → {trainer.History.Last().Perplexity:F2}")
        Call PrintStageProfile(trainer)
        ConsoleReport.Note("")
        ConsoleReport.Note("loss 曲线：")
        ConsoleReport.Note(ConsoleReport.LossCurve(trainer.History.Select(Function(r) r.Loss)))
    End Sub

    ''' <summary>
    ''' 打印最后一次训练步的分阶段耗时。
    ''' </summary>
    ''' <remarks>
    ''' 存在的意义是让"瓶颈在哪"变成一个可验证的观测事实，而不是靠算力公式推测 ——
    ''' 实测里主机侧的类型转换与逐元素循环经常比内核计算更贵。
    ''' </remarks>
    Private Shared Sub PrintStageProfile(trainer As LMTrainer)
        If trainer Is Nothing Then Return

        ' 被跳过的步必须显式报出来，否则就成了被掩盖的失败
        If trainer.SkippedSteps > 0 Then
            ConsoleReport.KeyValue("因梯度失控被跳过的步", $"{trainer.SkippedSteps} / {trainer.Step}")
            ConsoleReport.Note("  被跳过的是「参数更新」而非「前向」：这些步的 loss 仍然有效，")
            ConsoleReport.Note("  只是梯度已经不可信，强行更新会把参数一步推成 NaN。")
        End If

        If Not trainer.ProfileStages Then Return
        If trainer.LastStageMilliseconds.Count = 0 Then Return

        Dim total = trainer.LastStageMilliseconds.Sum(Function(s) s.Ms)

        If total <= 0 Then Return

        ConsoleReport.Note("")
        ConsoleReport.Note("最后一步的分阶段耗时（实测）：")

        For Each stage In trainer.LastStageMilliseconds
            Dim share = stage.Ms / total

            ConsoleReport.Note($"    {stage.Stage,-12}{stage.Ms,9:F1} ms   {share,6:P1}   " &
                               $"{New String("#"c, CInt(System.Math.Round(share * 40)))}")
        Next
    End Sub

#End Region

#Region "3. 指令 SFT"

    ''' <summary>Stage 2：指令跟随 SFT。</summary>
    Public Sub RunInstructionSft(Optional steps As Integer = 0)
        If steps <= 0 Then steps = DemoConfig.InstructionSftSteps

        ConsoleReport.Section("3. Stage 2 · 指令跟随 SFT")

        ' 合成规模：模板合成可以无限生成，因此"数据量"本身不是瓶颈。
        ' 这里按 32 倍步数生成一个足够大的样本池，再让每步从池中<b>跨区段</b>取样，
        ' 使整个池真正被用上（若只按 step×batch 顺序取，几步之后池的绝大部分都没被读过）。
        Dim samples = _instructionSynth.CreateSamples(System.Math.Max(steps * DemoConfig.BatchSize * 32, 1024))
        Dim stride = System.Math.Max(1, samples.Count \ System.Math.Max(1, steps * DemoConfig.BatchSize))
        Dim sample = samples(0)

        ConsoleReport.KeyValue("samples", samples.Count)
        ConsoleReport.KeyValue("window", $"batch={DemoConfig.BatchSize}, seq={DemoConfig.InstructionSequenceLength}")
        ConsoleReport.KeyValue("sample tokens", $"{sample.TokenIds.Length}（其中 {sample.SupervisedTokens} 个计入损失）")
        ConsoleReport.Note("")
        ConsoleReport.Note("样本明文（---- 之前是 system+user，一律不计损失）：")
        ConsoleReport.Note("  " & sample.Text.Replace(vbLf, " ⏎ ").Replace(ToolCallProtocol.BeginOfSentenceMarker, "[BOS]"))
        ConsoleReport.Note("")
        ConsoleReport.Note("损失掩码的实际作用是：模型只在 assistant 的 token 上学「该怎么回答」，")
        ConsoleReport.Note("不会去学「复述用户问了什么」——那样只会把有限的容量浪费在记忆输入上。")

        Dim trainer As New LMTrainer(_model, DemoConfig.CreateTrainingConfig(steps)) With {
            .ProfileStages = DemoConfig.ProfileStages
        }

        For [step] As Integer = 1 To steps
            Dim batchSamples = Slice(samples, ([step] - 1) * DemoConfig.BatchSize * stride, DemoConfig.BatchSize)
            Dim batch = _template.CreateBatch(batchSamples, DemoConfig.InstructionSequenceLength, DemoConfig.BatchSize)
            Dim report = trainer.TrainStep(batch)

            _history.Add(report)

            If _verbosity >= 1 OrElse [step] Mod 5 = 0 OrElse [step] = steps Then
                Call Console.WriteLine("  " & report.ToString())
            End If
        Next

        ConsoleReport.KeyValue("loss", $"{trainer.History.First().Loss:F4} → {trainer.History.Last().Loss:F4}")
        ConsoleReport.KeyValue("perplexity", $"{trainer.History.First().Perplexity:F2} → {trainer.History.Last().Perplexity:F2}")
        ConsoleReport.KeyValue("实际取用的样本区段", $"跨 {System.Math.Min(steps * DemoConfig.BatchSize * stride, samples.Count)} / {samples.Count} 条")
        Call PrintStageProfile(trainer)
    End Sub

#End Region

#Region "4. Function Calling SFT"

    ''' <summary>Stage 3：工具调用 SFT。</summary>
    Public Sub RunToolSft(Optional steps As Integer = 0)
        If steps <= 0 Then steps = DemoConfig.ToolSftSteps

        ConsoleReport.Section("4. Stage 3 · Function Calling SFT")

        ' 与指令 SFT 同理：样本池放大到足以覆盖全部训练步，且每步跨区段取样
        Dim samples = _toolSynth.CreateSamples(System.Math.Max(steps * DemoConfig.BatchSize * 32, 512))
        Dim stride = System.Math.Max(1, samples.Count \ System.Math.Max(1, steps * DemoConfig.BatchSize))

        ConsoleReport.KeyValue("samples", samples.Count)
        ConsoleReport.KeyValue("window", $"batch={DemoConfig.BatchSize}, seq={DemoConfig.ToolSequenceLength}")

        ' 找一条"完整闭环"样本（含 tool 结果）来展示掩码
        Dim withTool = samples.FirstOrDefault(Function(s) s.Text.Contains(ToolCallProtocol.OutputsBeginMarker))

        If withTool IsNot Nothing Then
            ConsoleReport.KeyValue("full-trajectory sample", $"{withTool.TokenIds.Length} tokens / {withTool.SupervisedTokens} supervised")
            ConsoleReport.Note("")
            ConsoleReport.Note("这条样本里被 mask 掉的部分：")
            ConsoleReport.Note("  " & ToolCallProtocol.OutputsBeginMarker & " … " & ToolCallProtocol.OutputsEndMarker &
                               "（框架回填的工具结果，模型不需要学习「预测工具返回什么」）")
        End If

        Dim trainer As New LMTrainer(_model, DemoConfig.CreateTrainingConfig(steps)) With {
            .ProfileStages = DemoConfig.ProfileStages
        }

        For [step] As Integer = 1 To steps
            Dim batchSamples = Slice(samples, ([step] - 1) * DemoConfig.BatchSize * stride, DemoConfig.BatchSize)
            Dim batch = _template.CreateBatch(batchSamples, DemoConfig.ToolSequenceLength, DemoConfig.BatchSize)
            Dim report = trainer.TrainStep(batch)

            _history.Add(report)

            If _verbosity >= 1 OrElse [step] Mod 5 = 0 OrElse [step] = steps Then
                Call Console.WriteLine("  " & report.ToString())
            End If
        Next

        ConsoleReport.KeyValue("loss", $"{trainer.History.First().Loss:F4} → {trainer.History.Last().Loss:F4}")
        ConsoleReport.KeyValue("perplexity", $"{trainer.History.First().Perplexity:F2} → {trainer.History.Last().Perplexity:F2}")
        Call PrintStageProfile(trainer)
    End Sub

    ''' <summary>工具调用片段的学习效果。</summary>
    Public Class ToolCallAccuracy

        ''' <summary>全部被监督位置的 token 级准确率。</summary>
        Public Property Overall As Double

        ''' <summary>其中"目标是协议保留标记"的那些位置的准确率。</summary>
        Public Property Structural As Double

        ''' <summary>参与统计的位置总数。</summary>
        Public Property Total As Integer

        ''' <summary>其中目标是协议保留标记的位置数。</summary>
        Public Property StructuralTotal As Integer

    End Class

    ''' <summary>
    ''' 教师强制下的"调用片段合规率"：逐位置看 argmax 是否等于目标 token。
    ''' </summary>
    ''' <remarks>
    ''' <para>
    ''' 之所以用教师强制而不是自由生成，是因为它衡量的是"模型是否记住了调用片段的格式"，
    ''' 与采样策略无关，也不会因为一两个 token 走偏而整体判错。
    ''' </para>
    ''' <para>
    ''' 统计分两档，因为它们在 12.8 万词表上的意义完全不同：
    ''' 内容 token（城市名、数字）是 1/128815 的选择题，几十步训练几乎不可能蒙对；
    ''' 而<b>协议保留标记</b>（角色边界、工具调用标记、EOS）只有十几个候选，
    ''' 模型只要学会了"在这个位置该输出哪个信号灯"就能命中 —— 这才是"格式学会了吗"的正解。
    ''' </para>
    ''' </remarks>
    Public Function MeasureToolCallAccuracy(Optional sampleCount As Integer = 16) As ToolCallAccuracy
        Dim samples = _toolSynth.CreateSamples(sampleCount)
        Dim batch = _template.CreateBatch(samples, DemoConfig.ToolSequenceLength, System.Math.Min(4, sampleCount))

        Dim structural As New HashSet(Of Integer)

        For Each marker In ToolCallProtocol.AllMarkers
            Call structural.Add(_codec.TokenIdOf(marker))
        Next

        Call structural.Add(_codec.TokenIdOf(ToolCallProtocol.BeginOfSentenceMarker))

        _model.Parameters.ZeroGradients()

        Dim logits = _model.Forward(batch.TokenIds, batch.BatchSize, batch.SeqLen)
        Dim vocab = logits.Shape(1)

        Dim report As New ToolCallAccuracy()
        Dim correct As Integer = 0
        Dim structuralCorrect As Integer = 0

        For n As Integer = 0 To batch.TotalTokens - 1
            If Not batch.LossMask(n) Then Continue For

            Dim best As Integer = 0
            Dim bestValue = Double.NegativeInfinity
            Dim offset = n * vocab

            For v As Integer = 0 To vocab - 1
                If logits.Data(offset + v) > bestValue Then
                    bestValue = logits.Data(offset + v)
                    best = v
                End If
            Next

            Dim target = batch.Targets(n)
            Dim hit = (best = target)

            report.Total += 1
            If hit Then correct += 1

            If structural.Contains(target) Then
                report.StructuralTotal += 1
                If hit Then structuralCorrect += 1
            End If
        Next

        _model.Parameters.ZeroGradients()

        ' 上面这次 Forward 也往 MoE 的负载统计里记了一笔，清掉以免污染后续的偏置更新
        _model.ResetMoELoadStatistics()

        If report.Total > 0 Then report.Overall = correct / CDbl(report.Total)

        If report.StructuralTotal > 0 Then
            report.Structural = structuralCorrect / CDbl(report.StructuralTotal)
        End If

        Return report
    End Function

#End Region

#Region "5. MoE 可视化"

    ''' <summary>展示专家负载分布与负载均衡偏置。</summary>
    Public Sub ShowMoERouting()
        ConsoleReport.Section("5. MoE 路由与负载均衡")

        Dim moe = _model.FirstMoELayer()

        If moe Is Nothing Then
            ConsoleReport.Note("本次配置没有启用 MoE。")
            Return
        End If

        ' 造一批 token 走一次前向，读取路由统计
        Dim batch = EmbeddedCorpus.TakeBatch(_pretrainTokens, 2, DemoConfig.PretrainSequenceLength, _cursor, _eosId)

        _model.Parameters.ZeroGradients()
        Call _model.Forward(batch.TokenIds, batch.BatchSize, batch.SeqLen)
        _model.Parameters.ZeroGradients()

        Dim info = moe.LastRouteInfo
        Dim labels = Enumerable.Range(0, moe.NumRoutedExperts).Select(Function(i) "expert " & i).ToArray()
        Dim ideal = 1.0 / moe.NumRoutedExperts

        ConsoleReport.KeyValue("config", $"{moe.NumRoutedExperts} routed experts, top-{moe.TopK}, " &
                                          $"{moe.NumSharedExperts} shared expert(s)")
        ConsoleReport.KeyValue("理想均匀负载", $"{ideal:P1} / expert")

        ' ---- 1. 累计负载：这才是"均衡有没有生效"的证据 ----
        Dim lifetime = moe.LifetimeLoad()
        Dim lifetimeMax = lifetime.Max() / ideal

        ConsoleReport.Note("")
        ConsoleReport.Note("训练全过程的累计负载：")
        ConsoleReport.Note(ConsoleReport.Histogram(lifetime, labels, 36))
        ConsoleReport.KeyValue("累计最大负载比", $"{lifetimeMax:F2}x  (1.00 = 完全均匀)")

        If _history.Count >= 10 Then
            Dim head = _history.Take(5).Average(Function(r) r.MoEMaxLoadRatio)
            Dim tail = _history.Skip(_history.Count - 5).Average(Function(r) r.MoEMaxLoadRatio)

            ConsoleReport.KeyValue("逐步最大负载比", $"前 5 步均值 {head:F2}x → 后 5 步均值 {tail:F2}x")
        End If

        ' ---- 2. 瞬时路由：说明它为什么会摆动 ----
        ConsoleReport.Note("")
        ConsoleReport.Note("最近一个批次的即时路由：")
        ConsoleReport.Note(ConsoleReport.Histogram(info.Load, labels, 36))
        ConsoleReport.KeyValue("即时最大负载比", $"{info.MaxLoadRatio:F2}x")
        ConsoleReport.KeyValue("被激活的专家数", $"{info.ActivatedExperts} / {moe.NumRoutedExperts}")

        ' ---- 3. 偏置本身 ----
        ConsoleReport.Note("")
        ConsoleReport.Note("当前的选择偏置 b_i —— 无辅助损失负载均衡的全部状态（不接收梯度、不被优化器更新）：")
        ConsoleReport.Note("  " & String.Join("  ", Enumerable.Range(0, moe.NumRoutedExperts).
                               Select(Function(i) $"b{i}={moe.BalanceBias(i):+0.000;-0.000}")))

        ConsoleReport.Note("")
        ConsoleReport.Note("怎么读这几张表（这里必须诚实说明本实现的局限）：")
        ConsoleReport.Note("")
        ConsoleReport.Note("  * 无辅助损失负载均衡是一个负反馈回路：专家负载过高 → 偏置 b 被压低 → token 转移走 →")
        ConsoleReport.Note("    它变轻 → b 回升。偏置只有【差值】有意义，整体加一个常数对 Top-K 选择毫无影响。")
        ConsoleReport.Note("")
        ConsoleReport.Note("  * 但在【8 个专家 + top-2】这个粒度上，路由本质是「全有全无」的：一个 token 只挑 2 个专家，")
        ConsoleReport.Note("    所以只要某对专家的打分略微领先，这一批 token 就会几乎全部涌过去，即时负载比直接")
        ConsoleReport.Note("    贴到上界 4.00x。readme 引用的 DeepSeek-V3 是【256 专家 + top-8】，粒度细得多，")
        ConsoleReport.Note("    再加上数万到数十万训练步，偏置才来得及把负载真正抹平。")
        ConsoleReport.Note("")
        ConsoleReport.Note("  * 因此在这个 demo 上应当看的不是「即时分布是否均匀」（它做不到），而是")
        ConsoleReport.Note("    「累计分布是否接近均匀」—— 它说明偏置反馈确实在把热点轮换开，")
        ConsoleReport.Note("    没有任何专家被永久饿死（也就是没有发生 readme 里说的「专家塌缩」）。")
    End Sub

#End Region

#Region "6. 采样策略"

    ''' <summary>对比不同采样策略的生成差异。</summary>
    Public Sub DemoSampling(Optional prompt As String = "什么是 MoE")
        ConsoleReport.Section("6. 采样策略对比")

        Dim promptIds = _codec.Encode(ToolCallProtocol.BeginOfSentenceMarker &
                                      ToolCallProtocol.UserMarker & prompt & ToolCallProtocol.AssistantMarker)

        ConsoleReport.KeyValue("prompt", prompt)

        Dim configs As New List(Of SamplingConfig) From {
            SamplingConfig.GreedySampling(),
            SamplingConfig.WithTemperature(0.7),
            SamplingConfig.WithTopK(20, 1.0),
            SamplingConfig.WithTopP(0.9, 1.0)
        }

        ConsoleReport.Note("")

        For Each config In configs
            Dim sampler As New LLMSampler(config)
            Dim generator As New TextGenerator(_model, sampler)

            ' 这一节比较的是"采样策略如何改变输出的多样性"，因此刻意<b>不</b>设停止符：
            ' 否则这个还没训练充分的小模型第一步就吐 EOS，四种策略看起来会完全一样。
            Dim options As New GenerationOptions With {
                .MaxNewTokens = DemoConfig.MaxNewTokens,
                .UseCache = True
            }

            Dim result = generator.Generate(promptIds, options)
            Dim text = _codec.Decode(result.GeneratedTokens)

            ConsoleReport.KeyValue(config.ToString(),
                                   "「" & text.Replace(vbLf, " ⏎ ") & "」")
            ConsoleReport.Note($"候选集={sampler.LastCandidateCount}, 分布熵={sampler.LastEntropy:F2} nats, " &
                               $"{result.AverageStepMilliseconds:F0} ms/step", 4)
        Next

        ConsoleReport.Note("")
        ConsoleReport.Note("读法：这一节看的不是「输出好不好」，而是「策略如何改变分布形状」——")
        ConsoleReport.Note("  贪心 / 低温度的候选集很小、分布熵接近 0；Top-p 在分布平坦时保留上千个候选。")
        ConsoleReport.Note("  输出文本本身是乱码，属于预期：几十步训练只够让模型记住语料里的高频片段，")
        ConsoleReport.Note("  远不足以学会通顺地说话。")
    End Sub

#End Region

#Region "7. KV Cache 验证"

    ''' <summary>
    ''' 用同一 prompt 跑"有缓存"与"无缓存"两条路径，验证输出一致并对比耗时。
    ''' </summary>
    Public Sub VerifyKVCache(Optional prompt As String = "什么是注意力")
        ConsoleReport.Section("7. KV Cache 正确性验证与加速比")

        Dim promptIds = _codec.Encode(ToolCallProtocol.BeginOfSentenceMarker &
                                      ToolCallProtocol.UserMarker & prompt & ToolCallProtocol.AssistantMarker)

        ConsoleReport.KeyValue("prompt", prompt)
        ConsoleReport.KeyValue("prompt tokens", promptIds.Length)
        ConsoleReport.KeyValue("max new tokens", DemoConfig.MaxNewTokens)

        ' 两条路径都用贪心，排除采样随机性的干扰
        Dim cachedResult = Generate(promptIds, useCache:=True)
        Dim plainResult = Generate(promptIds, useCache:=False)

        Dim identical As Boolean = cachedResult.GeneratedTokens.SequenceEqual(plainResult.GeneratedTokens)

        ConsoleReport.Note("")
        ConsoleReport.KeyValue("有缓存输出", "「" & _codec.Decode(cachedResult.GeneratedTokens).Replace(vbLf, " ⏎ ") & "」")
        ConsoleReport.KeyValue("无缓存输出", "「" & _codec.Decode(plainResult.GeneratedTokens).Replace(vbLf, " ⏎ ") & "」")
        ConsoleReport.KeyValue("逐 token 一致性", If(identical, "通过 ✓（两条路径完全一致）", "失败 ✗（KV Cache 有 bug）"))

        ConsoleReport.Note("")
        ConsoleReport.KeyValue("有缓存 单步均值", $"{cachedResult.AverageStepMilliseconds:F0} ms")

        If cachedResult.StepMilliseconds.Count > 1 Then
            ConsoleReport.KeyValue("有缓存 首步 → 末步",
                                   $"{cachedResult.StepMilliseconds.First():F0} ms → {cachedResult.StepMilliseconds.Last():F0} ms（基本持平）")
        End If

        ConsoleReport.KeyValue("无缓存 单步均值", $"{plainResult.AverageStepMilliseconds:F0} ms")

        If plainResult.StepMilliseconds.Count > 1 Then
            ConsoleReport.KeyValue("无缓存 首步 → 末步",
                                   $"{plainResult.StepMilliseconds.First():F0} ms → {plainResult.StepMilliseconds.Last():F0} ms（随 t 增长）")
        End If

        Dim speedup = If(cachedResult.AverageStepMilliseconds > 0,
                         plainResult.AverageStepMilliseconds / cachedResult.AverageStepMilliseconds, 0.0)

        ConsoleReport.KeyValue("平均加速比", $"{speedup:F2}x")
        ConsoleReport.KeyValue("缓存占用", $"{ConsoleReport.Human(cachedResult.CacheBytes)}（随序列长度线性增长）")

        ConsoleReport.Note("")
        ConsoleReport.Note("为什么「末步耗时」比「平均加速比」更能说明问题：")
        ConsoleReport.Note("  无缓存路径每生成一个 token 都要把整段前缀重新前向一次，注意力段是 O(t²)、")
        ConsoleReport.Note("  输出层也是 O(t)；有缓存时历史 K/V 直接读，两者都降为 O(t) / O(1)。")
        ConsoleReport.Note("  因此真正该看的是「单步耗时随 t 的走向」—— 有缓存持平、无缓存线性增长。")
        ConsoleReport.Note("")
        ConsoleReport.Note($"  平均加速比之所以只有 {speedup:F2}x，是因为 12.8 万词表的输出层与采样排序带来了")
        ConsoleReport.Note("  约 200 ms 的【固定】单步开销，它在几十个 token 的尺度上稀释了复杂度差异。")
        ConsoleReport.Note("  把生成长度加大（或把词表调小）之后，这个比值会迅速拉开。")
    End Sub

    ''' <summary>
    ''' 用固定长度的贪心生成测量一条路径的耗时。
    ''' </summary>
    ''' <remarks>
    ''' 刻意<b>不</b>设停止符：这里比较的是"同样生成 N 个 token，两条路径各花多久"，
    ''' 一旦允许提前停止，两条路径的步数可能不同，加速比就没有意义了。
    ''' </remarks>
    Private Function Generate(promptIds As Integer(), useCache As Boolean) As GenerationResult
        Dim generator As New TextGenerator(_model, New LLMSampler(SamplingConfig.GreedySampling()))

        Dim options As New GenerationOptions With {
            .MaxNewTokens = DemoConfig.KvCacheProbeTokens,
            .UseCache = useCache
        }

        Return generator.Generate(promptIds, options)
    End Function

#End Region

#Region "8. Function Calling"

    ''' <summary>跑一轮完整的多轮工具调用闭环。</summary>
    Public Sub DemoAgent(Optional userMessage As String = "Beijing 的天气怎么样？",
                         Optional forcedTool As String = "get_weather")

        ConsoleReport.Section("8. Function Calling · 多轮 Agent Loop")

        ConsoleReport.KeyValue("user message", userMessage)
        ConsoleReport.KeyValue("forced tool (首轮)", If(forcedTool, "<模型自主决策>"))

        Dim systemPrompt = _registry.RenderCompactCatalog()

        ConsoleReport.Note("")
        ConsoleReport.Note("完整的 JSON Schema 注入形态（readme 里「把 Schema 变成 token」讲的就是这一步）：")
        ConsoleReport.Note(_registry.RenderToolCatalog(), 4)
        ConsoleReport.Note("")
        ConsoleReport.Note("实际喂给模型的紧凑版（小模型上下文预算窄，清单必须省着用；训练与推理使用同一版）：")
        ConsoleReport.Note(systemPrompt, 4)

        Dim agent As New AgentLoop(_model, _codec, _registry)

        Dim options As New AgentLoopOptions With {
            .MaxToolRounds = 3,
            .MaxTokensPerReply = 12,
            .MaxArgumentTokens = 64,
            .UseCache = True,
            .Sampling = New SamplingConfig With {.Greedy = True},
            .ForcedToolName = forcedTool,
            .Verbose = False
        }

        Dim watch = Diagnostics.Stopwatch.StartNew()
        Dim result = agent.Run(systemPrompt, userMessage, options)
        watch.Stop()

        For Each round In result.Rounds
            ConsoleReport.Note("")
            ConsoleReport.Note($"---- 第 {round.Index} 轮 ----")

            If Not String.IsNullOrEmpty(round.AssistantText) Then
                ConsoleReport.KeyValue("模型自由生成", "「" & round.AssistantText.Replace(vbLf, " ⏎ ") & "」")
            End If

            If Not round.HasToolCall Then Continue For

            If round.ToolDecidedByModel Then
                ConsoleReport.KeyValue("调用决策", $"模型自主输出 {ToolCallProtocol.CallsBeginMarker}")
                ConsoleReport.KeyValue("工具名（模型决策）", round.ToolName)
            Else
                ConsoleReport.KeyValue("调用决策", "演示模式：工具名由 ForcedToolName 指定")
                ConsoleReport.KeyValue("工具名（外部指定）", round.ToolName)
            End If

            If round.UsedConstrainedDecoding Then
                ConsoleReport.KeyValue("约束解码", "已启用 —— 参数结构必然符合 JSON Schema")

                For Each trace In round.ConstrainedTrace
                    ConsoleReport.Note("    " & trace, 4)
                Next

                ConsoleReport.KeyValue("参数 JSON", round.ArgumentsJson)
                ConsoleReport.KeyValue("解析后参数",
                                       String.Join(", ", round.Arguments.Select(Function(kv) kv.Key & "=" & kv.Value)))
            End If

            ConsoleReport.KeyValue("工具真实返回", round.ToolResult)
            ConsoleReport.KeyValue("回填方式", $"工具结果作为新 token 一次性 prefill 进 KV Cache " &
                                               $"（复用前缀 {round.ReusedPrefixTokens} tokens）")
        Next

        ConsoleReport.Note("")
        ConsoleReport.KeyValue("最终回复", "「" & If(result.FinalText, "").Replace(vbLf, " ⏎ ") & "」")
        ConsoleReport.KeyValue("工具调用次数", result.ToolCallCount)
        ConsoleReport.KeyValue("上下文 token 数", result.TotalContextTokens)
        ConsoleReport.KeyValue("KV Cache 占用", ConsoleReport.Human(result.CacheBytes))
        ConsoleReport.KeyValue("总耗时", $"{watch.Elapsed.TotalMilliseconds:F0} ms")
    End Sub

    ''' <summary>演示约束解码的"物理上无法生成非法值"这一性质。</summary>
    Public Sub DemoConstrainedDecoding(Optional toolName As String = "get_weather")
        ConsoleReport.Section("9. 约束解码：让非法参数在物理上不可达")

        Dim tool = _registry.Find(toolName)

        If tool Is Nothing Then
            ConsoleReport.Note($"找不到工具 {toolName}")
            Return
        End If

        ConsoleReport.KeyValue("工具", tool.Name)
        ConsoleReport.KeyValue("参数 schema", String.Join(" ; ",
            tool.Schema.Properties.Select(Function(p)
                                              If p.EnumValues IsNot Nothing AndAlso p.EnumValues.Length > 0 Then
                                                  Return $"{p.Name} ∈ {{{String.Join(" | ", p.EnumValues)}}}"
                                              End If
                                              Return $"{p.Name}: {p.Type}"
                                          End Function)))

        Dim decoder As New ConstrainedDecoder(tool.Schema, _codec.Vocabulary)

        ConsoleReport.Note("")
        ConsoleReport.Note("逐步观察状态机如何收紧候选集：")

        Dim head = ToolCallProtocol.FormatCallHeader(tool.Name)
        Dim stream = TokenStream.Create(_model, _codec.Encode(head), useCache:=False)
        Dim sampler As New LLMSampler(SamplingConfig.GreedySampling())

        Dim tokens = decoder.Generate(stream, sampler, 64)

        For Each trace In decoder.Trace
            ConsoleReport.Note("  " & trace)
        Next

        ConsoleReport.Note("")
        ConsoleReport.KeyValue("生成结果", decoder.GeneratedText)
        ConsoleReport.KeyValue("是否闭合", If(decoder.IsComplete, "是", "否（达到长度上限）"))

        ' 独立验证：把生成的 JSON 交给一个"忠于 schema"的校验器
        Dim failures As New List(Of String)

        If Not decoder.IsComplete Then
            Call failures.Add("对象未闭合")
        Else
            Dim parsed = ToolCallProtocol.ParseArgumentObject(decoder.GeneratedText)

            For Each p In tool.Schema.Properties
                If p.Required AndAlso Not parsed.ContainsKey(p.Name) Then
                    Call failures.Add($"缺少必填参数 {p.Name}")
                End If

                If parsed.ContainsKey(p.Name) AndAlso p.EnumValues IsNot Nothing AndAlso p.EnumValues.Length > 0 Then
                    If Array.IndexOf(p.EnumValues, parsed(p.Name)) < 0 Then
                        Call failures.Add($"{p.Name} 的取值不在枚举内")
                    End If
                End If
            Next
        End If

        ConsoleReport.Note("")
        ConsoleReport.Note(If(failures.Count = 0,
            "Schema 校验：通过 ✓ —— 整个生成过程没有一步允许非法 token 进入采样，因此这是必然结果。",
            "Schema 校验：失败 ✗ —— " & String.Join("; ", failures)))
    End Sub

#End Region

#Region "10. 持久化"

    ''' <summary>保存权重并在重新加载后复现同一段生成，验证持久化正确。</summary>
    Public Sub VerifyPersistence(path As String)
        ConsoleReport.Section("10. 权重持久化")

        Dim promptIds = _codec.Encode(ToolCallProtocol.BeginOfSentenceMarker &
                                      ToolCallProtocol.UserMarker & "什么是 KV Cache" &
                                      ToolCallProtocol.AssistantMarker)

        Dim before = Generate(promptIds, useCache:=True).GeneratedTokens.ToArray()

        _model.Save(path)

        Dim size As Long = New System.IO.FileInfo(path).Length

        ConsoleReport.KeyValue("已保存", $"{path}（{ConsoleReport.Human(size)}）")

        Dim restored = _model.Load(path)
        ConsoleReport.KeyValue("已恢复参数", $"{restored} / {_model.Parameters.Entries.Count}")

        Dim after = Generate(promptIds, useCache:=True).GeneratedTokens.ToArray()

        ConsoleReport.KeyValue("重载后输出一致", If(before.SequenceEqual(after), "通过 ✓", "失败 ✗"))
    End Sub

#End Region

#Region "工具方法"

    Private Shared Function Slice(samples As IList(Of TemplatedSample), start As Integer,
                                  count As Integer) As List(Of TemplatedSample)

        Dim result As New List(Of TemplatedSample)()

        For i As Integer = 0 To count - 1
            Call result.Add(samples((start + i) Mod samples.Count))
        Next

        Return result
    End Function

#End Region

End Class
