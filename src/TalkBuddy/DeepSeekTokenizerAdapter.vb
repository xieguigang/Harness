' ---------------------------------------------------------------------------
' DeepSeekTokenizerAdapter —— 把 HuggingFace 分词器接到 LLM 算法层
'
' 它做三件事：
'
'   1. 加载 DeepSeek 的 tokenizer.json 并暴露 <see cref="ITextCodec"/>，
'      供模型与 Agent 循环使用；
'   2. 解析并缓存全部<b>保留 token</b>（角色标记与工具调用标记）的 id
'      —— 这就是"特殊 token 不是魔法，而是被加进词表的保留 token"那句话的落地；
'   3. 构建约束解码所需的词表文本视图（<see cref="TokenizerVocabulary"/>）。
'
' 关于 VocabularyLimit（词表上限）：
'
'   默认 0 表示使用<b>全量</b>词表（DeepSeek 的 BPE 词表约 12.8 万），这是本 demo 的
'   默认选择。把 LM head 从 12.8 万压小一个数量级可以大幅缩短单步耗时，因此提供了
'   这个旋钮用于快速冒烟。
'
'   注意不能简单地"截断前 N 个"：DeepSeek 把角色标记与工具调用标记都排在词表<b>末尾</b>
'   （id 在 128803 之后），截断会让协议彻底失效。因此这里做的是<b>重映射</b>：
'
'       模型词表 = [ 13 个协议必须的保留 token ] + [ 前 N-13 个普通 token ]
'
'   id 空间的这种重排对模型完全透明 —— 它只看到一堆整数 id，重要的是
'   "每个 id 对应什么文本"以及"协议标记始终可用"。
'
' 第 3 点是一次性的重活：需要把保留下来的 token 逐个单独解码，才能知道每个 token
' "实际贡献了什么文本"。注意不能直接用 IdToToken —— 那给出的是 byte-level 编码后的
' 原始词表项（例如 "▁weather"），而真正写进上下文的是解码（ByteLevel 逆映射）之后的文本。
' ---------------------------------------------------------------------------

Imports Microsoft.VisualBasic.Data.NLP.ChineseTokenizer.HuggingFace
Imports Microsoft.VisualBasic.DeepLearning.LLM.Agent.ToolCalls
Imports Microsoft.VisualBasic.DeepLearning.LLM.Text

