'Contains functions/variables common to all parts of the Analysis Manager
Imports System.IO
Imports PRISM.Logging
Imports System.Text
Imports System.Reflection

Public Class clsGlobal

	'Constants
	Public Const LOG_LOCAL_ONLY As Boolean = True
	Public Const LOG_DATABASE As Boolean = False

    Private Const FLAG_FILE_NAME As String = "FlagFile.txt"

	Public Shared Sub CreateStatusFlagFile()

		'Creates a dummy file in the application directory to be used for controlling task request
		'	bypass

        Dim fiAppProgram As New FileInfo(GetExePath())
        Dim fiFlagFile As New FileInfo(Path.Combine(fiAppProgram.DirectoryName, FLAG_FILE_NAME))
        Using swFlagFile As StreamWriter = fiFlagFile.AppendText()
            swFlagFile.WriteLine(DateTime.Now().ToString)
        End Using

    End Sub

    Public Shared Sub DeleteStatusFlagFile(ByVal MyLogger As ILogger)

        'Deletes the task request control flag file
        Dim fiAppProgram As New FileInfo(GetExePath())
        Dim flagFilePath As String = Path.Combine(fiAppProgram.DirectoryName, FLAG_FILE_NAME)

        Try
            If File.Exists(flagFilePath) Then
                File.Delete(flagFilePath)
            End If
        Catch ex As Exception
            MyLogger.PostEntry("DeleteStatusFlagFile, " & ex.Message, ILogger.logMsgType.logError, LOG_LOCAL_ONLY)
        End Try

    End Sub

    Public Shared Function DetectStatusFlagFile() As Boolean

        ' Returns True if task request control flag file exists
        Dim fiAppProgram As New FileInfo(GetExePath())
        Dim flagFilePath As String = Path.Combine(fiAppProgram.DirectoryName, FLAG_FILE_NAME)

        Return File.Exists(flagFilePath)

    End Function

    Public Shared Function GetExePath() As String
        ' Could use Application.ExecutablePath
        ' Instead, use reflection
        Return Assembly.GetExecutingAssembly().Location
    End Function

    Public Shared Function GetHostName() As String

        Dim hostName = Net.Dns.GetHostName()
        If String.IsNullOrWhiteSpace(hostName) Then
            hostName = Environment.MachineName
        End If

        Return hostName

    End Function
    Public Shared Function LoadXmlFileContentsIntoString(ByVal triggerFile As FileInfo, ByVal MyLogger As ILogger) As String
        Try
            ' Read the contents of the xml file into a string which will be passed into a stored procedure.
            If Not triggerFile.Exists Then
                MyLogger.PostEntry("clsGlobal.LoadXmlFileContentsIntoString(), File: " & triggerFile.FullName & " does not exist.", ILogger.logMsgType.logError, LOG_LOCAL_ONLY)
                Return String.Empty
            End If
            Dim xmlFileContents = New StringBuilder
            Using sr = New StreamReader(New FileStream(triggerFile.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                Do While Not sr.EndOfStream
                    Dim input = sr.ReadLine()
                    If xmlFileContents.Length > 0 Then xmlFileContents.Append(Environment.NewLine)
                    xmlFileContents.Append(input.Replace("&", "&#38;"))
                Loop
            End Using
            Return xmlFileContents.ToString()
        Catch ex As Exception
            MyLogger.PostEntry("clsGlobal.LoadXmlFileContentsIntoString(), Error reading xml file, " & ex.Message, ILogger.logMsgType.logError, LOG_LOCAL_ONLY)
            Return String.Empty
        End Try

    End Function

End Class
