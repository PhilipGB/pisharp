"""Execute each applicable committed terminal semantic assertion script."""
import json
from pathlib import Path
import shutil

def assertions(repo, folder, fixture, run):
    stem=fixture.stem
    paired=folder/f'terminal-{stem}-paired'
    calibration=folder/f'terminal-{stem}-calibration'
    if not (paired/'report.json').exists() or not (calibration/'report.json').exists():
        return
    report=json.loads((paired/'report.json').read_text())
    case=report['cases'][0]
    capture=paired/case['artifact'] if 'artifact' in case else paired/f'{case["id"]}.json.gz'
    base=['--report',str(paired/'report.json'),'--capture',str(capture),'--output',str(folder/f'assert-{stem}.json')]
    cal=['--calibration-report',str(calibration/'report.json')]
    scripts={
        'extension-resource-discovery-reload':'assert_extension_resource_reload.py',
        'legacy-project-skills-reload':'assert_legacy_project_skills_reload.py',
        'legacy-project-skills-settings':'assert_legacy_project_skills.py',
        'trusted-project-settings-reload':'assert_trusted_project_reload.py',
        'project-trust-extension':'assert_project_trust_extension.py',
        'project-trust-extension-ui':'assert_project_trust_extension_ui.py',
    }
    if stem in scripts:
        script=scripts[stem]
        command=['python3',f'tools/parity/terminal/{script}',*base]
        if stem!='extension-resource-discovery-reload':
            command+=cal
        if stem.startswith('project-trust-extension'):
            # Preserve the synthetic extension trace before the next fixture resets it.
            log=Path('/tmp/pisharp-terminal-fixture-agent/project-trust-extension.log')
            saved=paired/'extension-trace.log'
            if not saved.exists() and log.exists():
                shutil.copyfile(log,saved)
            command+=['--extension-log',str(saved)]
        run(f'assert-{stem}',command,folder)
    elif stem=='resource-skill-precedence':
        run(f'assert-{stem}',['python3','tools/parity/terminal/assert_resource_skill_precedence.py',str(paired),str(calibration)],folder)
    else:
        modes={'resource-skill-prompt-read-bash':'read', 'resource-skill-prompt-bash':'bash',
               'resource-skill-prompt-hidden-read':'bash','resource-skill-prompt-hidden-read-bash':'indirect',
               'resource-skill-prompt-no-reader':'none', 'legacy-project-skills-prompt-reload':'reload'}
        if stem in modes:
            run(f'assert-{stem}',['python3','tools/parity/terminal/assert_skill_prompt_parity.py',
                '--mode',modes[stem],'--paired',str(paired),*cal,'--output',str(folder/f'assert-{stem}.json')],folder)
