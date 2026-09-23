# Pinned upstream CLI baseline

Reproduction (upstream checkout `002fc8385268300ca91a5fc95f935c2afbbdac02`; after `npm ci --ignore-scripts`, `npm run hydrate:model-data`, `npm run build:offline`):

```sh
node packages/coding-agent/dist/bundle/cli.js --version
node packages/coding-agent/dist/bundle/cli.js --help
```

Observed version: `0.87.1`. Observed initial help output (first lines; this is a **baseline fixture**, not a PiSharp passing comparison):

```text
pi - AI coding assistant with read, bash, edit, write tools

Usage:
  pi [options] [--] [@files...] [messages...]

Commands:
  pi install <source> [-l]     Install extension source and add to settings
  pi remove <source> [-l]      Remove extension source from settings
  pi uninstall <source> [-l]   Alias for remove
  pi update [source|self|pi]   Update pi, extensions, or model catalogs
  pi list                      List installed extensions from settings
  pi config [-l]               Open TUI to enable/disable package resources (Tab switches scope)
  pi auth <command>            Print credentials or check provider readiness
  pi <command> --help          Show help for install/remove/uninstall/update/list/config/auth

Options:
  --provider <name>              Provider name (default: google)
  --model <pattern>              Model pattern or ID (supports "provider/id" and optional ":<thinking>")
  --api-key <key>                API key (defaults to env vars)
  --system-prompt <text>         System prompt (default: coding assistant prompt)
  --append-system-prompt <text>  Append text or file contents to the system prompt (can be used multiple times)
  --mode <mode>                  Output mode: text (default), json, or rpc
  --print, -p                    Non-interactive mode: process prompt and exit
```

Do not compare help verbatim until the applicable modes and options exist; Pi Packages command lines are explicitly excluded. The `--provider` default can depend on machine configuration and should be checked in a clean environment before treating it as a contract.
