# RPC `get_commands` baseline

Reference: `earendil-works/pi@2b0a123de98318c2ff8069661721ce0c3794c34e`, inspected with a real `pi --mode rpc` process on 2026-09-27. PiSharp process coverage lives in `RpcCommandDiscoveryProcessTests`.

## Observed fields

- Every listed command has `name`, `source`, and `sourceInfo`.
- `description` is optional. Pi omits the key for extension commands without a description.
- `sourceInfo` carries `path`, `source`, `scope`, and `origin`; `baseDir` is omitted when unavailable.
- Explicit extension assemblies report `sourceInfo.source = "cli"`, `scope = "temporary"`, `origin = "top-level"`, and omit `baseDir`.
- Explicit prompt and skill paths report `sourceInfo.source = "local"`, `scope = "temporary"`, `origin = "top-level"`, with their resource directory as `baseDir`.
- Auto-discovered project resources precede user resources. Their `sourceInfo.source` is `"auto"`, with `scope` set to `"project"` or `"user"` and the Pi/project agent root in `baseDir`.
- A prompt without a description uses its first nonempty body line, truncated to 60 characters plus `...` when longer.

## Scope remaining

PiSharp's CLI process tests cover these fields, supported source classes and ordering. The complete Pi command list can also include provider- or extension-contributed entries beyond PiSharp's current catalog. More importantly, PiSharp currently advertises extension commands but does not dispatch them when invoked through RPC `prompt`; that execution path is the next parity slice. This fixture records scoped behavior, not complete `get_commands` parity.
