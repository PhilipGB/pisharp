"""Recognize only observed pairs of conflicting upstream Git clone failures."""

import re


def clone_destination_collision(errors: list[str]) -> bool:
    if len(errors) != 2 or any(not isinstance(error, str) or 'Error: git clone ' not in error for error in errors):
        return False
    for first, second in ((errors[0], errors[1]), (errors[1], errors[0])):
        if 'fatal: destination path ' not in first or 'already exists and is not an empty directory' not in first:
            continue
        if ("fatal: cannot copy '/usr/share/git-core/templates/" in second
                and 'No such file or directory' in second):
            return True
        match = re.search(r"fatal: destination path '([^'\n]+)' already exists and is not an empty directory", first)
        if match and re.search(
                r"fatal: could not set 'core\.repositoryformatversion' to '[01]'", second):
            config_path = f"{match.group(1)}/.git/config: No such file or directory"
            if (f"could not lock config file {config_path}" in second or
                    f"could not write config file {config_path}" in second):
                return True
    return False
