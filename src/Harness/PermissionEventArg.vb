
Public Class PermissionEventArg : Inherits EventArgs

    ''' <summary>
    ''' The operation that the sandbox wants to perform, so that the host
    ''' can render a meaningful confirmation dialog.
    ''' </summary>
    Public Property Operation As SandboxOperation

    ''' <summary>
    ''' The resolved resource (usually a file path) the operation targets.
    ''' </summary>
    Public Property Target As String

    ''' <summary>
    ''' Set when the request comes from a one-shot escalation instead of the
    ''' active policy, e.g. "workspace-write" or "danger-full-access".
    ''' Nothing for a plain policy driven request.
    ''' </summary>
    Public Property RequestedPermission As String

    ''' <summary>
    ''' The model supplied sentence explaining why the wider access is
    ''' needed. Only set together with <see cref="RequestedPermission"/>.
    ''' </summary>
    Public Property Justification As String

    ''' <summary>
    ''' Set to True by the event handler to let the operation proceed.
    ''' </summary>
    Public Property PermissionGranted As Boolean = False

End Class