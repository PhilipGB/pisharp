# PiSharp (early development)

.NET 10 Linux coding-agent prototype built on Microsoft Agent Framework `ChatClientAgent` and Microsoft.Extensions.AI. **Not yet Pi-compatible.** See [the parity matrix](docs/parity/feature-matrix.md) and [handoff](docs/continuation.md) before using for real work.

```sh
export OPENAI_API_KEY=... # or PISHARP_API_KEY
export PISHARP_MODEL=gpt-4o-mini
# Optional for a Chat Completions-compatible server (e.g. vLLM):
# export PISHARP_BASE_URL=http://localhost:8000/v1

dotnet run --project src/PiSharp.Cli -- --print 'Describe this repository'
dotnet run --project src/PiSharp.Cli
```

Run from the repository the agent should edit. `read`, `write`, `edit` and `bash` operate with the local process's filesystem permissions; there is **no sandbox**. The early interactive UI is a line-oriented REPL (not Pi's terminal UI). Conversations last for the process lifetime only. Do not load untrusted repository instructions or extensions; those features are not implemented. `--help` lists implemented flags. Do not use on sensitive files without reviewing model requests and generated commands.

```sh
dotnet build PiSharp.slnx --warnaserror
dotnet test PiSharp.slnx
```

The deterministic integration test simulates the provider issuing a tool call and receiving its result; no credentials are needed for tests. No upstream differential tests have been completed.
