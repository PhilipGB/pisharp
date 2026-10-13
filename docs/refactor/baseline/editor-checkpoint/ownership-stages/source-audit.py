from pathlib import Path
import subprocess,json,hashlib,re
from collections import Counter
from tree_sitter import Parser,Language
import tree_sitter_c_sharp
p=Parser(Language(tree_sitter_c_sharp.language()));repo=Path.cwd();old=subprocess.check_output(['git','show','HEAD:src/PiSharp.Cli/Tui/TerminalEditor.cs'])
files=[repo/'src/PiSharp.Cli/Tui'/f'{s}.cs' for s in ['TerminalEditor','TerminalDialogs','TerminalDialogPanels','TerminalProgressDisplay','TerminalTranscriptSearchInput','TerminalAuthorizationInput']]
def walk(n):
 yield n
 for c in n.named_children:yield from walk(c)
def methods(data):
 return {n.child_by_field_name('name').text.decode():n for n in walk(p.parse(data).root_node) if n.type=='method_declaration'}
def tokens(text):
 root=p.parse(('class C { void M() '+text+' }').encode()).root_node
 def leaves(n):
  if not n.children:return [n.text.decode()]
  return sum((leaves(c) for c in n.children if c.type!='comment'),[])
 return leaves(root)
original=methods(old);current={k:n for f in files for k,n in methods(f.read_bytes()).items()};checks={}
renames={'Start':'StartTranscriptSearch','HandleInput':'HandleTranscriptSearchInput','Update':'UpdateTranscriptSearch','Close':'CloseTranscriptSearch'}
for f in files[1:]:
 for name,n in methods(f.read_bytes()).items():
  previous=renames.get(name,name);body=n.child_by_field_name('body')
  if body is None:continue
  text=body.text.decode()
  for before,after in [('getScreen()!','_screen'),('getScreen()','_screen'),('getInput()','EnsureInput()'),('keymap','_keymap'),('buffer','_buffer')]:text=re.sub(r'\b'+re.escape(before)+r'(?=\W|$)',lambda _:after,text) if before in ['keymap','buffer'] else text.replace(before,after)
  text=text.replace('TerminalDialogPanels.','').replace('dialogs.','')
  if f.stem=='TerminalTranscriptSearchInput':
   for before,after in renames.items():text=re.sub(r'\b'+before+r'\b',after,text)
  if previous=='PromptSequenceAsync':
   text=text.replace('await readRedirectedLine(cancellationToken)','await ReadLineAsync(_ => Task.CompletedTask, enableApplicationActions: false, allowEmptySubmit: true, cancellationToken: cancellationToken)')
  expected=original[previous].child_by_field_name('body').text.decode()
  checks[previous]=tokens(text)==tokens(expected)
assert all(checks.values()),checks
literaltypes={'string_literal','character_literal','interpolated_string_expression'}
def literals(data):return Counter((n.type,n.text.decode()) for n in walk(p.parse(data).root_node) if n.type in literaltypes)
combined=sum((literals(f.read_bytes()) for f in files),Counter());original_literals=literals(old)
assert set(combined)==set(original_literals)
assert not (original_literals-combined)
expected_defaults=Counter({('string_literal','"↑↓ move • enter select • escape/ctrl+c close"'):2,('string_literal','"All"'):1,('string_literal','"Scoped"'):1,('string_literal','"No matching items"'):1,('string_literal','"dim"'):1})
assert combined-original_literals==expected_defaults
def public_signatures(data):
 return sorted(re.sub(r'\s+',' ',n.text.decode().split('{',1)[0]).strip() for n in walk(p.parse(data).root_node) if n.type in {'method_declaration','constructor_declaration'} and any(c.type=='modifier' and c.text==b'public' for c in n.children))
assert public_signatures(old)==public_signatures(files[0].read_bytes())
record={'sourceHead':subprocess.check_output(['git','rev-parse','HEAD'],text=True).strip(),'originalEditorSha256':hashlib.sha256(old).hexdigest(),'candidateSources':{str(f.relative_to(repo)):hashlib.sha256(f.read_bytes()).hexdigest() for f in files},'publicEditorMethodAndConstructorSignaturesUnchanged':True,'movedBodiesEqualAfterExplicitBindingAndOwnerNormalization':checks,'literalSetUnchanged':True,'additionalLiteralOccurrences':'Six duplicated optional argument defaults on concrete owners; moved body tokens and literals are exact after explicit normalization','limitations':['Source token comparison is supporting mechanical evidence, not behavioral proof.','Redirected prompt callback is explicitly expanded back into the original call for comparison.','Current-screen/input callbacks preserve original lookup sites; UI-thread attachment assumptions remain.']}
Path('/tmp/pisharp-editor-slice/source-audit.json').write_text(json.dumps(record,indent=2)+'\n');print('AUDIT_OK',len(checks),'moved bodies; public API; exact literal set and accounted optional-default duplicates')
