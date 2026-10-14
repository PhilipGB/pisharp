#!/usr/bin/env python3
"""Compare canonical identity, collisions and source scope with Pi's full resource loader."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import tempfile

from pi_reference import verify_pi_checkout
from terminal.thread_limits import apply_test_thread_limit

apply_test_thread_limit()
ROOT = Path(__file__).resolve().parents[2]


def skill(name='guide', body='body'):
    return f'---\nname: {name}\ndescription: Guide\n---\n{body}'


def scenarios():
    cases = []

    def add(name, files, skill_paths=None, prompt_paths=None, links=None, **options):
        cases.append(dict(id=name, files=files, skillPaths=skill_paths or [], promptPaths=prompt_paths or [],
                          links=links or {}, **options))

    add('repeated-file', {'outside/a.md': skill()}, ['outside/a.md', 'outside/a.md'])
    add('linked-file', {'outside/a.md': skill()}, ['outside/a.md', 'outside/alias.md'], links={'outside/alias.md': 'a.md'})
    add('linked-parent', {'outside/original/SKILL.md': skill()}, ['outside/original', 'outside/linked'],
        links={'outside/linked': 'original'})
    add('linked-loser', {'outside/winner.md': skill(body='winner'), 'outside/loser.md': skill(body='loser')},
        ['outside/winner.md', 'outside/loser.md', 'outside/alias.md'], links={'outside/alias.md': 'loser.md'})
    add('linked-root-first-name-fallback', {'outside/real/SKILL.md': '---\ndescription: Guide\n---\nbody'},
        ['outside/alias', 'outside/real'], links={'outside/alias': 'real'})
    add('linked-invalid-name-file', {'outside/a.md': skill('BAD NAME')}, ['outside/a.md', 'outside/alias.md'],
        links={'outside/alias.md': 'a.md'})
    for bad in [False, True]:
        add('distinct-roots-shared-file' + ('-warnings' if bad else ''), {'outside/a/SKILL.md': skill('BAD NAME' if bad else 'guide')},
            ['outside/a', 'outside/b'], links={'outside/b/SKILL.md': '../a/SKILL.md'})
    add('auto-file-and-directory-warnings', {'agent/skills/guide/SKILL.md': skill('BAD NAME')},
        ['agent/skills/guide'], discover=True)
    add('skill-warning-before-collision', {'outside/a.md': skill(), 'outside/b.md': skill(), 'outside/c.md': skill('BAD NAME')},
        ['outside/a.md', 'outside/b.md', 'outside/c.md'])
    add('prompt-invalid-loser', {'outside/a/review.md': 'first body', 'outside/b/review.md': '---\ndescription: [broken\n---\nbody'},
        prompt_paths=['outside/a/review.md', 'outside/b/review.md'])
    add('prompt-warning-before-collision', {'outside/a/review.md': 'first body', 'outside/b/review.md': 'second body',
        'outside/c/error.md': '---\ndescription: [broken\n---\nbody'},
        prompt_paths=['outside/a/review.md', 'outside/b/review.md', 'outside/c/error.md'])
    add('prompt-linked-file-different-name', {'outside/one.md': 'body'}, prompt_paths=['outside/one.md', 'outside/two.md'],
        links={'outside/two.md': 'one.md'})
    add('prompt-one-directory-aliases', {'outside/one.md': 'body'}, prompt_paths=['outside'], links={'outside/two.md': 'one.md'})
    add('prompt-distinct-directories-shared-file', {'outside/a/review.md': 'body'}, prompt_paths=['outside/a', 'outside/b'],
        links={'outside/b/review.md': '../a/review.md'})
    for trusted in [False, True]:
        add('auto-trust-' + str(trusted).lower(), {'project/.pi/skills/guide/SKILL.md': skill(body='project'),
            'agent/skills/guide/SKILL.md': skill(body='user'), 'project/.pi/prompts/review.md': 'project prompt',
            'agent/prompts/review.md': 'user prompt'}, discover=True, trusted=trusted)
    add('configured-user-before-auto-user', {'agent/custom/SKILL.md': skill(body='configured'),
        'agent/skills/guide/SKILL.md': skill(body='auto')}, discover=True, user={'skills': ['custom']})
    add('configured-project-before-project-auto-user-configured', {'project/.pi/custom/SKILL.md': skill(body='project configured'),
        'project/.pi/skills/guide/SKILL.md': skill(body='project auto'), 'agent/custom/SKILL.md': skill(body='user configured')},
        discover=True, trusted=True, user={'skills': ['custom']}, project={'skills': ['custom']})
    add('explicit-user-file-defaults-disabled', {'agent/skills/guide/SKILL.md': skill()}, ['agent/skills/guide/SKILL.md'])
    add('explicit-untrusted-project-file', {'project/.pi/skills/guide/SKILL.md': skill()}, ['project/.pi/skills/guide/SKILL.md'])
    add('configured-defaults-disabled', {'agent/custom/SKILL.md': skill('configured'), 'outside/selected.md': skill('selected')},
        ['outside/selected.md'], user={'skills': ['custom']})
    add('auto-prompts-filter-hidden-and-ignored', {'agent/prompts/.hidden.md': 'hidden', 'agent/prompts/blocked.md': 'blocked',
        'agent/prompts/keep.md': 'keep', 'agent/prompts/.ignore': 'blocked.md\n'}, discover=True)
    add('configured-prompts-recurse-with-ignore', {'agent/custom/a.md': 'a', 'agent/custom/child/b.md': 'b',
        'agent/custom/child/.hidden.md': 'hidden', 'agent/custom/child/blocked.md': 'blocked',
        'agent/custom/.ignore': 'child/blocked.md\n'}, discover=True, user={'prompts': ['custom']})
    add('agents-root-versus-nested-aliases', {'project/.agents/skills/root.md': skill('root'),
        'project/.agents/skills/pack/guide.md': skill('nested')}, discover=True, trusted=True)
    add('unicode-file-order', {'outside/\ue000.md': skill('bmp'), 'outside/\U00010000.md': skill('astral')}, ['outside'])
    add('ancestor-agents-stop-at-git-root', {'project/.agents/skills/pack/guide.md': skill('ancestor'),
        '.agents/skills/outside/SKILL.md': skill('outside')}, discover=True, trusted=True, cwd='project/sub')
    add('cli-before-extension-resources', {'outside/cli.md': skill(body='cli'), 'outside/extension.md': skill(body='extension')},
        ['outside/cli.md'], extensionSkillPaths=['outside/extension.md'])
    add('extension-overlays-existing-source-info', {'agent/skills/guide/SKILL.md': skill()}, discover=True,
        extensionSkillPaths=['agent/skills/guide/SKILL.md'])
    add('missing-prompt-app-diagnostic', {}, prompt_paths=['outside/missing.md'])
    add('skill-missing-warning-position', {'outside/a.md': skill('BAD NAME')}, ['outside/a.md', 'outside/missing.md'])
    add('prompt-missing-after-warnings-collisions', {'outside/a/review.md': 'first', 'outside/b/review.md': 'second',
        'outside/c/error.md': '---\ndescription: [broken\n---\nbody'},
        prompt_paths=['outside/a/review.md', 'outside/b/review.md', 'outside/c/error.md', 'outside/missing.md'])
    return cases



def package_scenarios(edges=False):
    base = {'outside/package/skills/guide/SKILL.md': skill(body='package body'),
            'outside/package/prompts/review.md': '---\ndescription: Package review\n---\nReview $1',
            'outside/package/prompts/nested/detail.md': 'Nested detail'}
    manifest = dict(pi=dict(skills=['skills'], prompts=['prompts']))
    cases = []

    def add(name, declaration='../outside/package', package_manifest=manifest, project=None, **options):
        files = dict(base)
        if package_manifest is not None:
            files['outside/package/package.json'] = (package_manifest if isinstance(package_manifest, str)
                                                    else json.dumps(package_manifest))
        files.update(options.pop('files', {}))
        cases.append(dict(id='package-' + name, files=files, skillPaths=options.pop('skillPaths', []),
                          promptPaths=[], links=options.pop('links', {}), discover=options.pop('discover', True),
                          user=dict(packages=[declaration]) if declaration is not None else {},
                          project=project or {}, **options))

    add('manifest')
    add('conventions', package_manifest=None)
    add('invalid-json-conventions', package_manifest='{bad')
    add('invalid-pi-conventions', package_manifest={'pi': []})
    add('empty-manifest', package_manifest={'pi': {}})
    add('empty-manifest-object-fallback', declaration={'source': '../outside/package'}, package_manifest={'pi': {}})
    add('empty-manifest-skills', package_manifest={'pi': {'skills': [], 'prompts': ['prompts']}})
    add('empty-filter-skills', declaration={'source': '../outside/package', 'skills': []})
    add('empty-filter-prompts', declaration={'source': '../outside/package', 'prompts': []})
    add('positive-filter', declaration={'source': '../outside/package', 'prompts': ['review.md']})
    add('negative-filter', declaration={'source': '../outside/package', 'prompts': ['!review.md']})
    add('force-include', declaration={'source': '../outside/package', 'prompts': ['!*.md', '+prompts/review.md']})
    add('force-exclude', declaration={'source': '../outside/package', 'skills': ['+skills/guide', '-skills/guide']})
    add('filter-cannot-expose-undeclared', declaration={'source': '../outside/package', 'prompts': ['nested/*.md']},
        package_manifest={'pi': {'skills': ['skills'], 'prompts': ['prompts/review.md']}})
    add('manifest-glob', package_manifest={'pi': {'skills': ['skills/*'], 'prompts': ['prompts/**/*.md']}})
    add('manifest-exclusion', package_manifest={'pi': {'skills': ['skills'], 'prompts': ['prompts', '!review.md']}})
    for pattern in [['skills/guide'], ['!skills/guide'], ['!*.md', '+skills/guide']]:
        add('lowercase-skill-alias-' + str(len(cases)), declaration={'source': '../outside/package', 'skills': pattern},
            package_manifest={'pi': {'skills': ['skills/guide/skill.md']}},
            files={'outside/package/skills/guide/skill.md': skill(body='lowercase alias body')})
    add('project-trusted', declaration=None, project={'packages': ['../../outside/package']}, trusted=True)
    add('project-untrusted', declaration=None, project={'packages': ['../../outside/package']}, trusted=False)
    add('project-replaces-personal', project={'packages': [{'source': '../../outside/package', 'skills': []}]}, trusted=True)
    add('project-delta', project={'packages': [{'source': '../../outside/package', 'autoload': False, 'skills': ['-skills/guide']}]}, trusted=True)
    add('delta-without-personal', declaration=None, project={'packages': [{'source': '../../outside/package', 'autoload': False, 'prompts': ['+prompts/review.md']}]}, trusted=True)
    add('disabled-discovery', discover=False)
    add('explicit-disabled-package-file', declaration={'source': '../outside/package', 'skills': []},
        skillPaths=['outside/package/skills/guide/SKILL.md'], discover=False)
    add('local-before-package', trusted=True, files={'project/.pi/skills/guide/SKILL.md': skill(body='local winner')})
    add('personal-auto-before-package', files={'agent/skills/guide/SKILL.md': skill(body='personal winner')})
    if edges:
        add('hidden-glob', package_manifest={'pi': {'prompts': ['.hidden/*.md']}},
            files={'outside/package/.hidden/secret.md': 'Hidden prompt'})
        add('hidden-exact-file', package_manifest={'pi': {'prompts': ['.hidden/secret.md']}},
            files={'outside/package/.hidden/secret.md': 'Hidden prompt'})
        add('disabled-first-canonical-alias', declaration='../outside/alias-package',
            project={'packages': [{'source': '../../outside/package', 'skills': []}]}, trusted=True,
            links={'outside/alias-package': 'package'})
    return cases

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pi', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--packages', action='store_true', help='Include local package settings/manifest/filter/scope comparisons')
    parser.add_argument('--package-edges', action='store_true', help='Include additional package filesystem edge characterization')
    args = parser.parse_args()
    sha = verify_pi_checkout(args.pi)
    cases = scenarios()
    if args.packages or args.package_edges:
        cases.extend(package_scenarios(args.package_edges))
    with tempfile.TemporaryDirectory(prefix='pisharp-resource-selection-') as directory:
        root = Path(directory)
        home = root / 'home'
        home.mkdir()
        for case in cases:
            work = root / case['id']
            (work / 'project/.pi').mkdir(parents=True)
            (work / 'project/.git').mkdir()
            (work / case.get('cwd', 'project') / '.pi').mkdir(parents=True, exist_ok=True)
            (work / 'agent').mkdir()
            for name, text in case['files'].items():
                target = work / name
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_text(text)
            for name, target in case['links'].items():
                link = work / name
                link.parent.mkdir(parents=True, exist_ok=True)
                link.symlink_to(target)
            for scope, path in [('user', work / 'agent/settings.json'), ('project', work / case.get('cwd', 'project') / '.pi/settings.json')]:
                path.write_text(json.dumps(case.get(scope, {})))
        inputs = root / 'inputs.json'
        inputs.write_text(json.dumps(cases))
        (root / 'Program.cs').write_text((ROOT / 'tools/parity/resource-selection-probe.cs').read_text())
        (root / 'probe.csproj').write_text(f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup><ProjectReference Include="{ROOT}/src/PiSharp.Cli/PiSharp.Cli.csproj" /></ItemGroup></Project>')
        env = dict(os.environ, NUGET_HTTP_CACHE_PATH='/tmp/pisharp-nuget-http', NUGET_PACKAGES='/tmp/pisharp-nuget-packages')
        build = subprocess.run(['dotnet', 'build', str(root / 'probe.csproj'), '--warnaserror', '-m:1',
            '-p:UseSharedCompilation=false', f'-p:RestoreFallbackFolders={Path.home() / ".nuget/packages"}'],
            env=env, capture_output=True, text=True, timeout=120)
        if build.returncode:
            raise RuntimeError(build.stdout + build.stderr)
        env.update(HOME=str(home), PI_CODING_AGENT_DIR=str(root / 'agent'))
        expected = json.loads(subprocess.check_output(['node', '--experimental-transform-types',
            str(ROOT / 'tools/parity/resource-selection-probe.mjs'), str(args.pi.resolve()), str(inputs), str(root)], env=env, timeout=60))
        actual = json.loads(subprocess.check_output(['dotnet', str(root / 'bin/Debug/net10.0/probe.dll'), str(inputs), str(root)],
            env=env, timeout=60))
    differences = [dict(id=p['id'], pi=p, pisharp=n) for p, n in zip(expected, actual, strict=True) if p != n]
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(dict(piSha=sha, scenarios=cases, pi=expected, pisharp=actual,
        differences=differences, match=not differences), indent=2) + '\n')
    print(f'{len(cases)} full-loader selection scenarios; {len(differences)} mismatches.', flush=True)
    assert not differences, ', '.join(d['id'] for d in differences)


if __name__ == '__main__':
    main()
