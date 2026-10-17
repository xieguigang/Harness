<Global.Microsoft.VisualBasic.CompilerServices.DesignerGenerated()> _
Partial Class WebView2HarnessLogViewer
    Inherits System.Windows.Forms.UserControl

    'UserControl overrides dispose to clean up the component list.
    <System.Diagnostics.DebuggerNonUserCode()> _
    Protected Overrides Sub Dispose(ByVal disposing As Boolean)
        Try
            If disposing AndAlso components IsNot Nothing Then
                components.Dispose()
            End If
        Finally
            MyBase.Dispose(disposing)
        End Try
    End Sub

    'Required by the Windows Forms Designer
    Private components As System.ComponentModel.IContainer

    'NOTE: The following procedure is required by the Windows Forms Designer
    'It can be modified using the Windows Forms Designer.
    'Do not modify it using the code editor.
    <System.Diagnostics.DebuggerStepThrough()> _
    Private Sub InitializeComponent()
        WebView21 = New Microsoft.Web.WebView2.WinForms.WebView2()
        CType(WebView21, ComponentModel.ISupportInitialize).BeginInit()
        SuspendLayout()
        '
        ' WebView21
        '
        WebView21.AllowExternalDrop = False
        WebView21.CreationProperties = Nothing
        WebView21.DefaultBackgroundColor = Color.FromArgb(CType(15, Byte), CType(23, Byte), CType(42, Byte))
        WebView21.Dock = DockStyle.Fill
        WebView21.Location = New Point(0, 0)
        WebView21.Name = "WebView21"
        WebView21.Size = New Size(1180, 720)
        WebView21.TabIndex = 0
        WebView21.ZoomFactor = 1R
        '
        ' WebView2HarnessLogViewer
        '
        AutoScaleDimensions = New SizeF(7F, 15F)
        AutoScaleMode = AutoScaleMode.Font
        BackColor = Color.FromArgb(CType(15, Byte), CType(23, Byte), CType(42, Byte))
        Controls.Add(WebView21)
        Name = "WebView2HarnessLogViewer"
        Size = New Size(1180, 720)
        CType(WebView21, ComponentModel.ISupportInitialize).EndInit()
        ResumeLayout(False)
    End Sub

    Friend WithEvents WebView21 As Microsoft.Web.WebView2.WinForms.WebView2

End Class