Public Class DeepSeekTokenizerAdapter
    Implements ITextCodec

    ''' <summary>
    ''' 协议必须保留的标记。
    ''' </summary>
    ''' <remarks>
    ''' 这一组 token 会被放在模型词表的最前面，从而保证 VocabularyLimit 取任何值时
    ''' 角色边界与工具调用协议都完整可用。
    ''' </remarks>
    Private Shared ReadOnly MustKeepMarkers As String() = {
        ToolCallProtocol.BeginOfSentenceMarker,
        ToolCallProtocol.EndOfSentenceMarker,
        ToolCallProtocol.UserMarker,
        ToolCallProtocol.AssistantMarker,
        ToolCallProtocol.CallsBeginMarker,
        ToolCallProtocol.CallsEndMarker,
        ToolCallProtocol.CallBeginMarker,
        ToolCallProtocol.CallEndMarker,
        ToolCallProtocol.SepMarker,
        ToolCallProtocol.OutputsBeginMarker,
        ToolCallProtocol.OutputsEndMarker,
        ToolCallProtocol.OutputBeginMarker,
        ToolCallProtocol.OutputEndMarker
    }

    Private ReadOnly _tokenizer As HuggingFaceTokenizer

    ''' <summary>模型 id → 分词器原始 id。</summary>
    Private ReadOnly _modelToSource As Integer()

    ''' <summary>分词器原始 id → 模型 id（只包含被保留的 id）。</summary>
    Private ReadOnly _sourceToModel As Dictionary(Of Integer, Integer)

    Private ReadOnly _vocabView As TokenizerVocabulary
    Private ReadOnly _specialIds As New Dictionary(Of String, Integer)()
    Private ReadOnly _unkId As Integer

    ''' <summary>底层分词器（需要做 tokenize 展示时可以取用）。</summary>
    Public ReadOnly Property Tokenizer As HuggingFaceTokenizer
        Get
            Return _tokenizer
        End Get
    End Property

    ''' <summary>分词器原始词表大小。</summary>
    Public ReadOnly Property RawVocabSize As Integer

    ''' <summary>本适配器实际暴露给模型的词表大小。</summary>
    Public ReadOnly Property VocabSize As Integer

    ''' <summary>是否发生了词表重映射（即 VocabularyLimit 小于原始词表）。</summary>
    Public ReadOnly Property IsRemapped As Boolean

    ''' <summary>约束解码使用的词表文本视图。</summary>
    Public ReadOnly Property Vocabulary As TokenizerVocabulary Implements ITextCodec.Vocabulary
        Get
            Return _vocabView
        End Get
    End Property

    ''' <summary>保留 token 的字面形式 → 模型 id。</summary>
    Public ReadOnly Property SpecialTokenIds As IReadOnlyDictionary(Of String, Integer)
        Get
            Return _specialIds
        End Get
    End Property

    ''' <summary>构建词表所花的时间（毫秒），用于说明这一步是一次性成本。</summary>
    Public ReadOnly Property VocabularyBuildMilliseconds As Double

    ''' <summary>词表中"无法用于约束解码"的 token 个数。</summary>
    Public ReadOnly Property UnusableTokens As Integer

    Private Sub New(tokenizer As HuggingFaceTokenizer, limit As Integer, verbose As Boolean)
        _tokenizer = tokenizer

        ' ---- 1. 解析协议必须保留的 token ----
        Dim mustKeep As New List(Of Integer)()
        Dim mustKeepSet As New HashSet(Of Integer)()

        For Each marker In MustKeepMarkers
            Dim id As Integer? = tokenizer.TokenToId(marker)

            If Not id.HasValue Then
                Throw New InvalidOperationException($"分词器词表中缺少协议所需的保留 token：'{marker}'")
            End If

            If mustKeepSet.Add(id.Value) Then
                Call mustKeep.Add(id.Value)
            End If
        Next

        Dim maxSpecial = mustKeep.Max()

        ' DeepSeek 把角色标记与工具调用标记放在 added_tokens 里，它们的 id 会<b>超出</b>
        ' BPE 词表本身的长度（BPE 到 128000，而标记的 id 在 128803 附近）。
        ' 因此 id 空间取两者的上界；中间那段"空洞"id 既不参与编码、解码也拿不到文本，
        ' 会被词表文本视图标记为不可用。
        RawVocabSize = System.Math.Max(tokenizer.VocabSize, maxSpecial + 1)

        ' ---- 2. 建立 id 重映射 ----
        If limit <= 0 OrElse limit >= RawVocabSize Then
            VocabSize = RawVocabSize
            IsRemapped = False
            _modelToSource = New Integer(RawVocabSize - 1) {}

            For i As Integer = 0 To RawVocabSize - 1
                _modelToSource(i) = i
            Next
        Else
            If limit < mustKeep.Count Then
                Throw New ArgumentException($"VocabularyLimit={limit} 小于协议必须保留的 {mustKeep.Count} 个 token")
            End If

            VocabSize = limit
            IsRemapped = True
            _modelToSource = New Integer(limit - 1) {}

            For i As Integer = 0 To mustKeep.Count - 1
                _modelToSource(i) = mustKeep(i)
            Next

            Dim cursor = mustKeep.Count

            ' 普通 token 池只取 BPE 词表本身（[0, VocabSize)），
            ' 保证每个取到的 source id 都能解码出文本。
            For source As Integer = 0 To tokenizer.VocabSize - 1
                If cursor >= limit Then Exit For
                If mustKeepSet.Contains(source) Then Continue For

                _modelToSource(cursor) = source
                cursor += 1
            Next

            If cursor < limit Then
                Throw New ArgumentException($"词表不足以填充 VocabularyLimit={limit}（只凑到 {cursor} 个 token）")
            End If
        End If

        _sourceToModel = New Dictionary(Of Integer, Integer)(_modelToSource.Length)

        For model As Integer = 0 To _modelToSource.Length - 1
            _sourceToModel(_modelToSource(model)) = model
        Next

        ' 超出保留范围的 token 统一落到句尾标记上
        _unkId = _sourceToModel(tokenizer.TokenToId(ToolCallProtocol.EndOfSentenceMarker).Value)

        For Each marker In MustKeepMarkers
            _specialIds(marker) = _sourceToModel(tokenizer.TokenToId(marker).Value)
        Next

        ' ---- 3. 词表文本视图 ----
        Dim elapsed As Double = 0.0

        _vocabView = BuildVocabulary(VocabSize, elapsed)
        VocabularyBuildMilliseconds = elapsed
        UnusableTokens = VocabSize - _vocabView.UsableTokens - _vocabView.SpecialTokens

        If verbose Then
            Dim mode = If(IsRemapped, $"remapped (limit={limit:N0})", "full")

            Call Console.WriteLine($"[tokenizer] model={tokenizer.ModelType}, vocab={VocabSize:N0} of {RawVocabSize:N0} [{mode}]")
            Call Console.WriteLine($"[tokenizer] constrained-decoding view built in {elapsed:F0} ms: " &
                                   $"{_vocabView.UsableTokens:N0} usable tokens over {_vocabView.FirstCharacters.Length:N0} first-characters")
        End If
    End Sub

    ''' <summary>
    ''' 从模型目录加载分词器。
    ''' </summary>
    ''' <param name="directory">包含 tokenizer.json 的目录</param>
    ''' <param name="limit">词表上限；&lt;= 0 或大于词表本身表示使用全量词表</param>
    ''' <param name="verbose">是否打印加载摘要</param>
    Public Shared Function Load(directory As String, Optional limit As Integer = 0,
                                Optional verbose As Boolean = True) As DeepSeekTokenizerAdapter

        If verbose Then
            Call Console.WriteLine($"[tokenizer] loading from {directory} ...")
        End If

        Dim tokenizer = HuggingFaceTokenizer.FromPretrained(directory, verbose:=False)

        Return New DeepSeekTokenizerAdapter(tokenizer, limit, verbose)
    End Function

#Region "词表文本视图"

    ''' <summary>
    ''' 逐个解码被保留的 token，得到"该 token 贡献的文本"，并建立首字符分桶。
    ''' </summary>
    ''' <remarks>
    ''' 判定"是否为保留 token"的方式是：跳过特殊 token 以后什么都不剩。
    ''' 这样不需要分词器额外暴露 special token 表。
    ''' </remarks>
    Private Function BuildVocabulary(limit As Integer, ByRef elapsedMs As Double) As TokenizerVocabulary
        Dim watch = Diagnostics.Stopwatch.StartNew()

        Dim texts(limit - 1) As String
        Dim special(limit - 1) As Boolean

        For model As Integer = 0 To limit - 1
            Dim source = _modelToSource(model)
            Dim asPlain = _tokenizer.Decode(New Integer() {source}, skipSpecialTokens:=False)

            texts(model) = asPlain

            If asPlain.Length > 0 Then
                special(model) = _tokenizer.Decode(New Integer() {source}, skipSpecialTokens:=True).Length = 0
            End If
        Next

        watch.Stop()
        elapsedMs = watch.Elapsed.TotalMilliseconds

        Return New TokenizerVocabulary(texts, special)
    End Function

