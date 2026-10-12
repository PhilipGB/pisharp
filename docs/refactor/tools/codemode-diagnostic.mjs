import {pathToFileURL} from 'node:url';
import path from 'node:path';
const {executeCodemode}=await import(pathToFileURL(path.join(process.argv[2],'packages/coding-agent/src/extensions/codemode/execute.ts')).href);
const fail=process.argv[3]==='true';
const model={type:'classifier',provider:'fixture',id:'one',api:'typesafe-system-one',baseUrl:'http://fixture',headers:{authorization:'host-secret'}};
const registry={getModelsOfType:()=>[model],getAvailableOfType:async()=>[model],getModelOfType:(type,provider,id)=>provider==='fixture'&&id==='one'?model:undefined,classify:async(ref,context)=>({api:ref.api,provider:ref.provider,model:ref.id,answers:{q:{type:'bool',probability:.8}},stopReason:'stop',timestamp:1,usage:{input:100,output:10,cacheRead:0,cacheWrite:0,totalTokens:110,cost:{input:.0001,output:.00002,cacheRead:0,cacheWrite:0,total:.00012}}})};
const code="const m=await models.getModelOfType('classifier','fixture','one');text(typeof m.headers);m.baseUrl='https://attacker.invalid';m.headers={authorization:'guest'};const r=await models.classify(m,{state:{value:7},questions:{q:{type:'bool',instructions:'safe?',criteria:{}}}});text(r.answers.q.probability);text([r.usage.input,r.usage.output,r.usage.totalTokens,r.usage.cost.total]);"+(fail?"throw new Error('after billing');":"");
const result=await executeCodemode('script',{code},undefined,undefined,{tools:[],sessionManager:{getBranch:()=>[]},modelRegistry:registry},{models:true});
console.log(JSON.stringify(result,null,2));
