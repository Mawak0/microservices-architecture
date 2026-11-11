"""Regression checks for discovery in the actual embedded workflow script."""
import json
import os
import re
from pathlib import Path
import tempfile
import textwrap
import unittest
from unittest.mock import patch


class CoverageDiscoveryTests(unittest.TestCase):
    def test_branch_measurement_provides_real_database_and_private_object_storage(self):
        workflow = (Path(__file__).resolve().parents[1] /
                    '.github/workflows/coverage-badge.yml').read_text(encoding='utf-8')
        measurement = workflow.split('\n  measure:', 1)[1].split('\n  publish:', 1)[0]
        services = measurement.split('\n    services:\n', 1)[1].split('\n    env:\n', 1)[0]
        environment = measurement.split('\n    env:\n', 1)[1].split('\n    steps:\n', 1)[0]
        self.assertRegex(services, r'(?m)^      postgres:\n        image: postgres:17\.6$')
        self.assertRegex(services, r'(?m)^      s3:\n        image: versity/versitygw:v1\.11\.0$')
        self.assertIn("ports: ['5432:5432']", services)
        self.assertIn("ports: ['17070:7070']", services)
        self.assertIn("volumes: ['/data']", services)
        self.assertIn('pg_isready -U olympiad_test -d olympiad_tests', services)
        for key in ['TEST_POSTGRES_CONNECTION', 'TEST_S3_ENDPOINT',
                    'TEST_S3_ACCESS_KEY', 'TEST_S3_SECRET_KEY']:
            self.assertRegex(environment, rf'(?m)^      {key}: .+$')
        self.assertIn('Database=olympiad_tests;Username=olympiad_test;Password=local-ci-only', environment)
        self.assertIn('http://127.0.0.1:17070', environment)

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
