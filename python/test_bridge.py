"""Integration tests with real ARFT and a fake ADB; no phone is accessed."""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


@unittest.skipIf(os.name == 'nt', 'POSIX fake executable; Windows portable import is checked by packaging')
class BridgeTests(unittest.TestCase):
    def test_transfer_selection_atomicity_and_errors(self):
        temp_root = '/tmp/opencode' if Path('/tmp/opencode').is_dir() else None
        with tempfile.TemporaryDirectory(prefix='arft-bridge-test-', dir=temp_root) as directory:
            root = Path(directory)
            adb = root / 'fake-adb'
            adb.write_text('#!' + sys.executable + '\n'
                           'import os,sys,pathlib\n'
                           'assert os.environ["ANDROID_SERIAL"] == "selected-device"\n'
                           'assert sys.argv[1] == "pull"\n'
                           'pathlib.Path(sys.argv[3]).write_bytes(b"payload")\n'
                           'print("fake adb output must not corrupt JSON protocol")\n')
            adb.chmod(0o700)
            good = root / 'good'
            wrong = root / 'wrong-size'
            occupied = root / 'occupied'
            occupied.write_text('foreign')
            requests = [dict(remote='/sdcard/été.txt', staging=str(good), size=7),
                        dict(remote='/sdcard/wrong', staging=str(wrong), size=8),
                        dict(remote='/sdcard/existing', staging=str(occupied), size=7)]
            result = subprocess.run([sys.executable, str(Path(__file__).with_name('bridge.py')),
                                     '--adb', str(adb), '--serial', 'selected-device'],
                                    input=''.join(json.dumps(r) + '\n' for r in requests),
                                    capture_output=True, text=True, timeout=20)
            self.assertEqual(result.returncode, 0, result.stderr)
            responses = [json.loads(line) for line in result.stdout.splitlines()]
            self.assertEqual([r['ok'] for r in responses], [True, False, False])
            self.assertEqual(good.read_bytes(), b'payload')
            self.assertFalse(wrong.exists())
            self.assertFalse(wrong.with_name(wrong.name + '.part').exists())
            self.assertEqual(occupied.read_text(), 'foreign')


if __name__ == '__main__':
    unittest.main()
