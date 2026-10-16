Imports System.IO
Imports System.Text

Namespace Logging

    ''' <summary>
    ''' append-only writer that keeps its file a valid json array at every
    ''' moment: the array is always closed with ']' and the next record
    ''' overwrites that bracket before appending itself plus a new one. a
    ''' crash therefore leaves a readable, complete file behind instead of a
    ''' truncated one.
    ''' </summary>
    Friend NotInheritable Class JsonArrayAppender

        Private ReadOnly _path As String
        Private ReadOnly _sync As New Object()
        Private _stream As FileStream
        Private _tail As Long
        Private _empty As Boolean
        Private _broken As Boolean

        Sub New(path As String)
            _path = path
        End Sub

        ''' <summary>True after a write failed; the appender then stays silent.</summary>
        Friend ReadOnly Property Broken As Boolean
            Get
                Return _broken
            End Get
        End Property

        Friend ReadOnly Property Path As String
            Get
                Return _path
            End Get
        End Property

        ''' <summary>
        ''' create the file when it is missing and bring its tail into a state
        ''' the next <see cref="Append"/> can continue from.
        ''' </summary>
        Friend Function Open() As Boolean
            Try
                Dim dir As String = IO.Path.GetDirectoryName(_path)

                If Not String.IsNullOrEmpty(dir) AndAlso Not Directory.Exists(dir) Then
                    Call Directory.CreateDirectory(dir)
                End If

                _stream = New FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read)
                Call RepairTail()

                Return True
            Catch ex As Exception
                _broken = True
                Return False
            End Try
        End Function

        ''' <summary>
        ''' repair the tail of a file a killed process left behind: a dangling
        ''' separator is closed with a null instead of corrupting the array
        ''' further.
        ''' </summary>
        Private Sub RepairTail()
            If _stream.Length = 0 Then
                Call AppendRaw("[")
                _tail = 1
                _empty = True
                Return
            End If

            Dim last As Integer = ReadByteAt(_stream.Length - 1)

            Select Case last
                Case AscW("]"c)
                    _tail = _stream.Length - 1
                    _empty = IsBracketAt(_stream.Length - 2)

                Case AscW("["c)
                    _tail = _stream.Length
                    _empty = True

                Case AscW(","c), 10, 13, 9, 32
                    Call AppendRaw("null]")
                    _tail = _stream.Length - 1
                    _empty = False

                Case Else
                    Call AppendRaw("]")
                    _tail = _stream.Length - 1
                    _empty = False
            End Select
        End Sub

        Private Function ReadByteAt(position As Long) As Integer
            If position < 0 OrElse position >= _stream.Length Then
                Return -1
            End If

            _stream.Position = position

            Return _stream.ReadByte()
        End Function

        Private Function IsBracketAt(position As Long) As Boolean
            Return ReadByteAt(position) = AscW("["c)
        End Function

        Private Sub AppendRaw(text As String)
            _stream.Position = _stream.Length

            Dim bytes As Byte() = Encoding.UTF8.GetBytes(text)

            _stream.Write(bytes, 0, bytes.Length)
            _stream.Flush()
        End Sub

        ''' <summary>append one already serialized json value to the array.</summary>
        Friend Function Append(json As String) As Boolean
            SyncLock _sync
                If _broken OrElse _stream Is Nothing Then
                    Return False
                End If

                Try
                    Dim prefix As String = If(_empty, "", "," & vbLf)

                    _stream.Position = _tail

                    Dim bytes As Byte() = Encoding.UTF8.GetBytes(prefix & json & vbLf & "]")

                    _stream.Write(bytes, 0, bytes.Length)
                    _stream.Flush()

                    _tail = _stream.Length - 1
                    _empty = False

                    Return True
                Catch ex As Exception
                    _broken = True
                    Return False
                End Try
            End SyncLock
        End Function

        Friend Sub Close()
            SyncLock _sync
                If _stream IsNot Nothing Then
                    Try
                        _stream.Flush()
                        _stream.Dispose()
                    Catch
                    End Try

                    _stream = Nothing
                End If
            End SyncLock
        End Sub

    End Class

End Namespace
