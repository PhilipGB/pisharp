from pathlib import Path
import argparse,hashlib,re,json
from tree_sitter import Parser,Language
import tree_sitter_c_sharp
cli=argparse.ArgumentParser();cli.add_argument('--apply',action='store_true');args=cli.parse_args()
repo=Path.cwd();p=repo/'src/PiSharp.Cli/Tui/TerminalEditor.cs';data=p.read_bytes();original=data.decode()
expected=json.loads(Path('/tmp/pisharp-editor-work-plan.json').read_text())['sha256'];assert hashlib.sha256(data).hexdigest()==expected,'Editor source differs from audited H01 version'
parser=Parser(Language(tree_sitter_c_sharp.language()));root=parser.parse(data).root_node
assert not root.has_error
def walk(n):
 yield n
 for c in n.named_children:yield from walk(c)
methods={n.child_by_field_name('name').text.decode():n for n in walk(root) if n.type=='method_declaration'}
panels=['RenderInlinePanel','RenderExtensionSelectorPanel','RenderExtensionInputPanel','RenderExtensionKeyHint','RenderKeyHint','RenderPromptPanel','PanelBorder','PadPanelLine']
dialogs=['ShowSelectionList','ShowModelSelectionList','ShowInlineSelectionList','ShowInlineSelectionPanel','PromptSequenceAsync','ShowInlineStatus','ShowLlamaCatalogLoading']
search={'StartTranscriptSearch':'Start','HandleTranscriptSearchInput':'HandleInput','UpdateTranscriptSearch':'Update','CloseTranscriptSearch':'Close'}
auth=['TryReadLoginAction','MonitorMcpAuthorizationAsync']
chunks={k:[] for k in ['TerminalDialogPanels','TerminalDialogs','TerminalProgressDisplay','TerminalTranscriptSearchInput','TerminalAuthorizationInput']};edits=[]
def transform(text,mapping):
 return re.sub(r'\b(?:'+'|'.join(map(re.escape,mapping))+r')\b',lambda m:mapping[m.group()],text)
for group,names in [('TerminalDialogPanels',panels),('TerminalDialogs',dialogs),('TerminalProgressDisplay',['RunProgressAsync']),('TerminalTranscriptSearchInput',list(search)),('TerminalAuthorizationInput',auth)]:
 for name in names:
  n=methods[name];text=data[n.start_byte:n.end_byte].decode();body=n.child_by_field_name('body');signature=data[n.start_byte:body.start_byte].decode().rstrip()
  text=re.sub(r'^(private|internal)', 'public',text)
  if group=='TerminalDialogPanels':pass
  elif group=='TerminalDialogs':
   fallback='''await ReadLineAsync(_ => Task.CompletedTask, enableApplicationActions: false,
                    allowEmptySubmit: true, cancellationToken: cancellationToken)'''
   if name=='PromptSequenceAsync':assert fallback in text;text=text.replace(fallback,'await readRedirectedLine(cancellationToken)')
   text=transform(text,{'_screen':'getScreen()','_keymap':'keymap','EnsureInput':'getInput',**{k:'TerminalDialogPanels.'+k for k in panels}})
  elif group=='TerminalProgressDisplay':
   text=transform(text,{'_screen':'getScreen()','_keymap':'keymap','EnsureInput':'getInput',**{k:'TerminalDialogPanels.'+k for k in panels},'ShowInlineSelectionList':'dialogs.ShowInlineSelectionList','ShowInlineSelectionPanel':'dialogs.ShowInlineSelectionPanel'})
  elif group=='TerminalTranscriptSearchInput':
   text=transform(text,{'_screen':'getScreen()','_keymap':'keymap','_buffer':'buffer',**search})
  else:text=transform(text,{'_screen':'getScreen()','_keymap':'keymap','EnsureInput':'getInput'})
  chunks[group].append('    '+text+'\n')
  if group in ['TerminalDialogPanels','TerminalTranscriptSearchInput']:
   replacement=''
  else:
   parameters=n.child_by_field_name('parameters');arguments=', '.join(c.child_by_field_name('name').text.decode() for c in parameters.named_children if c.type=='parameter')
   owner={'TerminalDialogs':'_dialogs','TerminalProgressDisplay':'_progress','TerminalAuthorizationInput':'_authorizationInput'}[group]
   signature=signature.replace('async ','')
   replacement='    '+signature+f' =>\n        {owner}.{name}({arguments});\n'
  edits.append((n.start_byte-4,n.end_byte+1,replacement.encode()))
