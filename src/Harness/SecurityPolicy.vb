Public Enum SecurityPolicy

    ''' <summary>
    ''' read file only
    ''' </summary>
    [Readonly]
    ''' <summary>
    ''' ask user for permission for write file
    ''' </summary>
    AskPermission
    ''' <summary>
    ''' full controls of the sandbox workspace files without ask user for operation permission
    ''' </summary>
    FullControls

End Enum

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
    ''' Set to True by the event handler to let the operation proceed.
    ''' </summary>
    Public Property PermissionGranted As Boolean = False

End Class