#End Region

#Region "ITextCodec"

    ''' <summary>
    ''' 文本 → token。
    ''' </summary>
    ''' <remarks>
    ''' 刻意<b>不</b>自动追加 BOS / EOS：prompt 中的角色标记与工具调用标记都是显式写出来的，
    ''' 自动追加会破坏协议。
    ''' </remarks>
    Public Function Encode(text As String) As Integer() Implements ITextCodec.Encode
        If String.IsNullOrEmpty(text) Then Return New Integer() {}

        Dim sourceIds = _tokenizer.EncodeToIds(text, addSpecialTokens:=False)
        Dim result(sourceIds.Length - 1) As Integer

        For i As Integer = 0 To sourceIds.Length - 1
            Dim model As Integer = -1

            If Not _sourceToModel.TryGetValue(sourceIds(i), model) Then
                model = _unkId
            End If

            result(i) = model
        Next

        Return result
    End Function

    ''' <summary>token → 文本。</summary>
    Public Function Decode(ids As IEnumerable(Of Integer)) As String Implements ITextCodec.Decode
        Dim source As New List(Of Integer)()

        For Each id In ids
            Call source.Add(SourceIdOf(id))
        Next

        Return _tokenizer.Decode(source, skipSpecialTokens:=False)
    End Function

    ''' <summary>查询标记对应的模型 id；不存在时返回 -1。</summary>
    Public Function TokenIdOf(marker As String) As Integer Implements ITextCodec.TokenIdOf
        Dim id As Integer = -1

        If _specialIds.TryGetValue(marker, id) Then Return id

        Return -1
    End Function

#End Region

#Region "便捷访问"

    ''' <summary>模型 id → 分词器原始 id（越界时落到 unk）。</summary>
    Public Function SourceIdOf(modelId As Integer) As Integer
        If modelId < 0 OrElse modelId >= _modelToSource.Length Then Return _modelToSource(_unkId)

        Return _modelToSource(modelId)
    End Function

    ''' <summary>单个 token 贡献的文本（用于逐 token 打印协议）。</summary>
    Public Function TokenTextOf(modelId As Integer) As String
        If modelId < 0 OrElse modelId >= _modelToSource.Length Then Return "<out-of-range>"

        Return _tokenizer.Decode(New Integer() {_modelToSource(modelId)}, skipSpecialTokens:=False)
    End Function

    ''' <summary>把 token 序列渲染成 <c>[id]文本</c> 串联的形式，便于观察协议结构。</summary>
    Public Function Describe(ids As IEnumerable(Of Integer)) As String
        Dim text As New Text.StringBuilder()

        For Each id In ids
            Call text.Append($"[{id}]{TokenTextOf(id)}")
        Next

        Return text.ToString()
    End Function

    ''' <summary>把文本直接分词出来的 token 序列渲染成 <c>[id]文本</c> 形式。</summary>
    Public Function DescribeText(text As String) As String
        Return Describe(Encode(text))
    End Function

#End Region

End Class
