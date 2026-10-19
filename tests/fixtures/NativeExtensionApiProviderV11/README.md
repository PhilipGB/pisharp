# PR #11 legacy provider compatibility fixture

The public provider registration and connection types in `Contract` snapshot
the provider surface from PiSharp PR #11 merge
`1a0165c3858d973948c9ac8eb698e75ed0fcc535`. The package omits the contract DLL,
so the current host must bind the extension to its current Abstractions
assembly. Its provider implements only `IChatClient` and has no raw-wire
callbacks.
