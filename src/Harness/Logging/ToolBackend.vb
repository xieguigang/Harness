Imports System.ComponentModel
Imports System.Reflection
Imports Microsoft.VisualBasic.CommandLine.Reflection
Imports Microsoft.VisualBasic.Scripting
Imports Microsoft.VisualBasic.Serialization.JSON
Imports Ollama.JSON.FunctionCall
Imports any = Microsoft.VisualBasic.Scripting

Namespace Logging

    ''' <summary>
    ''' the clr half of a tool registration: locate the annotated method of a
    ''' tool class and build the backend delegate that runs it.
    ''' 
    ''' the tool call log needs that delegate so it can wrap it, and the
    ''' equivalent helper of the ollama project (<c>CLRFunction</c>) is a
    ''' Friend module this project cannot reach - and the plan keeps the
    ''' ollama project untouched. so this module does only the second half of
    ''' what <c>LLMClient.AddFunction(Of T)</c> does: the schema of the tool is
    ''' still built by ollama itself (see <see cref="Sandbox.RegisterTool(Of T)"/>),
    ''' only the backend - the delegate that binds the arguments of a call to
    ''' the parameters of the method - is rebuilt here, and it mirrors the
    ''' original binding rules one for one:
    ''' 
    ''' 1. a parameter is filled by name from the arguments of the call;
    ''' 2. a call that is not strict and whose tool takes exactly one
    '''    required parameter may hand that value over positionally;
    ''' 3. a missing parameter falls back to its clr default, and a missing
    '''    required parameter is reported instead of being guessed.
    ''' </summary>
    Public Module ToolBackend

        ''' <summary>
        ''' the methods of <paramref name="toolType"/> named
        ''' <paramref name="methodName"/> that carry a
        ''' <see cref="DescriptionAttribute"/> - the same filter the ollama
        ''' registration uses, so the same methods are found in the same order.
        ''' </summary>
        Public Function GetTargets(toolType As Type, methodName As String) As MethodInfo()
            If toolType Is Nothing OrElse String.IsNullOrEmpty(methodName) Then
                Return New MethodInfo() {}
            End If

            Dim declared As MethodInfo() = toolType.GetTypeInfo() _
                .DeclaredMethods _
                .Where(Function(m) String.Equals(m.Name, methodName, StringComparison.Ordinal)) _
                .ToArray()

            Return declared _
                .Where(Function(m) m.GetCustomAttribute(Of DescriptionAttribute)() IsNot Nothing) _
                .ToArray()
        End Function

        ''' <summary>
        ''' build the backend of one tool method: a delegate that turns the
        ''' flat string arguments of a function call into the parameters of
        ''' <paramref name="handle"/>, invokes it and returns what it returned.
        ''' </summary>
        ''' <param name="obj">the tool instance the method belongs to.</param>
        ''' <param name="handle">the tool method.</param>
        Public Function CreateCaller(obj As Object, handle As MethodInfo) As Func(Of FunctionCall, String)
            If handle Is Nothing Then
                Throw New ArgumentNullException(NameOf(handle))
            End If

            Dim runner As New ClrCaller(obj, handle)

            Return AddressOf runner.Invoke
        End Function

        Private NotInheritable Class ClrCaller

            ReadOnly obj As Object
            ReadOnly handle As MethodInfo
            ReadOnly pars As ParameterInfo()
            ReadOnly singleRequired As Boolean

            Sub New(instance As Object, method As MethodInfo)
                Me.pars = method.GetParameters()
                Me.obj = instance
                Me.handle = method

                singleRequired = pars.Length = 1 OrElse
                    pars.Where(Function(p) Not p.IsOptional).Count = 1
            End Sub

            ''' <summary>
            ''' the backend: bind the call arguments to the parameters, run the
            ''' tool and render its result the way the model expects it.
            ''' </summary>
            Public Function Invoke([call] As FunctionCall) As String
                Dim argVals As Object() = New Object(pars.Length - 1) {}

                ' a model that mis-names the single parameter a tool really
                ' needs still gets through: the first value of the call is
                ' taken as that parameter. filling every parameter by name
                ' afterwards keeps the optional arguments of the call (an
                ' "offset" or a "recursive" flag) from being dropped.
                If (Not [call].strict) AndAlso singleRequired Then
                    Dim head As ParameterInfo = pars.First()

                    If Not [call].has(head.Name) AndAlso [call].arguments IsNot Nothing AndAlso [call].arguments.Count > 0 Then
                        argVals(0) = TryCastValue([call](0), head)
                    End If
                End If

                For i As Integer = 0 To argVals.Length - 1
                    Dim name As String = pars(i).Name

                    If [call].has(name) Then
                        argVals(i) = TryCastValue([call](name), pars(i))
                    ElseIf argVals(i) Is Nothing Then
                        If Not pars(i).IsOptional Then
                            Return $"error: call of the function '{[call].name}' missing of the required parameter '{name}'!".GetJson
                        End If

                        argVals(i) = pars(i).DefaultValue
                    End If
                Next

                Dim result As Object = handle.Invoke(obj, argVals)

                Return any.ToString(result)
            End Function

        End Class

        ''' <summary>
        ''' turn the string an llm sent into the clr type the tool declares.
        ''' </summary>
        Private Function TryCastValue(val_str As String, p As ParameterInfo) As Object
            If GetType(String) Is p.ParameterType Then
                Return val_str
            End If

            Return val_str.CTypeDynamic(p.ParameterType)
        End Function

    End Module

End Namespace
