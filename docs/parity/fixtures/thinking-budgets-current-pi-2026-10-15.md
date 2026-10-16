# Thinking budgets — current Pi comparison, 2026-10-15

Reference Pi is pinned at `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`. The
implementation landed in `c8928c3d` and is integrated in the current branch.

`thinkingBudgets` loads as a validated integer map for `minimal`, `low`,
`medium`, and `high`. User settings apply by default; trusted project settings
overlay them after trust. Startup, reload, malformed values, persistence, and
provider request mapping have focused coverage. The runtime policy defaults to
1,024 / 2,048 / 8,192 / 16,384 reasoning tokens, maps `xhigh` and `max` to
`high`, reserves at least 1,024 answer tokens, and respects model output limits.

The pinned five-case policy differential matches Pi. Google and Vertex thinking
request controls match for all ten enabled cases; two off-control cases retain
the recorded P2 differences. The queue-7 exact-head Linux CI run
[38784569620](https://github.com/PhilipGB/pisharp/actions/runs/38784569620)
passed at `c8928c3d`. The integrated product source also passes exact-head Linux
CI [38789357300](https://github.com/PhilipGB/pisharp/actions/runs/38789357300).
The local .NET suite passes 2,057/2,057 with zero skips, including the
thinking-budget cases.

This evidence covers integer token budgets and their named runtime consumers.
It does not claim fractional-value parity or resolve the two Google off-control
differences. Broader provider behavior remains tracked in the provider tasks.
