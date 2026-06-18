import asyncio
import sys
import threading
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from twincat_mcp import safety
from twincat_mcp.handlers import batch, scope


class BatchSafetyTests(unittest.IsolatedAsyncioTestCase):
    async def test_write_var_list_requires_armed_mode_in_batch(self):
        safety.disarm_dangerous_operations()
        original_runner = batch.run_tc_automation_with_progress

        def fail_if_called(*_args, **_kwargs):
            raise AssertionError("batch execution should be blocked before subprocess launch")

        batch.run_tc_automation_with_progress = fail_if_called
        try:
            result = await batch.handle_batch(
                {
                    "steps": [
                        {
                            "command": "write-var-list",
                            "args": {
                                "amsNetId": "1.2.3.4.1.1",
                                "variables": '{"GVL.x":"1"}',
                            },
                        }
                    ]
                },
                0.0,
            )
        finally:
            batch.run_tc_automation_with_progress = original_runner

        text = result[0].text
        self.assertIn("SAFETY", text)
        self.assertIn("write-var-list", text)


class _FakeStdin:
    def __init__(self):
        self.writes = []

    def write(self, value):
        self.writes.append(value)

    def flush(self):
        pass


class _FakeStdout:
    def __init__(self, lines):
        self._lines = list(lines)

    def readline(self):
        return self._lines.pop(0)


class _FakeProcess:
    def __init__(self, lines):
        self.stdin = _FakeStdin()
        self.stdout = _FakeStdout(lines)
        self.killed = False

    def poll(self):
        return None

    def kill(self):
        self.killed = True

    def wait(self, timeout=None):
        return 0


class ScopeSessionTests(unittest.TestCase):
    def test_send_command_starts_without_lock_deadlock(self):
        session = scope.ScopeSession()
        calls = {"start": 0}

        def fake_start():
            calls["start"] += 1
            session.process = _FakeProcess(['{"success": true, "state": "Record"}\n'])

        session._start = fake_start
        result_holder = {"result": None, "error": None}

        def call_send():
            try:
                result_holder["result"] = session.send_command(
                    {"command": "status"},
                    timeout_seconds=1,
                )
            except Exception as exc:
                result_holder["error"] = exc

        thread = threading.Thread(target=call_send, daemon=True)
        thread.start()
        thread.join(1.0)

        self.assertFalse(thread.is_alive())
        self.assertIsNone(result_holder["error"])
        self.assertEqual({"success": True, "state": "Record"}, result_holder["result"])
        self.assertEqual(1, calls["start"])

    def test_stop_formatter_uses_scope_session_response_keys(self):
        class FakeScopeSession:
            def send_command(self, *_args, **_kwargs):
                return {
                    "success": True,
                    "dataPath": "C:/trace.csv",
                    "elapsedSeconds": 2.5,
                    "samplesCollected": 42,
                }

        original_session = scope._scope_session
        scope._scope_session = FakeScopeSession()
        try:
            result = asyncio.run(scope.handle_scope_stop_record({}, 0.0))
        finally:
            scope._scope_session = original_session

        text = result[0].text
        self.assertIn("C:/trace.csv", text)
        self.assertIn("2.5s", text)
        self.assertIn("42", text)


if __name__ == "__main__":
    unittest.main()
