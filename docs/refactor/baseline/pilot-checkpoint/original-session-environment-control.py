from pathlib import Path
import sys
repo=Path('/home/philip/Downloads/pisharp-bootstrap/pisharp')
sys.path.insert(0,str(repo/'tools/parity/terminal'))
import run
original=run.environment
def environment(agent):
    result=original(agent)
    result.pop('PISHARP_SESSION_DIR',None)
    return result
run.environment=environment
run.main()
