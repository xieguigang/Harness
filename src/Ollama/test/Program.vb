Imports System.IO
Imports Ollama

Module Program

    Sub Main(args As String())
        If args.Length > 0 AndAlso args(0) = "--sessionlog" Then
            ' 结构化会话日志的离线验证（不需要网络）
            Environment.Exit(SessionLogSelfTest.RunAll())
            Return
        End If

        ' 运行持久化与全文模糊检索的最小验证（无需网络 / LLM）
        Call PersistenceAndSearchDemo().GetAwaiter().GetResult()

        ' 原有 Demo（需要本地 Ollama 与 skills 目录，可选）
        ' Call DemoUsage.RunManagerOnlyDemo()
    End Sub

    ''' <summary>
    ''' 验证 MemoryPersistsStorage 的：保存 -> 加载恢复 -> 关键词模糊检索召回。
    ''' </summary>
    Private Async Function PersistenceAndSearchDemo() As Task
        Dim tmpFile As String = Path.Combine(Path.GetTempPath(), "chat_memory_demo_" & Guid.NewGuid().ToString("N") & ".json")

        Console.WriteLine("=== MemoryPersistsStorage 持久化与全文模糊检索验证 ===")
        Console.WriteLine($"临时文件: {tmpFile}")
        Console.WriteLine()

        ' 1. 构造上下文并填充若干会话消息
        Dim memory As New ChatContextMemory()
        Dim storage As New MemoryPersistsStorage(memory, tmpFile)

        Await memory.EnqueueAsync(New ChatMessage With {.Role = "system", .Content = "你是一个帮助分析生物信息学数据的助手。"})
        Await memory.EnqueueAsync(New ChatMessage With {.Role = "user", .Content = "请帮我分析 GCMS 代谢组学数据的峰对齐结果。"})
        Await memory.EnqueueAsync(New ChatMessage With {.Role = "assistant", .Content = "我已经完成了峰对齐，共识别出 1240 个代谢物特征。"})
        Await memory.EnqueueAsync(New ChatMessage With {.Role = "user", .Content = "对差异表达基因做 KEGG 通路富集分析，并输出 top 10 通路。"})
        Await memory.EnqueueAsync(New ChatMessage With {.Role = "assistant", .Content = "KEGG 富集显示 'ABC transporters' 与 'Purine metabolism' 是最显著的通路。"})

        ' 2. 保存到文件
        Dim saved As Boolean = storage.Save()
        Console.WriteLine($"[保存] 结果 = {saved}, 当前上下文消息数 = {memory.ExportMessages().Count}")
        Console.WriteLine()

        ' 3. 从文件加载到全新的上下文对象，验证恢复一致性
        Dim memory2 As New ChatContextMemory()
        Dim storage2 As New MemoryPersistsStorage(memory2, tmpFile)
        Dim loaded As List(Of ChatMessage) = storage2.Load()

        Console.WriteLine($"[加载] 恢复消息数 = {loaded.Count}")
        For i As Integer = 0 To loaded.Count - 1
            Console.WriteLine($"  [{i}] {loaded(i).Role}: {loaded(i).Content}")
        Next
        Console.WriteLine($"恢复一致性: {If(loaded.Count = 5 AndAlso loaded(3).Content.Contains("KEGG"), "通过", "失败")}")
        Console.WriteLine()

        ' 4. 关键词模糊检索（针对窗口外 / 长期记忆）
        Dim keywords As String() = {"代谢组学", "峰对齐", "KEGG", "通路"}
        Console.WriteLine($"[检索] 关键词组: {String.Join(", ", keywords)}")
        Dim hits = storage2.RecallMessages(keywords, top:=3).ToArray()

        Console.WriteLine($"召回命中数 = {hits.Length}")
        For Each m In hits
            Console.WriteLine($"  -> {m.Role}: {m.Content}")
        Next
        Console.WriteLine()

        ' 5. 单关键词检索（英文 / 跨语言近似）
        Dim hits2 = storage2.RecallMessages({"metabolomics"}, top:=2).ToArray()
        Console.WriteLine($"[检索] 关键词 'metabolomics' 召回数 = {hits2.Length}")
        For Each m In hits2
            Console.WriteLine($"  -> {m.Role}: {m.Content}")
        Next

        ' 清理
        If File.Exists(tmpFile) Then
            File.Delete(tmpFile)
        End If

        Console.WriteLine()
        Console.WriteLine("=== 验证结束 ===")
    End Function

End Module
