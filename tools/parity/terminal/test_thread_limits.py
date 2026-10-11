import json
import os
import subprocess
import sys
import unittest

from thread_limits import TEST_THREAD_LIMIT, apply_test_thread_limit, thread_limit_environment


apply_test_thread_limit()


class ThreadLimitTests(unittest.TestCase):
    def test_child_python_process_inherits_two_cpu_limit(self):
        environment = os.environ.copy()
        environment.update(thread_limit_environment())
        child = subprocess.run(
            [sys.executable, '-c',
             'import json, os; print(json.dumps({"affinity": len(os.sched_getaffinity(0)) if '
             'hasattr(os, "sched_getaffinity") else None, "cpuCount": os.process_cpu_count() if '
             'hasattr(os, "process_cpu_count") else None}))'],
            check=True, capture_output=True, text=True, env=environment)

        self.assertEqual('2', environment['PYTHON_CPU_COUNT'])
        limits = json.loads(child.stdout)
        if limits['affinity'] is not None:
            self.assertLessEqual(limits['affinity'], TEST_THREAD_LIMIT)
        if limits['cpuCount'] is not None:
            self.assertLessEqual(limits['cpuCount'], TEST_THREAD_LIMIT)


if __name__ == '__main__':
    unittest.main()
