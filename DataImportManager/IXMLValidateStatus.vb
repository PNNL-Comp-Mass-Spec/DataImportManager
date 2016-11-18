Public Interface IXMLValidateStatus
    ' Used for XML Validation status
    Enum XmlValidateStatus
        XML_VALIDATE_SUCCESS = 0
        XML_VALIDATE_FAILED = 1
        <Obsolete("Old enum")>
        XML_VALIDATE_NO_CHECK = 2
        XML_VALIDATE_ENCOUNTERED_ERROR = 3
        XML_VALIDATE_BAD_XML = 4
        XML_VALIDATE_CONTINUE = 5
        XML_WAIT_FOR_FILES = 6
        XML_VALIDATE_ENCOUNTERED_LOGON_FAILURE = 7
        XML_VALIDATE_SIZE_CHANGED = 8
        XML_VALIDATE_ENCOUNTERED_NETWORK_ERROR = 9
        XML_VALIDATE_NO_DATA = 10
        XML_VALIDATE_SKIP_INSTRUMENT = 11
        XML_VALIDATE_NO_OPERATOR = 12
        XML_VALIDATE_TRIGGER_FILE_MISSING = 13
    End Enum

End Interface
