Public Class Sandbox

    ReadOnly wsRoot As String
    ReadOnly wsSecurity As SecurityPolicy

    Sub New(workspace As String, Optional security As SecurityPolicy = SecurityPolicy.AskPermission)
        wsRoot = workspace
        wsSecurity = security
    End Sub



End Class
