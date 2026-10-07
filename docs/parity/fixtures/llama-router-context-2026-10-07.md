# llama.cpp context-window refresh and reload — 2026-10-07

The paired process fixture [llama-router-context-differential.py](../../../tools/parity/llama-router-context-differential.py) runs current Pi `503c605528f9af993c0e37ede468cf884fb0ff5b` and PiSharp `f64ca15efa2634bfeefcbc8cfc531714757955d0` against the same deterministic router. It creates separate persisted model state for each process, refreshes the catalog after the router changes, then starts both runtimes offline from the persisted state.

The captured comparison is [llama-router-context-2026-10-07.json](llama-router-context-2026-10-07.json). Both sides resolve the same windows:

- Initial catalog: runtime `n_ctx` 32768 beats configured `--ctx-size 8192`; configured `-c 4096` beats training metadata; training metadata supplies 16384; missing metadata falls back to 128000; the sleeping decision model uses runtime `n_ctx` 8192.
- Refreshed catalog: cached 32768 beats the runtime model's training window 65536; a new configured `--ctx-size 2048` beats its cached 4096; training and fallback remain 16384 and 128000; the decision model retains 8192 over its training value 16384.
- Offline process reload: chat and classifier context windows match the refreshed values, and neither process contacts the router.

Both processes issue the same authenticated `/models` and `/props` requests during refresh. Reproduce with:

```sh
python3 tools/parity/llama-router-context-differential.py \
  --pi /path/to/current/pi \
  --output docs/parity/fixtures/llama-router-context-2026-10-07.json
```

The focused `provider-llama-context-cache-reload` ledger capability is verified by this refresh/reload evidence; exact terminal rendering and the wider llama.cpp family remain open.
