﻿Imports System.Collections.Concurrent
Imports System.IO
Imports System.Net.Mail
Imports PRISM.Logging
Imports DataImportManager.clsGlobal

Public Class clsProcessXmlTriggerFile

    Public Structure udtXmlProcSettingsType
        Public DebugLevel As Integer
        Public MailDisabled As Boolean
        Public PreviewMode As Boolean
        Public TraceMode As Boolean
        Public FailureFolder As String
        Public SuccessFolder As String
    End Structure

#Region "Properties"

    Public Property ProcSettings As udtXmlProcSettingsType

#End Region

#Region "Member variables"
    Private ReadOnly m_MgrSettings As clsMgrSettings
    Protected ReadOnly m_InstrumentsToSkip As ConcurrentDictionary(Of String, Integer)
    Protected ReadOnly m_Logger As ILogger

    Protected mDataImportTask As clsDataImportTask
    Protected mDatabaseErrorMsg As String

    Private m_xml_operator_Name As String = String.Empty
    Private m_xml_operator_email As String = String.Empty
    Private m_xml_dataset_path As String = String.Empty
    Private m_xml_instrument_Name As String = String.Empty


#End Region

    Public Sub New(
       mgrSettings As clsMgrSettings,
       instrumentsToSkip As ConcurrentDictionary(Of String, Integer),
       oLogger As ILogger,
       udtSettings As udtXmlProcSettingsType)

        m_MgrSettings = mgrSettings
        m_InstrumentsToSkip = instrumentsToSkip
        m_Logger = oLogger
        ProcSettings = udtSettings

    End Sub

    Protected Function CreateMail(mailMsg As String, addtnlRecipient As String, subjectAppend As String) As Boolean

        Dim enableEmail = CBool(m_MgrSettings.GetParam("enableemail"))
        If Not enableEmail Then
            Return False
        End If

        Try
            Const addMsg As String = ControlChars.NewLine & ControlChars.NewLine & "(NOTE: This message was sent from an account that is not monitored. If you have any questions, please reply to the list of recipients directly.)"

            ' Create the mail message
            Dim mail As New MailMessage()

            ' Set the addresses
            mail.From = New MailAddress(m_MgrSettings.GetParam("from"))

            Dim mailRecipientsText = m_MgrSettings.GetParam("to")
            Dim mailRecipientsList = mailRecipientsText.Split(";"c).Distinct().ToList()

            For Each emailAddress As String In mailRecipientsList
                mail.To.Add(emailAddress)
            Next

            ' Possibly update the e-mail address for addtnlRecipient
            If Not String.IsNullOrEmpty(addtnlRecipient) AndAlso Not mailRecipientsList.Contains(addtnlRecipient) Then
                mail.To.Add(addtnlRecipient)
                mailRecipientsText &= ";" & addtnlRecipient
            End If

            ' Set the Subject and Body
            If String.IsNullOrEmpty(subjectAppend) Then
                mail.Subject = m_MgrSettings.GetParam("subject")
            Else
                mail.Subject = m_MgrSettings.GetParam("subject") + subjectAppend
            End If
            mail.Body = mailMsg & ControlChars.NewLine & ControlChars.NewLine & mDatabaseErrorMsg & addMsg

            Dim statusMsg As String = "E-mailing " & mailRecipientsText & " regarding " & m_xml_dataset_path
            If ProcSettings.TraceMode Then ShowTraceMessage(statusMsg)
            m_Logger.PostEntry(statusMsg, ILogger.logMsgType.logDebug, True)

            If ProcSettings.MailDisabled Then
                ShowTraceMessage("Email that would be sent:")
                ShowTraceMessage("  " & mailRecipientsText)
                ShowTraceMessage("  " & mail.Subject)
                ShowTraceMessage("  " & mail.Body)
            Else
                ' Send the message
                Dim smtp As New SmtpClient(m_MgrSettings.GetParam("smtpserver"))
                smtp.Send(mail)
            End If

            Return True

        Catch ex As Exception
            Dim statusMsg As String = "Error sending email message: " & ex.Message
            If ProcSettings.TraceMode Then ShowTraceMessage(statusMsg)
            m_Logger.PostEntry(statusMsg, ILogger.logMsgType.logError, True)
            Return False
        End Try


    End Function

    ''' <summary>
    ''' Returns a string with the path to the log file, assuming the file can be access with \\ComputerName\DMS_Programs\ProgramFolder\Logs\LogFileName.txt
    ''' </summary>
    ''' <returns></returns>
    ''' <remarks></remarks>
    Private Function GetLogFileSharePath() As String

        Dim strLogFilePath As String

        Dim FInfo = New FileInfo(GetExePath())

        strLogFilePath = Path.Combine(FInfo.Directory.Name, m_MgrSettings.GetParam("logfilename"))

        ' strLogFilePath should look like this:
        '	DataImportManager\Logs\DataImportManager

        ' Prepend the computer name and share name, giving a string similar to:
        ' \\proto-3\DMS_Programs\DataImportManager\Logs\DataImportManager

        strLogFilePath = "\\" & Environment.MachineName & "\DMS_Programs\" & strLogFilePath

        ' Append the date stamp to the log
        strLogFilePath &= "_" & DateTime.Now.ToString("MM-dd-yyyy") & ".txt"

        Return strLogFilePath

    End Function

    Public Function ProcessFile(triggerFile As FileInfo) As Boolean

        mDatabaseErrorMsg = String.Empty

        Dim statusMsg = "Starting data import task for dataset: " & triggerFile.FullName
        If ProcSettings.TraceMode Then
            Console.WriteLine()
            Console.WriteLine("-------------------------------------------")
            ShowTraceMessage(statusMsg)
        End If
        m_Logger.PostEntry(statusMsg, ILogger.logMsgType.logNormal, LOG_LOCAL_ONLY)

        If Not ValidateXMLFileMain(triggerFile) Then
            Return False
        End If

        If Not triggerFile.Exists Then
            statusMsg = "XML file no longer exists; cannot import: " & triggerFile.FullName
            If ProcSettings.TraceMode Then ShowTraceMessage(statusMsg)
            m_Logger.PostEntry(statusMsg, ILogger.logMsgType.logWarning, LOG_LOCAL_ONLY)
            Return False
        End If

        If ProcSettings.DebugLevel >= 2 Then
            statusMsg = "Posting Dataset XML file to database: " & triggerFile.Name
            If ProcSettings.TraceMode Then ShowTraceMessage(statusMsg)
            m_Logger.PostEntry(statusMsg, ILogger.logMsgType.logNormal, LOG_LOCAL_ONLY)
        End If

        ' create the object that will import the Data record
        '
        mDataImportTask = New clsDataImportTask(m_MgrSettings, m_Logger)
        mDataImportTask.TraceMode = ProcSettings.TraceMode
        mDataImportTask.PreviewMode = ProcSettings.PreviewMode

        mDatabaseErrorMsg = String.Empty
        Dim success = mDataImportTask.PostTask(triggerFile)

        mDatabaseErrorMsg = mDataImportTask.DBErrorMessage

        If mDatabaseErrorMsg.Contains("Timeout expired.") Then
            ' post the error and leave the file for another attempt
            statusMsg = "Encountered database timeout error for dataset: " & triggerFile.FullName
            If ProcSettings.TraceMode Then ShowTraceMessage(statusMsg)
            m_Logger.PostEntry(statusMsg, ILogger.logMsgType.logError, LOG_DATABASE)
            Return False
        End If

        If success Then
            MoveXmlFile(triggerFile, ProcSettings.SuccessFolder)
        Else
            Dim moveLocPath = MoveXmlFile(triggerFile, ProcSettings.FailureFolder)
            statusMsg = "Error posting xml file to database: " & mDataImportTask.PostTaskErrorMessage
            If ProcSettings.TraceMode Then ShowTraceMessage(statusMsg)
            m_Logger.PostEntry(statusMsg & ". View details in log at " & GetLogFileSharePath() & " for: " & moveLocPath, ILogger.logMsgType.logError, LOG_DATABASE)

            Dim mail_msg = "There is a problem with the following XML file: " & moveLocPath & ".  Check the log for details."
            mail_msg &= ControlChars.NewLine & "Operator: " & m_xml_operator_Name

            ' Send m_db_Err_Msg to see if there is a suggested solution in table T_DIM_Error_Solution for the error 
            ' If a solution is found, then m_db_Err_Msg will get auto-updated with the suggested course of action
            mDataImportTask.GetDbErrorSolution(mDatabaseErrorMsg)

            ' Send an e-mail
            CreateMail(mail_msg, m_xml_operator_email, " - Database error.")
        End If

        statusMsg = "Completed Data import task for dataset: " & triggerFile.FullName
        If ProcSettings.TraceMode Then ShowTraceMessage(statusMsg)
        m_Logger.PostEntry(statusMsg, ILogger.logMsgType.logNormal, LOG_LOCAL_ONLY)

        Return success

    End Function

    Private Function MoveXmlFile(triggerFile As FileInfo, moveFolder As String) As String

        Try
            If Not triggerFile.Exists Then
                Return String.Empty
            End If

            If Not Directory.Exists(moveFolder) Then
                If ProcSettings.TraceMode Then ShowTraceMessage("Creating target folder: " + moveFolder)
                Directory.CreateDirectory(moveFolder)
            End If

            Dim targetFilePath = Path.Combine(moveFolder, triggerFile.Name)
            If ProcSettings.TraceMode Then ShowTraceMessage("Instantiating file info object for " + targetFilePath)
            Dim xmlFileNewLoc = New FileInfo(targetFilePath)

            If xmlFileNewLoc.Exists Then
                If ProcSettings.PreviewMode Then
                    ShowTraceMessage("Preview: delete target file: " + xmlFileNewLoc.FullName)
                Else
                    If ProcSettings.TraceMode Then ShowTraceMessage("Deleting target file: " + xmlFileNewLoc.FullName)
                    xmlFileNewLoc.Delete()
                End If

            End If

            If ProcSettings.PreviewMode Then
                ShowTraceMessage("Preview: move XML file from " + triggerFile.FullName + " to " + xmlFileNewLoc.DirectoryName)
            Else
                If ProcSettings.TraceMode Then ShowTraceMessage("Moving XML file from " + triggerFile.FullName + " to " + xmlFileNewLoc.DirectoryName)
                triggerFile.MoveTo(xmlFileNewLoc.FullName)
            End If

            Return xmlFileNewLoc.FullName

        Catch ex As Exception
            Dim statusMsg As String = "Exception in MoveXmlFile, " & ex.Message
            If ProcSettings.TraceMode Then ShowTraceMessage(statusMsg)
            m_Logger.PostEntry(statusMsg, ILogger.logMsgType.logError, True)
            Return String.Empty
        End Try

    End Function

    ''' <summary>
    ''' Adds or updates strInstrumentName in m_InstrumentsToSkip
    ''' </summary>
    ''' <param name="strInstrumentName"></param>
    ''' <remarks></remarks>
    Private Sub UpdateInstrumentsToSkip(strInstrumentName As String)

        Dim intDatasetsSkipped = 0
        If m_InstrumentsToSkip.TryGetValue(strInstrumentName, intDatasetsSkipped) Then
            m_InstrumentsToSkip(strInstrumentName) = intDatasetsSkipped + 1
        Else
            If Not m_InstrumentsToSkip.TryAdd(strInstrumentName, 1) Then
                If m_InstrumentsToSkip.TryGetValue(strInstrumentName, intDatasetsSkipped) Then
                    m_InstrumentsToSkip(strInstrumentName) = intDatasetsSkipped + 1
                End If
            End If
        End If
    End Sub

    ''' <summary>
    ''' Process the specified XML file
    ''' </summary>
    ''' <param name="triggerFile">XML file to process</param>
    ''' <returns>True if XML file is valid and dataset is ready for import; otherwise false</returns>
    ''' <remarks></remarks>
    Private Function ValidateXMLFileMain(triggerFile As FileInfo) As Boolean

        Try
            Dim xmlRslt As IXMLValidateStatus.XmlValidateStatus
            Dim timeValFolder As String = m_MgrSettings.GetParam("timevalidationfolder")
            Dim moveLocPath As String
            Dim mail_msg As String
            Dim failureFolder As String = m_MgrSettings.GetParam("failurefolder")
            Dim rslt As Boolean


            Dim myDataXMLValidation = New clsXMLTimeValidation(m_MgrSettings, m_Logger, m_InstrumentsToSkip)
            myDataXMLValidation.TraceMode = ProcSettings.TraceMode

            xmlRslt = myDataXMLValidation.ValidateXMLFile(triggerFile)

            m_xml_operator_Name = myDataXMLValidation.OperatorName()
            m_xml_operator_email = myDataXMLValidation.OperatorEMail()
            m_xml_dataset_path = myDataXMLValidation.DatasetPath()
            m_xml_instrument_Name = myDataXMLValidation.InstrumentName()

            If xmlRslt = IXMLValidateStatus.XmlValidateStatus.XML_VALIDATE_NO_OPERATOR Then

                moveLocPath = MoveXmlFile(triggerFile, failureFolder)

                Dim statusMsg As String = "Operator not defined in " & moveLocPath
                If ProcSettings.TraceMode Then ShowTraceMessage(statusMsg)
                m_Logger.PostEntry(statusMsg, ILogger.logMsgType.logWarning, LOG_DATABASE)
                mail_msg = m_xml_operator_Name & ControlChars.NewLine
                mail_msg &= "The dataset was not added to DMS: " & ControlChars.NewLine & moveLocPath & ControlChars.NewLine
                mDatabaseErrorMsg = "Operator payroll number/HID was blank"
                rslt = mDataImportTask.GetDbErrorSolution(mDatabaseErrorMsg)
                If Not rslt Then
                    mDatabaseErrorMsg = String.Empty
                End If
                CreateMail(mail_msg, m_xml_operator_email, " - Operator not defined.")
                Return False

            ElseIf xmlRslt = IXMLValidateStatus.XmlValidateStatus.XML_VALIDATE_FAILED Then
                moveLocPath = MoveXmlFile(triggerFile, timeValFolder)

                Dim statusMsg As String = "XML Time validation error, file " & moveLocPath
                If ProcSettings.TraceMode Then ShowTraceMessage(statusMsg)
                m_Logger.PostEntry(statusMsg, ILogger.logMsgType.logWarning, LOG_LOCAL_ONLY)
                m_Logger.PostEntry("Time validation error. View details in log at " & GetLogFileSharePath() & " for: " & moveLocPath, ILogger.logMsgType.logError, LOG_DATABASE)
                mail_msg = "Operator: " & m_xml_operator_Name & ControlChars.NewLine
                mail_msg &= "There was a time validation error with the following XML file: " & ControlChars.NewLine & moveLocPath & ControlChars.NewLine
                mail_msg &= "Check the log for details.  " & ControlChars.NewLine
                mail_msg &= "Dataset filename and location: " + m_xml_dataset_path
                CreateMail(mail_msg, m_xml_operator_email, " - Time validation error.")
                Return False

            ElseIf xmlRslt = IXMLValidateStatus.XmlValidateStatus.XML_VALIDATE_ENCOUNTERED_ERROR Then
                moveLocPath = MoveXmlFile(triggerFile, failureFolder)

                Dim statusMsg As String = "An error was encountered during the validation process, file " & moveLocPath
                If ProcSettings.TraceMode Then ShowTraceMessage(statusMsg)
                m_Logger.PostEntry(statusMsg, ILogger.logMsgType.logWarning, LOG_DATABASE)
                mail_msg = "XML error encountered during validation process for the following XML file: " & ControlChars.NewLine & moveLocPath & ControlChars.NewLine
                mail_msg &= "Check the log for details.  " & ControlChars.NewLine
                mail_msg &= "Dataset filename and location: " + m_xml_dataset_path
                CreateMail(mail_msg, m_xml_operator_email, " - XML validation error.")
                Return False

            ElseIf xmlRslt = IXMLValidateStatus.XmlValidateStatus.XML_VALIDATE_ENCOUNTERED_LOGON_FAILURE Then
                ' Logon failure; Do not move the XML file
                Return False
            ElseIf xmlRslt = IXMLValidateStatus.XmlValidateStatus.XML_VALIDATE_ENCOUNTERED_NETWORK_ERROR Then
                ' Network error; Do not move the XML file
                ' Furthermore, do not process any more .XML files for this instrument
                UpdateInstrumentsToSkip(m_xml_instrument_Name)
                Return False

            ElseIf xmlRslt = IXMLValidateStatus.XmlValidateStatus.XML_VALIDATE_SKIP_INSTRUMENT Then

                Dim statusMsg As String = " ... skipped since m_InstrumentsToSkip contains " & m_xml_instrument_Name
                If ProcSettings.TraceMode Then ShowTraceMessage(statusMsg)
                m_Logger.PostEntry(statusMsg, ILogger.logMsgType.logNormal, LOG_LOCAL_ONLY)
                UpdateInstrumentsToSkip(m_xml_instrument_Name)
                Return False

            ElseIf xmlRslt = IXMLValidateStatus.XmlValidateStatus.XML_WAIT_FOR_FILES Then
                Return False

            ElseIf xmlRslt = IXMLValidateStatus.XmlValidateStatus.XML_VALIDATE_SIZE_CHANGED Then
                ' Size changed; Do not move the XML file
                Return False

            ElseIf xmlRslt = IXMLValidateStatus.XmlValidateStatus.XML_VALIDATE_NO_DATA Then
                moveLocPath = MoveXmlFile(triggerFile, failureFolder)

                Dim statusMsg As String = "Dataset " & myDataXMLValidation.DatasetName & " not found at " & myDataXMLValidation.SourcePath
                If ProcSettings.TraceMode Then ShowTraceMessage(statusMsg)
                m_Logger.PostEntry(statusMsg, ILogger.logMsgType.logWarning, LOG_DATABASE)
                mail_msg = "Operator: " & m_xml_operator_Name & ControlChars.NewLine
                mail_msg &= "The dataset data is not available for capture and was not added to DMS for dataset: " & ControlChars.NewLine & moveLocPath & ControlChars.NewLine

                If String.IsNullOrEmpty(myDataXMLValidation.ErrorMessage) Then
                    mail_msg &= "Check the log for details.  " & ControlChars.NewLine
                Else
                    mail_msg &= myDataXMLValidation.ErrorMessage & ControlChars.NewLine
                End If

                mail_msg &= "Dataset not found in following location: " + m_xml_dataset_path

                mDatabaseErrorMsg = "The dataset data is not available for capture"
                rslt = mDataImportTask.GetDbErrorSolution(mDatabaseErrorMsg)
                If Not rslt Then
                    mDatabaseErrorMsg = String.Empty
                End If
                CreateMail(mail_msg, m_xml_operator_email, " - Dataset not found.")
                Return False
            ElseIf xmlRslt = IXMLValidateStatus.XmlValidateStatus.XML_VALIDATE_TRIGGER_FILE_MISSING Then
                ' The file is now missing; silently move on
                Return False

            Else
                ' xmlRslt is one of the following:
                ' We'll return "True" below

                ' XML_VALIDATE_SUCCESS
                ' XML_VALIDATE_NO_CHECK
                ' XML_VALIDATE_CONTINUE
                ' XML_VALIDATE_SKIP_INSTRUMENT

            End If
        Catch ex As Exception
            Dim errMsg = "Error validating Xml Data file, file " & triggerFile.FullName
            If ProcSettings.TraceMode Then ShowTraceMessage(errMsg)
            m_Logger.PostError(errMsg, ex, LOG_DATABASE)
            Return False
        End Try

        Return True

    End Function
End Class