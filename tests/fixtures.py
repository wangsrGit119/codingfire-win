# -*- coding: utf-8 -*-
"""Build a synthetic-but-schema-accurate fixture tree for every aggregated source,
plus the token totals each one must yield.

Schemas are taken from the reference parsers in _parsers/*.ts (and, for Gemini,
from the shape actually observed in this machine's ~/.gemini/tmp archives).
No real user content is involved; every value here is invented.
"""
import io
import json
import os
import shutil
import sqlite3

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.join(HERE, "_fixtures")

expected = {}


def w(rel, text):
    p = os.path.join(ROOT, rel)
    d = os.path.dirname(p)
    if d and not os.path.isdir(d):
        os.makedirs(d)
    io.open(p, "w", encoding="utf-8", newline="\n").write(text)


def wl(rel, objs):
    w(rel, "\n".join(json.dumps(o) for o in objs) + "\n")


if os.path.isdir(ROOT):
    shutil.rmtree(ROOT)
os.makedirs(ROOT)

TS_MS = 1789000000000          # 2026-09-08-ish, any recent-ish instant
ISO = "2026-09-08T10:00:00.000Z"

# ---------------------------------------------------------------- WorkBuddy ---
# rawUsage: prompt_tokens includes cache; completion already includes reasoning.
# input = 1000-300-100 = 600 ; total = 600 + 500 + 300 + 100 = 1500
wl("workbuddy/projects/p1/s1.jsonl", [{
    "id": "wb-1", "sessionId": "s1", "type": "message", "timestamp": TS_MS,
    "providerData": {"rawUsage": {
        "prompt_tokens": 1000, "completion_tokens": 500,
        "cache_read_input_tokens": 300, "cache_creation_input_tokens": 100,
    }},
}])
expected["workbuddy"] = 1500

# ---------------------------------------------------------------- CodeBuddy ---
# cacheRead from prompt_tokens_details.cached_tokens; reasoning added to output.
# input = 2000-400 = 1600 ; total = 1600 + (300+50) + 400 + 0 = 2350
wl("codebuddy/projects/p1/s1.jsonl", [{
    "uuid": "cb-1", "sessionId": "s1", "type": "message", "role": "assistant",
    "timestamp": TS_MS,
    "providerData": {"rawUsage": {
        "prompt_tokens": 2000, "completion_tokens": 300,
        "prompt_tokens_details": {"cached_tokens": 400, "reasoning_tokens": 50},
    }},
}])
expected["codebuddy"] = 2350

# ------------------------------------------------------------------- Qoder ----
# (a) CLI transcript, Anthropic shape: input_tokens excludes cache.
#     100 + 20 + 30 + 5 = 155
wl("qoder/projects/p1/s1.jsonl", [{
    "type": "assistant", "timestamp": ISO,
    "message": {"id": "qd-1", "role": "assistant",
                "usage": {"input_tokens": 100, "output_tokens": 20,
                          "cache_read_input_tokens": 30,
                          "cache_creation_input_tokens": 5}},
}])
# (b) IDE local.db: chat_message.token_info, prompt_tokens includes cache.
#     input = 1000-400 = 600 ; total = 600 + 200 + 400 = 1200
#     the user row must be ignored
dbp = os.path.join(ROOT, "qoder-ide", "Qoder", "SharedClientCache", "cache", "db", "local.db")
if not os.path.isdir(os.path.dirname(dbp)):
    os.makedirs(os.path.dirname(dbp))
con = sqlite3.connect(dbp)
con.execute("CREATE TABLE chat_message (id TEXT, session_id TEXT, role TEXT, "
            "token_info TEXT, model_info TEXT, gmt_create INTEGER)")
con.execute("CREATE TABLE chat_session (session_id TEXT, project_name TEXT, project_uri TEXT)")
con.execute("INSERT INTO chat_message VALUES (?,?,?,?,?,?)",
            ("qid-1", "qs-1", "assistant",
             json.dumps({"prompt_tokens": 1000, "completion_tokens": 200,
                         "cached_tokens": 400}), "{}", TS_MS))
con.execute("INSERT INTO chat_message VALUES (?,?,?,?,?,?)",
            ("qid-2", "qs-1", "user",
             json.dumps({"prompt_tokens": 7777, "completion_tokens": 0,
                         "cached_tokens": 0}), "{}", TS_MS))
con.commit()
con.close()
expected["qoder"] = 155 + 1200

# --------------------------------------------------------------- Qwen Code ----
# promptTokenCount includes cached; thoughts are billed inside candidates.
# input = 500-200 = 300 ; output = 100 ; total = 300+100+200 = 600
wl("qwen/tmp/proj/chats/s.jsonl", [{
    "uuid": "qw-1", "type": "assistant", "timestamp": ISO,
    "usageMetadata": {"promptTokenCount": 500, "candidatesTokenCount": 100,
                      "cachedContentTokenCount": 200, "thoughtsTokenCount": 0},
}])
expected["qwen"] = 600

