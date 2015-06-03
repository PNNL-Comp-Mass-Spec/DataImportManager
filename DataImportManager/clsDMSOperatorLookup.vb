﻿Imports System.Data.SqlClient
Imports System.Runtime.InteropServices
Imports System.Threading
Imports PRISM.Logging

Public Class DMSInfoCache

#Region "Structures"
    Public Structure udtInstrumentInfoType
        Public InstrumentClass As String
        Public RawDataType As String
        Public CaptureType As String
        Public SourcePath As String
    End Structure

    Public Structure udtOperatorInfoType
        Public Name As String
        Public Email As String
        Public Username As String
        Public ID As Integer
    End Structure
#End Region
 

#Region "Properties and Events"

    Public ReadOnly Property DBConnection As SqlConnection
        Get
            Return mDBConnection
        End Get
    End Property

    Public Event DBErrorEvent(message As String)

#End Region

#Region "Member variables"
    Private ReadOnly mConnectionString As String
    Private ReadOnly m_logger As ILogger
    Private ReadOnly mTraceMode As Boolean

    ''' <summary>
    ''' Keys in this dictionary are error messages; values are suggested solutions to fix the error
    ''' </summary>
    ''' <remarks></remarks>
    Private ReadOnly mErrorSolutions As Dictionary(Of String, String)

    ''' <summary>
    ''' Keys in this dictionary are instrument names; values are instrument information
    ''' </summary>
    ''' <remarks></remarks>
    Private ReadOnly mInstruments As Dictionary(Of String, udtInstrumentInfoType)


    ''' <summary>
    ''' Keys in this dictionary are username; values are operator information
    ''' </summary>
    ''' <remarks></remarks>
    Private ReadOnly mOperators As Dictionary(Of String, udtOperatorInfoType)

    Private ReadOnly mDBConnection As SqlConnection

