﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Net.Mail;
using System.Text;
using System.Threading.Tasks;
using System.Xml.XPath;
using JetBrains.Annotations;
using PRISM;
using PRISM.AppSettings;
using PRISM.Logging;
using PRISMDatabaseUtils;
using PRISMDatabaseUtils.AppSettings;

namespace DataImportManager
{
    // ReSharper disable once InconsistentNaming
    public class MainProcess : LoggerBase
    {
        // Ignore Spelling: Proteinseqs, smtp, spam, yyyy-MM, yyyy-MM-dd hh:mm:ss tt

        private const string DEFAULT_BASE_LOGFILE_NAME = @"Logs\DataImportManager";

        internal const string DEVELOPER_COMPUTER_NAME = "WE43320";

        private const int MAX_ERROR_COUNT = 4;

        private const string MGR_PARAM_MGR_ACTIVE = "MgrActive";

        public enum CloseOutType
        {
            CLOSEOUT_SUCCESS = 0,
            CLOSEOUT_FAILED = 1
        }

        private MgrSettingsDB mMgrSettings;

        private bool mConfigChanged;

        private FileSystemWatcher mFileWatcher;

        private bool mMgrActive = true;

        /// <summary>
        /// Debug level
        /// </summary>
        /// <remarks>Higher values lead to more log messages</remarks>
        private int mDebugLevel;

        /// <summary>
        /// Keys in this dictionary are instrument names
        /// Values are the number of datasets skipped for the given instrument
        /// </summary>
        private readonly ConcurrentDictionary<string, int> mInstrumentsToSkip;

        private int mFailureCount;
        /// <summary>
        /// Keys in this dictionary are semicolon separated e-mail addresses
        /// Values are mail messages to send
        /// </summary>
        private readonly ConcurrentDictionary<string, ConcurrentBag<QueuedMail>> mQueuedMail;

