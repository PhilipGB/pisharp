"""Source transforms used to validate and script the ENV-PARSER-002 defects."""

DEFECT_A_OLD = """            var parts = line.Split('=');
            if (parts.Length != 2)
            {
                continue;
            }

            var key = parts[0].Trim();
"""
DEFECT_A_NEW = """            var separator = line.IndexOf('=');
            if (separator < 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
"""
DEFECT_A_VALUE_OLD = "            values[key] = parts[1];\n"
DEFECT_A_VALUE_NEW = "            values[key] = line[(separator + 1)..];\n"

DEFECT_B_OLD = """            values.TryGetValue(match.Groups[1].Value, out var value)
                ? value
                : string.Empty);
"""
DEFECT_B_NEW = """            values.TryGetValue(match.Groups[1].Value, out var value)
                ? value
                : match.Value);
"""


def correct_defect_a(text: str) -> str:
    if DEFECT_A_OLD not in text or DEFECT_A_VALUE_OLD not in text:
        raise ValueError("Defect A source pattern was not found")
    return text.replace(DEFECT_A_OLD, DEFECT_A_NEW).replace(DEFECT_A_VALUE_OLD, DEFECT_A_VALUE_NEW)


def correct_defect_b(text: str) -> str:
    if DEFECT_B_OLD not in text:
        raise ValueError("Defect B source pattern was not found")
    return text.replace(DEFECT_B_OLD, DEFECT_B_NEW)
