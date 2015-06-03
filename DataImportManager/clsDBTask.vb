Imports System.Collections.Specialized
Imports System.Data.SqlClient
Imports PRISM.Logging

Public MustInherit Class clsDBTask

#Region "Member Variables"

    ' access to the logger
    Protected m_logger As ILogger

    ' access to mgr parameters
    Protected m_mgrParams As IMgrParams

    ' DB access
    Protected m_DBCn As SqlConnection
    Protected m_error_list As New StringCollection()

    ' job status
    Protected m_TaskWasAssigned As Boolean = False

#End Region

#Region "Auto-properties"
    Public Property TraceMode As Boolean
#End Region

    ''' <summary>
    ''' Constructor
    ''' </summary>
    ''' <param name="mgrParams"></param>
    ''' <param name="logger"></param>
    ''' <param name="dbConnection">Database connection object (connection should already be open)</param>
    ''' <remarks></remarks>
    Public Sub New(mgrParams As IMgrParams, logger As ILogger, dbConnection As SqlConnection)
        m_mgrParams = mgrParams
        m_logger = logger
        m_DBCn = dbConnection
    End Sub

    Public ReadOnly Property TaskWasAssigned() As Boolean
        Get
            Return m_TaskWasAssigned
        End Get
    End Property

    Protected Sub LogErrorEvents()
        If m_error_list.Count > 0 Then
            m_logger.PostEntry("Warning messages were posted to local log", ILogger.logMsgType.logWarning, True)
        End If
        Dim s As String
        For Each s In m_error_list
            m_logger.PostEntry(s, ILogger.logMsgType.logWarning, True)
        Next
    End Sub

End Class

