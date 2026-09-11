"""Application errors returned as error-flagged MCP results."""


class OperationError(Exception):
    def __init__(self, code, message):
        super().__init__(message)
        self.code = code


class Cancelled(OperationError):
    def __init__(self):
        super().__init__(
            "cancelled", "Cancelled before the next dispatch; completed effects remain."
        )
