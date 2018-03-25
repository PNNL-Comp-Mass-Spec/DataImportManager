﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using PRISM;
using PRISM.Logging;

namespace DataImportManager
{
    // ReSharper disable once InconsistentNaming
    internal class clsProcessXmlTriggerFile : clsLoggerBase
    {

        #region "Constants and Structs"

        private const string CHECK_THE_LOG_FOR_DETAILS = "Check the log for details";

        public struct XmlProcSettingsType
        {
            public int DebugLevel;
            public bool IgnoreInstrumentSourceErrors;
            public bool PreviewMode;
            public bool TraceMode;
            public string FailureFolder;
            public string SuccessFolder;
        }

        #endregion

        #region "Properties"

        public XmlProcSettingsType ProcSettings { get; set; }

        /// <summary>
        /// Mail message(s) that need to be sent
        /// </summary>
        public ConcurrentDictionary<string, ConcurrentBag<clsQueuedMail>> QueuedMail => mQueuedMail;

        #endregion

        #region "Member Variables"

        private readonly clsMgrSettings mMgrSettings;

        private readonly ConcurrentDictionary<string, int> mInstrumentsToSkip;

        // ReSharper disable once InconsistentNaming
        private readonly DMSInfoCache mDMSInfoCache;

        private clsDataImportTask mDataImportTask;

        private string mDatabaseErrorMsg;

        private bool mSecondaryLogonServiceChecked;

        private string mXmlOperatorName = string.Empty;

        private string mXmlOperatorEmail = string.Empty;

        /// <summary>
        /// Path to the dataset on the instrument
        /// </summary>
        private string mXmlDatasetPath = string.Empty;

        private string mXmlInstrumentName = string.Empty;

        private readonly ConcurrentDictionary<string, ConcurrentBag<clsQueuedMail>> mQueuedMail;

        #endregion

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="mgrSettings"></param>
        /// <param name="instrumentsToSkip"></param>
        /// <param name="infoCache"></param>
        /// <param name="udtSettings"></param>
        /// <remarks></remarks>
        public clsProcessXmlTriggerFile(
            clsMgrSettings mgrSettings,
            ConcurrentDictionary<string, int> instrumentsToSkip,
            DMSInfoCache infoCache,
            XmlProcSettingsType udtSettings)
        {
            mMgrSettings = mgrSettings;
            mInstrumentsToSkip = instrumentsToSkip;
            ProcSettings = udtSettings;

            mDMSInfoCache = infoCache;

            mQueuedMail = new ConcurrentDictionary<string, ConcurrentBag<clsQueuedMail>>();
        }

        private void CacheMail(List<clsValidationError> validationErrors, string addnlRecipient, string subjectAppend)
        {
            var enableEmail = mMgrSettings.GetParam("enableemail", false);
            if (!enableEmail)
            {
                return;
            }

            try
            {
                var mailRecipients = mMgrSettings.GetParam("to");
                var mailRecipientsList = mailRecipients.Split(';').Distinct().ToList();

                // Possibly update the e-mail address for addnlRecipient
                if (!string.IsNullOrEmpty(addnlRecipient) && !mailRecipientsList.Contains(addnlRecipient))
                {
                    mailRecipients += ";" + addnlRecipient;
                }

                // Define the Subject
                string mailSubject;
                if (string.IsNullOrEmpty(subjectAppend))
                {
                    // Data Import Manager
                    mailSubject = mMgrSettings.GetParam("subject");
                }
                else
                {
                    // Data Import Manager - Appended Info
                    mailSubject = mMgrSettings.GetParam("subject") + subjectAppend;
                }

                // Store the message and metadata
                var messageToQueue = new clsQueuedMail(mXmlOperatorName, mailRecipients, mailSubject, validationErrors);
                if (!string.IsNullOrEmpty(mDatabaseErrorMsg))
                {
                    messageToQueue.DatabaseErrorMsg = mDatabaseErrorMsg;
                }

                messageToQueue.InstrumentDatasetPath = mXmlDatasetPath;

                // Queue the message
                if (mQueuedMail.TryGetValue(mailRecipients, out var existingQueuedMessages))
                {
                    existingQueuedMessages.Add(messageToQueue);
                }
                else
                {
                    var newQueuedMessages = new ConcurrentBag<clsQueuedMail>
                    {
                        messageToQueue
                    };

                    if (!mQueuedMail.TryAdd(mailRecipients, newQueuedMessages))
                    {
                        if (mQueuedMail.TryGetValue(mailRecipients, out existingQueuedMessages))
                        {
                            existingQueuedMessages.Add(messageToQueue);
                        }

                    }

                }

            }
            catch (Exception ex)
            {
                LogError("Error sending email message", ex);
            }

        }

