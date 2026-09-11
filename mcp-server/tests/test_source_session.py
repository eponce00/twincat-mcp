import asyncio
import os
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from twincat_mcp.cli import find_tc_automation_exe
from twincat_mcp.dispatch import run_shell_step
from twincat_mcp.host import HostError, ShellHost
from twincat_mcp.handlers.shell import handle_edit_plc_source
from twincat_mcp import safety
from twincat_mcp.tools.schemas import get_tool_schemas


class SourceSessionTests(unittest.TestCase):
    def test_delivered_rejection_preserves_receipt_instead_of_host_error(self):
        host = ShellHost(Path('unused.exe'))
        receipt = dict(Success=False, LoginAttempted=False, ErrorMessage='Configured ADS port mismatch')
        host._responses.put(dict(id=7, ok=False, command='matching-login', result=receipt))
        with patch.object(host, '_send_request', return_value=7):
            result = host._call_raw_locked('execute-step', dict(command='matching-login'), timeout=1)
        self.assertEqual(result['result'], receipt)

    def test_edit_cannot_replace_session(self):
        host = ShellHost(Path('unused.exe'))
        with patch.object(host, 'ensure_solution') as ensure:
            result, _ = host.execute_step('edit-plc-source', {}, 'sample.sln', None)
        self.assertFalse(result['dispatched'])
        ensure.assert_not_called()

    def test_mutations_never_replay_even_if_caller_allows_fallback(self):
        for command in ('matching-login', 'edit-plc-source'):
            with self.subTest(command=command):
                host = Mock()
                host.execute_step.side_effect = HostError('lost reply')
                with patch('twincat_mcp.dispatch.get_shell_host', return_value=host), patch('twincat_mcp.dispatch.run_tc_automation_with_progress') as cli:
                    result, _ = run_shell_step(command, {}, allow_fallback=True)
                self.assertTrue(result['outcomeUnknown'])
                cli.assert_not_called()

    def test_missing_hash_is_rejected_before_dispatch(self):
        with patch('twincat_mcp.handlers.shell.run_shell_step') as dispatch:
            asyncio.run(handle_edit_plc_source(dict(solutionPath='a', amsNetId='b', plcName='c', path='d', section='implementation', text=''), 0))
        dispatch.assert_not_called()

    def test_gates_and_schema(self):
        safety.disarm_dangerous_operations()
        for name in ('twincat_matching_login', 'twincat_edit_plc_source'):
            self.assertFalse(safety.check_armed_for_tool(name, {})[0])
        for command in ('matching-login', 'edit-plc-source'):
            self.assertIn(command, safety.DANGEROUS_BATCH_COMMANDS)
        schemas = {s.name: s for s in get_tool_schemas()}
        self.assertIn('expectedSha256', schemas['twincat_edit_plc_source'].inputSchema['required'])
        for name in ('configuration', 'platform'):
            self.assertIn(name, schemas['twincat_matching_login'].inputSchema['required'])
        self.assertTrue(schemas['twincat_read_plc_source'].annotations.readOnlyHint)

    def test_explicit_executable_never_silently_falls_back(self):
        with tempfile.TemporaryDirectory() as folder:
            exe = Path(folder) / 'isolated.exe'
            with patch.dict(os.environ, TWINCAT_AUTOMATION_EXE=str(exe)):
                with self.assertRaises(FileNotFoundError):
                    find_tc_automation_exe()
                exe.touch()
                self.assertEqual(find_tc_automation_exe(), exe.resolve())
