# Pinned Pi completion source inventory

Pinned Pi: `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`
(`tools/parity/pi-reference.txt`, checkout `/home/philip/Projects/pi-pinned`).

This inventory records where the pinned source defines each observable completion
behaviour exercised by the fixture. Paths are relative to the Pi repository.

## Ownership

| Behaviour | Owner |
|---|---|
| Slash-command candidate list and order | `packages/coding-agent/src/core/slash-commands.ts` (`BUILTIN_SLASH_COMMANDS`), assembled with prompt templates, extension commands and `skill:` commands in `packages/coding-agent/src/modes/interactive/interactive-mode.ts` (`createBaseAutocompleteProvider`) |
| Slash candidate discovery and filtering | `packages/tui/src/autocomplete.ts` (`CombinedAutocompleteProvider.getSuggestions`) |
| Fuzzy matching and score ordering | `packages/tui/src/fuzzy.ts` (`fuzzyMatch`, `fuzzyFilter`) |
| Argument completions | `packages/coding-agent/src/modes/interactive/interactive-mode.ts` (`getArgumentCompletions` for `/model`, `/thinking`, `/login`); `packages/coding-agent/src/modes/interactive/model-search.ts` (`getModelSearchText`); `packages/ai/src/models.ts` (`getSupportedThinkingLevels`); `packages/coding-agent/src/modes/interactive/components/oauth-selector.ts` (auth-type labels) |
| Completion activation, navigation, cancellation and requery | `packages/tui/src/components/editor.ts` (`handleInput`, `insertCharacter`, `handleBackspace`, `handleForwardDelete`, `moveCursor`, `handleTabCompletion`, `requestAutocomplete`, `applyAutocompleteSuggestions`, `cancelAutocomplete`) |
| Candidate list rendering, wrap navigation and `(n/m)` row | `packages/tui/src/components/select-list.ts` |
| Escape routing while the picker is visible | `packages/coding-agent/src/modes/interactive/components/custom-editor.ts` |
| Token boundaries (whitespace and CJK punctuation) | `packages/tui/src/utils.ts` (`autocompleteBoundaryRegex`, `autocompleteSeparatorRegex`) |
| `@file` discovery and application | `packages/tui/src/autocomplete.ts` (`getFuzzyFileSuggestions`, `walkDirectoryWithFd`, `applyCompletion`) |
| `fd` location | `packages/coding-agent/src/modes/interactive/interactive-mode.ts` (`ensureTool("fd")` in `packages/coding-agent/src/utils/tools-manager.ts`) |

## Activation

- `/` opens slash completion only when the cursor is on the first line and the
  trimmed text before the cursor is empty or exactly `/`
  (`isAtStartOfMessage`).
- Letters, digits, `.`, `-`, `_` and CJK letters re-query an open slash/argument
  picker or open one while the trimmed line starts with `/`
  (`isInSlashCommandContext`).
- `@` (and provider trigger characters, `#` by default) opens symbol completion
  only at a token boundary: start of the line, whitespace, CJK punctuation, or
  after opening wrappers `(`, `[`, `{`, `<`, `` ` ``.
- Automatic symbol completion debounces 20 ms; slash completion and explicit Tab
  run immediately. Tab outside a slash command name forces file completion.
- A bracketed paste cancels completion and inserts atomically without triggering;
  the next typed character follows the normal trigger rules.
- Escape cancels the picker without changing editor text. Deletions and cursor
  movements re-query an open picker; deleting with no picker re-triggers when the
  resulting text is in a completion context.

## Command name versus arguments

- The slash branch applies only when completion is not forced.
- Without a space after the command name, candidates are the registered command
  names filtered by `fuzzyFilter` over the bare name (skill commands strip the
  `skill:` prefix for the primary pass, then a full-name-only pass appends
  remaining skill matches).
- With a space, the command name must match exactly and that command must expose
  `getArgumentCompletions`; the remainder of the line is passed as the argument
  prefix. Commands without the hook produce no argument overlay.
- `/model` values are `provider/id` with the model id as label and provider as
  description. `/thinking` values are the model's supported levels. `/login`
  projects every provider into one option per supported auth method, merges
  options by provider id with `oauth` before `api_key`, sorts providers by
  display name, and matches over `"<id> <name> <authType> <label> ..."`.
  `oauth` is labelled `subscription` unless the provider marks it as a
  non-subscription account (Radius), and providers supporting neither auth
  method are omitted.

## File discovery

- Base directory is the session working directory
  (`sessionManager.getCwd()`), not a git/worktree root.
- `walkDirectoryWithFd` runs `fd` with:
  `--base-directory <dir> --max-results 100 --type f --type d --follow --hidden
  --exclude .git --exclude .git/* --exclude .git/** [--max-depth n]
  [--full-path] [pattern]`.
- Queries containing `/` use `--full-path`; segments are regex-escaped and joined
  with `[\\/]`, keeping a trailing separator.
- A query with a `/` scopes the search: the directory before the final separator
  becomes the fd base directory and the remainder the pattern; relative scopes
  resolve under the session working directory, `~/` under home, absolute scopes
  as-is. A nonexistent scope returns no candidates.
- Without `fd`, file suggestions are empty.
- `.git`, `.git/*` and `/.git/` results are filtered again after fd.
- Results are scored: exact basename 100, basename prefix 80, basename substring
  50, path substring 30, plus 10 for directories; zero scores drop. Ordering is
  score desc, path depth asc, path length asc, then `localeCompare`. The top 20
  are shown. A depth-1 base-directory query runs first and is deduplicated with
  the recursive query, so direct children survive flooding.
- Directories are labelled with a trailing `/`; labels are basenames, the
  description is the display path relative to the session working directory.

## Insertion and trailing spaces

- Command name: `/` + value + `" "` before the text after the cursor; cursor
  after the space. Enter applies then submits when the prefix starts with `/`.
- `@file`: value + `" "` for files, no space for directories; cursor before the
  closing quote when a directory value is quoted.
- `@file` values are quoted when the path contains whitespace or CJK punctuation;
  the closing quote follows the path and the trailing space follows the quote.
- Command arguments: value only, no suffix. Enter applies without submitting.
- Unprefixed, forced Tab file completion: value only, no trailing space.

## Upstream tests

- `packages/tui/test/autocomplete.test.ts` — path extraction, `@` activation,
  quoting, scoping, filtering, ordering, symlinks, `.git` exclusion, application.
- `packages/tui/test/fuzzy.test.ts` — subsequence scoring, word boundaries,
  stable ordering, alphanumeric swap.
- `packages/tui/test/autocomplete-skill-slash.test.ts` — skill bare-name and
  full-name ordering, leading whitespace, argument completion.
- `packages/tui/test/editor.test.ts` (`describe("Autocomplete")`) — trigger
  debounce, forced completion, deletion/requery, cursor-move requery, paste
  suppression, single-result auto-apply.
- `packages/tui/test/select-list.test.ts` — selection wrap and position row.

## Known surface differences outside this milestone

- Pi registers `/bug` and `/changelog`; PiSharp does not implement them. Slash
  completion is derived from the live registry, so they are absent. Frozen cases
  use queries where the registered catalogues agree.
- Pi exposes 43 `/login` candidate entries; PiSharp's provider catalogue is
  narrower (for example, Pi exposes OpenRouter OAuth and PiSharp does not).
  `/login` completion uses PiSharp's live provider surface with Pi's
  per-auth-method projection, dedupe, ordering, labels and insertion rules; the
  remaining catalogue breadth belongs to provider work.
