from pathlib import Path
import subprocess,time,json,sys
root=Path('/tmp/pisharp-phase-c-checkpoint')
deadline=time.monotonic()+7200
while 'COMPLETE run-1' not in (root/'driver.log').read_text():
 if time.monotonic()>deadline:raise SystemExit('Capture has not completed; no controls started.')
 time.sleep(10)
assert len(list((root/'run-1').glob('terminal-*/report.json')))==66
print('Full capture complete; starting untouched-baseline controls.',flush=True)
assert subprocess.run(['python3','/tmp/pisharp-phase-c-baseline-controls.py']).returncode==0
print('Controls complete; starting unchanged full-fixture candidate repeats.',flush=True)
raise SystemExit(subprocess.run(['python3','/tmp/finish-pisharp-phase-c.py']).returncode)
