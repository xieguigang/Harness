Imports System.ComponentModel
Imports System.Diagnostics
Imports System.Net.Http
Imports System.Text
Imports System.Threading
Imports Microsoft.VisualBasic.CommandLine.Reflection
Imports Microsoft.VisualBasic.Serialization.JSON
Imports Ollama.JSON
Imports Ollama.JSON.FunctionCall
Imports Ollama.Logging

''' <summary>
''' 统一的大语言模型客户端：持有记忆、函数注册表与日志，调用任意 <see cref="ILLMProvider"/> 完成多轮对话与工具调用，
''' 对外返回包含「思考(think)」与「正文(output)」的统一响应对象 <see cref="LLMsResponse"/>。
''' </summary>
Public Class LLMClient : Implements IDisposable

    ReadOnly _provider As ILLMProvider
    ReadOnly _model As String
    ReadOnly _preserveMemory As Boolean = True

    Dim _context As ChatContextMemory
    Dim _caller As FunctionCaller
    Dim _calls As New List(Of FunctionCall)

    ''' <summary>
    ''' 每次请求的用量明细快照，按请求顺序追加；超过 <see cref="MaxUsageLogSize"/> 后丢弃最旧的记录
    ''' </summary>
    Dim _usageLog As New List(Of ChatUsageRecord)
    ' 会话累计的 token 用量（不做加锁，与 _calls 一致，并发调用下统计不保证精确）
    Dim _totalPromptTokens As Long
    Dim _totalCompletionTokens As Long
    Dim _totalCacheHit As Long
    Dim _totalCacheMiss As Long
    Dim _lastUsage As ChatUsage

    ''' <summary>
    ''' 结构化会话日志。只有通过构造函数传入日志目录、或调用 <see cref="EnableSessionLog"/> 后才有值，
    ''' 为 Nothing 时所有埋点自动跳过，客户端行为与启用前完全一致。
    ''' </summary>
    Dim _sessionLog As ChatSessionLogger

    ''' <summary>
    ''' 用量明细列表的容量上限，避免长时间 Agent 会话导致无界增长
    ''' </summary>
    Public Const MaxUsageLogSize As Integer = 10000

    Private disposedValue As Boolean

    Public Property temperature As Double = 0.1
    Public Property tools As List(Of FunctionTool)

    ''' <summary>
    ''' 是否在流式请求中向后端索取 token 用量统计（默认 True）。
    ''' 关闭后 <see cref="cache_hit_rate"/> 等统计将不再更新，可用于兼容不支持 
    ''' <c>stream_options</c> 扩展参数的 OpenAI 兼容端点。
    ''' </summary>
    ''' <returns></returns>
    Public Property enable_usage_stats As Boolean = True

    ''' <summary>
    ''' Max tool call rounds
    ''' </summary>
    ''' <returns></returns>
    Public Property maxRounds As Integer = 15

    Public Property echo_think_chunks As Boolean = True

    ''' <summary>
    ''' the current model name that configured for this LLM client, use for display in the UI host
    ''' </summary>
    ''' <returns></returns>
    Public ReadOnly Property Model As String
        Get
            Return _model
        End Get
    End Property

    ''' <summary>
    ''' 获取当前上下文的估算的Token数量大小
    ''' </summary>
    ''' <returns></returns>
    Public ReadOnly Property context_tokens As Integer
        Get
            Return _context.EstimatedTokens
        End Get
    End Property

    ''' <summary>
    ''' 当前后端是否提供 KV 缓存命中统计。
    ''' DeepSeek 等 OpenAI 兼容后端为 True，Ollama 本地后端为 False（此时命中率恒为 0）。
    ''' </summary>
    ''' <returns></returns>
    Public ReadOnly Property cache_supported As Boolean
        Get
            Return _provider.SupportsCacheStats
        End Get
    End Property

    ''' <summary>
    ''' 会话累计的 KV 缓存命中率（0~1）：累计命中 token 数 / (累计命中 + 累计未命中)。
    ''' 后端不支持缓存统计或尚无统计数据时返回 0。
    ''' </summary>
    ''' <returns></returns>
    ''' <remarks>
    ''' 统计口径为整个会话（<see cref="LLMClient"/> 实例生命周期）内所有请求之和，
    ''' 包含工具调用轮次与网络重试产生的请求；如需清零请调用 <see cref="ResetCacheStats"/>。
    ''' </remarks>
    Public ReadOnly Property cache_hit_rate As Double
        Get
            Return CacheUsageMath.HitRate(_totalCacheHit, _totalCacheMiss)
        End Get
    End Property

    ''' <summary>
    ''' 最近一次请求的 KV 缓存命中率（0~1）；尚无统计数据或后端不支持时返回 0
    ''' </summary>
    ''' <returns></returns>
    Public ReadOnly Property last_cache_hit_rate As Double
        Get
            If _lastUsage Is Nothing Then
                Return 0.0R
            Else
                Return _lastUsage.HitRate
            End If
        End Get
    End Property

    ''' <summary>
    ''' 会话累计的缓存命中 token 数
    ''' </summary>
    ''' <returns></returns>
    Public ReadOnly Property cache_hit_tokens As Long
        Get
            Return _totalCacheHit
        End Get
    End Property

    ''' <summary>
    ''' 会话累计的缓存未命中 token 数
    ''' </summary>
    ''' <returns></returns>
    Public ReadOnly Property cache_miss_tokens As Long
        Get
            Return _totalCacheMiss
        End Get
    End Property

    ''' <summary>
    ''' 最近一次请求的缓存命中 token 数；无数据时返回 0
    ''' </summary>
    ''' <returns></returns>
    Public ReadOnly Property last_cache_hit_tokens As Long
        Get
            Return If(_lastUsage IsNot Nothing AndAlso _lastUsage.CacheHitTokens.HasValue, _lastUsage.CacheHitTokens.Value, 0L)
        End Get
    End Property

    ''' <summary>
    ''' 最近一次请求的缓存未命中 token 数；无数据时返回 0
    ''' </summary>
    ''' <returns></returns>
    Public ReadOnly Property last_cache_miss_tokens As Long
        Get
            Return If(_lastUsage IsNot Nothing AndAlso _lastUsage.CacheMissTokens.HasValue, _lastUsage.CacheMissTokens.Value, 0L)
        End Get
    End Property

    ''' <summary>
    ''' 会话累计的输入 token 数（prompt tokens）
    ''' </summary>
    ''' <returns></returns>
    Public ReadOnly Property prompt_tokens As Long
        Get
            Return _totalPromptTokens
        End Get
    End Property

    ''' <summary>
    ''' 会话累计的输出 token 数（completion tokens）
    ''' </summary>
    ''' <returns></returns>
    Public ReadOnly Property completion_tokens As Long
        Get
            Return _totalCompletionTokens
        End Get
    End Property

    ''' <summary>
    ''' 按时间顺序记录的每次请求用量明细快照（返回副本）
    ''' </summary>
    ''' <returns></returns>
    Public ReadOnly Property cache_usage_log As ChatUsageRecord()
        Get
            Return _usageLog.ToArray()
        End Get
    End Property

    ''' <summary>
    ''' 清零会话累计的用量统计与明细列表，不影响对话上下文记忆
    ''' </summary>
    ''' <returns>当前客户端实例，便于链式调用</returns>
    Public Function ResetCacheStats() As LLMClient
        _usageLog.Clear()
        _lastUsage = Nothing
        _totalPromptTokens = 0
        _totalCompletionTokens = 0
        _totalCacheHit = 0
        _totalCacheMiss = 0

        Return Me
    End Function

    ''' <summary>
    ''' system message to the LLMs AI
    ''' </summary>
    Public Property system_message As String

    ''' <summary>
    ''' 暴露底层对话上下文记忆对象，供 <see cref="MemoryPersistsStorage"/> 等持久化门面挂载使用。
    ''' 调用方不应直接向该上下文手动入队 system 消息（系统提示请使用 <see cref="system_message"/> 属性），
    ''' 以免持久化重载后产生重复的 system 消息。
    ''' </summary>
    ''' <returns>当前客户端所持有的对话上下文记忆实例</returns>
    Public ReadOnly Property Context As ChatContextMemory
        Get
            Return _context
        End Get
    End Property

    ''' <summary>
    ''' 可选：外部工具执行引擎（当函数未在 ai_caller 中注册时使用）
    ''' </summary>
    Public Property tool_invoke As Func(Of FunctionCall, String)

    ''' <summary>
    ''' 记忆上下文的最大 Token 数量上限（近似估算），默认 1,000,000（1M）。超过后从历史最旧端裁剪。
    ''' 裁剪逻辑由底层 <see cref="ChatContextMemory"/> 负责，并保证工具调用消息成组保留。
    ''' </summary>
    Public Property max_context_tokens As Integer
        Get
            Return CInt(_context.MaxTokens)
        End Get
        Set(value As Integer)
            _context.MaxTokens = value
        End Set
    End Property

    Friend Shared ReadOnly SharedHttpClient As New HttpClient(New HttpClientHandler With {
        .Proxy = Nothing,
        .UseProxy = False
    }) With {.Timeout = TimeSpan.FromHours(1)}

    Dim _onThink As Action(Of String)
    Dim _onOutput As Action(Of String)

    ''' <summary>
    ''' 
    ''' </summary>
    ''' <param name="provider"></param>
    ''' <param name="model"></param>
    ''' <param name="logfile"></param>
    ''' <param name="preserveMemory"></param>
    ''' <param name="maxRound">
    ''' For LLM agent run a complex task, please increase this rounds number
    ''' </param>
    ''' <param name="chatLogFolder">
    ''' 结构化会话日志的根目录。留空则不写任何日志文件；传入目录后，每一次对话、每一轮请求、
    ''' 每一次工具调用与每一份用量都会以 json 数组的形式写进该目录下的会话子目录。
    ''' </param>
    ''' <param name="chatLogOptions">会话日志的记录详细程度；留空则保留全部内容。</param>
    Sub New(provider As ILLMProvider, model As String,
            Optional logfile As String = Nothing,
            Optional preserveMemory As Boolean = True,
            Optional maxRound As Integer = 15,
            Optional debug As Boolean = False,
            Optional chatLogFolder As String = Nothing,
            Optional chatLogOptions As ChatSessionLogOptions = Nothing)

        _provider = provider
        _model = model
        _maxRounds = maxRound
        _preserveMemory = preserveMemory
        _caller = New FunctionCaller(verbose:=debug)
        _context = New ChatContextMemory(logfile)

        If Not String.IsNullOrWhiteSpace(chatLogFolder) Then
            Call EnableSessionLog(chatLogFolder, chatLogOptions)
        End If
    End Sub

    ''' <summary>
    ''' 结构化会话日志器：未启用时为 Nothing。
    ''' </summary>
    ''' <returns>
    ''' 当前客户端持有的日志器；通过它可以在会话结束时调用 <c>Close()</c> 收尾，
    ''' 或在开始前设置 <c>SessionId</c> 让两套日志共享同一个会话 id。
    ''' </returns>
    Public ReadOnly Property SessionLog As ChatSessionLogger
        Get
            Return _sessionLog
        End Get
    End Property

    ''' <summary>
    ''' 启用结构化会话日志：之后每一次对话、每一轮请求、每一次工具调用与每一份用量都会被
    ''' 写进 <paramref name="folder"/> 下的会话子目录（session.json / turns.json /
    ''' tool-calls.json / usage.json / messages.json）。不调用本方法、也不通过构造函数传入
    ''' 目录时不会写盘，客户端行为完全不变。
    ''' </summary>
    ''' <param name="folder">日志根目录，其下按会话 id 建立子目录。</param>
    ''' <param name="options">记录的详细程度；留空保留全部内容。</param>
    ''' <param name="sessionId">
    ''' 会话 id，留空自动生成。宿主可以传入自己的会话 id（例如 Harness 工具日志的会话 id），
    ''' 这样两份日志可以按会话 id 对账。
    ''' </param>
    ''' <returns>当前客户端，便于链式调用。</returns>
    Public Function EnableSessionLog(folder As String,
                                     Optional options As ChatSessionLogOptions = Nothing,
                                     Optional sessionId As String = Nothing) As LLMClient

        _sessionLog = New ChatSessionLogger(
            folder,
            _model,
            If(_provider Is Nothing, Nothing, _provider.ApiEndpoint),
            options,
            sessionId)

        ' 上下文裁剪事件：先记进日志，再把消息交给宿主自己挂的归档回调
        Dim hostEvict As Action(Of List(Of ChatMessage)) = _context.OnEvict

        _context.OnEvict = Sub(removed As List(Of ChatMessage))
                               Call LogEvictedMessages(removed)

                               If hostEvict IsNot Nothing Then
                                   Call hostEvict(removed)
                               End If
                           End Sub

        Return Me
    End Function

    ''' <summary>
    ''' 记录一次上下文窗口裁剪：被移出活跃窗口的消息进会话日志的事件列表。
    ''' </summary>
    ''' <param name="removed">被裁剪掉的消息。</param>
    Private Sub LogEvictedMessages(removed As List(Of ChatMessage))
        If _sessionLog Is Nothing OrElse removed Is Nothing OrElse removed.Count = 0 Then
            Return
        End If

        Dim text As New StringBuilder()

        For Each msg As ChatMessage In removed
            If msg Is Nothing Then
                Continue For
            End If

            Call text.AppendLine("[" & msg.Role & "] " & msg.Content)
        Next

        Call _sessionLog.LogEvent(ChatLogText.EventEvict,
                                  removed.Count & " messages left the context window",
                                  text.ToString())
    End Sub

    ''' <summary>
    ''' 打开一次对话的记录；未启用日志时返回 Nothing，调用方据此跳过后续埋点。
    ''' </summary>
    ''' <param name="prompt">用户的提问。</param>
    ''' <param name="req">为这次对话构造好的请求。</param>
    Private Function BeginTurn(prompt As String, req As ChatRequestOptions) As ChatTurnRecord
        If _sessionLog Is Nothing Then
            Return Nothing
        End If

        ' system prompt 与温度等设置可能在构造之后才配好，所以每次对话开始时重新同步一次
        Call _sessionLog.Configure(
            systemMessage:=system_message,
            temperature:=temperature,
            maxRounds:=_maxRounds,
            preserveMemory:=_preserveMemory)

        Return _sessionLog.BeginTurn(prompt, req, _maxRounds)
    End Function

    ''' <summary>
    ''' 记下模型在这一轮里请求了哪些工具调用。
    ''' </summary>
    ''' <param name="logRound">本轮的记录。</param>
    ''' <param name="calls">模型请求的工具调用。</param>
    Private Sub LogRequestedCalls(logRound As ChatRoundRecord, calls As IEnumerable(Of ToolCallInfo))
        If logRound Is Nothing OrElse calls Is Nothing Then
            Return
        End If

        Dim list As New List(Of LoggedToolCallRequest)()

        For Each tc As ToolCallInfo In calls
            If tc Is Nothing Then
                Continue For
            End If

            list.Add(New LoggedToolCallRequest With {
                .Id = tc.Id,
                .Name = tc.FunctionName,
                .Arguments = tc.FunctionArguments,
                .DsmlLeak = tc.DeepSeekDSMLLeak
            })
        Next

        logRound.RequestedToolCalls = list
    End Sub

    ''' <summary>
    ''' 关闭一轮请求的记录；未启用日志时什么也不做。
    ''' </summary>
    ''' <param name="logRound">本轮的记录。</param>
    ''' <param name="think">本轮的推理文本。</param>
    ''' <param name="output">本轮的正文。</param>
    ''' <param name="errorMessage">本轮失败的原因。</param>
    Private Sub EndRound(logRound As ChatRoundRecord, think As String, output As String, Optional errorMessage As String = Nothing)
        If logRound Is Nothing Then
            Return
        End If

        Call _sessionLog.EndRound(logRound, think, output, errorMessage)
    End Sub

    Public Function HookResponseStream(getOutputToken As Action(Of String), Optional getThinkToken As Action(Of String) = Nothing) As LLMClient
        _onThink = getThinkToken
        _onOutput = getOutputToken
        Return Me
    End Function

    ''' <summary>
    ''' 执行单个工具调用：优先使用 ai_caller 中注册的函数，否则使用外部 tool_invoke 引擎
    ''' </summary>
    ''' <param name="calle">模型请求的工具调用。</param>
    ''' <param name="logTurn">所属对话的记录；未启用日志时为 Nothing。</param>
    ''' <param name="logRound">所属轮次的记录；未启用日志时为 Nothing。</param>
    Private Function ExecuteTool(calle As ToolCallInfo,
                                 Optional logTurn As ChatTurnRecord = Nothing,
                                 Optional logRound As ChatRoundRecord = Nothing) As String

        Dim [call] As New FunctionCall With {
            .name = calle.FunctionName,
            .arguments = calle.FunctionArguments
        }
        Dim result_str As String

        If _sessionLog Is Nothing Then
            If _caller.CheckFunction([call].name) Then
                result_str = _caller.Call([call])
            ElseIf tool_invoke IsNot Nothing Then
                result_str = tool_invoke([call])
            Else
                result_str = $"error: the tool function '{[call].name}' is not registered or no external function tool_call invoke engine was configured!"
            End If
        Else
            Dim source As String

            If _caller.CheckFunction([call].name) Then
                source = ChatLogText.SourceRegistry
            ElseIf tool_invoke IsNot Nothing Then
                source = ChatLogText.SourceExternal
            Else
                source = ChatLogText.SourceMissing
            End If

            result_str = _sessionLog.LogToolCall(
                logTurn, logRound,
                [call].name,
                calle.Id,
                calle.FunctionArguments,
                source,
                calle.DeepSeekDSMLLeak,
                Function() As String
                    If source = ChatLogText.SourceRegistry Then
                        Return _caller.Call([call])
                    ElseIf source = ChatLogText.SourceExternal Then
                        Return tool_invoke([call])
                    End If

                    Return $"error: the tool function '{[call].name}' is not registered or no external function tool_call invoke engine was configured!"
                End Function)
        End If

        If calle.DeepSeekDSMLLeak Then
            result_str = $"<tool_result>{result_str}</tool_result>"
        End If

        Return result_str
    End Function

    ''' <summary>
    ''' 构造发送给 Provider 的消息列表：若首条不是 system 且设置了 system_message，则前置系统消息。
    ''' </summary>
    ''' <param name="currentUserMsg">
    ''' 当前用户消息。当 <see cref="_preserveMemory"/> 为 False（用户消息未进入上下文队列）时，
    ''' 由该方法负责将其追加到请求消息尾部，保证 prompt 不会丢失。
    ''' </param>
    Private Function BuildRequestMessages(Optional currentUserMsg As ChatMessage = Nothing) As List(Of ChatMessage)
        Dim msgs As New List(Of ChatMessage)(_context)
        If currentUserMsg IsNot Nothing Then
            msgs.Add(currentUserMsg)
        End If
        If (msgs.Count = 0 OrElse msgs(0).Role <> "system") AndAlso Not String.IsNullOrEmpty(system_message) Then
            msgs.Insert(0, New ChatMessage With {.Role = "system", .Content = system_message})
        End If
        Return msgs
    End Function

    ''' <summary>
    ''' Chat with LLMs, send the user message to LLMs model and then get response result text
    ''' </summary>
    ''' <param name="prompt_text">the prompt text message that send to the LLMs model</param>
    ''' <returns>
    ''' the LLMs response output text data, includes <see cref="LLMsResponse.think"/> text and 
    ''' the real LLMs content <see cref="LLMsResponse.output"/>.
    ''' </returns>
    Public Async Function Chat(prompt_text As String, Optional cancellationToken As CancellationToken = Nothing) As Task(Of LLMsResponse)
        Dim newUserMsg As New ChatMessage With {.Role = "user", .Content = prompt_text}

        If _preserveMemory Then
            Await _context.EnqueueAsync(newUserMsg)
        End If

        Dim reqOptions As New ChatRequestOptions With {
            .Model = _model,
            .Messages = BuildRequestMessages(If(_preserveMemory, Nothing, newUserMsg)),
            .Tools = Me.tools,
            .Temperature = Me.temperature,
            .StreamUsage = Me.enable_usage_stats
        }
        Dim currentReq As ChatRequestOptions = reqOptions
        Dim fullThink As New StringBuilder
        Dim fullOutput As New StringBuilder
        Dim llmResponse As LLMsResponse

        ' 结构化会话日志：未启用时 logTurn 为 Nothing，下面所有埋点自动跳过
        Dim logTurn As ChatTurnRecord = BeginTurn(prompt_text, reqOptions)
        Dim turnWatch As Stopwatch = If(logTurn Is Nothing, Nothing, Stopwatch.StartNew())

        If logTurn IsNot Nothing Then
            Call _sessionLog.LogMessage(newUserMsg, logTurn, Nothing)
        End If

        ' 循环处理：如果模型返回 Tool Calls，执行后继续请求，直到返回最终文本
        ' 在这个for循环中用来处理工具函数调用，以及由于网络失败或者文本解析错误导致的重试
        For round As Integer = 1 To _maxRounds
            Try
                llmResponse = Nothing
                llmResponse = Await ChatRound(currentReq, cancellationToken, fullThink, fullOutput, logTurn)
            Catch ex As Exception
                ' 20260723 network error will retry in next round
                llmResponse = Nothing

                Call App.LogException(ex)
                Call ex.Message.warning

                If logTurn IsNot Nothing Then
                    Call _sessionLog.LogEvent(ChatLogText.EventRetry,
                        $"round {round} failed: {ex.GetType().Name}: {ex.Message}",
                        ex.ToString(),
                        logTurn)

                    ' the round the exception came from stays in the record of
                    ' the turn: mark it as the one that failed, its text is
                    ' whatever the stream had delivered before it broke
                    If logTurn.Rounds.Count > 0 Then
                        Dim broken As ChatRoundRecord = logTurn.Rounds(logTurn.Rounds.Count - 1)

                        If broken.Status = ChatLogText.StatusOk Then
                            broken.Status = ChatLogText.StatusFailed
                            broken.ErrorMessage = $"{ex.GetType().Name}: {ex.Message}"
                        End If
                    End If
                End If
            End Try

            If Not llmResponse Is Nothing Then
                If logTurn IsNot Nothing Then
                    logTurn.DurationMs = Math.Round(turnWatch.Elapsed.TotalMilliseconds, 3)
                    Call _sessionLog.EndTurn(logTurn, llmResponse.think, llmResponse.output, ChatLogText.StatusOk)
                End If

                Return llmResponse
            Else
                ' 5. 准备下一轮请求 (带上工具结果)
                ' 通常第二轮不需要再传 tools 定义

                ' 20260730
                ' 或许不应该将Tools设置为空，不然LLM很可能出现记不住函数名称的问题，浪费Token
                ' 在这里注释掉下面的代码
                ' currentReq.Tools = Nothing
            End If
        Next

        If logTurn IsNot Nothing Then
            logTurn.DurationMs = Math.Round(turnWatch.Elapsed.TotalMilliseconds, 3)

            Call _sessionLog.LogEvent(ChatLogText.EventMaxRounds,
                $"the turn used up all {_maxRounds} rounds without a final answer",
                Nothing,
                logTurn)
            Call _sessionLog.EndTurn(logTurn, fullThink.ToString(), fullOutput.ToString(), ChatLogText.StatusMaxRounds)
        End If

        Throw New MaxTryRunException(_maxRounds)
    End Function

    ''' <param name="logTurn">所属对话的记录；未启用日志时为 Nothing。</param>
    Private Async Function ChatRound(currentReq As ChatRequestOptions, cancellationToken As CancellationToken,
                                     fullThink As StringBuilder, fullOutput As StringBuilder,
                                     Optional logTurn As ChatTurnRecord = Nothing) As Task(Of LLMsResponse)

        Dim thinkBuf As New StringBuilder
        Dim outBuf As New StringBuilder
        Dim toolCallsToExecute As New List(Of ToolCallInfo)

        ' 本轮的记录：未启用日志时为 Nothing
        Dim logRound As ChatRoundRecord = If(logTurn Is Nothing, Nothing, _sessionLog.BeginRound(logTurn, currentReq))
        Dim streamWatch As New Stopwatch()

        ' 1. 通过 Provider 拉取流式数据
        Dim chunks As IEnumerable(Of ChatResponseChunk) = Await _provider.StreamChatAsync(currentReq, cancellationToken)

        If logRound IsNot Nothing Then
            Call streamWatch.Start()
        End If

        ' 2. 处理流式响应
        For Each chunk As ChatResponseChunk In chunks
            If logRound IsNot Nothing Then
                logRound.ChunkCount += 1

                If logRound.FirstTokenMs <= 0 Then
                    logRound.FirstTokenMs = Math.Round(streamWatch.Elapsed.TotalMilliseconds, 3)
                End If

                If _sessionLog.Options.CaptureChunks Then
                    If logRound.Chunks Is Nothing Then
                        logRound.Chunks = New List(Of String)()
                    End If

                    Dim label As String = ""

                    If chunk.IsDone Then
                        label &= "[done] "
                    End If
                    If chunk.Usage IsNot Nothing Then
                        label &= "[usage] "
                    End If
                    If chunk.ToolCalls IsNot Nothing Then
                        label &= "[tools:" & chunk.ToolCalls.Count & "] "
                    End If

                    Call logRound.Chunks.Add(label & If(chunk.ThinkContent, "") & If(chunk.DeltaContent, ""))
                End If
            End If

            If Not String.IsNullOrEmpty(chunk.ThinkContent) Then
                If echo_think_chunks Then
                    Call Console.Write(chunk.ThinkContent)
                End If

                Call thinkBuf.Append(chunk.ThinkContent)

                If _onThink IsNot Nothing Then
                    Call _onThink(chunk.ThinkContent)
                End If
            End If
            If Not String.IsNullOrEmpty(chunk.DeltaContent) Then
                Console.Write(chunk.DeltaContent)
                outBuf.Append(chunk.DeltaContent)

                If _onOutput IsNot Nothing Then
                    Call _onOutput(chunk.DeltaContent)
                End If
            End If

            ' 收集 Tool Calls
            If chunk.ToolCalls IsNot Nothing Then
                toolCallsToExecute.AddRange(chunk.ToolCalls)
            End If

            ' 用量统计由 provider 统一挂载在结束帧上，必须早于下面的 IsDone 跳出判断，
            ' 否则会漏掉本次请求的 usage 数据
            If chunk.Usage IsNot Nothing Then
                Call RecordUsage(chunk.Usage, logTurn, logRound)
            End If

            If chunk.IsDone Then
                Exit For
            End If
        Next

        If logRound IsNot Nothing Then
            streamWatch.Stop()
            logRound.StreamMs = Math.Round(streamWatch.Elapsed.TotalMilliseconds, 3)
        End If

        fullThink.Append(thinkBuf.ToString())
        fullOutput.Append(outBuf.ToString())

        ' 3. 如果没有工具调用，直接返回结果
        If toolCallsToExecute.IsNullOrEmpty Then
            ' 20260723
            ' parse tool call information from output
            ' <｜｜DSML｜｜tool_calls>
            ' <｜｜DSML｜｜invoke name="peek_file">
            ' <｜｜DSML｜｜parameter name="path" string="true">F:/datapool/2026.7.6-energy/FWMS20256511-human_cell_pellet/agent_test/expression.csv</｜｜DSML｜｜parameter>
            ' <｜｜DSML｜｜parameter name="limit" string="false">5</｜｜DSML｜｜parameter>
            ' </｜｜DSML｜｜invoke>
            ' <｜｜DSML｜｜invoke name="execute_r">
            ' <｜｜DSML｜｜parameter name="r_script" string="true"># Quick check of expression data dimensions and value rangesexpr <- read.csv("F:/datapool/2026.7.6-energy/FWMS20256511-human_cell_pellet/agent_test/expression.csv", row.names=1, check.names=FALSE)
            ' cat("Dimensions:", nrow(expr), "x", ncol(expr), "\n")
            ' cat("Column names:", paste(colnames(expr), collapse=", "), "\n")
            ' cat("First3 rows:\n")
            ' print(head(expr[,1:8],3))
            ' cat("\nValue range: min =", min(expr, na.rm=TRUE), ", max =", max(expr, na.rm=TRUE), "\n")
            ' cat("NA count:", sum(is.na(expr)), "\n")
            ' cat("Zero count:", sum(expr==0, na.rm=TRUE), "\n")
            ' cat("\nRow sums (first5):\n")
            ' print(head(rowSums(expr, na.rm=TRUE),5))
            ' cat("\nColumn sums (first5):\n")
            ' print(head(colSums(expr, na.rm=TRUE),5))
            ' cat("\nPer-row min positive values (first5):\n")
            ' min_pos <- apply(expr,1, function(x) min(x[x>0], na.rm=TRUE))
            ' print(head(min_pos))
            ' </｜｜DSML｜｜parameter>
            ' </｜｜DSML｜｜invoke>
            ' </｜｜DSML｜｜tool_calls>
            If outBuf.Length > 0 Then
                Dim firstLine As String, lastLine As String

                With outBuf.ToString.LineTokens
                    firstLine = .First
                    lastLine = .Last
                End With

                If firstLine = "<｜｜DSML｜｜tool_calls>" AndAlso lastLine = "</｜｜DSML｜｜tool_calls>" Then
                    Dim result As New List(Of String)

                    Dim dsmlCalls As IEnumerable(Of ToolCallInfo) = DsmlParser.ParseToolCalls(outBuf.ToString)

                    If logRound IsNot Nothing Then
                        logRound.DsmlFallback = True
                        Call LogRequestedCalls(logRound, dsmlCalls)
                    End If

                    For Each tc As ToolCallInfo In dsmlCalls
                        Dim fval As String = ExecuteTool(tc, logTurn, logRound)

                        Call result.Add("工具函数调用：" & tc.GetJson)
                        Call result.Add("工具返回结果：" & fval)
                        Call result.Add("")
                    Next

                    Dim llm As New ChatMessage With {
                        .Role = "assistant",
                        .Content = outBuf.ToString()
                    }
                    Dim err As New ChatMessage With {
                        .Role = "user",
                        .Content = result.JoinBy(vbCrLf)
                    }

                    Await _context.EnqueueAsync(llm)
                    Await _context.EnqueueAsync(err)

                    If logTurn IsNot Nothing Then
                        Call _sessionLog.LogMessage(llm, logTurn, logRound)
                        Call _sessionLog.LogMessage(err, logTurn, logRound)
                    End If

                    currentReq.Messages.AddRange({llm, err})

                    Call EndRound(logRound, thinkBuf.ToString(), outBuf.ToString())

                    Return Nothing
                End If
            End If

            Dim finalAssistantMsg As New ChatMessage With {
                .Role = "assistant",
                .Content = outBuf.ToString()
            }

            If _preserveMemory Then
                Await _context.EnqueueAsync(finalAssistantMsg)
            End If

            If logTurn IsNot Nothing Then
                Call _sessionLog.LogMessage(finalAssistantMsg, logTurn, logRound)
            End If

            Call EndRound(logRound, thinkBuf.ToString(), outBuf.ToString())

            Return New LLMsResponse With {
                .think = fullThink.ToString().Trim(),
                .output = fullOutput.ToString().Trim(),
                .usage = _lastUsage
            }
        End If
