"""One dispatch, one receipt. Never replay after losing the worker response."""

from .host import HostError, _ci_wrap, get_shell_host


def run_shell_step(command, step_args, solution_path=None, tc_version=None, timeout_minutes=10):
    host = get_shell_host()
    if host is None:
        return _ci_wrap(
            {
                "success": False,
                "dispatched": False,
                "errorMessage": "Persistent automation worker unavailable. Build TcAutomation and check configuration.",
            }
        ), []
    try:
        result, progress = host.execute_step(
            command, step_args or {}, solution_path, tc_version, timeout=timeout_minutes * 60 + 180
        )
        return _ci_wrap(result), progress
    except HostError as exc:
        return _ci_wrap(
            {
                "success": False,
                "outcomeUnknown": True,
                "errorMessage": str(exc),
                "message": "Worker response lost. Inspect target; no replay was attempted.",
            }
        ), []
