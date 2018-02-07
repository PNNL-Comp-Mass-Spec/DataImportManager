Imports System.Data.SqlClient
Imports System.IO
Imports DataImportManager.clsGlobal
Imports PRISM.Logging

Public Class clsDataImportTask
    Inherits clsDBTask

#Region "Member Variables"
    Private mPostTaskErrorMessage As String = String.Empty
    Private mDBErrorMessage As String

    Private mp_stored_proc As String
    Private mp_xmlContents As String
#End Region

#Region "Properties"

    Public ReadOnly Property PostTaskErrorMessage As String
        Get
            If String.IsNullOrEmpty(mPostTaskErrorMessage) Then
                Return String.Empty
            Else
                Return mPostTaskErrorMessage
            End If
        End Get
    End Property

    Public ReadOnly Property DBErrorMessage As String
        Get
            If String.IsNullOrEmpty(mDBErrorMessage) Then
                Return String.Empty
            Else
                Return mDBErrorMessage
            End If
        End Get
    End Property

    Public Property PreviewMode As Boolean

#End Region

    ''' <summary>
    ''' Constructor
    ''' </summary>
    ''' <param name="mgrParams"></param>
    ''' <param name="dbConnection"></param>
    ''' <remarks></remarks>
    Public Sub New(mgrParams As IMgrParams, dbConnection As SqlConnection)
        MyBase.New(mgrParams, dbConnection)
    End Sub

    Public Function PostTask(triggerFile As FileInfo) As Boolean

        mPostTaskErrorMessage = String.Empty
        mDBErrorMessage = String.Empty

        Dim fileImported As Boolean

        Try
            ' Load the XML file into memory
            mp_xmlContents = LoadXmlFileContentsIntoString(triggerFile)
            If String.IsNullOrEmpty(mp_xmlContents) Then
                Return False
            End If

            ' Call the stored procedure (typically AddNewDataset)
            fileImported = ImportDataTask()

        Catch ex As Exception
            LogTools.LogError("clsDatasetImportTask.PostTask(), Error running PostTask", ex)
            Return False
        End Try

        Return fileImported

    End Function

    ''' <summary>
    ''' Posts the given XML to DMS5 using stored procedure AddNewDataset
    ''' </summary>
    ''' <returns>True if success, false if an error</returns>
    ''' <remarks></remarks>
    Private Function ImportDataTask() As Boolean

        Dim Outcome As Boolean

        Try

            ' Initialize database error message
            mDBErrorMessage = String.Empty

            ' Prepare to call the stored procedure (typically AddNewDataset in DMS5, which in turn calls AddUpdateDataset)
            '
            mp_stored_proc = m_mgrParams.GetParam("storedprocedure")

            Dim sc = New SqlCommand(mp_stored_proc, m_DBCn)
            sc.CommandType = CommandType.StoredProcedure
            sc.CommandTimeout = 45

            '
            ' define parameter for stored procedure's return value
            '
            sc.Parameters.Add("@Return", SqlDbType.Int).Direction = ParameterDirection.ReturnValue
            sc.Parameters.Add("@XmlDoc", SqlDbType.VarChar, 4000).Value = mp_xmlContents
            sc.Parameters.Add("@mode", SqlDbType.VarChar, 24).Value = "add"
            sc.Parameters.Add("@message", SqlDbType.VarChar, 512).Direction = ParameterDirection.Output

            If PreviewMode Then
                clsMainProcess.ShowTraceMessage("Preview: call stored procedure " & mp_stored_proc & " in database " & m_DBCn.Database)
                Return True
            End If

            If TraceMode Then
                clsMainProcess.ShowTraceMessage("Calling stored procedure " & mp_stored_proc & " in database " & m_DBCn.Database)
            End If

            ' execute the stored procedure
            '
            sc.ExecuteNonQuery()

            ' get return value
            '
            Dim ret = CInt(sc.Parameters("@Return").Value)

            If ret = 0 Then
                ' get values for output parameters
                '
                Outcome = True
            Else
                mPostTaskErrorMessage = CStr(sc.Parameters("@message").Value)
                LogTools.LogError("clsDataImportTask.ImportDataTask(), Problem posting dataset: " & mPostTaskErrorMessage)
                Outcome = False
            End If

        Catch ex As Exception
            LogTools.LogError("clsDataImportTask.ImportDataTask(), Error posting dataset", ex, True)
            mDBErrorMessage = ControlChars.NewLine & "Database Error Message:" & ex.Message
            Outcome = False
        End Try

        Return Outcome

    End Function

End Class
