''' <summary>
''' The kind of filesystem operation that a sandbox tool is trying to
''' perform. Every tool implementation must be checked against the
''' active <see cref="SecurityPolicy"/> through <see cref="Sandbox.Authorize"/>
''' before it touches the disk.
''' </summary>
Public Enum SandboxOperation

    ''' <summary>
    ''' Read a file or list a directory. Always allowed inside the
    ''' workspace; a target outside the workspace follows the active
    ''' <see cref="SecurityPolicy"/>.
    ''' </summary>
    Read
    ''' <summary>Create, overwrite or delete a file inside the workspace.</summary>
    Write
    ''' <summary>Launch a child process, e.g. to run a script or a build.</summary>
    Execute

End Enum