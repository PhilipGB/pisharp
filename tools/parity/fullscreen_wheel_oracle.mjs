#!/usr/bin/env node
import { execFileSync } from 'node:child_process';
import { readFileSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
const [pi, input, output] = process.argv.slice(2);
const sha = execFileSync('git', ['-C',pi,'rev-parse','HEAD'], {encoding:'utf8'}).trim();
const corpus = JSON.parse(readFileSync(input,'utf8'));
if (sha !== corpus.piSha) throw Error('Wrong Pi SHA');
const load = p => import(pathToFileURL(resolve(pi,p)).href);
const [{SettingsManager,InMemorySettingsStorage},{WheelScrollAccelerator},{VirtualTerminal},
    {Text},{SelectList},{ScrollView},{VStack},{createInteractiveTui},{SettingsSelectorComponent},{initTheme}] = await Promise.all([
    load('packages/coding-agent/src/core/settings-manager.ts'),load('packages/tui/src/wheel-scroll.ts'),
    load('packages/tui/test/virtual-terminal.ts'),load('packages/tui/src/components/text.ts'),
    load('packages/tui/src/components/select-list.ts'),load('packages/tui/src/components/scroll-view.ts'),
    load('packages/tui/src/components/v-stack.ts'),load('packages/coding-agent/src/modes/interactive/tui-renderer.ts'),
    load('packages/coding-agent/src/modes/interactive/components/settings-selector.ts'),
    load('packages/coding-agent/src/modes/interactive/theme/theme.ts')]);
initTheme('dark');
const managerFor = item => {
    const storage = new InMemorySettingsStorage();
    storage.withLock('global',()=>item.userRaw ?? '{}');
    storage.withLock('project',()=>item.projectRaw ?? '{}');
    return SettingsManager.fromStorage(storage,{projectTrusted:item.projectTrusted ?? true});
};
const settings = corpus.settings.map(item => {
    const manager = managerFor(item);
    const effective = manager.getFullscreenWheelScrollLines();
    const list = new SettingsSelectorComponent({fullscreenWheelScrollLines:effective,warnings:{},defaultModel:'not set',
        availableDefaultModels:[],availableThinkingLevels:[],modelThinkingLevels:{},availableThemes:[]},{}).getSettingsList();
    const row = list.items.find(x=>x.id==='fullscreen-wheel-scroll-lines');
    const raw = manager.getSettings().fullscreenWheelScrollLines;
    return {id:item.id,loaded:true,mergedRaw:raw === undefined ? '<absent>' : JSON.stringify(raw),effective,
        current:row.currentValue,label:row.label,description:row.description,options:row.values};
});
const sequences = corpus.sequences.map(item=>{
    const platform = Object.getOwnPropertyDescriptor(process,'platform');
    const saved = Object.fromEntries(['SSH_CONNECTION','SSH_CLIENT','SSH_TTY'].map(k=>[k,process.env[k]]));
    let accelerator;
    try {
        if (item.platform) {
            Object.defineProperty(process,'platform',{value:item.platform});
            for (const k of Object.keys(saved)) delete process.env[k];
            if(item.ssh) process.env[item.ssh]=''; // Presence, including empty, disables the local-mac exemption.
            accelerator = new WheelScrollAccelerator(item.setting);
        } else accelerator = new WheelScrollAccelerator(item.setting,item.accelerate);
    } finally {
        Object.defineProperty(process,'platform',platform);
        for(const [k,v] of Object.entries(saved)) { if(v===undefined) delete process.env[k]; else process.env[k]=v; }
    }
    return {id:item.id,accelerate:accelerator.accelerate,lines:item.events.map(e=>{
        if(Object.hasOwn(e,'set')) accelerator.setLines(e.set);
        return accelerator.next(e.direction,e.t);
    })};
});
let now=0;
Object.defineProperty(performance,'now',{value:()=>now, configurable:true});
async function screen(item, overlay=false) {
    now=0;
    const terminal=new VirtualTerminal(80,24);
    const manager=managerFor({userRaw:JSON.stringify({fullscreenWheelScrollLines:item.setting})});
    const tui=createInteractiveTui({tuiMode:item.mode ?? 'fullscreen',showHardwareCursor:false,logDirectory:undefined,
        terminal,fullscreenWheelScrollLines:manager.getFullscreenWheelScrollLines()});
    const text=new Text(Array.from({length:300},(_,i)=>`ROW-${String(i+1).padStart(3,'0')}`).join('\n'),0,0);
    // Equal 18-row transcript viewport at 80x24; PiSharp reserves six rows for editor/footer chrome.
    if(item.mode==='regular') tui.addChild(text);
    else tui.setLayoutRoot(new VStack([
        {component:new ScrollView(text,{follow:'end',primary:true}),basis:0,grow:1,minSize:1},
        {component:new Text('editor\n\n\n\n\nfooter',0,0),basis:6,minSize:6}]));
    const calculated=[];
    if(tui.wheelScroll) {
        const original=tui.wheelScroll.next.bind(tui.wheelScroll);
        tui.wheelScroll.next=(d,t)=>{const n=original(d,t);calculated.push(n);return n;};
    }
    tui.start();
    await terminal.waitForRender();
    const maximum=overlay || item.mode!=='regular' ? 300-18 : 0;
    if(item.start==='top' || item.start==='near-top') {
        tui.scrollBy(-100000); await terminal.waitForRender();
        if(item.start==='near-top') { tui.scrollBy(2); await terminal.waitForRender(); }
    }
    if(item.start==='near-bottom') { tui.scrollBy(-2); await terminal.waitForRender(); }
    const offset=()=>item.mode==='regular'?0:maximum-tui.viewportTop;
    const initial=offset();
    const events=[];
    for(const e of item.events ?? []) {
        now=e.t;
        if(Object.hasOwn(e,'set')) tui.setWheelScrollLines(e.set);
        const before=offset();
        terminal.sendInput(`\x1b[<${(e.direction<0?64:65)+(e.alt?8:0)};3;2M`);
        await terminal.waitForRender();
        events.push({before,lines:item.mode==='regular'?0:calculated.at(-1),multiplier:e.alt?5:1,
            after:offset(),movement:offset()-before});
    }
    let result={id:item.id,initial,maximum,events};
    if(overlay) {
        const identity=x=>x;
        const list=new SelectList(Array.from({length:60},(_,i)=>({value:String(i),label:`item ${i}`})),8,
            {selectedPrefix:identity,selectedText:identity,description:identity,scrollInfo:identity,noMatch:identity});
        list.setSelectedIndex(20);
        const deltas=[];
        const original=list.handleMouse.bind(list);
        list.handleMouse=e=>{if(e.type==='wheel')deltas.push(e.wheelDelta);return original(e);};
        tui.showOverlay(list,{width:60,maxHeight:12});
        await terminal.waitForRender();
        for(const t of [0,20]) {
            now=t;terminal.sendInput(`\x1b[<${64+(item.alt?8:0)};40;12M`);await terminal.waitForRender();
        }
        result={id:item.id,selected:list.selectedIndex,deltas,transcriptMovement:offset()-initial};
        tui.hideOverlay();
        now=40;terminal.sendInput('\x1b[<64;3;2M');await terminal.waitForRender();
        result.nextTranscriptMovement=offset()-initial;
    }
    tui.stop();
    return result;
}
const viewports=[];
for(const item of corpus.viewports) viewports.push(await screen(item));
const overlays=[];
for(const item of corpus.overlays) overlays.push(await screen(item,true));
const setters=[];
for(const value of corpus.setters) {
    const manager=managerFor({});manager.setFullscreenWheelScrollLines(value);await manager.flush();
    setters.push({input:value,stored:manager.getGlobalSettings().fullscreenWheelScrollLines,effective:manager.getFullscreenWheelScrollLines()});
}
writeFileSync(output,JSON.stringify({piSha:sha,settings,sequences,viewports,overlays,setters},null,2)+'\n');