        /// <summary>
        /// Returns a string with the path to the log file, assuming the file can be accessed with \\ComputerName\DMS_Programs\ProgramFolder\Logs\LogFileName.txt
        /// </summary>
        /// <returns></returns>
        /// <remarks></remarks>
        private string GetLogFileSharePath()
        {
            var logFileName = mMgrSettings.GetParam("logfilename");
            return GetLogFileSharePath(logFileName);
        }

        /// <summary>
        /// Returns a string with the path to the log file, assuming the file can be accessed with \\ComputerName\DMS_Programs\ProgramFolder\Logs\LogFileName.txt
        /// </summary>
        /// <param name="logFileName">Name of the current log file</param>
        /// <returns></returns>
        /// <remarks></remarks>
        public static string GetLogFileSharePath(string logFileName)
        {
            string logDirPath;

            var exeDirectoryPath = clsGlobal.GetExeDirectoryPath();

            if (string.IsNullOrEmpty(logFileName))
            {
                logDirPath = Path.Combine(exeDirectoryPath, "DataImportManager");
            }
            else
            {
                logDirPath = Path.Combine(exeDirectoryPath, logFileName);
            }

            // logFilePath should look like this:
            //    DataImportManager\Logs\DataImportManager

            // Prepend the computer name and share name, giving a string similar to:
            // \\proto-3\DMS_Programs\DataImportManager\Logs\DataImportManager
            var logFilePath = @"\\" + clsGlobal.GetHostName() + @"\DMS_Programs\" + logDirPath +
                              "_" + DateTime.Now.ToString("MM-dd-yyyy") + ".txt";

            return logFilePath;
        }