        public bool IgnoreInstrumentSourceErrors { get; set; }
        public bool MailDisabled { get; set; }
        public bool PreviewMode { get; set; }
        public bool TraceMode { get; set; }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="traceMode"></param>
        public MainProcess(bool traceMode)
        {
            TraceMode = traceMode;

            mInstrumentsToSkip = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            mQueuedMail = new ConcurrentDictionary<string, ConcurrentBag<QueuedMail>>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Load the manager settings
        /// </summary>
        public bool InitMgr()
        {
            var defaultModuleName = "DataImportManager: " + Global.GetHostName();

            try
            {
                // Define the default logging info
                // This will get updated below
                LogTools.CreateFileLogger(DEFAULT_BASE_LOGFILE_NAME);

                // Create a database logger connected to the Manager_Control database
                // Once the initial parameters have been successfully read,
                // we update the dbLogger to use the connection string read from the Manager Control DB
                string dmsConnectionString;

                // Open DataImportManager.exe.config to look for setting MgrCnfgDbConnectStr, so we know which server to log to by default
                var dmsConnectionStringFromConfig = GetXmlConfigDefaultConnectionString();

                if (string.IsNullOrWhiteSpace(dmsConnectionStringFromConfig))
                {
                    // Use the hard-coded default that points to Proteinseqs
                    dmsConnectionString = Properties.Settings.Default.MgrCnfgDbConnectStr;
                }
                else
                {
                    // Use the connection string from DataImportManager.exe.config
                    dmsConnectionString = dmsConnectionStringFromConfig;
                }

                var managerName = "Data_Import_Manager_" + Global.GetHostName();

                var dbLoggerConnectionString = DbToolsFactory.AddApplicationNameToConnectionString(dmsConnectionString, managerName);

                ShowTrace("Instantiate a DbLogger using " + dbLoggerConnectionString);

                // ReSharper disable once ConditionIsAlwaysTrueOrFalse
                LogTools.CreateDbLogger(dbLoggerConnectionString, managerName, false);

                mMgrSettings = new MgrSettingsDB
                {
                    TraceMode = TraceMode
                };
                RegisterEvents(mMgrSettings);
                mMgrSettings.CriticalErrorEvent += ErrorEventHandler;

                var localSettings = GetLocalManagerSettings();

                Console.WriteLine();
                mMgrSettings.ValidatePgPass(localSettings);

                var success = mMgrSettings.LoadSettings(localSettings, true);

                if (!success)
                {
                    if (string.Equals(mMgrSettings.ErrMsg, MgrSettings.DEACTIVATED_LOCALLY))
                        throw new ApplicationException(MgrSettings.DEACTIVATED_LOCALLY);

                    throw new ApplicationException("Unable to initialize manager settings class: " + mMgrSettings.ErrMsg);
                }

                var mgrActiveLocal = mMgrSettings.GetParam(MgrSettings.MGR_PARAM_MGR_ACTIVE_LOCAL, false);

                if (!mgrActiveLocal)
                {
                    ShowTrace(MgrSettings.MGR_PARAM_MGR_ACTIVE_LOCAL + " is false in the .exe.config file");
                    return false;
                }

                var mgrActive = mMgrSettings.GetParam(MGR_PARAM_MGR_ACTIVE, false);

                if (!mgrActive)
                {
                    ShowTrace("Manager parameter " + MGR_PARAM_MGR_ACTIVE + " is false");
                    return false;
                }
            }
            catch (Exception ex)
            {
                throw new Exception("InitMgr, " + ex.Message, ex);
            }

            var connectionString = mMgrSettings.GetParam("ConnectionString");
            var connectionStringToUse = DbToolsFactory.AddApplicationNameToConnectionString(connectionString, mMgrSettings.ManagerName);

            var logFileBaseName = mMgrSettings.GetParam("LogFileName");

            try
            {
                // Load initial settings
                mMgrActive = mMgrSettings.GetParam(MGR_PARAM_MGR_ACTIVE, false);
                mDebugLevel = mMgrSettings.GetParam("DebugLevel", 2);

                // Create the object that will manage the logging
                var moduleName = mMgrSettings.GetParam("ModuleName", defaultModuleName);

                LogTools.CreateFileLogger(logFileBaseName);
                LogTools.CreateDbLogger(connectionStringToUse, moduleName);

                // Write the initial log and status entries
                var appVersion = AppUtils.GetEntryOrExecutingAssembly().GetName().Version;
                LogTools.WriteLog(LogTools.LoggerTypes.LogFile, BaseLogger.LogLevels.INFO,
                    "===== Started Data Import Manager V" + appVersion + " =====");
            }
            catch (Exception ex)
            {
                throw new Exception("InitMgr, " + ex.Message, ex);
            }

            var exeFile = new FileInfo(Global.GetExePath());

            // Set up the FileWatcher to detect setup file changes
            mFileWatcher = new FileSystemWatcher();
            mFileWatcher.BeginInit();
            mFileWatcher.Path = exeFile.DirectoryName;
            mFileWatcher.IncludeSubdirectories = false;
            mFileWatcher.Filter = mMgrSettings.GetParam("ConfigFileName");
            mFileWatcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size;
            mFileWatcher.EndInit();
            mFileWatcher.EnableRaisingEvents = true;
            mFileWatcher.Changed += FileWatcher_Changed;

            // Get the debug level
            mDebugLevel = mMgrSettings.GetParam("DebugLevel", 2);
            return true;
        }

        /// <summary>
        /// Append text to the string builder, using the given format string and arguments
        /// </summary>
        /// <param name="sb"></param>
        /// <param name="format">Message format string</param>
        /// <param name="args">Arguments to use with formatString</param>
        [StringFormatMethod("format")]
        private void AppendLine(StringBuilder sb, string format, params object[] args)
        {
            sb.AppendFormat(format, args);
            sb.AppendLine();
        }

        /// <summary>
        /// Look for new XML files to process
        /// </summary>
        /// <remarks>Returns true even if no XML files are found</remarks>
        /// <returns>True if success, false if an error</returns>
        public bool DoImport()
        {
            try
            {
                // Verify an error hasn't left the system in an odd state
                if (Global.DetectStatusFlagFile())
                {
                    LogWarning("Flag file exists - auto-deleting it, then closing program");
                    Global.DeleteStatusFlagFile();

                    if (!Global.GetHostName().Equals(DEVELOPER_COMPUTER_NAME, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                if (WindowsUpdateStatus.ServerUpdatesArePending(DateTime.Now, out var pendingWindowsUpdateMessage))
                {
                    var warnMessage = "Monthly windows updates are pending; aborting check for new XML trigger files: " + pendingWindowsUpdateMessage;

                    if (TraceMode)
                    {
                        ShowTraceMessage(warnMessage);
                    }
                    else
                    {
                        Console.WriteLine(warnMessage);
                    }

                    return true;
                }

                // Check to see if machine settings have changed
                if (mConfigChanged)
                {
                    ShowTrace("Loading manager settings from the database");

                    mConfigChanged = false;

                    var localSettings = GetLocalManagerSettings();

                    var success = mMgrSettings.LoadSettings(localSettings, true);

                    if (!success)
                    {
                        if (!string.IsNullOrEmpty(mMgrSettings.ErrMsg))
                        {
                            // Report the error
                            LogWarning(mMgrSettings.ErrMsg);
                        }
                        else
                        {
                            // Unknown problem reading config file
                            LogError("Unknown error re-reading config file");
                        }

                        return false;
                    }

                    mFileWatcher.EnableRaisingEvents = true;
                }

                // Check to see if excessive consecutive failures have occurred
                if (mFailureCount > MAX_ERROR_COUNT)
                {
                    // More than MAX_ERROR_COUNT consecutive failures; there must be a generic problem, so exit
                    LogError("Excessive task failures, exiting");
                    return false;
                }

                // Check to see if the manager is still active
                if (!mMgrActive)
                {
                    LogMessage("Manager inactive");
                    return false;
                }

                var connectionString = mMgrSettings.GetParam("ConnectionString");
                var connectionStringToUse = DbToolsFactory.AddApplicationNameToConnectionString(connectionString, mMgrSettings.ManagerName);

                DMSInfoCache infoCache;

                try
                {
                    infoCache = new DMSInfoCache(connectionStringToUse, TraceMode);
                }
                catch (Exception ex)
                {
                    LogError("Unable to connect to the database using " + connectionStringToUse, ex);
                    return false;
                }

                // Check to see if there are any data import files ready
                DoDataImportTask(infoCache);
                AppUtils.SleepMilliseconds(250);

                return true;
            }
            catch (Exception ex)
            {
                LogErrorToDatabase("Exception in MainProcess.DoImport()", ex);
                return false;
            }
        }

        /// <summary>
        /// Return an empty string if value is 1; otherwise return "s"
        /// </summary>
        /// <param name="value"></param>
        private static string CheckPlural(int value)
        {
            return value == 1 ? string.Empty : "s";
        }

        private void DoDataImportTask(DMSInfoCache infoCache)
        {
            var delBadXmlFilesDays = Math.Max(7, mMgrSettings.GetParam("DeleteBadXmlFiles", 180));
            var delGoodXmlFilesDays = Math.Max(7, mMgrSettings.GetParam("DeleteGoodXmlFiles", 30));
            var successDirectory = mMgrSettings.GetParam("SuccessFolder");
            var failureDirectory = mMgrSettings.GetParam("FailureFolder");

            try
            {
                var result = ScanXferDirectory(out var xmlFilesToImport);

                if (result == CloseOutType.CLOSEOUT_SUCCESS && xmlFilesToImport.Count > 0)
                {
                    // Set status file for control of future runs
                    Global.CreateStatusFlagFile();

                    // Add a delay
                    var importDelayText = mMgrSettings.GetParam("ImportDelay");

                    if (!int.TryParse(importDelayText, out var importDelay))
                    {
                        var statusMsg = "Manager parameter ImportDelay was not numeric: " + importDelayText;
                        LogMessage(statusMsg);
                        importDelay = 2;
                    }

                    if (Global.GetHostName().Equals(DEVELOPER_COMPUTER_NAME, StringComparison.OrdinalIgnoreCase))
                    {
                        // Console.WriteLine("Changing importDelay from " & importDelay & " seconds to 1 second since host is {0}", DEVELOPER_COMPUTER_NAME)
                        importDelay = 1;
                    }
                    else if (PreviewMode)
                    {
                        // Console.WriteLine("Changing importDelay from " & importDelay & " seconds to 1 second since PreviewMode is enabled")
                        importDelay = 1;
                    }

                    ShowTrace(string.Format("ImportDelay, sleep for {0} second{1}", importDelay, CheckPlural(importDelay)));
                    ConsoleMsgUtils.SleepSeconds(importDelay);

                    // Load information from DMS
                    infoCache.LoadDMSInfo();

                    // Randomize order of files in m_XmlFilesToLoad
                    xmlFilesToImport.Shuffle();
                    ShowTrace(string.Format("Processing {0} XML file{1}", xmlFilesToImport.Count, CheckPlural(xmlFilesToImport.Count)));

                    // Process the files in parallel, in groups of 50 at a time
                    while (xmlFilesToImport.Count > 0)
                    {
                        var currentChunk = GetNextChunk(ref xmlFilesToImport, 50).ToList();

                        // Prevent duplicate entries in T_Storage_Path due to a race condition
                        EnsureInstrumentDataStorageDirectories(currentChunk, infoCache.DBTools);

                        var itemCount = currentChunk.Count;

                        if (itemCount > 1)
                        {
                            LogMessage("Processing " + itemCount + " XML files in parallel");
                        }

                        Parallel.ForEach(currentChunk, (currentFile) => ProcessOneFile(currentFile, successDirectory, failureDirectory, infoCache));
                    }
                }
                else
                {
                    if (mDebugLevel > 4 || TraceMode)
                    {
                        LogDebug("No data files to import");
                    }

                    return;
                }

                // Send any queued mail
                if (mQueuedMail.Count > 0)
                {
                    SendQueuedMail();
                }

                foreach (var kvItem in mInstrumentsToSkip)
                {
                    var message = "Skipped " + kvItem.Value + " dataset";

                    if (kvItem.Value != 1)
                    {
                        message += "s";
                    }

                    message += " for instrument " + kvItem.Key + " due to network errors";
                    LogMessage(message);
                }

                // Remove successful XML files older than x days
                DeleteXmlFiles(successDirectory, delGoodXmlFilesDays);

                // Remove failed XML files older than x days
                DeleteXmlFiles(failureDirectory, delBadXmlFilesDays);

                // If we got to here, closeout the task as a success
                Global.DeleteStatusFlagFile();

                mFailureCount = 0;
                LogMessage("Completed task");
            }
            catch (Exception ex)
            {
                mFailureCount++;
                LogError("Exception in MainProcess.DoDataImportTask", ex);
            }
        }

        /// <summary>
        /// Call procedure get_instrument_storage_path_for_new_datasets on all instruments with multiple trigger files
        /// to avoid a race condition in the database that leads to identical single-use entries in T_Storage_Path
        /// </summary>
        /// <param name="xmlFiles"></param>
        /// <param name="dbTools"></param>
        private void EnsureInstrumentDataStorageDirectories(List<FileInfo> xmlFiles, IDBTools dbTools)
        {
            var counts = GetFileCountsForInstruments(xmlFiles);

            var serverType = DbToolsFactory.GetServerTypeFromConnectionString(dbTools.ConnectStr);

            foreach (var instrument in counts.Where(x => x.Value > 1).Select(x => x.Key))
            {
                try
                {
                    var query = $"SELECT id FROM V_Instrument_List_Export WHERE name = '{instrument}'";

                    var success = dbTools.GetQueryScalar(query, out var queryResult);

                    if (!success)
                    {
                        LogWarning(string.Format("GetQueryScalar returned false for query: {0}", query));
                        continue;
                    }

                    if (queryResult is not int instrumentId)
                    {
                        LogWarning(string.Format("Query did not return an integer: {0}", query));
                        continue;
                    }

                    if (serverType == DbServerTypes.PostgreSQL)
                    {
                        EnsureInstrumentDataStorageDirectoryPostgres(dbTools, instrumentId, instrument);
                        continue;
                    }

                    EnsureInstrumentDataStorageDirectorySqlServer(dbTools, instrumentId, instrument);
                }
                catch (Exception ex)
                {
                    // If an error occurs, it's likely a database communication error, and this is not a critical step, so just exit the method
                    LogError("MainProcess.EnsureInstrumentDataStorageDirectories(), Error ensuring dataset storage directories", ex, true);
                    return;
                }
            }
        }

        private void EnsureInstrumentDataStorageDirectoryPostgres(IDBTools dbTools, int instrumentId, string instrument)
        {
            const string instrumentStorageFunction = "get_instrument_storage_path_for_new_datasets";

            // Prepare to query the function
            // The function will create a new storage path if the auto-defined path does not exist in t_storage_path

            var query = string.Format("SELECT * FROM {0}({1}) AS storage_path_id", instrumentStorageFunction, instrumentId);

            if (PreviewMode)
            {
                ShowTraceMessage(string.Format(
                    "Preview: run query in database {0}: {1}",
                    dbTools.DatabaseName, query));

                return;
            }

            if (TraceMode)
            {
                ShowTraceMessage(string.Format(
                    "Running query in database {0}: {1}",
                    dbTools.DatabaseName, query));
            }

            // Run the query
            var storagePathSuccess = dbTools.GetQueryScalar(query, out var storagePathID);

            if (!storagePathSuccess)
            {
                LogWarning(string.Format("GetQueryScalar returned false for query: {0}", query));
                return;
            }

            if (TraceMode)
            {
                ShowTraceMessage(string.Format(
                    "Function {0} returned storage path ID {1} for instrument {2}",
                    instrumentStorageFunction, storagePathID, instrument));
            }
        }

        private void EnsureInstrumentDataStorageDirectorySqlServer(IDBTools dbTools, int instrumentId, string instrument)
        {
            const string instrumentStorageProcedure = "get_instrument_storage_path_for_new_datasets";

            // Prepare to call the stored procedure
            // The procedure will create a new storage path if the auto-defined path does not exist in T_Storage_Path

            var command = dbTools.CreateCommand(instrumentStorageProcedure, CommandType.StoredProcedure);
            command.CommandTimeout = 45;

            dbTools.AddParameter(command, "@Return", SqlType.Int, direction: ParameterDirection.ReturnValue);
            dbTools.AddParameter(command, "@InstrumentID", SqlType.Int, 1, instrumentId);

            if (PreviewMode)
            {
                ShowTraceMessage(string.Format(
                    "Preview: call stored procedure {0} in database {1}",
                    instrumentStorageProcedure, dbTools.DatabaseName));

                return;
            }

            if (TraceMode)
            {
                ShowTraceMessage(string.Format(
                    "Calling stored procedure {0} in database {1}",
                    instrumentStorageProcedure, dbTools.DatabaseName));
            }

            // Execute the stored procedure
            var storagePathID = dbTools.ExecuteSP(command);

            if (TraceMode)
            {
                ShowTraceMessage(string.Format(
                    "Stored procedure {0}: returned storage path ID {1} for instrument {2}",
                    instrumentStorageProcedure, storagePathID, instrument));
            }
        }

        public static Dictionary<string, int> GetFileCountsForInstruments(List<FileInfo> xmlFiles)
        {
            var counts = new Dictionary<string, int>();

            foreach (var file in xmlFiles)
            {
                var instrument = GetInstrumentFromXmlFile(file);

                if (!string.IsNullOrWhiteSpace(instrument))
                {
                    if (!counts.ContainsKey(instrument))
                    {
                        counts.Add(instrument, 0);
                    }

                    counts[instrument]++;
                }
            }

            return counts;
        }

        private static string GetInstrumentFromXmlFile(FileSystemInfo file)
        {
            if (file?.Exists != true)
            {
                return string.Empty;
            }

            try
            {
                var xDoc = new XPathDocument(file.FullName);
                var xNav = xDoc.CreateNavigator();

                var xPathNode = xNav.SelectSingleNode("//Dataset/Parameter[@Name='Instrument Name']/@Value");

                return xPathNode?.IsNode == true ? xPathNode.Value : string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private Dictionary<string, string> GetLocalManagerSettings()
        {
            var defaultSettings = new Dictionary<string, string>
            {
                {MgrSettings.MGR_PARAM_MGR_CFG_DB_CONN_STRING, Properties.Settings.Default.MgrCnfgDbConnectStr},
                {MgrSettings.MGR_PARAM_MGR_ACTIVE_LOCAL, Properties.Settings.Default.MgrActive_Local.ToString()},
                {MgrSettings.MGR_PARAM_MGR_NAME, Properties.Settings.Default.MgrName},
                {MgrSettings.MGR_PARAM_USING_DEFAULTS, Properties.Settings.Default.UsingDefaults.ToString()}
            };

            var mgrExePath = AppUtils.GetAppPath();
            var localSettings = mMgrSettings.LoadMgrSettingsFromFile(mgrExePath + ".config");

            if (localSettings == null)
            {
                localSettings = defaultSettings;
            }
            else
            {
                // Make sure the default settings exist and have valid values
                foreach (var setting in defaultSettings)
                {
                    if (!localSettings.TryGetValue(setting.Key, out var existingValue) ||
                        string.IsNullOrWhiteSpace(existingValue))
                    {
                        localSettings[setting.Key] = setting.Value;
                    }
                }
            }

            return localSettings;
        }

        private string GetLogFileSharePath()
        {
            var logFileName = mMgrSettings.GetParam("LogFileName");
            return ProcessXmlTriggerFile.GetLogFileSharePath(logFileName);
        }

        /// <summary>
        /// Retrieve the next chunk of items from a list
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="sourceList">List of items to retrieve a chunk from; will be updated to remove the items in the returned list</param>
        /// <param name="chunkSize">Number of items to return</param>
        private IEnumerable<T> GetNextChunk<T>(ref List<T> sourceList, int chunkSize)
        {
            if (chunkSize < 1)
            {
                chunkSize = 1;
            }

            if (sourceList.Count < 1)
            {
                return new List<T>();
            }

            IEnumerable<T> nextChunk;

            if (chunkSize >= sourceList.Count)
            {
                nextChunk = sourceList.Take(sourceList.Count).ToList();
                sourceList = new List<T>();
            }
            else
            {
                nextChunk = sourceList.Take(chunkSize).ToList();
                var remainingItems = sourceList.Skip(chunkSize);
                sourceList = remainingItems.ToList();
            }

            return nextChunk;
        }

        /// <summary>
        /// Extract the value MgrCnfgDbConnectStr from DataImportManager.exe.config
        /// </summary>
        private string GetXmlConfigDefaultConnectionString()
        {
            return GetXmlConfigFileSetting("MgrCnfgDbConnectStr");
        }

        /// <summary>
        /// Extract the value for the given setting from DataImportManager.exe.config
        /// </summary>
        /// <remarks>Uses a simple text reader in case the file has malformed XML</remarks>
        /// <returns>Setting value if found, otherwise an empty string</returns>
        private string GetXmlConfigFileSetting(string settingName)
        {
            if (string.IsNullOrWhiteSpace(settingName))
                throw new ArgumentException("Setting name cannot be blank", nameof(settingName));

            var exePath = Global.GetExePath();

            var configFilePaths = new List<string>();

            if (settingName.Equals("MgrCnfgDbConnectStr", StringComparison.OrdinalIgnoreCase) ||
                settingName.Equals("DefaultDMSConnString", StringComparison.OrdinalIgnoreCase))
            {
                configFilePaths.Add(Path.ChangeExtension(exePath, ".exe.db.config"));
            }

            configFilePaths.Add(Global.GetExePath() + ".config");

            var mgrSettings = new MgrSettings();
            RegisterEvents(mgrSettings);

            var valueFound = mgrSettings.GetXmlConfigFileSetting(configFilePaths, settingName, out var settingValue);
            return valueFound ? settingValue : string.Empty;
        }

        public static void LogErrorToDatabase(string message, Exception ex = null)
        {
            LogError(message, ex, true);
        }

        private void ProcessOneFile(FileInfo currentFile, string successDirectory, string failureDirectory, DMSInfoCache infoCache)
        {
            var objRand = new Random();

            // Delay for anywhere between 1 to 15 seconds so that the tasks don't all fire at once
            var waitSeconds = objRand.Next(1, 15);
            ConsoleMsgUtils.SleepSeconds(waitSeconds);

            // Validate the xml file
            var udtSettings = new ProcessXmlTriggerFile.XmlProcSettingsType
            {
                DebugLevel = mDebugLevel,
                IgnoreInstrumentSourceErrors = IgnoreInstrumentSourceErrors,
                PreviewMode = PreviewMode,
                TraceMode = TraceMode,
                FailureDirectory = failureDirectory,
                SuccessDirectory = successDirectory
            };

            var triggerProcessor = new ProcessXmlTriggerFile(mMgrSettings, mInstrumentsToSkip, infoCache, udtSettings);

            triggerProcessor.ProcessFile(currentFile);

            if (triggerProcessor.QueuedMail.Count > 0)
            {
                AddToMailQueue(triggerProcessor.QueuedMail);
            }
        }

        /// <summary>
        /// Add one or more mail messages to mQueuedMail
        /// </summary>
        /// <param name="newQueuedMail"></param>
        private void AddToMailQueue(ConcurrentDictionary<string, ConcurrentBag<QueuedMail>> newQueuedMail)
        {
            foreach (var newQueuedMessage in newQueuedMail)
            {
                var recipients = newQueuedMessage.Key;

                if (mQueuedMail.TryGetValue(recipients, out var queuedMessages))
                {
                    foreach (var msg in newQueuedMessage.Value)
                    {
                        queuedMessages.Add(msg);
                    }

                    continue;
                }

                if (mQueuedMail.TryAdd(recipients, newQueuedMessage.Value))
                    continue;

                if (mQueuedMail.TryGetValue(recipients, out var queuedMessagesRetry))
                {
                    foreach (var msg in newQueuedMessage.Value)
                    {
                        queuedMessagesRetry.Add(msg);
                    }
                }
            }
        }

        public CloseOutType ScanXferDirectory(out List<FileInfo> xmlFilesToImport)
        {
            // Copies the results to the transfer directory
            var serverXferDir = mMgrSettings.GetParam("xferDir");

            if (string.IsNullOrWhiteSpace(serverXferDir))
            {
                LogErrorToDatabase("Manager parameter xferDir is empty (" + Global.GetHostName() + ")");
                xmlFilesToImport = new List<FileInfo>();
                return CloseOutType.CLOSEOUT_FAILED;
            }

            var diXferDirectory = new DirectoryInfo(serverXferDir);

            // Verify transfer directory exists
            if (!diXferDirectory.Exists)
            {
                // There's a serious problem if the transfer directory can't be found!!!
                LogErrorToDatabase("Xml transfer directory not found: " + serverXferDir);
                xmlFilesToImport = new List<FileInfo>();
                return CloseOutType.CLOSEOUT_FAILED;
            }

            // Load all the Xml File names and dates in the transfer directory into a string dictionary
            try
            {
                ShowTrace("Finding XML files at " + serverXferDir);

                xmlFilesToImport = diXferDirectory.GetFiles("*.xml").ToList();
            }
            catch (Exception ex)
            {
                LogErrorToDatabase("Error loading Xml Data files from " + serverXferDir, ex);
                xmlFilesToImport = new List<FileInfo>();
                return CloseOutType.CLOSEOUT_FAILED;
            }

            // Everything must be OK if we got to here
            return CloseOutType.CLOSEOUT_SUCCESS;
        }

        /// <summary>
        /// Send one digest e-mail to each unique combination of recipients
        /// </summary>
        /// <remarks>
        /// Use of a digest e-mail reduces the e-mail spam sent by this tool
        /// </remarks>
        private void SendQueuedMail()
        {
            var currentTask = "Initializing";

            try
            {
                currentTask = "Get SmtpServer param";

                var mailServer = mMgrSettings.GetParam("SmtpServer");

                if (string.IsNullOrEmpty(mailServer))
                {
                    LogError("Manager parameter SmtpServer is empty; cannot send mail");
                    return;
                }

                currentTask = "Check for new log file";

                var logFileName = "MailLog_" + DateTime.Now.ToString("yyyy-MM") + ".txt";

                FileInfo mailLogFile;

                if (string.IsNullOrWhiteSpace(LogTools.CurrentLogFilePath))
                {
                    var exeDirectoryPath = Global.GetExeDirectoryPath();
                    mailLogFile = new FileInfo(Path.Combine(exeDirectoryPath, "Logs", logFileName));
                }
                else
                {
                    currentTask = "Get current log file path";
                    var currentLogFile = new FileInfo(LogTools.CurrentLogFilePath);

                    currentTask = "Get new log file path";

                    if (currentLogFile.Directory == null)
                        mailLogFile = new FileInfo(logFileName);
                    else
                        mailLogFile = new FileInfo(Path.Combine(currentLogFile.Directory.FullName, logFileName));
                }

                var newLogFile = !mailLogFile.Exists;

                currentTask = "Initialize StringBuilder";

                var mailContentPreview = new StringBuilder();

                if (newLogFile)
                    ShowTrace("Creating new mail log file " + mailLogFile.FullName);
                else
                    ShowTrace("Appending to mail log file " + mailLogFile.FullName);

                currentTask = "Create the mail logger";

                using var mailLogger = new StreamWriter(new FileStream(mailLogFile.FullName, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                {
                    AutoFlush = true
                };

                currentTask = "Iterate over mQueuedMail";

                foreach (var queuedMailContainer in mQueuedMail)
                {
                    var recipients = queuedMailContainer.Key;
                    var messageCount = queuedMailContainer.Value.Count;

                    if (messageCount < 1)
                    {
                        ShowTrace("Empty QueuedMail list; this should never happen");

                        LogWarning("Empty mail queue for recipients " + recipients + "; nothing to do", true);
                        continue;
                    }

                    currentTask = "Get first queued mail";

                    var firstQueuedMail = queuedMailContainer.Value.First();

                    if (firstQueuedMail == null)
                    {
                        LogErrorToDatabase("firstQueuedMail item is null in SendQueuedMail");

                        var defaultRecipients = mMgrSettings.GetParam("to");
                        firstQueuedMail = new QueuedMail("Unknown Operator", defaultRecipients, "Exception", new List<ValidationError>());
                    }

                    // Create the mail message
                    var mailToSend = new MailMessage
                    {
                        From = new MailAddress(mMgrSettings.GetParam("from"))
                    };

                    foreach (var emailAddress in firstQueuedMail.Recipients.Split(';').Distinct().ToList())
                    {
                        mailToSend.To.Add(emailAddress);
                    }

                    mailToSend.Subject = firstQueuedMail.Subject;

                    var subjectList = new SortedSet<string>();
                    var databaseErrorMessages = new SortedSet<string>();
                    var instrumentFilePaths = new SortedSet<string>();

                    var mailBody = new StringBuilder();

                    if (messageCount == 1)
                    {
                        LogDebug("E-mailing " + recipients + " regarding " + firstQueuedMail.InstrumentDatasetPath);
                    }
                    else
                    {
                        LogDebug("E-mailing " + recipients + " regarding " + messageCount + " errors");
                    }

                    if (!string.IsNullOrWhiteSpace(firstQueuedMail.InstrumentOperator))
                    {
                        AppendLine(mailBody, "Operator: {0}", firstQueuedMail.InstrumentOperator);

                        if (messageCount > 1)
                        {
                            mailBody.AppendLine();
                        }
                    }

                    // Summarize the validation errors
                    var summarizedErrors = new Dictionary<string, ValidationErrorSummary>();
                    var messageNumber = 0;
                    var nextSortWeight = 1;

                    currentTask = "Summarize validation errors in queuedMailContainer.Value";

                    foreach (var queuedMailItem in queuedMailContainer.Value)
                    {
                        if (queuedMailItem == null)
                        {
                            LogErrorToDatabase("queuedMailItem is nothing for " + queuedMailContainer.Key);
                            continue;
                        }

                        messageNumber++;
                        string statusMsg;

                        if (string.IsNullOrWhiteSpace(queuedMailItem.InstrumentDatasetPath))
                        {
                            statusMsg = string.Format("XML File {0}: queuedMailItem.InstrumentDatasetPath is empty", messageNumber);
                        }
                        else
                        {
                            statusMsg = string.Format("XML File {0}: {1}", messageNumber, queuedMailItem.InstrumentDatasetPath);

                            // Add the instrument dataset path (if not yet present)
                            instrumentFilePaths.Add(queuedMailItem.InstrumentDatasetPath);
                        }

                        LogDebug(statusMsg);
                        currentTask = "Iterate over queuedMailItem.ValidationErrors, message " + messageNumber;

                        foreach (var validationError in queuedMailItem.ValidationErrors)
                        {
                            if (!summarizedErrors.TryGetValue(validationError.IssueType, out var errorSummary))
                            {
                                errorSummary = new ValidationErrorSummary(validationError.IssueType, nextSortWeight);
                                nextSortWeight++;
                                summarizedErrors.Add(validationError.IssueType, errorSummary);
                            }

                            var affectedItem = new ValidationErrorSummary.AffectedItemType
                            {
                                IssueDetail = validationError.IssueDetail,
                                AdditionalInfo = validationError.AdditionalInfo
                            };

                            errorSummary.AffectedItems.Add(affectedItem);

                            if (string.IsNullOrWhiteSpace(queuedMailItem.DatabaseErrorMsg))
                                continue;

                            if (databaseErrorMessages.Contains(queuedMailItem.DatabaseErrorMsg))
                                continue;

                            databaseErrorMessages.Add(queuedMailItem.DatabaseErrorMsg);
                            errorSummary.DatabaseErrorMsg = queuedMailItem.DatabaseErrorMsg;
                        } // for each validationError

                        subjectList.Add(queuedMailItem.Subject);
                    } // for each queuedMailItem

                    currentTask = "Iterate over summarizedErrors, sorted by SortWeight";

                    var additionalInfoList = new List<string>();

                    foreach (var errorEntry in (from item in summarizedErrors orderby item.Value.SortWeight select item))
                    {
                        var errorSummary = errorEntry.Value;

                        var affectedItems = (from item in errorSummary.AffectedItems where !String.IsNullOrWhiteSpace(item.IssueDetail) select item).ToList();

                        if (affectedItems.Count > 0)
                        {
                            AppendLine(mailBody, "{0}: ", errorEntry.Key);

                            foreach (var affectedItem in affectedItems)
                            {
                                AppendLine(mailBody, "  {0}", affectedItem.IssueDetail);

                                if (string.IsNullOrWhiteSpace(affectedItem.AdditionalInfo))
                                    continue;

                                if (!string.Equals(additionalInfoList.LastOrDefault(), affectedItem.AdditionalInfo))
                                {
                                    additionalInfoList.Add(affectedItem.AdditionalInfo);
                                }
                            }

                            foreach (var infoItem in additionalInfoList)
                            {
                                // Add the cached additional info items
                                AppendLine(mailBody, "  {0}", infoItem);
                            }
                        }
                        else
                        {
                            mailBody.AppendLine(errorEntry.Key);
                        }

                        mailBody.AppendLine();

                        if (string.IsNullOrWhiteSpace(errorSummary.DatabaseErrorMsg))
                            continue;

                        mailBody.AppendLine(errorSummary.DatabaseErrorMsg);
                        mailBody.AppendLine();
                    } // for each errorEntry

                    if (instrumentFilePaths.Count == 1)
                    {
                        AppendLine(mailBody, "Instrument file:{0}{1}", Environment.NewLine, instrumentFilePaths.First());
                    }
                    else if (instrumentFilePaths.Count > 1)
                    {
                        mailBody.AppendLine("Instrument files:");

                        foreach (var triggerFile in instrumentFilePaths)
                        {
                            AppendLine(mailBody, "  {0}", triggerFile);
                        }
                    }

                    currentTask = "Examine subject";

                    if (subjectList.Count > 1)
                    {
                        // Possibly update the subject of the e-mail
                        // Common subjects:
                        // "Data Import Manager - Database error."
                        //   or
                        // "Data Import Manager - Database warning."
                        //  or
                        // "Data Import Manager - Operator not defined."
                        // If any of the subjects contains "error", use it for the mail subject
                        foreach (var subject in (from item in subjectList where item.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 select item))
                        {
                            mailToSend.Subject = subject;
                            break;
                        }
                    }

                    mailBody.AppendLine();
                    mailBody.AppendLine("Log file location:");
                    AppendLine(mailBody, "  {0}", GetLogFileSharePath());
                    mailBody.AppendLine();

                    mailBody.AppendLine(
                        "This message was sent from an account that is not monitored. " +
                        "If you have any questions, please reply to the list of recipients directly.");

                    if (messageCount > 1)
                    {
                        // Add the message count to the subject, e.g. 3x
                        mailToSend.Subject = string.Format("{0} ({1}x)", mailToSend.Subject, messageCount);
                    }

                    mailToSend.Body = mailBody.ToString();

                    if (MailDisabled)
                    {
                        currentTask = "Cache the mail for preview";
                        mailContentPreview.AppendLine("E-mail that would be sent:");
                        AppendLine(mailContentPreview, "To: {0}", recipients);
                        AppendLine(mailContentPreview, "Subject: {0}", mailToSend.Subject);
                        mailContentPreview.AppendLine();
                        mailContentPreview.AppendLine(mailToSend.Body);
                        mailContentPreview.AppendLine();
                    }
                    else
                    {
                        currentTask = "Send the mail";
                        var smtp = new SmtpClient(mailServer);
                        smtp.Send(mailToSend);

                        AppUtils.SleepMilliseconds(100);
                    }

                    if (newLogFile)
                    {
                        newLogFile = false;
                    }
                    else
                    {
                        mailLogger.WriteLine();
                        mailLogger.WriteLine("===============================================");
                    }

                    mailLogger.WriteLine(DateTime.Now.ToString("yyyy-MM-dd hh:mm:ss tt"));
                    mailLogger.WriteLine();
                    mailLogger.WriteLine("To: " + recipients);
                    mailLogger.WriteLine("Subject: " + mailToSend.Subject);
                    mailLogger.WriteLine();
                    mailLogger.WriteLine(mailToSend.Body);
                } // for each queuedMailContainer

                currentTask = "Preview cached messages";

                if (MailDisabled && mailContentPreview.Length > 0)
                {
                    ShowTraceMessage("Mail content preview" + Environment.NewLine + mailContentPreview);
                }
            }
            catch (Exception ex)
            {
                var msg = "Error in SendQueuedMail, task " + currentTask;
                LogErrorToDatabase(msg, ex);
                throw new Exception(msg, ex);
            }
        }

        private void FileWatcher_Changed(object sender, FileSystemEventArgs e)
        {
            mConfigChanged = true;

            if (mDebugLevel > 3)
            {
                LogDebug("Config file changed");
            }

            mFileWatcher.EnableRaisingEvents = false;
        }

        private void DeleteXmlFiles(string directoryPath, int fileAgeDays)
        {
            var workingDirectory = new DirectoryInfo(directoryPath);

            // Verify directory exists
            if (!workingDirectory.Exists)
            {
                // There's a serious problem if the success/failure directory can't be found!!!
                LogErrorToDatabase("Xml success/failure directory not found: " + directoryPath);
                return;
            }

            var deleteFailureCount = 0;

            try
            {
                foreach (var xmlFile in workingDirectory.GetFiles("*.xml"))
                {
                    var fileDate = xmlFile.LastWriteTimeUtc;
                    var daysDiff = DateTime.UtcNow.Subtract(fileDate).Days;

                    if (daysDiff <= fileAgeDays)
                        continue;

                    if (PreviewMode)
                    {
                        Console.WriteLine("Preview: delete old file: " + xmlFile.FullName);
                        continue;
                    }

                    try
                    {
                        xmlFile.Delete();
                    }
                    catch (Exception ex)
                    {
                        LogError("Error deleting old Xml Data file " + xmlFile.FullName, ex);
                        deleteFailureCount++;
                    }
                }
            }
            catch (Exception ex)
            {
                LogErrorToDatabase("Error deleting old Xml Data files at " + directoryPath, ex);
                return;
            }

            if (deleteFailureCount == 0)
                return;

            var errMsg = "Error deleting " + deleteFailureCount + " XML files at " + directoryPath +
                         " -- for a detailed list, see log file " + GetLogFileSharePath();

            LogErrorToDatabase(errMsg);
        }

        /// <summary>
        /// Show a message at the console, preceded by a time stamp
        /// </summary>
        /// <param name="message"></param>
        private void ShowTrace(string message)
        {
            if (!TraceMode)
                return;

            ShowTraceMessage(message);
        }

        /// <summary>
        /// Show a message at the console, preceded with a timestamp
        /// </summary>
        /// <param name="message"></param>
        public static void ShowTraceMessage(string message)
        {
            BaseLogger.ShowTraceMessage(message, false);
        }
    }
}
