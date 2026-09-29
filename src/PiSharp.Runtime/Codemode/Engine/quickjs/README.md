QuickJS-WASI 3.6.2 runtime files are vendored from the `quickjs-wasi` npm package
(`quickjs-wasi-3.6.2.tgz`). The package is MIT licensed; see `LICENSE`.
The npm tarball SHA-256 is
`f1f4349f19a2d849e33ea0ae9bec2e7062b8839f4eceb17c9051ddbaa2720982`.

The files `index.js`, `extensions.js`, `version.js`, `wasi-shim.js`, and
`quickjs.wasm` are used by PiSharp's Node worker. Model scripts execute inside
the WASM QuickJS VM. PiSharp owns the worker protocol and tool authorization.
