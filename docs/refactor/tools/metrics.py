#!/usr/bin/env python3
"""Standalone syntax metrics. Install parser packages in a temporary PYTHONPATH.

Decision complexity is a syntax diagnostic, not a Roslyn control-flow metric.
Constructor counts distinguish scalar options from injected collaborator types.
Only Git-tracked production C# is measured; source is always loaded from a SHA.
"""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import xml.etree.ElementTree as ET
from tree_sitter import Language, Parser
import tree_sitter_c_sharp

cli = argparse.ArgumentParser()
cli.add_argument('--repo', type=Path, required=True)
cli.add_argument('--sha', required=True)
cli.add_argument('--output', type=Path, required=True)
args = cli.parse_args()
repo = args.repo.resolve()
parser = Parser(Language(tree_sitter_c_sharp.language()))

def git(*argv):
    return subprocess.check_output(['git', *argv], cwd=repo)

def content(path):
    return git('show', f'{args.sha}:{path}')

def walk(node):
    yield node
    for child in node.named_children:
        yield from walk(child)

def text(node):
    return node.text.decode() if node else ''

def owner(node):
    names = []
    parent = node.parent
    while parent:
        if parent.type in ['class_declaration', 'struct_declaration', 'record_declaration', 'interface_declaration']:
            names.append(text(parent.child_by_field_name('name')))
        parent = parent.parent
    return '.'.join(reversed(names))

def complexity(node):
    score = 1
    for n in walk(node):
        if n.type in ['if_statement', 'for_statement', 'foreach_statement', 'while_statement',
                      'do_statement', 'catch_clause', 'conditional_expression', 'switch_expression_arm', 'case_switch_label']:
            score += 1
        elif n.type == 'binary_expression' and any(c.type in ['&&', '||', '??'] for c in n.children):
            score += 1
    return score

paths = git('ls-tree', '-r', '--name-only', args.sha).decode().splitlines()
files, types, constructors, primary_constructors, methods, errors = [], [], [], [], [], []
for path in paths:
    if not path.startswith('src/') or not path.endswith('.cs'):
        continue
    data = content(path)
    source = data.decode()
    tree = parser.parse(data)
    if tree.root_node.has_error:
        errors.append(path)
    imports = [text(n).removeprefix('using ').removesuffix(';') for n in tree.root_node.named_children if n.type == 'using_directive']
    changes = git('log', '--format=%H', args.sha, '--', path).decode().splitlines()
    files.append(dict(path=path, lines=len(source.splitlines()), imports=imports,
                      commits=len(changes), sha256=hashlib.sha256(data).hexdigest()))
    if tree.root_node.has_error:
        continue
    for node in walk(tree.root_node):
        if node.type in ['class_declaration', 'struct_declaration', 'record_declaration', 'interface_declaration']:
            types.append(dict(path=path, name=owner(node)+('.' if owner(node) else '')+text(node.child_by_field_name('name')),
                              kind=node.type, line=node.start_point.row+1, lines=node.end_point.row-node.start_point.row+1,
                              decisionComplexity=complexity(node)))
            parameters=node.child_by_field_name('parameters') or next((c for c in node.named_children if c.type=='parameter_list'),None)
            if parameters:
                params=[n for n in parameters.named_children if n.type=='parameter']
                primary_constructors.append(dict(path=path,name=text(node.child_by_field_name('name')),
                    kind=node.type,parameterCount=len(params),
                    parameters=[dict(name=text(p.child_by_field_name('name')),type=text(p.child_by_field_name('type'))) for p in params]))
        if node.type in ['constructor_declaration', 'method_declaration', 'local_function_statement']:
            parameters = node.child_by_field_name('parameters')
            params = [n for n in parameters.named_children if n.type == 'parameter'] if parameters else []
            details = [dict(name=text(p.child_by_field_name('name')), type=text(p.child_by_field_name('type')),
                            optional=any(c.type == '=' for c in p.children)) for p in params]
            record = dict(path=path, owner=owner(node), name=text(node.child_by_field_name('name')),
                          line=node.start_point.row+1, lines=node.end_point.row-node.start_point.row+1,
                          decisionComplexity=complexity(node), parameters=details, parameterCount=len(details))
            if node.type == 'constructor_declaration':
                scalar = {'bool', 'int', 'long', 'double', 'float', 'decimal', 'string', 'char', 'CancellationToken', 'TimeSpan'}
                record['collaboratorCount'] = sum(p['type'].rstrip('?') not in scalar for p in details)
                constructors.append(record)
            else:
                methods.append(record)

projects = []
for path in paths:
    if not path.endswith('.csproj'):
        continue
    root = ET.fromstring(content(path))
    projects.append(dict(path=path, properties={n.tag:n.text for g in root.findall('PropertyGroup') for n in g},
                         references=[n.attrib['Include'] for n in root.findall('.//ProjectReference')],
                         packages=[n.attrib for n in root.findall('.//PackageReference')],
                         internalsVisibleTo=[n.attrib['Include'] for n in root.findall('.//InternalsVisibleTo')]))
metrics = dict(sha=args.sha, methodology='Git source at SHA; tree-sitter C# syntax. Decision complexity counts branch/loop/catch/conditional/switch and &&/||/?? syntax. It includes nested lambdas/local functions and is diagnostic, not control-flow cyclomatic complexity. Collaborator counts exclude common scalar options but can include data/options types; all constructor parameters retained for audit. Churn is commit count through SHA for the current path, without rename following. Primary constructors/data records are separately recorded and excluded from ordinary injected constructor maxima. Parser errors are listed; those files are usable for file size/churn only.',
               parserVersions={'tree-sitter':'0.25.2','tree-sitter-c-sharp':'0.23.1'}, parseErrors=errors,
               projects=projects, files=files, types=types, constructors=constructors, primaryConstructors=primary_constructors, methods=methods,
               top15Files=sorted(files,key=lambda x:x['lines'],reverse=True)[:15],
               largestTypes=sorted(types,key=lambda x:x['lines'],reverse=True)[:20],
               largestConstructors=sorted(constructors,key=lambda x:x['parameterCount'],reverse=True)[:20],
               maximumConstructorParameterCount=max(x['parameterCount'] for x in constructors),
               maximumConstructorCollaboratorCount=max(x['collaboratorCount'] for x in constructors),
               highComplexityMethods=sorted(methods,key=lambda x:x['decisionComplexity'],reverse=True)[:30])
args.output.parent.mkdir(parents=True,exist_ok=True)
args.output.write_text(json.dumps(metrics,indent=2)+'\n')
print(json.dumps({k:metrics[k] for k in ['sha','parseErrors','maximumConstructorParameterCount','maximumConstructorCollaboratorCount','top15Files']},indent=2))
