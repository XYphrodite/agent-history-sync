# Flicker-Free Session Manager Design

## Goal

Make `agent-sync --manage` update navigation, messages, and confirmations without clearing and repainting the terminal window, while restoring the user's previous terminal contents when the manager exits.

## Current Problem

`SessionManagerApplication` calls `Render` after every command. `SpectreSessionManagerView.Render` begins every frame with `ESC[2J ESC[H`, which clears the complete screen before writing both panels again. The clear is visible as flicker and leaves the manager mixed with the caller's terminal history.

## Display Lifecycle

The view boundary will gain one asynchronous display-session method. `SessionManagerApplication.RunAsync` will execute its existing controller loop inside that method. Test views will execute the supplied loop directly; `SpectreSessionManagerView` will own the terminal-specific lifecycle.

The Spectre view will:

1. Enter the terminal alternate screen and hide the cursor once.
2. Start one `Spectre.Console.Live` display.
3. Run the controller loop inside the live-display callback.
4. Restore the cursor and leave the alternate screen in `finally`.

The restore sequence must run after normal exit, cancellation, or any exception. Entering and leaving the alternate screen must never happen once per navigation command.

## Frame Updates

The current panel-building and viewport calculations remain unchanged. Instead of writing individual renderables directly to the console, the view will build one immutable frame containing:

- the Codex and Grok panels;
- the current information/error/confirmation message line when present;
- the keyboard-help footer.

`Render` updates the live display target and refreshes it. It does not emit the full-screen clear sequence. Spectre's live renderer owns cursor-relative replacement of the previous frame.

## Messages and Confirmation

Writing directly to the console while a live display is active would corrupt its cursor accounting. `ShowMessage` therefore records the message for the next frame without immediately writing outside the live region.

For deletion confirmation, the view retains the most recently rendered state, places the prompt in the frame, refreshes the live target, reads the key, and then removes the prompt. The controller's following render displays the operation result or normal frame. Dynamic titles and messages continue to use `Text`, not markup parsing.

## Fallback and Scope

Manager mode already requires an interactive terminal. No redirected-input fallback is added. The implementation targets the existing Windows terminal support and Spectre.Console 0.57.2. Panel layout, key mappings, copy/delete semantics, catalog refresh, and session data are unchanged.

## Testing

- A composed manager run enters alternate screen once and leaves it once after `Q`.
- Multiple navigation commands update the live target without `ESC[2J`.
- Cursor and previous screen restoration occur when the loop throws or is cancelled.
- Confirmation text and persistent operation messages appear inside live frames.
- Existing layout, escaping, truncation, scrolling, minimum-size, controller, and CLI tests remain green.
- A Windows interactive smoke verifies that arrow-key navigation no longer visibly flashes and that exiting restores the prior PowerShell screen.

## Alternatives Rejected

- Replacing full clear with cursor-home and erase-tail would be smaller but would still rewrite the whole region and could visibly tear.
- A custom cell-by-cell terminal diff would duplicate Spectre's renderer and introduce unnecessary terminal-emulation complexity.

## Success Criteria

- No full-screen clear sequence is emitted while navigating.
- One live display serves the entire manager run.
- The prior terminal screen and cursor are restored on every exit path.
- All current manager behavior and safety checks remain intact.
