"""Validation of the supported evidence schema subset."""

import json
import re
from typing import Any


def _schema_errors(value: Any, schema: dict[str, Any], path: str = '$') -> list[str]:
    """Validate the deliberately small, strict JSON-Schema subset used by this evidence."""
    errors: list[str] = []
    expected_type = schema.get('type')
    if expected_type:
        type_map = {
            'object': lambda item: isinstance(item, dict),
            'array': lambda item: isinstance(item, list),
            'string': lambda item: isinstance(item, str),
            'integer': lambda item: isinstance(item, int) and not isinstance(item, bool),
            'number': lambda item: isinstance(item, (int, float)) and not isinstance(item, bool),
            'boolean': lambda item: isinstance(item, bool),
            'null': lambda item: item is None,
        }
        accepted_types = expected_type if isinstance(expected_type, list) else [expected_type]
        if not any(type_map[kind](value) for kind in accepted_types):
            return [f'{path}: expected {" or ".join(accepted_types)}']

    if 'enum' in schema and value not in schema['enum']:
        errors.append(f'{path}: value is outside the allowed enum')
    if isinstance(value, str):
        if len(value) < schema.get('minLength', 0):
            errors.append(f'{path}: string is too short')
        if 'pattern' in schema and re.fullmatch(schema['pattern'], value) is None:
            errors.append(f'{path}: string does not match {schema["pattern"]!r}')
    if isinstance(value, list):
        if len(value) < schema.get('minItems', 0):
            errors.append(f'{path}: array has too few items')
        if schema.get('uniqueItems') and len(value) != len({json.dumps(item, sort_keys=True) for item in value}):
            errors.append(f'{path}: array items are not unique')
        if 'items' in schema:
            for index, item in enumerate(value):
                errors.extend(_schema_errors(item, schema['items'], f'{path}[{index}]'))
    if isinstance(value, dict):
        if len(value) < schema.get('minProperties', 0):
            errors.append(f'{path}: object has too few properties')
        for required in schema.get('required', []):
            if required not in value:
                errors.append(f'{path}: missing required property {required!r}')
        properties = schema.get('properties', {})
        patterns = schema.get('patternProperties', {})
        for key, item in value.items():
            child_schema = properties.get(key)
            if child_schema is None:
                child_schema = next((child for pattern, child in patterns.items()
                                     if re.fullmatch(pattern, key)), None)
            if child_schema is None:
                additional = schema.get('additionalProperties', True)
                if additional is False:
                    errors.append(f'{path}: unexpected property {key!r}')
                elif isinstance(additional, dict):
                    errors.extend(_schema_errors(item, additional, f'{path}.{key}'))
            else:
                errors.extend(_schema_errors(item, child_schema, f'{path}.{key}'))
    if isinstance(value, (int, float)) and not isinstance(value, bool):
        if 'minimum' in schema and value < schema['minimum']:
            errors.append(f'{path}: number is below the minimum')
        if 'maximum' in schema and value > schema['maximum']:
            errors.append(f'{path}: number is above the maximum')
    return errors


