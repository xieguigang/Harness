' ---------------------------------------------------------------------------
' TalkBuddy LLM 算法 demo —— 从训练到测试的完整主线
'
' 运行顺序与验收主线一一对应：
'
'   0. 初始化（CUDA 后端 / DeepSeek 分词器 / 模型 / 工具）
'   1. 模型结构与参数量（总参数 vs 单 token 激活参数）
'   2. Stage 1 预训练
'   3. Stage 2 指令跟随 SFT
'   4. Stage 3 Function Calling SFT
'   5. MoE 路由与负载均衡
'   6. 采样策略对比
'   7. KV Cache 正确性验证与加速比
'   8. Function Calling 多轮闭环
'   9. 约束解码
'  10. 权重持久化
'
' 命令行参数（都可省略）：
'   --pretrain=N      预训练步数
'   --instruction=N   指令 SFT 步数
'   --tool=N          工具 SFT 步数
'   --vocab=N         词表上限；0 = 使用 DeepSeek 全量词表
'   --no-cuda         不尝试注册 CUDA 后端
'   --quick           冒烟档：步数压到 3、词表压到 4096，用于快速验证流程
'   --quiet           只在阶段之间打印，不逐训练步打印
'   --no-train        跳过训练，直接做推理演示（只观察机制）
'   --profile         打印训练步内部各阶段的实测耗时（用于定位瓶颈）
' ---------------------------------------------------------------------------

' TalkBuddy.vbproj 的 RootNamespace 是 TalkBuddy，装配层类型都在这个名字空间下
Imports Microsoft.VisualBasic.DeepLearning.LLM
Imports TalkBuddy

