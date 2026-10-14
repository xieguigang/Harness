Imports System.ComponentModel
Imports System.IO
Imports System.Text
Imports Microsoft.VisualBasic.CommandLine.Reflection

Namespace Tools

    ''' <summary>
    ''' Tool "read": read a UTF-8 text file and return its line-numbered
    ''' content.
    ''' 
    ''' The tool holds a reference to the <see cref="Sandbox"/> that owns it,
    ''' so path resolution and permission checks always use the workspace and
    ''' the security policy of that sandbox.
    ''' </summary>
    Public Class ReadTool

        Private ReadOnly _sandbox As Sandbox

        ''' <param name="sandbox">The sandbox that resolves the paths and enforces the security policy.</param>
        Public Sub New(sandbox As Sandbox)
            If sandbox Is Nothing Then
                Throw New ArgumentNullException(NameOf(sandbox))
            End If

            _sandbox = sandbox
        End Sub

        ''' <summary>
        ''' Read a UTF-8 text file and return its line-numbered content.
        ''' </summary>
        ''' <param name="file_path">Path to read, resolved by the filesystem backend.</param>
        ''' <param name="offset">1-based first line to return. Defaults to 1.</param>
        ''' <param name="limit">Maximum number of lines to return. Defaults to 2000.</param>
        <Description("Read a UTF-8 text file and return line-numbered content.")>
        <ExportAPI("read")>
        Public Function read(
            <Argument("file_path", Description:="Path to read, resolved by the filesystem backend.")> file_path As String,
            <Argument("offset", Description:="1-based first line to return. Defaults to 1.")> Optional offset As Double = 1,
            <Argument("limit", Description:="Maximum number of lines to return. Defaults to 2000.")> Optional limit As Double = 2000
        ) As String
            Dim fullPath As String = Nothing
            Dim denied As String = _sandbox.ResolveAndAuthorize(file_path, SandboxOperation.Read, fullPath)

            If denied IsNot Nothing Then
                Return denied
            End If

            If Not File.Exists(fullPath) Then
                Return $"Error: File not found: {fullPath}"
            End If

            Try
                Dim lines() As String = File.ReadAllLines(fullPath, Encoding.UTF8)
                Return FormatLineNumbered(fullPath, lines, offset, limit)
            Catch ex As Exception
                Return $"Error reading file '{fullPath}': {ex.Message}"
            End Try
        End Function

        Private Function FormatLineNumbered(fullPath As String, lines As String(), offset As Double, limit As Double) As String
            Dim total As Integer = lines.Length
            Dim first As Long = ToLineNumber(offset, 1L)
            Dim count As Long = ToLineNumber(limit, 2000L)

            Dim startIndex As Long = Math.Min(first - 1L, CLng(total))
            Dim stopIndex As Long = Math.Min(startIndex + count, CLng(total))

            Dim sb As New StringBuilder()
            sb.AppendLine($"<path>{fullPath}</path>")
            sb.AppendLine("<type>file</type>")
            sb.AppendLine("<content>")

            For i As Long = startIndex To stopIndex - 1L
                sb.AppendLine($"{i + 1L}: {lines(CInt(i))}")
            Next

            sb.AppendLine()

            If stopIndex >= CLng(total) Then
                sb.AppendLine($"(End of file - total {total} lines)")
            Else
                sb.AppendLine($"(Showing lines {startIndex + 1L}-{stopIndex} of {total}. Use offset={stopIndex + 1L} to continue.)")
            End If

            sb.Append("</content>")

            Return sb.ToString()
        End Function

        ''' <summary>
        ''' Coerce a JSON number into a positive line count, clamping NaN,
        ''' infinity and out-of-range values so that a malformed tool call
        ''' can never throw.
        ''' </summary>
        Private Shared Function ToLineNumber(value As Double, fallback As Long) As Long
            If Double.IsNaN(value) OrElse Double.IsInfinity(value) Then
                Return fallback
            End If

            If value >= Long.MaxValue Then
                Return Long.MaxValue
            End If

            Dim n As Long = CLng(Math.Floor(value))

            If n < 1L Then
                n = 1L
            End If

            Return n
        End Function

    End Class

End Namespace
