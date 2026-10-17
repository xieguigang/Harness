Imports System.IO

''' <summary>
''' a demo host for <see cref="WebView2HarnessLogViewer"/>: it puts the
''' control on a form and hands it the log folder to read, which is all a
''' host has to do. run it with <c>dotnet WebView2UI.dll --logs</c>.
''' 
''' the path box and the buttons belong to the host, not to the control: the
''' control itself only exposes <see cref="WebView2HarnessLogViewer.LogFolder"/>
''' and cannot be pointed somewhere else from inside the page.
''' </summary>
Public Class FormLogViewer

    ''' <summary>
    ''' the folder the demo opens by default. a host would hand in the
    ''' workspace it cares about instead.
    ''' </summary>
    Public Const DefaultLogFolder As String = "G:\OmicsWorks\test\smoke_20261017\.harness"

    Private Sub FormLogViewer_Load(sender As Object, e As EventArgs) Handles Me.Load
        pathBox.Text = DefaultLogFolder
        logViewer1.LogFolder = DefaultLogFolder

        AddHandler logViewer1.InitializationFailed, AddressOf OnInitializationFailed
    End Sub

    Private Sub OnInitializationFailed(message As String)
        MessageBox.Show(Me, "日志查看器初始化失败：" & message, "Harness 日志查看器",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning)
    End Sub

    Private Sub btnBrowse_Click(sender As Object, e As EventArgs) Handles btnBrowse.Click
        Using dialog As New FolderBrowserDialog With {
            .Description = "选择 .harness 日志目录",
            .ShowNewFolderButton = False,
            .SelectedPath = pathBox.Text
        }
            If dialog.ShowDialog(Me) = DialogResult.OK Then
                pathBox.Text = dialog.SelectedPath
                logViewer1.LogFolder = dialog.SelectedPath
            End If
        End Using
    End Sub

    Private Sub btnRefresh_Click(sender As Object, e As EventArgs) Handles btnRefresh.Click
        Dim folder As String = pathBox.Text

        If Not Directory.Exists(folder) Then
            MessageBox.Show(Me, "目录不存在：" & folder, "Harness 日志查看器",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning)
            Return
        End If

        logViewer1.LogFolder = folder
        logViewer1.Reload()
    End Sub

End Class
