Namespace SkillSystem

    ''' <summary>
    ''' The result of running a user task through the SkillAgent pipeline.
    ''' </summary>
    Public Class SkillExecutionResult

        ''' <summary>The original user task string.</summary>
        Public Property UserTask As String

        ''' <summary>
        ''' The name of the skill that was matched in Layer 2, or empty
        ''' if no skill was matched.
        ''' </summary>
        Public Property MatchedSkillName As String = ""

        ''' <summary>True if the pipeline produced a final answer.</summary>
        Public Property Success As Boolean

        ''' <summary>The LLM's reasoning/thinking text (for chain-of-thought models).</summary>
        Public Property ThinkText As String = ""

        ''' <summary>The LLM's final answer text.</summary>
        Public Property Answer As String = ""

        Public Overrides Function ToString() As String
            Return $"[skill={MatchedSkillName}, success={Success}] {Answer}"
        End Function

    End Class
End Namespace