Imports System.Collections.Generic
Imports System.Threading.Tasks

Namespace Tools

    ''' <summary>
    ''' The event argument of <see cref="Sandbox.AskUser"/>: the batch of
    ''' questions the model asked through the "ask_user_question" tool,
    ''' plus the future the host completes once the user decided.
    '''
    ''' A host has two ways to answer, and both end in the same place:
    '''
    '''   - a synchronous host (a console prompt, a modal dialog) blocks
    '''     inside its handler until the user decided, then calls
    '''     <see cref="Reply"/> or <see cref="Decline"/> before it returns -
    '''     the tool call that raised the event continues at once;
    '''   - an asynchronous host (a non blocking notification) returns from
    '''     the handler immediately and calls <see cref="Reply"/> or
    '''     <see cref="Decline"/> later, from any thread - this is what keeps
    '''     a "timed" ask_user_question call pending while the question
    '''     remains answerable.
    '''
    ''' The answer of every question is keyed by its
    ''' <see cref="UserQuestion.Id"/>, the id the model gave and the id the
    ''' tool echoes back to it.
    ''' </summary>
    Public Class QuestionEventArg
        Inherits EventArgs

        ''' <summary>
        ''' The continuations of <see cref="AnswerTask"/> never run inline on
        ''' the thread that completes it, so that a host answering from its
        ''' ui thread cannot deadlock against the tool call that waits.
        ''' </summary>
        Private ReadOnly _answer As New TaskCompletionSource(Of UserAnswer)(TaskCreationOptions.RunContinuationsAsynchronously)

        ''' <param name="questions">The questions to show to the user, in the order the model asked them.</param>
        Public Sub New(questions As IReadOnlyList(Of UserQuestion))
            Me.Questions = questions
        End Sub

        ''' <summary>
        ''' The questions to show to the user, in the order the model asked
        ''' them.
        ''' </summary>
        Public ReadOnly Property Questions As IReadOnlyList(Of UserQuestion)

        ''' <summary>
        ''' Completes when the user answered (<see cref="Reply"/>) or
        ''' declined (<see cref="Decline"/>). A host that returns from its
        ''' handler without answering leaves this pending - the tool call
        ''' that raised the event decides how long it waits for it.
        ''' </summary>
        Public ReadOnly Property AnswerTask As Task(Of UserAnswer)
            Get
                Return _answer.Task
            End Get
        End Property

        ''' <summary>
        ''' The user answered: hand the response of every question back,
        ''' keyed by its <see cref="UserQuestion.Id"/>. The value is the
        ''' label of the chosen option, the comma joined labels of a
        ''' multi-select question, or the free text the user typed for a
        ''' question without options. May be called from any thread, and
        ''' only the first call has an effect.
        ''' </summary>
        Public Sub Reply(responses As Dictionary(Of String, String))
            Call _answer.TrySetResult(New UserAnswer With {
                .Responses = If(responses, New Dictionary(Of String, String)())
            })
        End Sub

        ''' <summary>
        ''' The user dismissed the question without answering. May be called
        ''' from any thread, and only the first call has an effect.
        ''' </summary>
        Public Sub Decline()
            Call _answer.TrySetResult(New UserAnswer With {.Declined = True})
        End Sub

    End Class

End Namespace
