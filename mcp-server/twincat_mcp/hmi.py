"""HMI context validation; no credentials or native calls."""

from pathlib import Path
from urllib.parse import urlsplit

from .errors import OperationError


def target_url(value):
    try:
        if any(c.isspace() or ord(c) < 32 for c in value):
            raise ValueError()
        url = urlsplit(value)
        port = url.port
        if (
            url.scheme != "https"
            or not url.hostname
            or url.username
            or url.password
            or url.query
            or url.fragment
            or url.path not in ("", "/")
        ):
            raise ValueError()
        host = url.hostname.lower()
        if ":" in host:
            host = "[" + host + "]"
        return "https://" + host + (":" + str(port) if port and port != 443 else "") + "/"
    except ValueError:
        raise OperationError(
            "invalid_hmi_target",
            "Use an HTTPS server root URL without credentials, query or fragment.",
        ) from None


def project_path(value):
    path = Path(value)
    if not path.is_absolute() or not path.is_file() or path.suffix.lower() != ".hmiproj":
        raise OperationError(
            "invalid_hmi_project", "projectPath must name an existing absolute .hmiproj file."
        )
    return str(path.resolve())
