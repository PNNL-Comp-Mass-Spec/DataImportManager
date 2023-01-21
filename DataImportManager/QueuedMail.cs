﻿using System.Collections.Generic;

namespace DataImportManager
{
    // ReSharper disable once InconsistentNaming
    internal class QueuedMail
    {
        public string InstrumentOperator { get; }

        /// <summary>
        /// Semicolon separated list of e-mail addresses
        /// </summary>
        public string Recipients { get; }

        public string Subject { get; }

        /// <summary>
        /// Tracks any database message errors
        /// Also used to track suggested solutions
        /// </summary>
        public string DatabaseErrorMsg { get; set; }

        public List<ValidationError> ValidationErrors { get; }

        /// <summary>
        /// Tracks the path to the dataset on the instrument
        /// </summary>
        public string InstrumentDatasetPath { get; set; }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="operatorName"></param>
        /// <param name="recipientList"></param>
        /// <param name="mailSubject"></param>
        /// <param name="lstValidationErrors"></param>
        public QueuedMail(string operatorName, string recipientList, string mailSubject, List<ValidationError> lstValidationErrors)
        {
            InstrumentOperator = operatorName;
            Recipients = recipientList;
            Subject = mailSubject;
            ValidationErrors = lstValidationErrors;

            DatabaseErrorMsg = string.Empty;
            InstrumentDatasetPath = string.Empty;
        }
    }
}