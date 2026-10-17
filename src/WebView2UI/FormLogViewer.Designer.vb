<Global.Microsoft.VisualBasic.CompilerServices.DesignerGenerated()> _
Partial Class FormLogViewer
    Inherits System.Windows.Forms.Form

    'Form overrides dispose to clean up the component list.
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
        toolStrip = New ToolStrip()
        toolLabel = New ToolStripLabel()
        pathBox = New ToolStripTextBox()
        btnBrowse = New ToolStripButton()
        btnRefresh = New ToolStripButton()
        logViewer1 = New WebView2HarnessLogViewer()
        toolStrip.SuspendLayout()
        SuspendLayout()
        '
        'toolStrip
        '
        toolStrip.GripStyle = ToolStripGripStyle.Hidden
        toolStrip.ImageScalingSize = New Drawing.Size(20, 20)
        toolStrip.Items.AddRange(New ToolStripItem() {toolLabel, pathBox, btnBrowse, btnRefresh})
        toolStrip.Location = New Point(0, 0)
        toolStrip.Name = "toolStrip"
        toolStrip.Padding = New Padding(8, 4, 8, 4)
        toolStrip.RenderMode = ToolStripRenderMode.System
        toolStrip.Size = New Size(1180, 32)
        toolStrip.TabIndex = 0
        '
        'toolLabel
        '
        toolLabel.Name = "toolLabel"
        toolLabel.Size = New Size(56, 23)
        toolLabel.Text = "日志目录"
        '
        'pathBox
        '
        pathBox.AutoSize = False
        pathBox.Margin = New Padding(4, 0, 4, 0)
        pathBox.Name = "pathBox"
        pathBox.Size = New Drawing.Size(700, 27)
        '
        'btnBrowse
        '
        btnBrowse.DisplayStyle = ToolStripItemDisplayStyle.Text
        btnBrowse.Name = "btnBrowse"
        btnBrowse.Size = New Drawing.Size(44, 23)
        btnBrowse.Text = "浏览…"
        '
        'btnRefresh
        '
        btnRefresh.DisplayStyle = ToolStripItemDisplayStyle.Text
        btnRefresh.Name = "btnRefresh"
        btnRefresh.Size = New Drawing.Size(44, 23)
        btnRefresh.Text = "刷新"
        '
        'logViewer1
        '
        logViewer1.Dock = DockStyle.Fill
        logViewer1.Location = New Point(0, 32)
        logViewer1.Name = "logViewer1"
        logViewer1.Size = New Size(1180, 688)
        logViewer1.TabIndex = 1
        '
        'FormLogViewer
        '
        AutoScaleDimensions = New Drawing.SizeF(7.0F, 15.0F)
        AutoScaleMode = AutoScaleMode.Font
        ClientSize = New Drawing.Size(1180, 720)
        Controls.Add(logViewer1)
        Controls.Add(toolStrip)
        Name = "FormLogViewer"
        StartPosition = FormStartPosition.CenterScreen
        Text = "Harness 日志查看器"
        toolStrip.ResumeLayout(False)
        toolStrip.PerformLayout()
        ResumeLayout(False)
        PerformLayout()
    End Sub

    Friend WithEvents toolStrip As ToolStrip
    Friend WithEvents toolLabel As ToolStripLabel
    Friend WithEvents pathBox As ToolStripTextBox
    Friend WithEvents btnBrowse As ToolStripButton
    Friend WithEvents btnRefresh As ToolStripButton
    Friend WithEvents logViewer1 As WebView2HarnessLogViewer
End Class