exec:
        ' 4. 如果有工具调用，执行并追加历史记录
        Dim assistantMsg As New ChatMessage With {
            .Role = "assistant",
            .Content = outBuf.ToString(),
            .ToolCalls = toolCallsToExecute.ToArray
        }
        If _preserveMemory Then
            Await _context.EnqueueAsync(assistantMsg)
        End If

        If logTurn IsNot Nothing Then
            Call _sessionLog.LogMessage(assistantMsg, logTurn, logRound)
            Call LogRequestedCalls(logRound, toolCallsToExecute)
        End If

        currentReq.Messages.Add(assistantMsg)

        ' 逐个执行工具
        For Each tc As ToolCallInfo In toolCallsToExecute
            Dim fval As String = ExecuteTool(tc, logTurn, logRound)
            Dim toolMsg As New ChatMessage With {
                .Role = "tool",
                .ToolCallId = tc.Id,
                .Content = fval
            }

            Call _calls.Add(New FunctionCall With {
                .name = tc.FunctionName,
                .arguments = tc.FunctionArguments
            })

            If _preserveMemory Then
                Await _context.EnqueueAsync(toolMsg)
            End If

            If logTurn IsNot Nothing Then
                Call _sessionLog.LogMessage(toolMsg, logTurn, logRound)
            End If

            currentReq.Messages.Add(toolMsg)
        Next

        Call EndRound(logRound, thinkBuf.ToString(), outBuf.ToString())

        Return Nothing
    End Function

    ''' <summary>
    ''' 累计一次请求的 token 用量数据并追加明细快照
    ''' </summary>
    ''' <param name="usage">provider 归一化后的用量数据</param>
    ''' <param name="logTurn">所属对话的记录；未启用日志时为 Nothing。</param>
    ''' <param name="logRound">所属轮次的记录；未启用日志时为 Nothing。</param>
    Private Sub RecordUsage(usage As ChatUsage,
                            Optional logTurn As ChatTurnRecord = Nothing,
                            Optional logRound As ChatRoundRecord = Nothing)

        _lastUsage = usage

        If _sessionLog IsNot Nothing Then
            Call _sessionLog.LogUsage(usage, logTurn, logRound)
        End If
        _totalPromptTokens += usage.PromptTokens
        _totalCompletionTokens += usage.CompletionTokens

        ' 后端未提供缓存统计时不做累加，命中率保持为 0
        If usage.HasCacheStats Then
            _totalCacheHit += usage.CacheHitTokens.Value
            _totalCacheMiss += usage.CacheMissTokens.Value
        End If

        Call _usageLog.Add(ChatUsageRecord.Create(usage, _model))

        If _usageLog.Count > MaxUsageLogSize Then
            Call _usageLog.RemoveRange(0, _usageLog.Count - MaxUsageLogSize)
        End If
    End Sub

    ''' <summary>
    ''' get the function calls and then clear the function calls history temp cache list
    ''' </summary>
    Public Function GetLastFunctionCalls() As FunctionCall()
        Dim calls = _calls.ToArray
        _calls.Clear()
        Return calls
    End Function

    ''' <summary>
    ''' registry custom function tool for LLMs function calling
    ''' </summary>
    ''' <param name="func">the function tool metadata, includes the function name, parameter information</param>
    ''' <param name="f">the clr function call backend for the given <paramref name="func"/> metadata</param>
    ''' <example>
    ''' ' define two function parameter
    ''' Dim a As New ParameterProperties("a", "number a", TypeCode.Double)
    ''' Dim b As New ParameterProperties("b", "number b", TypeCode.Double)
    ''' 
    ''' ' register a new function for add two number.
    ''' LLMs_ollama.AddFunction(
    '''    func:=New FunctionModel("number_add", "add two number, return a json string of the two number math add result.", a, b), 
    '''    f:=Function(calls) 
    '''           Dim num1 = Val(calls!a)
    '''           Dim num2 = Val(calls!b)
    '''           
    '''           ' return a json string of the two number math add result. 
    '''           Return $"{{result: {num1 + num2}}}"
    '''       End Function
    ''' )
    ''' </example>
    Public Sub AddFunction(func As FunctionModel, Optional f As Func(Of FunctionCall, String) = Nothing)
        If tools Is Nothing Then
            tools = New List(Of FunctionTool)
        End If

        Call tools.Add(New FunctionTool With {.[function] = func})

        If Not f Is Nothing Then
            Call _caller.Register(func.name, f)
        End If
    End Sub

    ''' <summary>
    ''' registry new clr function
    ''' </summary>
    ''' <typeparam name="T"></typeparam>
    ''' <param name="obj">A clr object instance, the container for the function tools that could be called by the LLMs</param>
    ''' <param name="fun">target function name to get clr function from the given clr <paramref name="obj"/>.</param>
    ''' <example>
    ''' ' example for define a container class in VB.NET
    ''' Public Class MathTool
    '''     
    '''     &lt;Description("add two number, return a json string of the two number math add result.")>
    '''     Public Function number_add(&lt;Argument("a", Description:="number a")>a As Double, 
    '''                                &lt;Argument("b", Description:="number b")>b As Double) As String
    '''         Return $"{{result: {a + b}}}"
    '''     End Function
    ''' End Class
    ''' 
    ''' LLMs_ollama.AddFunction(New MathTool(), fun:= "number_add")
    ''' </example>
    ''' <remarks>
    ''' Reflection custom attribute that used for make function tool annotations for LLMs:
    ''' 
    ''' 1. <see cref="DescriptionAttribute"/>: export the function tool description text to LLMs
    ''' 2. <see cref="ArgumentAttribute"/>: make the annotation description of the function parameters, for make export of the function parameters to LLMs
    ''' </remarks>
    Public Sub AddFunction(Of T)(obj As T, fun As String)
        For Each handle As Reflection.MethodInfo In CLRFunction.GetTarget(GetType(T), fun)
            Call AddFunction(handle.GetMetadata, CLRFunction.Caller(obj, handle))
        Next
    End Sub

    Public Function Clear() As LLMClient
        Call _context.Clear()
        Return Me
    End Function

    Public Sub AddSystemPrompt(text As String)
        system_message = If(String.IsNullOrEmpty(system_message), text, system_message & vbCrLf & text)
    End Sub

    ''' <summary>
    ''' 获取当前模型的归一化信息：委托给底层 Provider 实现，自动兼容 Ollama / OpenAI 等后端。
    ''' </summary>
    ''' <param name="timeout">请求超时（秒）</param>
    ''' <param name="verbose">是否返回详细信息（Ollama 有效，OpenAI 忽略）</param>
    ''' <returns>归一化后的 <see cref="ModelInfo"/>；请求失败则向上抛出异常</returns>
    Public Async Function GetModelInformation(Optional timeout As Double = 1, Optional verbose As Boolean = True) As Task(Of ModelInfo)
        Return Await _provider.GetModelInformation(_model, timeout, verbose)
    End Function

    Protected Overridable Sub Dispose(disposing As Boolean)
        If Not disposedValue Then
            If disposing Then
                If _sessionLog IsNot Nothing Then
                    Call _sessionLog.Close()
                End If

                Call _context.Dispose()
            End If

            disposedValue = True
        End If
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        Dispose(disposing:=True)
        GC.SuppressFinalize(Me)
    End Sub
End Class
