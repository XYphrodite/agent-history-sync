"""Prepare synthetic Muse files for an ext4/VHDX CI fixture; no real user data."""
import json
from pathlib import Path
import sqlite3
import sys

root = Path(sys.argv[1]) / "home/linux-user/.local/share/muse"
parent_id = "11111111-1111-4111-8111-111111111111"
child_id = "22222222-2222-4222-8222-222222222222"
parent = root / "sessions/2026/09/25" / parent_id


def write(directory, turns):
    directory.mkdir(parents=True, exist_ok=True)
    (directory / "session.jsonl").write_text(
        "\n".join(json.dumps({"role": role, "text": text}) for role, text in turns),
        encoding="utf-8",
    )


write(parent, [("user", "hello offline"), ("assistant", "offline answer")])
write(parent / "subagent" / child_id, [("user", "child offline")])
write(parent / "tool-outputs" / child_id, [("user", "must not be a session")])
with sqlite3.connect(root / "session-index.db") as db:
    db.execute("CREATE TABLE sessions (session_id TEXT, title TEXT, session_name TEXT)")
    db.execute("INSERT INTO sessions VALUES (?, ?, ?)", (parent_id, "fallback", "Offline fixture title"))
