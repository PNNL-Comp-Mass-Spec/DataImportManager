Public Interface IXMLValidateStatus
    'Used for XML Validation status
    Enum XmlValidateStatus
        XML_VALIDATE_SUCCESS = 0
        XML_VALIDATE_FAILED = 1
        XML_VALIDATE_NO_CHECK = 2
        XML_VALIDATE_ENCOUNTERED_ERROR = 3
        XML_VALIDATE_BAD_XML = 4
        XML_VALIDATE_CONTINUE = 5
        XML_VALIDATE_NO_DATA = 10
    End Enum

End Interface
