"""Durable receipts and a bounded, single-worker queue.

The transport never owns an operation lifetime. Closing an MCP request leaves its
job inspectable. No execution is retried; request keys deduplicate submissions.
"""

import hashlib
import json
import queue
import secrets
import sqlite3
import threading
import time
from pathlib import Path

from .errors import Cancelled, OperationError

TERMINAL = {"succeeded", "failed", "cancelled", "outcome_unknown"}


class Jobs:
    def __init__(self, path, runner):
        self.path = Path(path)
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self.runner = runner
        self.owner = secrets.token_urlsafe(24)
        self.lock = threading.RLock()
        self.db = sqlite3.connect(self.path, check_same_thread=False)
        self.db.execute("PRAGMA journal_mode=WAL")
        self.db.execute("PRAGMA busy_timeout=5000")
        self.db.executescript("""
            CREATE TABLE IF NOT EXISTS owners (id TEXT PRIMARY KEY, heartbeat REAL NOT NULL);
            CREATE TABLE IF NOT EXISTS jobs (
                handle TEXT PRIMARY KEY, request_key TEXT UNIQUE NOT NULL,
                fingerprint TEXT NOT NULL, operation TEXT NOT NULL, owner TEXT NOT NULL,
                state TEXT NOT NULL, created REAL NOT NULL, updated REAL NOT NULL,
                progress TEXT NOT NULL, result TEXT, cancel_requested INTEGER NOT NULL DEFAULT 0);
        """)
        self.db.execute("INSERT INTO owners VALUES (?,?)", (self.owner, time.time()))
        self.db.commit()
        self.pending = queue.Queue(maxsize=64)
        self.events = {}
        self.closing = threading.Event()
        self.worker = threading.Thread(target=self._work, daemon=True, name="twincat-operations")
        self.pulse = threading.Thread(target=self._heartbeat, daemon=True, name="twincat-receipts")
        self.worker.start()
        self.pulse.start()

    def _heartbeat(self):
        while not self.closing.wait(2):
            with self.lock:
                self.db.execute(
                    "UPDATE owners SET heartbeat=? WHERE id=?", (time.time(), self.owner)
                )
                self.db.commit()

    def submit(self, operation, arguments, request_key):
        encoded = json.dumps(
            [operation, arguments], sort_keys=True, separators=(",", ":"), ensure_ascii=False
        )
        fingerprint = hashlib.sha256(encoded.encode()).hexdigest()
        with self.lock:
            if self.closing.is_set():
                raise OperationError("shutting_down", "Server is shutting down.")
            # Serialize both duplicate detection and insertion across server processes.
            self.db.execute("BEGIN IMMEDIATE")
            try:
                old = self.db.execute(
                    "SELECT handle,fingerprint FROM jobs WHERE request_key=?", (request_key,)
                ).fetchone()
                if old:
                    if old[1] != fingerprint:
                        raise OperationError(
                            "request_key_conflict",
                            "Reuse of requestKey with different operation/arguments is prohibited.",
                        )
                    self.db.commit()
                    return self.get(old[0])
                if self.pending.full():
                    raise OperationError(
                        "queue_full",
                        "Queue is full; retry submission later using the same requestKey.",
                    )
                handle = secrets.token_urlsafe(32)
                now = time.time()
                self.db.execute(
                    "INSERT INTO jobs VALUES (?,?,?,?,?,?,?,?,?,?,?)",
                    (
                        handle,
                        request_key,
                        fingerprint,
                        operation,
                        self.owner,
                        "queued",
                        now,
                        now,
                        "[]",
                        None,
                        0,
                    ),
                )
                self.db.commit()
            except Exception:
                self.db.rollback()
                raise
            event = threading.Event()
            self.events[handle] = event
            self.pending.put_nowait((handle, operation, arguments, event))
            return self.get(handle)

    def _row(self, handle):
        self.db.row_factory = sqlite3.Row
        row = self.db.execute("SELECT * FROM jobs WHERE handle=?", (handle,)).fetchone()
        if row is None:
            raise OperationError("unknown_job", "Unknown job handle.")
        if row["state"] not in TERMINAL:
            owner = self.db.execute(
                "SELECT heartbeat FROM owners WHERE id=?", (row["owner"],)
            ).fetchone()
            if not owner or owner[0] < time.time() - 30:
                receipt = dict(
                    success=False,
                    code="worker_lost",
                    outcomeUnknown=True,
                    message="Owning server stopped heartbeating. Inspect target before another operation; no replay occurred.",
                )
                self.db.execute(
                    "UPDATE jobs SET state='outcome_unknown',result=?,updated=? WHERE handle=?",
                    (json.dumps(receipt), time.time(), handle),
                )
                self.db.commit()
                row = self.db.execute("SELECT * FROM jobs WHERE handle=?", (handle,)).fetchone()
        return row

    def get(self, handle):
        with self.lock:
            row = self._row(handle)
            receipt = dict(
                jobHandle=handle,
                operation=row["operation"],
                state=row["state"],
                cancelRequested=bool(row["cancel_requested"]),
                progress=json.loads(row["progress"]),
                createdAt=row["created"],
                updatedAt=row["updated"],
                resultAvailable=row["result"] is not None,
                nextOperation="job.result" if row["result"] is not None else "job.get",
            )
            if row["result"] is not None and len(row["result"]) <= 4000:
                receipt["result"] = json.loads(row["result"])
            return receipt

    def result(self, handle, offset=0, limit=8000):
        with self.lock:
            row = self._row(handle)
            if row["result"] is None:
                raise OperationError("result_pending", "Job has not finished; poll job.get.")
            text = row["result"]
            if offset > len(text):
                raise OperationError("invalid_offset", "Offset exceeds result length.")
            return dict(
                jobHandle=handle,
                state=row["state"],
                text=text[offset : offset + limit],
                offset=offset,
                nextOffset=offset + limit if offset + limit < len(text) else None,
                totalCharacters=len(text),
            )

    def cancel(self, handle):
        with self.lock:
            row = self._row(handle)
            if row["state"] not in TERMINAL:
                self.db.execute(
                    "UPDATE jobs SET cancel_requested=1,updated=? WHERE handle=?",
                    (time.time(), handle),
                )
                self.db.commit()
                if handle in self.events:
                    self.events[handle].set()
            return self.get(handle)

    def checkpoint(self, handle, event):
        with self.lock:
            row = self._row(handle)
            if row["cancel_requested"] or row["state"] == "outcome_unknown":
                event.set()
        if event.is_set():
            raise Cancelled()

    def progress(self, handle, message):
        with self.lock:
            row = self._row(handle)
            items = json.loads(row["progress"])
            items.append(str(message)[:1000])
            self.db.execute(
                "UPDATE jobs SET progress=?,updated=? WHERE handle=?",
                (json.dumps(items[-12:]), time.time(), handle),
            )
            self.db.commit()

    def _work(self):
        while not self.closing.is_set() or not self.pending.empty():
            try:
                handle, operation, arguments, event = self.pending.get(timeout=0.2)
            except queue.Empty:
                continue
            try:
                self.checkpoint(handle, event)
                with self.lock:
                    self.db.execute(
                        "UPDATE jobs SET state='running',updated=? WHERE handle=?",
                        (time.time(), handle),
                    )
                    self.db.commit()
                result = self.runner(
                    operation,
                    arguments,
                    lambda h=handle, e=event: self.checkpoint(h, e),
                    lambda message, h=handle: self.progress(h, message),
                )
                state = (
                    "outcome_unknown"
                    if result.get("outcomeUnknown")
                    else "cancelled"
                    if result.get("code") == "cancelled"
                    else "succeeded"
                    if result.get("success", False)
                    else "failed"
                )
            except Cancelled as exc:
                state, result = "cancelled", dict(success=False, code=exc.code, message=str(exc))
            except OperationError as exc:
                state, result = "failed", dict(success=False, code=exc.code, message=str(exc))
            except Exception as exc:
                state, result = (
                    "outcome_unknown",
                    dict(
                        success=False,
                        code="unexpected_failure",
                        outcomeUnknown=True,
                        message=str(exc),
                    ),
                )
            try:
                serialized = json.dumps(result, ensure_ascii=False, allow_nan=False)
            except (TypeError, ValueError):
                state = "outcome_unknown"
                serialized = json.dumps(
                    dict(
                        success=False,
                        outcomeUnknown=True,
                        code="invalid_receipt",
                        message="Backend result was not JSON serializable; no replay occurred.",
                    )
                )
            with self.lock:
                # A stale owner must not overwrite a conservative unknown receipt.
                row = self._row(handle)
                if row["state"] != "outcome_unknown":
                    self.db.execute(
                        "UPDATE jobs SET state=?,result=?,updated=? WHERE handle=?",
                        (state, serialized, time.time(), handle),
                    )
                    self.db.commit()
                self.events.pop(handle, None)
            self.pending.task_done()

    def close(self):
        self.closing.set()
        with self.lock:
            for event in self.events.values():
                event.set()
            self.db.execute(
                "UPDATE jobs SET state='cancelled',cancel_requested=1,result=? WHERE owner=? AND state='queued'",
                (
                    json.dumps(
                        dict(
                            success=False, code="shutdown", message="Server closed before dispatch."
                        )
                    ),
                    self.owner,
                ),
            )
            self.db.execute("UPDATE owners SET heartbeat=0 WHERE id=?", (self.owner,))
            self.db.commit()
        self.worker.join(timeout=2)
        self.pulse.join(timeout=2)
        # A native command may still finish; its durable receipt remains unknown.
        if not self.worker.is_alive():
            self.db.close()
