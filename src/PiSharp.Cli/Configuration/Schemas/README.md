# Configuration schema sources

These four JSON Schema artifacts are copied byte for byte from audited Pi
`3daa20280f6a7c7cbe0935680c9a38fdc3863005`,
`packages/coding-agent/schemas/`. Their original schema IDs, generated comments
and definitions remain intact. The accompanying MIT license is retained.

PiSharp embeds them under `PiSharp.Cli.Configuration.Schemas` and publishes files
in `schemas/` beside the CLI assembly. `ConfigSchemaCatalog` opens the embedded
versions. Compare provenance with:

```sh
python3 tools/parity/config-schema-differential.py --pi /tmp/pisharp-parity-audit-pi --output artifacts/parity/config-schemas.json
```

These artifacts describe the current parity target. PiSharp still implements a
subset of the configuration fields and has stricter validation for some fields.
Publication and byte identity do not establish complete runtime validation parity;
`feature-config-schemas-publication` retains that work in the execution ledger.
