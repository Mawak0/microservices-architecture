"""Regression checks for discovery in the actual embedded workflow script."""
import json
import os
from pathlib import Path
import tempfile
import textwrap
import unittest
from unittest.mock import patch


class CoverageDiscoveryTests(unittest.TestCase):
    def test_coalesced_completion_preserves_every_branch(self):
        workflow = (Path(__file__).resolve().parents[1] /
                    '.github/workflows/coverage-badge.yml').read_text(encoding='utf-8')
        discovery = textwrap.dedent(workflow.split('shell: python\n        run: |\n', 1)[1]
                                   .split('\n  measure:', 1)[0])
        branches = [{'name': name, 'commit': {'sha': str(i) * 40}}
                    for i, name in enumerate(['main', 'materials-06', 'review-06'], 1)]
        for event_name in ['workflow_run', 'workflow_dispatch', 'push', 'create', 'delete']:
            with self.subTest(event=event_name), tempfile.TemporaryDirectory() as directory:
                output = Path(directory) / 'output'
                event = Path(directory) / 'event.json'
                event.write_text(json.dumps({'workflow_run': {'head_branch': 'review-06'}}), encoding='utf-8')
                with patch.dict(os.environ, {'GH_REPO': 'owner/repository',
                                             'GITHUB_OUTPUT': str(output),
                                             'GITHUB_EVENT_NAME': event_name,
                                             'GITHUB_EVENT_PATH': str(event)}), \
                        patch('subprocess.check_output', return_value=json.dumps([branches])):
                    exec(compile(discovery, 'coverage-discovery', 'exec'), {})
                matrix = json.loads(output.read_text(encoding='utf-8').removeprefix('matrix='))
                self.assertEqual([b['name'] for b in branches],
                                 [b['branch'] for b in matrix['include']])
                self.assertEqual([b['commit']['sha'] for b in branches],
                                 [b['sha'] for b in matrix['include']])


if __name__ == '__main__':
    unittest.main()
