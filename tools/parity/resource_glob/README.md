# Resource glob parity harness

`cases.json` is the frozen matcher contract. Each case carries a stable ID, the
resource path and type, base directory, upstream source reference, and pinned Pi
expectation. The expected values were observed with Pi's package-manager matcher
and its default `minimatch(path, pattern)` options.

Run the differential with:

```sh
python3 tools/parity/resource_glob/differential.py \
  --pi /path/to/pi-b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6 \
  --output artifacts/parity/resource-glob-matcher.json
```

The runner verifies the Pi source pin, invokes its installed `minimatch` package
twice, checks the frozen observations, builds a temporary probe against
`PiSharp.Runtime`, and compares every Boolean result. It does not add a Node or
`minimatch` runtime dependency to PiSharp.

This is the initial G-series matcher slice. It does not yet cover the complete
path-override, settings, trust, all-resource process, reload, or verifier
negative-control contract from the milestone acceptance plan.
