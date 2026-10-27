"""Verifier exit-code errors."""

class VerificationError(Exception):
    """Evidence cannot be interpreted or does not satisfy the committed contract."""

    exit_code = 2


class MandatoryFailure(VerificationError):
    """A required case, assertion or runtime acceptance condition failed."""

    exit_code = 1


class ExecutionUnavailable(VerificationError):
    """The environment cannot run a mandatory check."""

    exit_code = 3


