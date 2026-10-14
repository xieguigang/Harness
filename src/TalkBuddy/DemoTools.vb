' ---------------------------------------------------------------------------
' DemoTools —— demo 用的真实函数（function calling 里"框架负责做"的那一半）
'
' 这些工具必须是<b>确定性</b>的：同样的参数永远返回同样的结果。
' 原因有两层：
'   * 演示时可以把"模型说了什么"与"工具返回了什么"清楚地对应起来；
'   * SFT 样本里的工具结果必须与推理时真实执行的结果一致，
'     否则模型学到的是"A 结果"，推理时看到的是"B 结果"，会直接破坏格式学习。
'
' schema 的设计刻意覆盖了三种典型取值形态：
'   string（自由字符串）、enum（枚举，约束解码最有用武之地）、number（数字字面量）。
' ---------------------------------------------------------------------------

Imports Microsoft.VisualBasic.DeepLearning.LLM.Agent.ToolCalls

''' <summary>demo 工具集的构造入口。</summary>
Public Module DemoTools

    ''' <summary>get_weather 的城市候选（同时用于合成训练样本）。</summary>
    Public ReadOnly Cities As String() = {"Beijing", "Shanghai", "Tokyo", "London", "New York"}

    ''' <summary>get_current_time 的时区候选。</summary>
    Public ReadOnly Timezones As String() = {"Asia/Shanghai", "UTC", "America/New_York"}

    ''' <summary>calculate 的运算候选。</summary>
    Public ReadOnly Operators As String() = {"add", "subtract", "multiply", "divide"}

    ''' <summary>温度单位（就是 readme 里 <c>units</c> 枚举那个例子）。</summary>
    Public ReadOnly TemperatureUnits As String() = {"celsius", "fahrenheit"}

    ''' <summary>构建带 schema 的 demo 工具注册表。</summary>
    Public Function CreateRegistry() As ToolRegistry
        Dim registry As New ToolRegistry()

        Call registry.Register(
            "get_weather",
            "查询指定城市的当前天气与温度",
            New JsonSchema("weather lookup",
                New JsonSchemaProperty("city", "string", "要查询的城市名", Cities),
                New JsonSchemaProperty("units", "string", "温度单位", TemperatureUnits)),
            AddressOf GetWeather)

        Call registry.Register(
            "get_current_time",
            "查询指定时区的当前时间",
            New JsonSchema("time lookup",
                New JsonSchemaProperty("timezone", "string", "IANA 时区名", Timezones)),
            AddressOf GetCurrentTime)

        Call registry.Register(
            "calculate",
            "对两个数做四则运算并返回结果",
            New JsonSchema("arithmetic",
                New JsonSchemaProperty("a", "number", "左操作数"),
                New JsonSchemaProperty("op", "string", "运算符", Operators),
                New JsonSchemaProperty("b", "number", "右操作数")),
            AddressOf Calculate)

        Call registry.Register(
            "search_knowledge",
            "在本地知识库里检索与关键词相关的条目",
            New JsonSchema("knowledge search",
                New JsonSchemaProperty("query", "string", "检索关键词")),
            AddressOf SearchKnowledge)

        Return registry
    End Function

#Region "工具实现"

    ''' <summary>天气查询：用一个确定性的伪随机表代替真实 API。</summary>
    Private Function GetWeather(args As Dictionary(Of String, String)) As String
        Dim city = args("city")
        Dim units = args("units")

        Dim index = Array.IndexOf(Cities, city)

        If index < 0 Then
            ' 语法合法但语义不合理的取值 —— 约束解码保证不了这一类，只能由工具自己判断
            Return "{""error"": ""unknown city: " & city & """}"
        End If

        Dim celsius = 12 + index * 3
        Dim value = If(units = "fahrenheit", celsius * 9 \ 5 + 32, celsius)
        Dim suffix = If(units = "fahrenheit", "F", "C")

        Return "{""city"": """ & city & """, ""temperature"": " & value & ", ""unit"": """ & units & """}"

    End Function

    ''' <summary>时间查询：返回固定时刻，保证 demo 可复现。</summary>
    Private Function GetCurrentTime(args As Dictionary(Of String, String)) As String
        Dim zone = args("timezone")
        Dim index = Array.IndexOf(Timezones, zone)

        If index < 0 Then Return "{""error"": ""unknown timezone: " & zone & """}"

        Dim hour = (9 + index * 8) Mod 24

        Return "{""timezone"": """ & zone & """, ""time"": """ & hour.ToString("00") & ":30""}"

    End Function

    ''' <summary>四则运算：真正执行一次计算。</summary>
    Private Function Calculate(args As Dictionary(Of String, String)) As String
        Dim a As Double = 0, b As Double = 0

        If Not Double.TryParse(args("a"), a) Then Return "{""error"": ""left operand is not a number""}"
        If Not Double.TryParse(args("b"), b) Then Return "{""error"": ""right operand is not a number""}"

        Dim op = args("op")
        Dim result As Double

        Select Case op
            Case "add" : result = a + b
            Case "subtract" : result = a - b
            Case "multiply" : result = a * b
            Case "divide"
                If b = 0 Then Return "{""error"": ""division by zero""}"
                result = a / b
            Case Else
                Return "{""error"": ""unknown operator: " & op & """}"
        End Select

        Return "{""expression"": """ & a & " " & op & " " & b & """, ""result"": " & result & "}"
    End Function

    ''' <summary>本地知识库检索：直接在固定条目里做子串匹配。</summary>
    Private Function SearchKnowledge(args As Dictionary(Of String, String)) As String
        Dim query = args("query")

        Dim entry = KnowledgeBase.FirstOrDefault(Function(kv) kv.Key.Contains(query) OrElse query.Contains(kv.Key))

        If entry.Key Is Nothing Then
            Return "{""query"": """ & query & """, ""result"": ""no entry found""}"
        End If

        Return "{""query"": """ & query & """, ""result"": """ & entry.Value & """}"
    End Function

    ''' <summary>本地知识库（键 = 关键词，值 = 一句话解释）。</summary>
    Public ReadOnly KnowledgeBase As New Dictionary(Of String, String) From {
        {"MoE", "混合专家把 FFN 拆成多个专家，每个 token 只激活其中少数几个"},
        {"KV Cache", "缓存已生成 token 的 K 和 V，把增量解码的单步开销从 O(t^2) 降到 O(t)"},
        {"RoPE", "对 Q 和 K 按位置做旋转，使点积只依赖相对距离"},
        {"RMSNorm", "只按均方根缩放、不减均值的归一化，计算量比 LayerNorm 更小"},
        {"SwiGLU", "用 SiLU 做门控的前馈网络，比 ReLU 的硬门限表达能力更强"},
        {"function calling", "模型生成特殊 token 包裹的调用片段，框架解析并执行真实函数"}
    }

#End Region

End Module
