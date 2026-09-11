import asyncio
import json
import sys
import unittest
from pathlib import Path
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from twincat_mcp.dispatch import run_shell_step
from twincat_mcp.host import HostError, ShellHost
from twincat_mcp.handlers.shell import handle_online_change
from twincat_mcp import safety
from twincat_mcp.tools.schemas import get_tool_schemas


class OnlineChangeTests(unittest.TestCase):
    def test_online_change_cannot_replace_or_start_host_session(self):
        host = ShellHost(Path('unused.exe'))
        with patch.object(host, 'ensure_solution') as ensure, patch.object(host, '_start_locked') as start:
            result, _ = host.execute_step('online-change', {}, 'sample.sln', None)
        self.assertFalse(result['dispatched'])
        ensure.assert_not_called()
        start.assert_not_called()

    def test_uncertain_dispatch_is_never_replayed(self):
        host = Mock()
        host.execute_step.side_effect = HostError("connection lost after dispatch")
        with patch('twincat_mcp.dispatch.get_shell_host', return_value=host), patch('twincat_mcp.dispatch.run_tc_automation_with_progress') as cli:
            result, _ = run_shell_step('online-change', {}, allow_fallback=False)
        self.assertFalse(result['success'])
        self.assertTrue(result['outcomeUnknown'])
        host.execute_step.assert_called_once()
        cli.assert_not_called()

    def test_missing_host_does_not_open_one_shot_session(self):
        with patch('twincat_mcp.dispatch.get_shell_host', return_value=None), patch('twincat_mcp.dispatch.run_tc_automation_with_progress') as cli:
            result, _ = run_shell_step('online-change', {}, allow_fallback=False)
        self.assertFalse(result['dispatched'])
        cli.assert_not_called()

    def test_handler_requires_explicit_target(self):
        with patch('twincat_mcp.handlers.shell.run_shell_step') as dispatch:
            asyncio.run(handle_online_change({'solutionPath': 'sample.sln'}, 0))
        dispatch.assert_not_called()

    def test_handler_disables_replay_and_preserves_unverified_receipt(self):
        arguments = dict(solutionPath='sample.sln', amsNetId='1.2.3.4.1.1', plcName='PLC',
                         port=854, cycleSymbol='MAIN.cycles', expectedOnlineChangeCount=0)
        with patch('twincat_mcp.handlers.shell.run_shell_step', return_value=({'success': False, 'dispatched': True, 'runtimeVerified': False}, [])) as dispatch:
            result = asyncio.run(handle_online_change(arguments, 0))
        self.assertFalse(dispatch.call_args.kwargs['allow_fallback'])
        self.assertFalse(json.loads(result[0].text)['runtimeVerified'])

    def test_direct_and_batch_gates(self):
        safety.disarm_dangerous_operations()
        self.assertFalse(safety.check_armed_for_tool('twincat_online_change', {})[0])
        self.assertFalse(safety.check_confirmation('twincat_online_change', {})[0])
        self.assertIn('online-change', safety.DANGEROUS_BATCH_COMMANDS)
        self.assertIn('online-change', safety.CONFIRMATION_REQUIRED_BATCH_COMMANDS)

    def test_schema_has_no_default_target_and_requires_expected_counter(self):
        schema = next(t for t in get_tool_schemas() if t.name == 'twincat_online_change')
        self.assertNotIn('default', schema.inputSchema['properties']['amsNetId'])
        self.assertIn('expectedOnlineChangeCount', schema.inputSchema['required'])
        self.assertFalse(schema.annotations.idempotentHint)


if __name__ == '__main__':
    unittest.main()
