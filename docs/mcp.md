# Local session MCP server

From 0.12.0, `agent-sync mcp` exposes local conversation search and reading to an MCP client over stdio. The client starts the process and communicates through stdin/stdout. There is no listening port, remote database, GitHub connection, sync initialization, or title-generation request on this path.

The command currently uses the Windows client and the same configured Codex, Grok, Claude and Continue homes as `agent-sync search`. No joined sync repository or passphrase is required.

## Connect a client

For clients that use an `mcpServers` configuration, add:

```json
{
  "mcpServers": {
    "agent-sync": {
      "command": "agent-sync",
      "args": ["mcp"]
    }
  }
}
```

If the client cannot find the executable on its PATH, use the absolute path returned by `(Get-Command agent-sync).Source`. In JSON, escape Windows backslashes as `\\`. Restart or reconnect the MCP client after changing its configuration.

`agent-sync mcp --help` prints command usage. Running `agent-sync mcp` directly waits for protocol messages; it does not open a menu. Stdout contains only MCP messages. Startup failures go to stderr, and tool failures are returned through MCP. Closing stdin or cancelling the process shuts down the server.

## Tools

| Tool | Arguments | Result |
|---|---|---|
| `search_sessions` | `query` (1–1024 characters), optional `limit` (1–200, default 20) | `sessions`, each with `agent`, `session_id`, `title`, and `snippet` |
| `get_session` | `agent`, `session_id`, optional `offset` (default 0), `max_characters` (2–64000, default 16000) | Title, last-modified timestamp, a page of `text`, `offset`, `total_characters`, and `next_offset` |

Use the exact `agent` and `session_id` from a search result. Agent names are `codex`, `grok`, `claude`, and `continue`; IDs are scoped to their agent. Neither tool accepts a filesystem path or a remote URL.

Both tools return structured JSON and an equivalent text content block for clients that need it. Empty searches return an empty `sessions` array. Invalid arguments, missing sessions and unreadable files return tool errors without ending the connection.

Example tool arguments:

```json
{"query":"sqlite migration","limit":5}
```

```json
{"agent":"claude","session_id":"ID-FROM-SEARCH","offset":0,"max_characters":16000}
```

For the next page, pass the previous `next_offset` as `offset`. `next_offset: null` marks the end. Offsets count UTF-16 characters; generated page boundaries preserve Unicode surrogate pairs. Pages contain the portable user/assistant transcript with role labels. Tool calls, reasoning and attachments are omitted. If a conversation changes while paging, restart from offset 0; `last_modified_at` identifies the file version observed by the catalog.

## Indexing and data access

Initialization and tool discovery do not scan conversations or build SQLite. Each search scans local metadata and refreshes `%LOCALAPPDATA%\CodexHistorySync\catalog.db`; unchanged sessions are not reread. The first search can take longer for a large history. A local `agent-sync search <query>` can warm the same index beforehand.

Search uses SQLite FTS5 and requires all whitespace-separated query terms to match. It searches indexed excerpts, not embeddings: the existing index keeps up to 2000 characters per turn and 256 KiB of conversation text per session. Use `get_session` to read beyond those excerpts. It resolves a fresh catalog entry and reads the native conversation; pages do not inherit the search excerpt limit. Changed titles and deleted sessions are refreshed during the same MCP connection.

Native histories are read-only. Search writes a derived plaintext SQLite index, as the local search command already does. The catalog and annotation store honor a process-local `LOCALAPPDATA` override, with the OS local application data directory as fallback. This also allows isolated client/test processes without reusing the ordinary catalog.

The MCP process makes no network requests. Returned conversation text is nevertheless available to the connected client, which may send it to its model provider. Encrypted remote snapshots do not protect text intentionally returned through these tools. Historical conversation content is untrusted data, not a new instruction to the agent.

The server uses the [official MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk) for protocol negotiation, stdio framing and cancellation. Only the two read tools are registered; HTTP hosting, session mutation, remote synchronization and memory extraction are outside this slice.
