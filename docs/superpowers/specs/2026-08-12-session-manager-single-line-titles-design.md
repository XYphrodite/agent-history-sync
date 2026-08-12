# Session manager single-line title design

Date: 2026-08-12

## Problem

The session manager can render a logical session as several terminal rows when its title contains line breaks. Spectre then produces a frame taller than the terminal, so the top of the frame—including the selected row—can be clipped above the visible screen. Grok sessions may also fall back to UUIDs because current metadata extraction recognizes `input_text` blocks but real Grok history uses `text` blocks.

## Intended behavior

- Every session occupies exactly one terminal row.
- Titles replace every run of whitespace, including line breaks and tabs, with one ordinary space and are trimmed.
- Empty normalized titles retain the existing session-ID fallback.
- Grok title extraction accepts user records whose role is stored in either `role` or `type`, and text blocks whose type is either `text` or `input_text`.
- Existing title length bounds, viewport navigation, focused panel, selection styling, timestamps, and safe markup rendering remain unchanged.

## Design

Normalize display titles at the catalog boundary so both Codex and Grok snapshots expose stable one-line titles. Extend Grok user-preview parsing to recognize its native `text` block shape while retaining compatibility with `input_text`.

Also normalize inside the terminal formatter as a defensive boundary. This protects the layout when a state is constructed from tests, future adapters, or other callers that bypass the local catalog. Truncation is applied after whitespace normalization, so the rendered value remains within the existing column budget.

No dynamic-height rows or alternate scrolling model will be introduced.

## Error handling and safety

Malformed Grok records continue to follow the existing conservative behavior. The change only broadens recognition of a known text-block type and does not make arbitrary JSON content executable or render it as Spectre markup.

## Verification

Regression tests will cover:

- native Grok `type: "user"` with a `type: "text"` content block produces a meaningful title instead of a UUID;
- repeated whitespace and line breaks in catalog titles become one line;
- a multi-line title supplied directly to the view remains one rendered row;
- with a constrained terminal height, the selected session stays inside the rendered viewport;
- existing manager/view and full solution suites remain green.

## Delivery

Implement in an isolated worktree from current `main`, review the focused diff, merge locally after green verification, rebuild `agent-sync.exe`, then retry pushing `main` to `origin`.