#End Region

    ''' <summary>
    ''' Constructor
    ''' </summary>
    ''' <remarks></remarks>
    Public Sub New(connectionString As String, logger As ILogger, traceMode As Boolean)
        mConnectionString = connectionString
        m_logger = logger
        mTraceMode = traceMode

        mErrorSolutions = New Dictionary(Of String, String)(StringComparison.CurrentCultureIgnoreCase)

        mInstruments = New Dictionary(Of String, udtInstrumentInfoType)(StringComparison.CurrentCultureIgnoreCase)

        mOperators = New Dictionary(Of String, udtOperatorInfoType)(StringComparison.CurrentCultureIgnoreCase)

        mDBConnection = OpenDbConnection()
        
    End Sub
    
    Protected Function OpenDbConnection() As SqlConnection
        Dim retryCount = 3
        While retryCount > 0
            Try
                If mTraceMode Then clsMainProcess.ShowTraceMessage("Opening database connection using " & mConnectionString)
                Dim newDbConnection = New SqlConnection(mConnectionString)
                AddHandler newDbConnection.InfoMessage, New SqlInfoMessageEventHandler(AddressOf OnInfoMessage)

                newDbConnection.Open()

                Return newDbConnection

            Catch e As SqlException
                retryCount -= 1

                m_logger.PostError("Connection problem: ", e, clsGlobal.LOG_LOCAL_ONLY)
                Thread.Sleep(300)
            End Try
        End While

        Throw New Exception("Unable to connect to the database after 3 tries")

    End Function

    ''' <summary>
    ''' Close the database connection
    ''' </summary>
    ''' <remarks>Access to DMS will no longer work after this has been called</remarks>
    Public Sub CloseDatabaseConnection()

        If Not mDBConnection Is Nothing Then
            If mDBConnection.State <> ConnectionState.Closed And mDBConnection.State <> ConnectionState.Broken Then
                mDBConnection.Close()
            End If
        End If
    End Sub

    Public Function GetDbErrorSolution(errorText As String) As String

        If mErrorSolutions.Count = 0 Then
            LoadErrorSolutionsFromDMS()
        End If

        Dim query = (From item In mErrorSolutions Where errorText.Contains(item.Key) Select item.Value).ToList()

        If query.Count > 0 Then
            Return query.FirstOrDefault()
        End If

        Return String.Empty

    End Function

    Public Function GetInstrumentInfo(instrumentName As String, <Out()> ByRef udtInstrumentInfo As udtInstrumentInfoType) As Boolean

        If mInstruments.Count = 0 Then
            LoadInstrumentsFromDMS()
        End If

        If mInstruments.TryGetValue(instrumentName, udtInstrumentInfo) Then
            Return True
        End If

        udtInstrumentInfo = New udtInstrumentInfoType
        Return False

    End Function

    Public Function GetOperatorName(operatorPRN As String, <Out()> ByRef userCountMatched As Integer) As udtOperatorInfoType

        If mOperators.Count = 0 Then
            LoadOperatorsFromDMS()
        End If

        userCountMatched = 0

        Dim operatorInfo = New udtOperatorInfoType
        Dim blnSuccess = LookupOperatorName(operatorPRN, operatorInfo, userCountMatched)

        If blnSuccess AndAlso Not String.IsNullOrEmpty(operatorInfo.Name) Then

            If mTraceMode Then
                ShowTraceMessage("  Operator: " & operatorInfo.Name)
                ShowTraceMessage("  EMail: " & operatorInfo.Email)
                ShowTraceMessage("  Username: " & operatorInfo.Username)
            End If

            Return operatorInfo
        End If

        ' No match; make sure the operator info is blank
        operatorInfo = New udtOperatorInfoType

        ShowTraceMessage("  Warning: operator not found: " & operatorPRN)

        Return operatorInfo

    End Function

    Private Function GetValue(reader As SqlDataReader, columnIndex As Integer, valueIfNull As Integer) As Integer
        If reader.IsDBNull(columnIndex) Then
            Return valueIfNull
        Else
            Return reader.GetInt32(columnIndex)
        End If
    End Function

    Private Function GetValue(reader As SqlDataReader, columnIndex As Integer, valueIfNull As String) As String
        If reader.IsDBNull(columnIndex) Then
            Return valueIfNull
        Else
            Return reader.GetString(columnIndex)
        End If
    End Function

    ''' <summary>
    ''' Reload all DMS info now
    ''' </summary>
    ''' <remarks></remarks>
    Public Sub LoadDMSInfo()
        LoadErrorSolutionsFromDMS()
        LoadInstrumentsFromDMS()
        LoadOperatorsFromDMS()
    End Sub

    Private Sub LoadErrorSolutionsFromDMS()

        Dim retryCount = 3
        Dim timeoutSeconds = 5

        ' Get a list of error messages in T_DIM_Error_Solution
        Dim sqlQuery As String =
            "SELECT Error_Text, Solution " &
            "FROM T_DIM_Error_Solution " &
            "ORDER BY Error_Text"

        If mTraceMode Then ShowTraceMessage("Getting error messages and solutions using " & sqlQuery)

        While retryCount > 0
            Try
                mErrorSolutions.Clear()

                Using cmd = New SqlCommand(sqlQuery, DBConnection)

                    cmd.CommandTimeout = timeoutSeconds

                    Using reader = cmd.ExecuteReader()
                        While reader.Read()

                            Dim errorMessage = GetValue(reader, 0, String.Empty)
                            Dim solutionMessage = GetValue(reader, 1, String.Empty)

                            If Not mErrorSolutions.ContainsKey(errorMessage) Then
                                mErrorSolutions.Add(errorMessage, solutionMessage)
                            End If

                        End While
                    End Using

                End Using

                Exit While

            Catch ex As Exception
                retryCount -= 1S
                Dim errorMessage = "Exception querying database in LoadErrorSolutionsFromDMS: " + ex.Message

                m_logger.PostEntry(errorMessage, ILogger.logMsgType.logError, clsGlobal.LOG_LOCAL_ONLY)

                If retryCount > 0 Then
                    ' Delay for 5 second before trying again
                    Thread.Sleep(5000)
                End If

            End Try
        End While

    End Sub

    Private Sub LoadInstrumentsFromDMS()

        Dim retryCount = 3
        Dim timeoutSeconds = 5

        ' Get a list of error messages in T_DIM_Error_Solution
        Dim sqlQuery As String =
           "SELECT Name, Class, RawDataType, Capture, SourcePath " &
           "FROM dbo.V_Instrument_List_Export " &
           "ORDER BY Name "

        If mTraceMode Then ShowTraceMessage("Getting instruments using " & sqlQuery)

        While retryCount > 0
            Try
                mInstruments.Clear()

                Using cmd = New SqlCommand(sqlQuery, DBConnection)

                    cmd.CommandTimeout = timeoutSeconds

                    Using reader = cmd.ExecuteReader()
                        While reader.Read()

                            Dim instrumentName = GetValue(reader, 0, String.Empty)

                            Dim udtInstrumentInfo = New udtInstrumentInfoType
                            udtInstrumentInfo.InstrumentClass = GetValue(reader, 1, String.Empty)
                            udtInstrumentInfo.RawDataType = GetValue(reader, 2, String.Empty)
                            udtInstrumentInfo.CaptureType = GetValue(reader, 3, String.Empty)
                            udtInstrumentInfo.SourcePath = GetValue(reader, 4, String.Empty)

                            If Not mInstruments.ContainsKey(instrumentName) Then
                                mInstruments.Add(instrumentName, udtInstrumentInfo)
                            End If

                        End While
                    End Using

                End Using

                Exit While

            Catch ex As Exception
                retryCount -= 1S
                Dim errorMessage = "Exception querying database in LoadErrorSolutionsFromDMS: " + ex.Message

                m_logger.PostEntry(errorMessage, ILogger.logMsgType.logError, clsGlobal.LOG_LOCAL_ONLY)

                If retryCount > 0 Then
                    ' Delay for 5 second before trying again
                    Thread.Sleep(5000)
                End If

            End Try
        End While

    End Sub

    Private Sub LoadOperatorsFromDMS()

        Dim retryCount = 3
        Dim timeoutSeconds = 5

        ' Get a list of all users in the database
        Dim sqlQuery As String =
            "SELECT U_Name, U_email, U_PRN, ID " &
            "FROM dbo.T_Users " &
            "ORDER BY ID desc"

        If mTraceMode Then ShowTraceMessage("Getting DMS users using " & sqlQuery)

        While retryCount > 0
            Try
                mOperators.Clear()

                Using cmd = New SqlCommand(sqlQuery, DBConnection)

                    cmd.CommandTimeout = timeoutSeconds

                    Using reader = cmd.ExecuteReader()
                        While reader.Read()

                            Dim udtOperator = New udtOperatorInfoType

                            udtOperator.Name = GetValue(reader, 0, String.Empty)
                            udtOperator.Email = GetValue(reader, 1, String.Empty)
                            udtOperator.Username = GetValue(reader, 2, String.Empty)
                            udtOperator.ID = GetValue(reader, 3, 0)

                            If Not String.IsNullOrWhiteSpace(udtOperator.Username) AndAlso Not mOperators.ContainsKey(udtOperator.Username) Then
                                mOperators.Add(udtOperator.Username, udtOperator)
                            End If

                        End While
                    End Using

                End Using

                Exit While

            Catch ex As Exception
                retryCount -= 1S
                Dim errorMessage = "Exception querying database in LoadOperatorsFromDMS: " + ex.Message

                m_logger.PostEntry(errorMessage, ILogger.logMsgType.logError, clsGlobal.LOG_LOCAL_ONLY)

                If retryCount > 0 Then
                    ' Delay for 5 second before trying again
                    Thread.Sleep(5000)
                End If

            End Try
        End While

    End Sub

    ''' <summary>
    ''' Lookup the operator information given operatorPrnToFind
    ''' </summary>
    ''' <param name="operatorPrnToFind">Typically username, but could be a person's real name</param>
    ''' <param name="operatorInfo">Output: Matching operator info</param>    
    ''' <param name="userCountMatched">Output: Number of users matched by operatorPrnToFind</param>
    ''' <returns>True if success, otherwise false</returns>
    ''' <remarks></remarks>
    Private Function LookupOperatorName(
        operatorPrnToFind As String,
        <Out()> ByRef operatorInfo As udtOperatorInfoType,
        <Out()> ByRef userCountMatched As Integer) As Boolean

        ' Get a list of all operators (hopefully just one) matching the user PRN

        If mTraceMode Then ShowTraceMessage("Looking for operator by username: " & operatorPrnToFind)
        operatorInfo = New udtOperatorInfoType

        If mOperators.TryGetValue(operatorPrnToFind, operatorInfo) Then
            ' Match found
            If mTraceMode Then ShowTraceMessage("Matched " & operatorInfo.Name)
            userCountMatched = 1
            Return True
        End If

        ' No match to an operator with username operatorPrnToFind
        ' operatorPrnToFind may contain the person's name instead of their PRN; check for this
        ' In other words, operatorPrnToFind may be "Baker, Erin M" instead of "D3P347"

        Dim strQueryName As String = String.Copy(operatorPrnToFind)
        If strQueryName.IndexOf("("c) > 0 Then
            ' Name likely is something like: Baker, Erin M (D3P347)
            ' Truncate any text after the parenthesis
            strQueryName = strQueryName.Substring(0, strQueryName.IndexOf("("c)).Trim()
        End If

        Dim query = (From item In mOperators
                     Order By item.Value.ID Descending
                     Where item.Value.Name.ToLower().StartsWith(strQueryName.ToLower())
                     Select item.Value()).ToList()

        userCountMatched = query.Count

        If userCountMatched = 1 Then
            ' We matched a single user
            ' Update the operator name, e-mail, and PRN
            operatorInfo = query.FirstOrDefault()
            Return True
        ElseIf userCountMatched > 1 Then
            operatorInfo = query.FirstOrDefault()
            Dim strLogMsg = "LookupOperatorName: Ambiguous match found for '" & strQueryName & "' in T_Users; will e-mail '" & operatorInfo.Email & "'"
            m_logger.PostEntry(strLogMsg, ILogger.logMsgType.logWarning, clsGlobal.LOG_LOCAL_ONLY)

            operatorInfo.Name = "Ambiguous match found for operator (" + strQueryName + "); use network login instead, e.g. D3E154"

            ' Note that the notification e-mail will get sent to operatorInfo.email
            Return False
        Else
            ' No match
            Dim strLogMsg = "LookupOperatorName: Operator not found in T_Users.U_PRN: " & operatorPrnToFind
            m_logger.PostEntry(strLogMsg, ILogger.logMsgType.logWarning, clsGlobal.LOG_LOCAL_ONLY)

            operatorInfo.Name = "Operator " + operatorPrnToFind + " not found in T_Users; should be network login name, e.g. D3E154"
            Return False
        End If

    End Function

    ' event handler for InfoMessage event
    ' errors and warnings sent from the SQL server are caught here
    '
    Private Sub OnInfoMessage(ByVal sender As Object, ByVal args As SqlInfoMessageEventArgs)
        Dim err As SqlError
        Dim s As String
        For Each err In args.Errors
            s = "Message: " & err.Message & _
                ", Source: " & err.Source & _
                ", Class: " & err.Class & _
                ", State: " & err.State & _
                ", Number: " & err.Number & _
                ", LineNumber: " & err.LineNumber & _
                ", Procedure:" & err.Procedure & _
                ", Server: " & err.Server
            RaiseEvent DBErrorEvent(s)            
        Next
    End Sub

    'Protected Overrides Sub Finalize()
    '    MyBase.Finalize()
    '    CloseDatabaseConnection()
    'End Sub
End Class
