# Interactive keybindings

Interactive keybindings load from `keybindings.json` in the agent directory. The default directory is `~/.pisharp/agent`; set `PISHARP_AGENT_DIR` to use another directory.

Each JSON property names a supported action. Its value is one key or an array of keys. An empty array disables the action. For example:

```json
{
  "tui.editor.cursorLeft": ["ctrl+h", "alt+left"],
  "app.model.cycleForward": "ctrl+p",
  "app.model.cycleBackward": ["ctrl+shift+p", "alt+p"],
  "app.thinking.cycle": "shift+tab"
}
```

Only actions shown by `/hotkeys` are recognized. Unknown action names are ignored. Invalid JSON or a file larger than 256 KiB restores all defaults. `/reload` reads the file again, and `/hotkeys` shows the active assignments.

## Application actions

| Action | Default | Behavior |
|---|---|---|
| `app.interrupt` | Escape | Cancel or abort the current run |
| `app.clear` | Ctrl+C | Clear the editor; abort an active run |
| `app.exit` | Ctrl+D | Exit when the editor is empty |
| `app.thinking.cycle` | Shift+Tab | Cycle `off`, `minimal`, `low`, `medium`, `high`, and `xhigh` on reasoning-capable models |
| `app.model.select` | Ctrl+L | Open the searchable model selector |
| `app.model.cycleForward` | Ctrl+P | Switch to the next available model in the current catalogue scope |
| `app.model.cycleBackward` | Ctrl+Shift+P, Alt+P | Switch to the previous available model; Windows and WSL use Alt+P |
| `app.tools.expand` | Ctrl+O | Expand or collapse active-run tool output |
| `app.message.followUp` | Alt+Enter; Ctrl+Q on Windows and WSL | Queue a follow-up message during a run |
| `app.message.dequeue` | Alt+Up; Alt+Q on Windows and WSL | Restore queued messages during a run |

The model selector opens from Ctrl+L or `/model`; it supports fuzzy filtering and Tab switches between all models and the configured scope. The thinking, selector and model cycling actions run at the idle prompt and preserve the current draft. A model switch is saved in the active session. On terminals that do not report Ctrl+Shift+P separately, use its Alt+P alias or assign another key. PiSharp decodes common modified CSI-u character sequences, but does not negotiate the Kitty keyboard protocol.

During a run, the active transcript handles its own scroll and search actions before editor input. `/hotkeys` lists the full implemented subset; session navigation/settings dialogs, scoped-model configuration, thinking visibility, selection/copy, and complete context-sensitive conflict handling remain open.
