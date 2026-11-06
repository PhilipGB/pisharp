# ENV-PARSER-002 fixture

A small .NET 10 library that parses `.env` assignments and expands `${KEY}`
placeholders in strings.

`EnvFile.Parse(string text)` returns a case-sensitive mapping of keys to values.
`EnvInterpolator.Expand(string input, IReadOnlyDictionary<string, string> values)`
expands references to parsed values.

## Documented behaviour

- Assignments are separated at the first equals sign.
- Additional equals signs are preserved inside values.
- Keys are trimmed; values are preserved.
- Blank lines and comment lines beginning with `#` (after optional leading
  whitespace) are ignored.
- Empty values are valid.
- Duplicate assignments use the last value.
- Keys are case-sensitive.
- `${KEY}` expands to the corresponding parsed value.
- References to unknown keys remain unchanged.
- Literal content surrounding placeholders is preserved.

## Build and test

```sh
dotnet test Fixture.slnx
```
