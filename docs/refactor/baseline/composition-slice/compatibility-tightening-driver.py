from pathlib import Path
import gzip,hashlib,json,textwrap
r=Path('/home/philip/Downloads/pisharp-bootstrap/pisharp');o=Path('/tmp/pisharp-composition-slice');assert json.loads((o/'verification/preservation-comparison.json').read_text())['pass_']
files=['src/PiSharp.Cli/Program.cs','src/PiSharp.Cli/Tui/InteractivePromptExecution.cs','src/PiSharp.Cli/Protocols/JsonPromptMode.cs'];snap=[]
for name in files:
 p=r/name;data=p.read_bytes();dest=o/'stage-1-source'/f'{name}.gz';dest.parent.mkdir(parents=True,exist_ok=True);dest.write_bytes(gzip.compress(data,mtime=0));snap.append(dict(path=name,sha256=hashlib.sha256(data).hexdigest(),artifact=str(dest.relative_to(o))))
(o/'stage-1-source/manifest.json').write_text(json.dumps(snap,indent=2)+'\n')
p=r/files[0];s=p.read_text();a=s.index('                if (await sessionCommands.TryExecuteAsync(');b=s.index('                switch (command)',a);end=s.index('            }\n            catch (Exception e) { Console.Error.WriteLine($"Session error:',b)
switch=s[b:end];s=s[:a]+'''                if (!await sessionCommands.TryExecuteAsync(command, argument, conversation, conversationRun, sessionPath))
                {
'''+textwrap.indent(switch,'    ')+'                }\n'+s[end:]
s=s.replace('new InteractivePromptExecution(editor, terminalScreen, Console.Error,','new InteractivePromptExecution(editor, terminalScreen, () => Console.Error,')
s=s.replace('Environment.ExitCode = await JsonPromptMode.RunAsync','await JsonPromptMode.RunAsync');p.write_text(s)
p=r/files[1];s=p.read_text().replace('TextWriter error, Func<ResourceCatalog>','Func<TextWriter> getError, Func<ResourceCatalog>').replace('error.WriteLine','getError().WriteLine');p.write_text(s)
p=r/files[2];s=p.read_text().replace('async Task<int> RunAsync','async Task RunAsync').replace('        var exitCode = 0;\n','').replace('        return exitCode;\n','').replace('exitCode','Environment.ExitCode');p.write_text(s)
(o/'compatibility-tightening.json').write_text(json.dumps(dict(changes=['Keep final footer update outside session-command error catch','Resolve Console.Error at write time during terminal suspend/resume','Preserve original Environment.ExitCode writes and timing in JSON mode'],files=files),indent=2)+'\n')
