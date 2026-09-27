# RPC new-session optional parent baseline

Reference: `earendil-works/pi@2b0a123de98318c2ff8069661721ce0c3794c34e`.

In a current Pi process, `new_session` with `parentSession: null` or `parentSession: ""` succeeds with `data.cancelled: false` and creates a session with no parent path. Supplying a valid string path succeeds and persists that path. Requests without an `id` receive responses without an `id`.

PiSharp process coverage is `RpcSessionProcessTests.NewSessionTreatsNullAndEmptyParentAsAbsentAndOmitsMissingCorrelation`; `NewSessionSwitchesTheActiveRpcSessionAndTracksItsParent` covers a valid parent path.

Observed out-of-contract difference: current Pi also accepts a truthy non-string `parentSession` because this command path does not validate JSON at runtime. PiSharp returns a validation error because the documented field is a string path. This malformed-value difference remains open; valid, null, empty and omitted forms are covered.
