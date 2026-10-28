# Managed package verification cases

The committed executable case inventory is `../checkpoint_contract.json`. Case
inputs and outputs are generated under the ignored parity artifact directory at
execution time, then hashed into `evidence.json`. The `fixture_support.py`
control data is synthetic and is accepted only by verifier unit tests; it cannot
be accepted as milestone evidence.

Add source-backed, deterministic inputs here as individual case fixtures when
the I0 inventory identifies their exact Pi and PiSharp behavior. Never store
credentials, user caches, or raw package-manager state from a real home here.
