import {pathToFileURL} from 'node:url';
import path from 'node:path';
const root=process.argv[2];
const {agentLoop}=await import(pathToFileURL(path.join(root,'packages/agent/src/agent-loop.ts')).href);
const {EventStream}=await import(pathToFileURL(path.join(root,'packages/ai/src/utils/event-stream.ts')).href);
const flags=JSON.parse(process.argv[3]);
const mode=process.argv[4];
let requests=0;
let executed=[];
const model={id:'fixture',name:'fixture',api:'openai-responses',provider:'openai',baseUrl:'https://fixture.invalid',reasoning:false,input:['text'],cost:{input:0,output:0,cacheRead:0,cacheWrite:0},contextWindow:8192,maxTokens:2048};
const tools=[0,1].map(index=>({name:'tool'+index,label:'tool'+index,description:'fixture',parameters:{type:'object',properties:{}},execute:async()=>{executed.push(index);return{content:[{type:'text',text:'result '+index}],details:{},terminate:flags[Math.floor((executed.length-1)/2)][index]};}}));
const stream=agentLoop([{role:'user',content:'batch',timestamp:1}],{messages:[],tools},{model,convertToLlm:x=>x,toolExecution:mode},undefined,()=>{
 const request=requests++;
 const result=new EventStream(x=>x.type==='done',x=>x.message);
 queueMicrotask(()=>{
  const calls=request<flags.length;
  const message={role:'assistant',content:calls?[0,1].map(index=>({type:'toolCall',id:'call'+request+'-'+index,name:'tool'+index,arguments:{}})):[{type:'text',text:'done'}],api:model.api,provider:model.provider,model:model.id,usage:{input:0,output:0,cacheRead:0,cacheWrite:0,totalTokens:0,cost:{input:0,output:0,cacheRead:0,cacheWrite:0,total:0}},stopReason:calls?'toolUse':'stop',timestamp:1};
  result.push({type:'done',reason:message.stopReason,message});
 });
 return result;
});
const events=[];
for await(const event of stream){if(event.type==='tool_execution_end')events.push(event);}
const messages=await stream.result();
console.log(JSON.stringify({requests,results:events.map(x=>({name:x.toolName,terminate:x.result.terminate??false,text:x.result.content.map(y=>y.text).join('')}))}));
