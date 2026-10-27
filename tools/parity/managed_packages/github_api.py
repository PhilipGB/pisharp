"""Read live GitHub provenance for the milestone verifier."""

import json
from typing import Any
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen

from errors import ExecutionUnavailable


def _run_github_api(endpoint: str, *, label: str) -> tuple[Any, dict[str, Any]]:
    """Read public repository evidence through GitHub REST without requiring gh auth."""
    url = f'https://api.github.com/repos/PhilipGB/pisharp/{endpoint.lstrip("/")}'
    request = Request(url, headers={
        'Accept': 'application/vnd.github+json',
        'User-Agent': 'pisharp-managed-package-verifier',
        'X-GitHub-Api-Version': '2022-11-28',
    })
    try:
        with urlopen(request, timeout=30) as response:
            raw = response.read().decode('utf-8')
            value = json.loads(raw)
            command = {'argv': ['GET', url], 'exitCode': 0, 'httpStatus': response.status,
                       'stdout': raw, 'stderr': ''}
    except (HTTPError, URLError, TimeoutError, OSError, UnicodeError, json.JSONDecodeError) as error:
        raise ExecutionUnavailable(f'{label} could not be verified through GitHub REST: {error}') from error
    return value, command