        /// <summary>
        /// Validate the XML trigger file, then send it to the database using mDataImportTask.PostTask
        /// </summary>
        /// <param name="triggerFile"></param>
        /// <returns>True if success, false if an error</returns>
        public bool ProcessFile(FileInfo triggerFile)
        {
            mDatabaseErrorMsg = string.Empty;

            var statusMsg = "Starting data import task for dataset: " + triggerFile.FullName;
            if (ProcSettings.TraceMode)
            {
                Console.WriteLine();
                Console.WriteLine("-------------------------------------------");
            }

            LogMessage(statusMsg);

            if (!ValidateXmlFileMain(triggerFile))
            {
                if (mSecondaryLogonServiceChecked)
                {
                    return false;
                }

                mSecondaryLogonServiceChecked = true;

                // Check the status of the Secondary Logon service
                var sc = new ServiceController("seclogon");
                if (sc.Status == ServiceControllerStatus.Running)
                {
                    return false;
                }

                clsMainProcess.LogErrorToDatabase("The Secondary Logon service is not running; this is required to access files on Bionet");
                try
                {
                    // Try to start it
                    LogMessage("Attempting to start the Secondary Logon service");

                    sc.Start();

                    ConsoleMsgUtils.SleepSeconds(3);

                    statusMsg = "Successfully started the Secondary Logon service (normally should be running, but found to be stopped)";
                    LogWarning(statusMsg, true);

                    // Now that the service is running, try the validation one more time
                    if (!ValidateXmlFileMain(triggerFile))
                    {
                        return false;
                    }

                }
                catch (Exception ex)
                {
                    LogWarning("Unable to start the Secondary Logon service: " + ex.Message);
                    return false;
                }

            }

            if (!triggerFile.Exists)
            {
                LogWarning("XML file no longer exists; cannot import: " + triggerFile.FullName);
                return false;
            }

            if (ProcSettings.DebugLevel >= 2)
            {
                LogMessage("Posting Dataset XML file to database: " + triggerFile.Name);
            }

            // Open a new database connection
            // Doing this now due to database timeouts that were seen when using mDMSInfoCache.DBConnection
            var dbConnection = mDMSInfoCache.GetNewDbConnection();

            // Create the object that will import the Data record
            //
            mDataImportTask = new clsDataImportTask(mMgrSettings, dbConnection)
            {
                TraceMode = ProcSettings.TraceMode,
                PreviewMode = ProcSettings.PreviewMode
            };

            mDatabaseErrorMsg = string.Empty;
            var success = mDataImportTask.PostTask(triggerFile);

            mDatabaseErrorMsg = mDataImportTask.DatabaseErrorMessage;

            if (mDatabaseErrorMsg.ToLower().Contains("timeout expired."))
            {
                // Log the error and leave the file for another attempt
                clsMainProcess.LogErrorToDatabase("Encountered database timeout error for dataset: " + triggerFile.FullName);
                return false;
            }

            if (success)
            {
                MoveXmlFile(triggerFile, ProcSettings.SuccessFolder);
                LogMessage("Completed Data import task for dataset: " + triggerFile.FullName);
                return true;
            }

            // Look for:
            // Transaction (Process ID 67) was deadlocked on lock resources with another process and has been chosen as the deadlock victim. Rerun the transaction
            if (mDataImportTask.PostTaskErrorMessage.ToLower().Contains("deadlocked"))
            {
                // Log the error and leave the file for another attempt
                statusMsg = "Deadlock encountered";
                LogError(statusMsg + ": " + triggerFile.Name);
                return false;
            }

            // Look for:
            // The current transaction cannot be committed and cannot support operations that write to the log file. Roll back the transaction
            if (mDataImportTask.PostTaskErrorMessage.ToLower().Contains("current transaction cannot be committed"))
            {
                // Log the error and leave the file for another attempt
                statusMsg = "Transaction commit error";
                LogError(statusMsg + ": " + triggerFile.Name);
                return false;
            }

            BaseLogger.LogLevels messageType;

            var moveLocPath = MoveXmlFile(triggerFile, ProcSettings.FailureFolder);
            statusMsg = "Error posting xml file to database: " + mDataImportTask.PostTaskErrorMessage;

            if (mDataImportTask.PostTaskErrorMessage.ToLower().Contains("since already in database"))
            {
                messageType = BaseLogger.LogLevels.WARN;
                LogWarning(statusMsg + ". See: " + moveLocPath);
            }
            else
            {
                messageType = BaseLogger.LogLevels.ERROR;
                clsMainProcess.LogErrorToDatabase(statusMsg + ". View details in log at " + GetLogFileSharePath() + " for: " + moveLocPath);
            }

            var validationErrors = new List<clsValidationError>();
            var newError = new clsValidationError("XML trigger file problem", moveLocPath);

            string msgTypeString;
            if (messageType == BaseLogger.LogLevels.ERROR)
            {
                msgTypeString = "Error";
            }
            else
            {
                msgTypeString = "Warning";
            }

            if (string.IsNullOrWhiteSpace(mDataImportTask.PostTaskErrorMessage))
            {
                newError.AdditionalInfo = msgTypeString + ": " + CHECK_THE_LOG_FOR_DETAILS;
            }
            else
            {
                newError.AdditionalInfo = msgTypeString + ": " + mDataImportTask.PostTaskErrorMessage;
            }

            validationErrors.Add(newError);

            // Check whether there is a suggested solution in table T_DIM_Error_Solution for the error
            var errorSolution = mDMSInfoCache.GetDbErrorSolution(mDatabaseErrorMsg);
            if (!string.IsNullOrWhiteSpace(errorSolution))
            {
                // Store the solution in the database error message variable so that it gets included in the message body
                mDatabaseErrorMsg = errorSolution;
            }

            // Send an e-mail; subject will be "Data Import Manager - Database error." or "Data Import Manager - Database warning."
            CacheMail(validationErrors, mXmlOperatorEmail, " - Database " + msgTypeString.ToLower() + ".");
            return false;
        }

        /// <summary>
        /// Move a trigger file to the target folder
        /// </summary>
        /// <param name="triggerFile"></param>
        /// <param name="moveFolder"></param>
        /// <returns>New path of the trigger file</returns>
        /// <remarks></remarks>
        private string MoveXmlFile(FileInfo triggerFile, string moveFolder)
        {
            try
            {
                if (!triggerFile.Exists)
                {
                    return string.Empty;
                }

                if (!Directory.Exists(moveFolder))
                {
                    if (ProcSettings.TraceMode)
                    {
                        ShowTraceMessage("Creating target folder: " + moveFolder);
                    }

                    Directory.CreateDirectory(moveFolder);
                }

                var targetFilePath = Path.Combine(moveFolder, triggerFile.Name);
                if (ProcSettings.TraceMode)
                {
                    ShowTraceMessage("Instantiating file info var for " + targetFilePath);
                }

                var xmlFileNewLoc = new FileInfo(targetFilePath);
                if (xmlFileNewLoc.Exists)
                {
                    if (ProcSettings.PreviewMode)
                    {
                        ShowTraceMessage("Preview: delete target file: " + xmlFileNewLoc.FullName);
                    }
                    else
                    {
                        if (ProcSettings.TraceMode)
                        {
                            ShowTraceMessage("Deleting target file: " + xmlFileNewLoc.FullName);
                        }

                        xmlFileNewLoc.Delete();
                    }

                }

                var movePaths =
                    "XML file " + Environment.NewLine +
                    "  from " + triggerFile.FullName + Environment.NewLine +
                    "  to   " + xmlFileNewLoc.DirectoryName;

                if (ProcSettings.PreviewMode)
                {
                    ShowTraceMessage("Preview: move " + movePaths);
                }
                else
                {
                    if (ProcSettings.TraceMode)
                    {
                        ShowTraceMessage("Moving " + movePaths);
                    }

                    triggerFile.MoveTo(xmlFileNewLoc.FullName);
                }

                return xmlFileNewLoc.FullName;
            }
            catch (Exception ex)
            {
                LogError("Exception in MoveXmlFile", ex);
                return string.Empty;
            }

        }