for start,end,new in sorted(edits,reverse=True):data=data[:start]+new+data[end:]
s=data.decode()
search_fields='''    private bool _searchingTranscript;
'''
saved_fields='''    private string _savedDraft = "";
    private string _savedFooter = "";
    private int _savedCursor;
    private string _lastSearchQuery = "";
'''
assert search_fields in s and saved_fields in s;s=s.replace(search_fields,'').replace(saved_fields,'')
progress_delay='    private static readonly TimeSpan InitialProgressFrameDelay = TimeSpan.FromMilliseconds(16);\n'
assert progress_delay in s;s=s.replace(progress_delay,'')
records='''internal sealed record TerminalProgressState(string Message, double? Ratio = null, string? Detail = null);
internal readonly record struct TerminalProgressOutcome<T>(bool Cancelled, T? Value);
internal enum TerminalLoginInputAction { CopyAuthorizationUrl, Cancel }
'''
assert records in s;s=s.replace(records,'')
s=s.replace('    private readonly EditorKeymap _keymap;','''    private readonly EditorKeymap _keymap;
    private readonly TerminalDialogs _dialogs;
    private readonly TerminalProgressDisplay _progress;
    private readonly TerminalTranscriptSearchInput _transcriptSearch;
    private readonly TerminalAuthorizationInput _authorizationInput;''')
s=s.replace('        _toolResultsExpanded = initiallyExpanded;','''        _toolResultsExpanded = initiallyExpanded;
        _dialogs = new(_keymap, EnsureInput, () => _screen,
            token => ReadLineAsync(_ => Task.CompletedTask, enableApplicationActions: false,
                allowEmptySubmit: true, cancellationToken: token));
        _progress = new(_keymap, EnsureInput, () => _screen, _dialogs);
        _transcriptSearch = new(_buffer, _keymap, () => _screen);
        _authorizationInput = new(_keymap, EnsureInput, () => _screen);''')
s=transform(s,{'_searchingTranscript':'_transcriptSearch.IsActive',**{k:'_transcriptSearch.'+v for k,v in search.items()}})
s=re.sub(r'\n(?:[ \t]*\n){2,}','\n\n',s)
constructors={
'TerminalDialogPanels':'internal static class TerminalDialogPanels',
'TerminalDialogs':'internal sealed class TerminalDialogs(EditorKeymap keymap, Func<TerminalInput> getInput,\n    Func<TerminalScreen?> getScreen, Func<CancellationToken, Task<string?>> readRedirectedLine)',
'TerminalProgressDisplay':'internal sealed class TerminalProgressDisplay(EditorKeymap keymap, Func<TerminalInput> getInput,\n    Func<TerminalScreen?> getScreen, TerminalDialogs dialogs)',
'TerminalTranscriptSearchInput':'internal sealed class TerminalTranscriptSearchInput(EditorBuffer buffer, EditorKeymap keymap,\n    Func<TerminalScreen?> getScreen)',
'TerminalAuthorizationInput':'internal sealed class TerminalAuthorizationInput(EditorKeymap keymap, Func<TerminalInput> getInput,\n    Func<TerminalScreen?> getScreen)'}
output=repo/'src/PiSharp.Cli/Tui' if args.apply else Path('/tmp/pisharp-editor-draft');output.mkdir(exist_ok=True)
for name,parts in chunks.items():
 header='namespace PiSharp.Cli.Tui;\n\n'
 extra=''
 if name=='TerminalProgressDisplay':header+='\n'.join(records.splitlines()[:2])+'\n\n';extra=progress_delay+'\n'
 if name=='TerminalAuthorizationInput':header+=records.splitlines()[2]+'\n\n'
 if name=='TerminalTranscriptSearchInput':extra=search_fields+saved_fields+'\n    public bool IsActive => _searchingTranscript;\n\n'
 text=header+constructors[name]+'\n{\n'+extra+'\n'.join(parts)+'}\n'
 assert not parser.parse(text.encode()).root_node.has_error,name
 (output/f'{name}.cs').write_text(text)
assert not parser.parse(s.encode()).root_node.has_error,'Editor'
(output/'TerminalEditor.cs').write_text(s)
if args.apply:
 test=repo/'tests/PiSharp.Tests/TerminalScreenTests.cs';t=test.read_text();assert t.count('TerminalEditor.RenderInlinePanel')==2;test.write_text(t.replace('TerminalEditor.RenderInlinePanel','TerminalDialogPanels.RenderInlinePanel'))
print('Drafted' if not args.apply else 'Applied',[(f.name,len(f.read_text().splitlines())) for f in output.glob('*.cs')])
