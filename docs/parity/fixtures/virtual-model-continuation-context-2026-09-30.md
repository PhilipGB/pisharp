# Virtual-model continuation context — 2026-09-30

Started from `2ff42c49634374d9398f928f81b5e8d353f605be`. Current Pi remains `1b347794e2a630e4359f2584f4eea388145d0ddf` after fetch.

Fail-first provider-loop evidence with configured policies reported `user -> direct -> continuation`: InFlightContextBudget summarized before the next physical route was selected. An unknown logical budget avoided that premature summary but exposed duplicated prompt/assistant history after canonical compaction. Normal routed requests now defer budgeting to the physical dispatch boundary, including pre-prompt checks. Overflow recovery carries a forced-compaction hint into retry routing instead of using a previous physical policy. Ordinary-model in-flight projection is preserved.

Reconciliation after canonical compaction matches the retained raw active branch rather than only the shortened context, preventing previously checkpointed messages from being appended again. Four real extension-tool/provider-loop cases cover unknown, large and stale-small initial policies; initial physical context is 10,000 and continuation context 8,000. They assert routing before summaries, balanced provider tool history, canonical compaction, no duplicate prompts/answers, unchanged logical identity and physical usage. Two overflow cases force canonical compaction below the estimated threshold after an HTTP 400 context overflow, preserving failed physical identity and retry routing.

Split history and turn-prefix summaries use separate physical models/prices: 2/10 and 7/20 per million input/output tokens. Individual costs are 0.00004 and 0.00011, with total session cost 0.00023 including inference. These tests validate existing request-local summary accounting without changing it.

Focused virtual-model/compaction/overflow/history/active-model RPC tests pass 107/107. Final format verification passes; warnings-as-errors build has zero warnings/errors; full solution tests pass 920/920 with zero skips. The paired current-Pi/PiSharp CLI context-compaction replay matches logical selection, physical dispatches, route reasons/state/previous identity and canonical compaction count. This existing paired replay covers later-turn routing; the new tool-continuation and overflow evidence is deterministic provider-loop integration, not a paired tool-loop replay.

Exact-head CI is pending publication. Remaining virtual-model work: physical image resize/compatibility options and TUI state presentation, then a bounded audit before classifiers/Codemode. The broader goal remains incomplete.