Module Program

    ' 步数不再在字段初始化时取默认值 —— 它依赖规模档位，
    ' 而档位要到参数解析之后才确定（见 ParseArguments 末尾）
    Private _pretrainSteps As Integer
    Private _instructionSteps As Integer
    Private _toolSteps As Integer
    Private _stepsExplicit As Boolean
    Private _verbosity As Integer = 1
    Private _skipTraining As Boolean = False

    Sub Main(args As String())
        Call ConsoleReport.EnableUtf8()
        Call ParseArguments(args)

        ConsoleReport.Section("TalkBuddy · LLM 算法 DEMO（MoE / KV Cache / Function Calling）")
        ConsoleReport.Note("这是一个用于算法原理学习的实验性实现，规模刻意压得很小。")
        ConsoleReport.Note("验收口径是：算法链路正确、指标趋势合理、机制可验证，而不是通用对话质量。")

        Dim exitCode As Integer = 0

        Try
            Dim pipeline As New DemoPipeline(_verbosity)

            Call pipeline.ShowModelStructure()

            If Not _skipTraining Then
                Call pipeline.RunPretrain(_pretrainSteps)
                Call pipeline.RunInstructionSft(_instructionSteps)
                Call pipeline.RunToolSft(_toolSteps)

                ConsoleReport.Section("4b. 工具调用片段的学习效果")

                Dim accuracy = pipeline.MeasureToolCallAccuracy(16)

                ConsoleReport.KeyValue("全部被监督位置", $"{accuracy.Overall:P1}（{accuracy.Total} 个位置）")
                ConsoleReport.KeyValue("其中协议标记位置", $"{accuracy.Structural:P1}（{accuracy.StructuralTotal} 个位置）")
                ConsoleReport.Note("统计范围只包括损失掩码为 True 的位置，即 assistant 的回复与工具调用片段。")
                ConsoleReport.Note("")
                ConsoleReport.Note("两档指标要分开看：内容 token 是 12.8 万选一的选择题，几十步训练几乎不可能蒙对；")
                ConsoleReport.Note("而协议保留标记只有十几个候选，它命中的比例才真正反映「格式学会了吗」。")
            End If

            Call pipeline.ShowMoERouting()
            Call pipeline.DemoSampling("什么是 MoE")
            Call pipeline.VerifyKVCache("什么是注意力")
            Call pipeline.DemoAgent("Beijing 的天气怎么样？", "get_weather")
            Call pipeline.DemoConstrainedDecoding("get_weather")
            Call pipeline.VerifyPersistence("talkbuddy-demo.model")

            Call PrintSummary(pipeline)
        Catch ex As Exception
            ConsoleReport.Section("运行失败")
            Call Console.WriteLine(ex.ToString())
            exitCode = 1
        End Try

        Console.WriteLine()
        Console.WriteLine("演示结束。")

        Environment.ExitCode = exitCode
    End Sub

    ''' <summary>极简命令行解析。</summary>
    Private Sub ParseArguments(args As String())
        If args Is Nothing Then Return

        Dim scaleSpecified As Boolean = False

        For Each raw In args
            Dim arg = raw.Trim()
            Dim value As Integer = 0

            Select Case True

                Case arg.StartsWith("--pretrain=", StringComparison.Ordinal)
                    If Integer.TryParse(arg.Substring(11), value) Then
                        _pretrainSteps = value
                        _stepsExplicit = True
                    End If

                Case arg.StartsWith("--instruction=", StringComparison.Ordinal)
                    If Integer.TryParse(arg.Substring(14), value) Then
                        _instructionSteps = value
                        _stepsExplicit = True
                    End If

                Case arg.StartsWith("--tool=", StringComparison.Ordinal)
                    If Integer.TryParse(arg.Substring(7), value) Then
                        _toolSteps = value
                        _stepsExplicit = True
                    End If

                Case arg.StartsWith("--vocab=", StringComparison.Ordinal)
                    If Integer.TryParse(arg.Substring(8), value) Then DemoConfig.VocabularyLimit = value

                Case arg.Equals("--no-cuda", StringComparison.OrdinalIgnoreCase)
                    DemoConfig.TryCuda = False

                Case arg.Equals("--quiet", StringComparison.OrdinalIgnoreCase)
                    _verbosity = 0

                Case arg.Equals("--profile", StringComparison.OrdinalIgnoreCase)
                    DemoConfig.ProfileStages = True

                Case arg.Equals("--no-device-resident", StringComparison.OrdinalIgnoreCase)
                    ParameterSet.EnableDeviceResidency = False
                    ConsoleReport.Note("已禁用设备常驻训练：全部参数走主机 AdamW（用于 A/B 对比）")

                Case arg.Equals("--tiny", StringComparison.OrdinalIgnoreCase)
                    DemoConfig.Scale = DemoConfig.ModelScale.Tiny
                    scaleSpecified = True

                Case arg.Equals("--200m", StringComparison.OrdinalIgnoreCase)
                    DemoConfig.Scale = DemoConfig.ModelScale.Scale200M
                    scaleSpecified = True

                Case arg.Equals("--400m", StringComparison.OrdinalIgnoreCase)
                    DemoConfig.Scale = DemoConfig.ModelScale.Scale400M
                    scaleSpecified = True

                Case arg.StartsWith("--scale=", StringComparison.OrdinalIgnoreCase)
                    Dim scaleName = arg.Substring(8).Trim().ToLowerInvariant()
                    Dim applied As Boolean = True

                    Select Case scaleName
                        Case "tiny", "18m" : DemoConfig.Scale = DemoConfig.ModelScale.Tiny
                        Case "200m" : DemoConfig.Scale = DemoConfig.ModelScale.Scale200M
                        Case "400m" : DemoConfig.Scale = DemoConfig.ModelScale.Scale400M
                        Case Else : applied = False
                    End Select

                    If applied Then
                        scaleSpecified = True
                    Else
                        ConsoleReport.Note($"未知的规模档位 '{scaleName}'，可选 tiny / 200m / 400m（保持原档位）")
                    End If

                Case arg.Equals("--no-train", StringComparison.OrdinalIgnoreCase)
                    _skipTraining = True

                Case arg.Equals("--quick", StringComparison.OrdinalIgnoreCase)
                    _pretrainSteps = 3
                    _instructionSteps = 3
                    _toolSteps = 3
                    _stepsExplicit = True
                    DemoConfig.VocabularyLimit = 4096
                    ConsoleReport.Note("已启用 --quick：步数 3 / 词表 4096（仅用于验证流程）")

            End Select
        Next

        ' 规模档位决定单步耗时，因此"未显式指定步数"时按档位给默认值：
        ' 大档单步要数秒，若沿用教学档的 25/25/16 步，整次演示会远超半小时预算
        If Not _stepsExplicit Then
            Dim preset = DemoConfig.ScaleDefaults()

            _pretrainSteps = preset.Pretrain
            _instructionSteps = preset.Instruction
            _toolSteps = preset.Tool
        End If

        If scaleSpecified Then
            ConsoleReport.Note($"规模档位：{DemoConfig.Scale}  " &
                               $"(训练步数 {_pretrainSteps}/{_instructionSteps}/{_toolSteps})")
        End If
    End Sub

    Private Sub PrintSummary(pipeline As DemoPipeline)
        ConsoleReport.Section("小结")

        ConsoleReport.KeyValue("后端", If(pipeline.CudaEnabled, "CUDA GPU", "SIMD CPU"))
        ConsoleReport.KeyValue("词表", $"{pipeline.Codec.VocabSize:N0}（{pipeline.Codec.Tokenizer.ModelType}）")
        ConsoleReport.KeyValue("总参数", $"{pipeline.Model.TotalParameters:N0}")
        ConsoleReport.KeyValue("单 token 激活参数", $"{pipeline.Model.ActiveParametersPerToken:N0} " &
                                                     $"（激活率 {pipeline.Model.ActivationRatio:P2}）")

        If pipeline.CudaEnabled Then
            Dim pinnedMb = pipeline.PinnedDeviceBytes / 1024.0 / 1024.0

            ConsoleReport.KeyValue("设备端更新的参数", $"{pipeline.DeviceUpdatedParameters} / {pipeline.Model.ParameterCount} 项")
            ConsoleReport.KeyValue("常驻显存", $"{pinnedMb:N1} MB（权重 + 一阶/二阶矩）")
            ConsoleReport.Note("")
            ConsoleReport.Note("为什么不是全部参数都走设备端：词嵌入与 RMSNorm 的 γ 在主机侧被直接读取")
            ConsoleReport.Note("（Embed 按行查表、RmsNorm 在主机循环里用 γ 缩放），把它们钉进显存")
            ConsoleReport.Note("会让主机读到陈旧值。其余只被矩阵乘消费的权重则全部常驻显存。")
        End If

        ConsoleReport.Note("")
        ConsoleReport.Note("三条主线的代码落点：")
        ConsoleReport.Note("  MoE            → DeepLearning\LLM\MoELayer.vb（细粒度专家 + 共享专家 + 无辅助损失负载均衡）")
        ConsoleReport.Note("  KV Cache       → DeepLearning\LLM\KVCache.vb + CausalSelfAttention.vb（prefill + 增量解码）")
        ConsoleReport.Note("  Function Call  → DeepLearning\LLM\ConstrainedDecoder.vb / ToolCallProtocol.vb / AgentLoop.vb")
        ConsoleReport.Note("")
        ConsoleReport.Note("对应的理论说明在 TalkBuddy\readme.md。")
    End Sub

End Module