# ------------------------------------------------------------------- Kimi -----
# new shape: inputOther is the raw input, cache is separate.
# 1000 + 200 + 300 + 50 = 1550
wl("kimi-code/sessions/ws/sess1/wire.jsonl", [
    {"type": "config.update", "modelAlias": "kimi-k2"},
    {"type": "step.end", "uuid": "km-1", "time": TS_MS,
     "usage": {"inputOther": 1000, "output": 200,
               "inputCacheRead": 300, "inputCacheCreation": 50}},
])
# legacy shape: message.payload.token_usage, epoch *seconds*.
# 100 + 10 + 20 + 30 = 160
wl("kimi/sessions/ws/sess2/wire.jsonl", [{
    "timestamp": 1789000000,
    "message": {"type": "StatusUpdate",
                "payload": {"message_id": "km-legacy-1",
                            "token_usage": {"input_other": 100, "output": 10,
                                            "input_cache_read": 20,
                                            "input_cache_creation": 30}}},
}])
expected["kimi"] = 1550 + 160

# ---------------------------------------------------------------- Copilot -----
# one shutdown row carries every model's usage; merged into a single event.
# gpt-5: (1000-400) + 200 + 400 + 0 = 1200 ; claude: (100-0) + 50 + 0 + 10 = 160
wl("copilot/session-state/abc/events.jsonl", [{
    "type": "session.shutdown", "timestamp": ISO,
    "modelMetrics": {
        "gpt-5": {"usage": {"inputTokens": 1000, "outputTokens": 200,
                            "cacheReadTokens": 400, "cacheWriteTokens": 0}},
        "claude-sonnet-4.5": {"usage": {"inputTokens": 100, "outputTokens": 50,
                                        "cacheReadTokens": 0, "cacheWriteTokens": 10}},
    },
}])
expected["copilot"] = 1360

# ------------------------------------------------------- ZCode / OpenCode -----
# Same message-table shape as OpenCode, but ZCode excludes anthropic/openai/google
# providers because those are the embedded sub-agents counted by their own sources.
# kept   : 100 + (50+10) + 20 + 5 = 185
# dropped: providerID anthropic  -> 9999 (must not be counted)
for name, path in (("zcode", "zcode/cli/db/db.sqlite"),):
    dbp = os.path.join(ROOT, path)
    if not os.path.isdir(os.path.dirname(dbp)):
        os.makedirs(os.path.dirname(dbp))
    con = sqlite3.connect(dbp)
    con.execute("CREATE TABLE message (id TEXT PRIMARY KEY, session_id TEXT, data TEXT)")
    con.execute("INSERT INTO message VALUES (?,?,?)", ("zc-1", "sess-1", json.dumps({
        "role": "assistant", "providerID": "zhipu", "time": {"completed": TS_MS},
        "tokens": {"input": 100, "output": 50, "reasoning": 10,
                   "cache": {"read": 20, "write": 5}}})))
    con.execute("INSERT INTO message VALUES (?,?,?)", ("zc-2", "sess-1", json.dumps({
        "role": "assistant", "providerID": "anthropic", "time": {"completed": TS_MS},
        "tokens": {"input": 9999, "output": 0, "reasoning": 0,
                   "cache": {"read": 0, "write": 0}}})))
    con.execute("INSERT INTO message VALUES (?,?,?)", ("zc-3", "sess-1", json.dumps({
        "role": "user", "time": {"completed": TS_MS},
        "tokens": {"input": 777, "output": 0, "reasoning": 0,
                   "cache": {"read": 0, "write": 0}}})))
    con.commit()
    con.close()
expected["zcode"] = 185

# legacy OpenCode layout (storage/message/*.json) — only used when no db exists.
# 40 + (10+2) + 8 + 0 = 60
w("opencode/storage/message/m1.json", json.dumps({
    "id": "oc-1", "sessionID": "s1", "role": "assistant",
    "time": {"completed": TS_MS},
    "tokens": {"input": 40, "output": 10, "reasoning": 2,
               "cache": {"read": 8, "write": 0}}}))
expected["opencode"] = 60

