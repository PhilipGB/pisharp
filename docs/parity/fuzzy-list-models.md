# Narrow pinned-upstream differential: list-models fuzzy inclusion

Upstream `earendil-works/pi@a7d17e39aaa0091c7573d0790714751956f10bd1`, `packages/tui/src/fuzzy.ts` (file hash SHA-256 `0ae2bedc6a4f043d875ec415202a6e3d9e45405741359d7d2dc2855240dff633`). With `/tmp/pisharp-upstream` checked out at the earlier baseline, this file's content is identical to the pinned blob (`git show FETCH_HEAD:packages/tui/src/fuzzy.ts`).

Run with Node 24:

```sh
node --input-type=module -e 'import { fuzzyFilter } from "/tmp/pisharp-upstream/packages/tui/src/fuzzy.ts"; const models=[{provider:"fixture",id:"static-only"},{provider:"fixture",id:"omit-me"},{provider:"openai",id:"gpt-4o"}]; for(const q of ["fi sta","FIX/STA","ftc stc","fixture/other","gpt4","4gpt","gpt5"]) console.log(q,JSON.stringify(fuzzyFilter(models,q,m=>`${m.provider} ${m.id}`).map(m=>m.id)))'
```

Observed output:

```
fi sta ["static-only"]
FIX/STA ["static-only"]
ftc stc ["static-only"]
fixture/other []
gpt4 ["gpt-4o"]
4gpt ["gpt-4o"]
gpt5 []
```

`CliModelFilterTests.MatchesPinnedFuzzyTokenAndSwappedAlphaNumericCases` verifies matching inclusion/exclusion for these seven query cases in C#. This is **not** a full CLI comparison: ordering/scoring, Unicode, displayed columns, auth/catalog loading and errors are still different or unverified.