        /// <summary>
        /// Adds or updates instrumentName in m_InstrumentsToSkip
        /// </summary>
        /// <param name="instrumentName"></param>
        /// <remarks></remarks>
        private void UpdateInstrumentsToSkip(string instrumentName)
        {
            // Look for the instrument in m_InstrumentsToSkip
            if (mInstrumentsToSkip.TryGetValue(instrumentName, out var datasetsSkipped))
            {
                mInstrumentsToSkip[instrumentName] = datasetsSkipped + 1;
                return;
            }

            // Instrument not found; add it
            if (!mInstrumentsToSkip.TryAdd(instrumentName, 1))
            {
                // Instrument add failed; try again to get the datasets skipped value
                if (mInstrumentsToSkip.TryGetValue(instrumentName, out var datasetsSkippedRetry))
                {
                    mInstrumentsToSkip[instrumentName] = datasetsSkippedRetry + 1;
                }
            }

        }

        /// <summary>
        /// Process the specified XML file
        /// </summary>
        /// <param name="triggerFile">XML file to process</param>
        /// <returns>True if XML file is valid and dataset is ready for import; otherwise false</returns>
        /// <remarks>
        /// PerformValidation in clsXMLTimeValidation will monitor the dataset file (or dataset folder)
        /// to make sure the file size (folder size) remains unchanged over 30 seconds (see VerifyConstantFileSize and VerifyConstantFolderSize)
        /// </remarks>
        private bool ValidateXmlFileMain(FileInfo triggerFile)
        {
            try
            {
                var timeValFolder = mMgrSettings.GetParam("timevalidationfolder");
                string moveLocPath;
                var failureFolder = mMgrSettings.GetParam("failurefolder");

                var myDataXmlValidation = new clsXMLTimeValidation(mMgrSettings, mInstrumentsToSkip, mDMSInfoCache, ProcSettings)
                {
                    TraceMode = ProcSettings.TraceMode
                };

                var xmlRslt = myDataXmlValidation.ValidateXmlFile(triggerFile);

                mXmlOperatorName = myDataXmlValidation.OperatorName;
                mXmlOperatorEmail = myDataXmlValidation.OperatorEMail;
                mXmlDatasetPath = myDataXmlValidation.DatasetPath;
                mXmlInstrumentName = myDataXmlValidation.InstrumentName;

                if (xmlRslt == clsXMLTimeValidation.XmlValidateStatus.XML_VALIDATE_NO_OPERATOR)
                {
                    moveLocPath = MoveXmlFile(triggerFile, failureFolder);

                    LogWarning("Undefined Operator in " + moveLocPath, true);

                    var validationErrors = new List<clsValidationError>();

                    if (string.IsNullOrWhiteSpace(mXmlOperatorName))
                    {
                        validationErrors.Add(new clsValidationError("Operator name not listed in the XML file", string.Empty));
                    }
                    else
                    {
                        validationErrors.Add(new clsValidationError("Operator name not defined in DMS", mXmlOperatorName));
                    }

                    validationErrors.Add(new clsValidationError("Dataset trigger file path", moveLocPath));

                    mDatabaseErrorMsg = "Operator payroll number/HID was blank";
                    var errorSolution = mDMSInfoCache.GetDbErrorSolution(mDatabaseErrorMsg);
                    if (string.IsNullOrWhiteSpace(errorSolution))
                    {
                        mDatabaseErrorMsg = string.Empty;
                    }
                    else
                    {
                        mDatabaseErrorMsg = errorSolution;
                    }

                    CacheMail(validationErrors, mXmlOperatorEmail, " - Operator not defined.");
                    return false;
                }
                else if (xmlRslt == clsXMLTimeValidation.XmlValidateStatus.XML_VALIDATE_FAILED)
                {
                    moveLocPath = MoveXmlFile(triggerFile, timeValFolder);

                    LogWarning("XML Time validation error, file " + moveLocPath);
                    clsMainProcess.LogErrorToDatabase("Time validation error. View details in log at " + GetLogFileSharePath() + " for: " + moveLocPath);

                    var validationErrors = new List<clsValidationError>
                    {
                        new clsValidationError("Time validation error", moveLocPath)
                    };
                    CacheMail(validationErrors, mXmlOperatorEmail, " - Time validation error.");
                    return false;
                }
                else if (xmlRslt == clsXMLTimeValidation.XmlValidateStatus.XML_VALIDATE_ENCOUNTERED_ERROR)
                {
                    moveLocPath = MoveXmlFile(triggerFile, failureFolder);

                    LogWarning("An error was encountered during the validation process, file " + moveLocPath, true);

                    var validationErrors = new List<clsValidationError>
                    {
                        new clsValidationError("XML error encountered during validation process", moveLocPath)
                    };
                    CacheMail(validationErrors, mXmlOperatorEmail, " - XML validation error.");
                    return false;
                }
                else if (xmlRslt == clsXMLTimeValidation.XmlValidateStatus.XML_VALIDATE_ENCOUNTERED_LOGON_FAILURE)
                {
                    // Logon failure; Do not move the XML file
                    return false;
                }
                else if (xmlRslt == clsXMLTimeValidation.XmlValidateStatus.XML_VALIDATE_ENCOUNTERED_NETWORK_ERROR)
                {
                    // Network error; Do not move the XML file
                    // Furthermore, do not process any more .XML files for this instrument
                    UpdateInstrumentsToSkip(mXmlInstrumentName);
                    return false;
                }
                else if (xmlRslt == clsXMLTimeValidation.XmlValidateStatus.XML_VALIDATE_SKIP_INSTRUMENT)
                {
                    LogMessage(" ... skipped since m_InstrumentsToSkip contains " + mXmlInstrumentName);
                    UpdateInstrumentsToSkip(mXmlInstrumentName);
                    return false;
                }
                else if (xmlRslt == clsXMLTimeValidation.XmlValidateStatus.XML_WAIT_FOR_FILES)
                {
                    return false;
                }
                else if (xmlRslt == clsXMLTimeValidation.XmlValidateStatus.XML_VALIDATE_SIZE_CHANGED)
                {
                    // Size changed; Do not move the XML file
                    return false;
                }
                else if (xmlRslt == clsXMLTimeValidation.XmlValidateStatus.XML_VALIDATE_NO_DATA)
                {
                    moveLocPath = MoveXmlFile(triggerFile, failureFolder);

                    LogWarning("Dataset " + myDataXmlValidation.DatasetName + " not found at " + myDataXmlValidation.SourcePath, true);

                    var validationErrors = new List<clsValidationError>();

                    var newError = new clsValidationError("Dataset not found on the instrument", moveLocPath);
                    if (string.IsNullOrEmpty(myDataXmlValidation.ErrorMessage))
                    {
                        newError.AdditionalInfo = string.Empty;
                    }
                    else
                    {
                        newError.AdditionalInfo = myDataXmlValidation.ErrorMessage;
                    }

                    validationErrors.Add(newError);

                    mDatabaseErrorMsg = "The dataset data is not available for capture";
                    var errorSolution = mDMSInfoCache.GetDbErrorSolution(mDatabaseErrorMsg);
                    if (string.IsNullOrWhiteSpace(errorSolution))
                    {
                        mDatabaseErrorMsg = string.Empty;
                    }
                    else
                    {
                        mDatabaseErrorMsg = errorSolution;
                    }

                    CacheMail(validationErrors, mXmlOperatorEmail, " - Dataset not found.");

                    return false;
                }
                else if (xmlRslt == clsXMLTimeValidation.XmlValidateStatus.XML_VALIDATE_TRIGGER_FILE_MISSING)
                {
                    // The file is now missing; silently move on
                    return false;
                }
                else
                {
                    // xmlRslt is one of the following:
                    // We'll return "True" below
                    // XML_VALIDATE_SUCCESS
                    // XML_VALIDATE_NO_CHECK
                    // XML_VALIDATE_CONTINUE
                    // XML_VALIDATE_SKIP_INSTRUMENT
                }

            }
            catch (Exception ex)
            {
                clsMainProcess.LogErrorToDatabase("Error validating Xml Data file, file " + triggerFile.FullName, ex);
                return false;
            }

            return true;
        }

        private void ShowTraceMessage(string message)
        {
            clsMainProcess.ShowTraceMessage(message);
        }
    }
}