# --------------------------------------------------------------- Gemini CLI ---
# .json archive: cumulative per-message snapshots; user messages carry no tokens.
# 500 + (100+20+30) + 200 = 850  (total field must not double it)
w("gemini/tmp/proj/chats/session-2026-09-08T10-00-abc.json", json.dumps({
    "sessionId": "g-1", "startTime": ISO, "lastUpdated": ISO, "projectHash": "deadbeef",
    "messages": [
        {"id": "g-u1", "type": "user", "timestamp": ISO, "content": "hi"},
        {"id": "g-a1", "type": "gemini", "timestamp": ISO, "tokens": {
            "cached": 200, "input": 500, "output": 100, "thoughts": 20, "tool": 30,
            "total": 850}},
    ],
}))
# .jsonl variant: one message per line, must go through the line parser.
wl("gemini/tmp/proj/chats/extra.jsonl", [{
    "id": "g-a2", "type": "gemini", "timestamp": ISO,
    "tokens": {"cached": 0, "input": 10, "output": 5, "thoughts": 0, "tool": 0,
               "total": 15}}])
expected["gemini"] = 850 + 15

# ------------------------------------------------------------------- Droid ----
# cumulative session snapshot: totalTokens is authoritative.
w("factory/sessions/dr-1/dr-1.settings.json", json.dumps({
    "tokenUsage": {"inputTokens": 100, "outputTokens": 200,
                   "cacheReadTokens": 300, "cacheCreationTokens": 10,
                   "thinkingTokens": 5, "totalTokens": 615}}))
expected["droid"] = 615

# --------------------------------------------- Cline / Roo Code / Kilo Code ---
# ui_messages.json is a JSON array; the accounting hides in say=api_req_started.
for src, ext in (("cline", "saoudrizwan.claude-dev"),
                 ("roocode", "rooveterinaryinc.roo-cline"),
                 ("kilocode", "kilocode.kilo-code")):
    w("vscode/%s/tasks/task-1/ui_messages.json" % ext, json.dumps([
        {"type": "say", "say": "text", "text": "ignore me", "ts": 1},
        {"type": "say", "say": "api_req_started", "ts": TS_MS,
         "text": json.dumps({"tokensIn": 70, "tokensOut": 30,
                             "cacheReads": 20, "cacheWrites": 10})},
    ]))
    expected[src] = 130

# ------------------------------------------------------ DeepSeek Harness ------
# inputTokens excludes cache; totalTokens covers the four plain columns.
wl("dsh/sessions/ws/sess9/session.jsonl", [{
    "type": "assistant", "data": {"id": "dsh-1", "time": TS_MS,
        "usage": {"inputTokens": 300, "outputTokens": 40,
                  "cacheReadTokens": 500, "cacheWriteTokens": 10,
                  "reasoningTokens": 7, "totalTokens": 850}}},
])
expected["dsh"] = 850

# ----------------------------------------------------------- Command Code -----
# inputTokens includes cache here (subtracted); one line, one delta.
# input = 900-600 = 300 ; total = 300 + 80 + 600 + 20 = 1000
wl("commandcode/projects/p/s.jsonl", [{
    "type": "message", "timestamp": TS_MS,
    "message": {"id": "cc-1", "role": "assistant",
                "usage": {"inputTokens": 900, "outputTokens": 80,
                          "cacheReadTokens": 600, "cacheWriteTokens": 20}},
}])
expected["command-code"] = 1000

# ---------------------------------------------------------------- OpenClaw ----
# msg.usage, agent dir as owner.  Reference subtracts cacheRead from the input
# column and then carries cacheRead separately, so the total is
# (100-30) + 20 + 30 + 5 = 125.
wl("openclaw/agents/agentA/sessions/s.jsonl", [{
    "type": "message", "timestamp": TS_MS,
    "msg": {"id": "oc-1", "role": "assistant",
            "usage": {"input": 100, "output": 20, "cacheRead": 30, "cacheWrite": 5}},
}])
expected["openclaw"] = 125

# --------------------------------------------------------------- Every Code ---
# last_token_usage is already a per-turn delta -> taken verbatim.
# cached is subtracted from input and re-added; reasoning likewise on output, so
# the total collapses to input_tokens + output_tokens + cache_creation
# = 1000 + 420 + 0 = 1420.  (The reported total_tokens field is deliberately
# ignored by the reference's normalizer, so it must not be trusted here either.)
wl("everycode/sessions/2026/rollout.jsonl", [{
    "timestamp": ISO,
    "payload": {"type": "token_count", "session_id": "ec-1",
        "info": {"last_token_usage": {"input_tokens": 1000, "cached_input_tokens": 300,
                                      "output_tokens": 420,
                                      "reasoning_output_tokens": 20,
                                      "total_tokens": 1400}}},
}])
expected["every-code"] = 1420

io.open(os.path.join(HERE, "_fixtures_expected.json"), "w", encoding="utf-8").write(
    json.dumps(expected, indent=2, sort_keys=True))
print(json.dumps(expected, indent=2, sort_keys=True))
