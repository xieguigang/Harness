Imports System.IO
Imports System.Reflection
Imports System.Text
Imports System.Text.Json
Imports Microsoft.Web.WebView2.Core
Imports WebView2UI.Logs

''' <summary>
''' a winforms control that shows the harness logs of a workspace - the tool
''' call log under <c>.harness\toolcalls</c> and the chat log under
''' <c>.harness\llm-sessions</c> - in an embedded web page.
''' 
''' the host only has to say where the logs are:
''' 
''' <code>
''' Dim viewer As New WebView2HarnessLogViewer With {.LogFolder = "G:\OmicsWorks\test\smoke_20261017\.harness"}
''' </code>
''' 
''' every file read happens on the host side through <see cref="LogHost"/>: the
''' page asks for one page of records at a time, so a session whose turns.json
''' is several megabytes still opens at once.
''' </summary>
Public Class WebView2HarnessLogViewer

    ''' <summary>
    ''' the name the embedded host object is registered under in the page.
    ''' </summary>
    Public Const HostObjectName As String = "log_host"

    Private ReadOnly _sync As New Object
    Private _host As LogHost
    Private _folder As String
    Private _webViewReady As Boolean = False
    Private _lastError As String = ""

    ''' <summary>
    ''' the log folder the control reads, i.e. the <c>.harness</c> folder of a
    ''' workspace. only the host sets it: the page has no way to change it.
    ''' setting it again while the control is open reloads the session list.
    ''' </summary>
    Public Property LogFolder As String
        Get
            Return _folder
        End Get
        Set(value As String)
            _folder = value
            Call Rebind()
        End Set
    End Property

    ''' <summary>why the control could not be initialized, when it could not.</summary>
    Public ReadOnly Property LastError As String
        Get
            Return _lastError
        End Get
    End Property

    ''' <summary>how many sessions the current log folder holds.</summary>
    Public ReadOnly Property Sessions As Integer
        Get
            If _host Is Nothing Then
                Return 0
            End If

            Try
                Return _host.LogIndex.Scan().Length
            Catch ex As Exception
                Return 0
            End Try
        End Get
    End Property

    ''' <summary>
    ''' raised when the webview could not be initialized or the log folder
    ''' could not be read at all.
    ''' </summary>
    Public Event InitializationFailed(message As String)

    ''' <summary>
    ''' ask the host to drop its cached documents and tell the page to reload,
    ''' so a log that grew while the viewer was open shows up.
    ''' </summary>
    Public Sub Reload()
        If _host Is Nothing Then
            Return
        End If

        Try
            Call _host.Refresh()
        Catch ex As Exception
            _lastError = ex.Message
        End Try

        Call SendMessage(New With {.action = "refresh"})
    End Sub

    ''' <summary>
    ''' the host object the page talks to. a host may read it directly to
    ''' drive the log from its own code instead of the page.
    ''' </summary>
    Public ReadOnly Property Host As LogHost
        Get
            Return _host
        End Get
    End Property

    ' ========================================================================
    ' webview wiring
    ' ========================================================================

    Private Async Sub WebView2HarnessLogViewer_Load(sender As Object, e As EventArgs) Handles Me.Load
        Try
            Await WebViewLoader.Init(WebView21)
        Catch ex As Exception
            _lastError = ex.Message
            RaiseEvent InitializationFailed(ex.Message)
        End Try
    End Sub

    Private Sub WebView21_CoreWebView2InitializationCompleted(sender As Object, e As CoreWebView2InitializationCompletedEventArgs) Handles WebView21.CoreWebView2InitializationCompleted
        If Not e.IsSuccess Then
            _lastError = If(e.InitializationException Is Nothing, "the webview could not be initialized", e.InitializationException.Message)
            RaiseEvent InitializationFailed(_lastError)
            Return
        End If

        Call AttachHost(If(_folder, ""))

        ' this page is served from a virtual domain, so a file:// navigation
        ' can only come from dropping a file into the window
        AddHandler WebView21.CoreWebView2.NavigationStarting, AddressOf WebView21_NavigationStarting

        Call WebViewLoader.NavigateToLargeString(WebView21, LoadDocument())
    End Sub

    ''' <summary>
    ''' point the host object at a log folder and tell the page about it. the
    ''' host object is registered again so the page always talks to the object
    ''' of the folder it is showing.
    ''' </summary>
    Private Sub AttachHost(folder As String)
        Try
            _host = New LogHost(folder)
            WebView21.CoreWebView2.AddHostObjectToScript(HostObjectName, _host)
        Catch ex As Exception
            _lastError = ex.Message
            RaiseEvent InitializationFailed(ex.Message)
        End Try
    End Sub

    Private Sub WebView21_NavigationStarting(sender As Object, e As CoreWebView2NavigationStartingEventArgs)
        If e.Uri.StringEmpty OrElse Not e.Uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase) Then
            Return
        End If

        ' a dropped file is not a log: the viewer never navigates away
        e.Cancel = True
    End Sub

    Private Sub WebView21_NavigationCompleted(sender As Object, e As CoreWebView2NavigationCompletedEventArgs) Handles WebView21.NavigationCompleted
        _webViewReady = True
        Call SendBind()
    End Sub

    Private Sub Rebind()
        If Not _webViewReady Then
            Return
        End If

        Call AttachHost(If(_folder, ""))
        Call SendBind()
    End Sub

    Private Sub SendBind()
        If Not _webViewReady OrElse WebView21.CoreWebView2 Is Nothing Then
            Return
        End If

        Call SendMessage(New With {
            .action = "bind",
            .folder = If(_folder, ""),
            .title = If(_folder, "")
        })
    End Sub

    ''' <summary>
    ''' post a json payload to the page. the call is marshalled to the ui
    ''' thread, because the webview is not happy about being touched from a
    ''' background one.
    ''' </summary>
    Private Sub SendMessage(Of T As Class)(payload As T)
        If WebView21 Is Nothing OrElse WebView21.IsDisposed OrElse WebView21.CoreWebView2 Is Nothing Then
            Return
        End If

        Dim json = JsonSerializer.Serialize(payload)

        If WebView21.InvokeRequired Then
            Call WebView21.BeginInvoke(Sub()
                                           If WebView21 Is Nothing OrElse WebView21.IsDisposed OrElse WebView21.CoreWebView2 Is Nothing Then
                                               Return
                                           End If

                                           Call WebView21.CoreWebView2.PostWebMessageAsJson(json)
                                       End Sub)
        Else
            Call WebView21.CoreWebView2.PostWebMessageAsJson(json)
        End If
    End Sub

    ' ========================================================================
    ' the embedded page
    ' ========================================================================

    ''' <summary>
    ''' assemble the document the control shows: the html of the viewer with
    ''' its style sheet and its script inlined, so one document is all the
    ''' webview has to load.
    ''' </summary>
    Private Shared Function LoadDocument() As String
        Dim html As String = ReadResource("WebView2UI.Logs.harness-log.html")
        Dim css As String = ReadResource("WebView2UI.Logs.harness-log.css")
        Dim script As String = ReadResource("WebView2UI.Logs.harness-log.js")

        If html.StringEmpty Then
            ' the resources are missing: show why instead of a blank page
            Return "<!DOCTYPE html><html><body style=""font-family:Segoe UI;background:#0F172A;color:#E2E8F0;padding:24px"">" &
                   "<h3>the harness log viewer page is missing</h3>" &
                   "<p>the embedded resources WebView2UI.Logs.harness-log.html / .css / .js were not found.</p></body></html>"
        End If

        Dim style As String = If(css.StringEmpty, "", "<style>" & vbCrLf & css & vbCrLf & "</style>")
        Dim code As String = If(script.StringEmpty, "", "<script>" & vbCrLf & script & vbCrLf & "</script>")

        Return html _
            .Replace("<!--@css-->", style) _
            .Replace("<!--@js-->", code)
    End Function

    Private Shared Function ReadResource(name As String) As String
        Dim assembly As Assembly = GetType(WebView2HarnessLogViewer).Assembly

        Using stream As Stream = assembly.GetManifestResourceStream(name)
            If stream Is Nothing Then
                Return ""
            End If

            Using reader As New StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks:=True)
                Return reader.ReadToEnd()
            End Using
        End Using
    End Function

End Class
