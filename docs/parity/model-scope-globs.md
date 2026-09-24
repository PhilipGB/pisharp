# Scoped model glob differential slice

Pinned `earendil-works/pi@a7d17e39aaa0091c7573d0790714751956f10bd1`, `packages/coding-agent/src/core/model-resolver.ts` calls `minimatch(fullId, globPattern, {nocase:true}) || minimatch(model.id, globPattern, {nocase:true})`. Evaluated the pinned checkout's installed `minimatch` under Node 24:

```sh
node --input-type=module -e 'import {minimatch} from "/tmp/pisharp-upstream/node_modules/minimatch/dist/esm/index.js"; for(const [v,p] of [["openrouter/openai/gpt-4o-mini","openrouter/*"],["openrouter/openai/gpt-4o-mini","openrouter/**"],["custom/reasoner","custom/r[ea]asoner"],["custom/other-3","custom/other-[0-5]"],["custom/other-a","custom/other-[!0-5]"],["custom/foo/bar","custom/**/bar"],["custom/bar","custom/**/bar"],["custom/.private","custom/*"],["custom/.private","custom/**"],["custom/.private","custom/[.]private"]]) console.log(v,p,minimatch(v,p,{nocase:true}))'
```

Observed results in order: `false, true, true, true, true, true, true, false, false, true`.
`ModelScopeGlobTests` contains matching C# cases for these `*`, `**`, `?`, bracket and hidden-segment behaviors, plus bounds for many-star patterns. `ModelScopeGlob` is **only a subset** of minimatch: brace expansion, extglobs, escapes and exact failure diagnostics are not yet implemented. Per-pattern thinking-level suffixes and upstream scope ordering also remain open. This is narrow function-level differential evidence, not CLI parity.
