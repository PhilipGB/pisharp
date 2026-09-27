# Malformed and forward-linked Pi JSONL recovery baseline

Reference source: earendil-works/pi commit 2b0a123de98318c2ff8069661721ce0c3794c34e, packages/coding-agent/src/core/session-manager.ts. Current upstream main still resolves to this commit. A local Pi 0.87.1 RPC process was used for the behavioral comparison.

## Input

The first v3 session contains a valid user entry, a malformed physical row, an assistant entry whose parentId points to the skipped row, and a truncated JSON record at EOF.

## Observable behavior

Pi's loader skips malformed rows and indexes the later valid assistant entry. With that entry as the leaf, get_messages returns only its assistant message. PiSharp now maps the dangling entry to a root in its canonical conversation tree, retains the original parentId for Pi entry projection and export, and returns the same active message. The PiSharp CLI process test also verifies that reading the imported source does not rewrite its bytes.

The local Pi 0.87.1 process returned:

    {"id":"read-1","type":"response","command":"get_messages","success":true,"data":{"messages":[{"role":"assistant","content":"survives as orphan","timestamp":1780000002000,"provider":"openai","model":"gpt-4o"}]}}

The PiSharp process returns the same response. The Pi loader appends a newline after loading a truncated tail; PiSharp retains the source unchanged and imports into its project session store.

A second v3 fixture places child-first before its parent-later entry. Both current Pi and PiSharp get_tree place child-first under parent-later, even though physical entry order remains unchanged. PiSharp ConversationTree.FromEntries now validates and resolves forward references while keeping the source order. Parent cycles fail closed to avoid infinite traversal.

PiJsonlSessionRecoveryProcessTests covers the malformed-parent active message and the forward-parent tree shape. This remains scoped evidence: duplicate IDs, cycles, invalid message fields and broader malformed-entry behavior are not claimed equivalent.
