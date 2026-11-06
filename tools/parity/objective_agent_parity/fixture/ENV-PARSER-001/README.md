# ENV-PARSER-001 fixture

A small .NET 10 library that parses `.env` files.

`EnvFile.Parse(string text)` returns a case-sensitive mapping of keys to values.

## Documented behaviour

- A valid line contains a key, an equals sign and a value.
- Split each assignment at the **first** equals sign.
- Preserve subsequent equals signs inside the value.
- Trim whitespace from keys.
- Preserve whitespace in values.
- Ignore blank lines.
- Ignore comment lines whose first non-whitespace character is `#`.
- Ignore lines without an equals sign and lines with an empty key.
- Support both LF and CRLF line endings.
- Keys are case-sensitive.
- When a key appears more than once, the last assignment wins.
- An empty value is valid.

## Build and test

```sh
dotnet test Fixture.slnx
```
