import fs from 'node:fs';
import path from 'node:path';
import http from 'node:http';
import {spawn, execFileSync} from 'node:child_process';

const root = path.resolve(process.argv[2]);
const arm = process.argv[3];
if (!['baseline', 'candidate'].includes(arm)) throw new Error('Unknown arm');
fs.mkdirSync(root, {recursive:true});
const profile = path.join(root, 'profile');
const edge = 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe';
const wait = ms => new Promise(resolve => setTimeout(resolve, ms));
const source = `<!doctype html><meta charset="utf-8"><title>Owned sleeping-tab workload</title>
<p>Local performance fixture</p><script>
const data = new Float64Array(4*1024*1024);for(let i=0;i<data.length;i+=512)data[i]=i;
let ticks=0,checksum=0;setInterval(()=>{const stop=performance.now()+40;while(performance.now()<stop){
  for(let i=0;i<10000;i++)checksum=Math.sin(checksum+i); }ticks++;},500);
</script>`;
const server = http.createServer((req,res)=>{res.setHeader('content-type','text/html');res.end(req.url==='/home'?'<!doctype html><title>Owned test home</title>Idle test home':source);});
await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
const sitePort = server.address().port;
const portServer=http.createServer();await new Promise(resolve=>portServer.listen(0,'127.0.0.1',resolve));
const debugPort=portServer.address().port;await new Promise(resolve=>portServer.close(resolve));
const ps = script => execFileSync('powershell.exe',['-NoProfile','-NonInteractive','-EncodedCommand',Buffer.from(script,'utf16le').toString('base64')],{encoding:'utf8',windowsHide:true});
let child, ws, nextId=0, pending=new Map(), pages=[];
const snapshots=[];
const call=(method,params={})=>new Promise((resolve,reject)=>{
 const id=++nextId;const timeout=setTimeout(()=>{pending.delete(id);reject(new Error('CDP timeout '+method));},10000);
 pending.set(id,{resolve,reject,timeout});ws.send(JSON.stringify({id,method,params}));
});
try {
 child=spawn(edge,[`--user-data-dir=${profile}`,`--remote-debugging-port=${debugPort}`,'--no-first-run','--no-default-browser-check',
   '--disable-sync','--disable-component-update','--window-size=300,200','--new-window',`http://127.0.0.1:${sitePort}/home`],{windowsHide:true,stdio:'ignore'});
 let version;
 for(let attempt=0;attempt<100;attempt++){
   try{version=await(await fetch(`http://127.0.0.1:${debugPort}/json/version`)).json();break;}catch{await wait(200);}
 }
 if(!version)throw new Error('Owned Edge initialization failed');
 ws=new WebSocket(version.webSocketDebuggerUrl);
 await new Promise((resolve,reject)=>{ws.addEventListener('open',resolve,{once:true});ws.addEventListener('error',reject,{once:true});});
 ws.addEventListener('message',event=>{const message=JSON.parse(event.data);const item=pending.get(message.id);if(item){
   clearTimeout(item.timeout);pending.delete(message.id);if(message.error)item.reject(new Error(JSON.stringify(message.error)));else item.resolve(message.result);}});
 for(let index=0;index<8;index++)pages.push((await call('Target.createTarget',{url:`http://127.0.0.1:${sitePort}/workload?tab=${index}`,background:true})).targetId);
 const targets=await call('Target.getTargets');const home=targets.targetInfos.find(t=>t.url.endsWith('/home'));
 if(home)await call('Target.activateTarget',{targetId:home.targetId});
 const start=Date.now();
 for(let index=0;index<19;index++){
   const info=await call('SystemInfo.getProcessInfo');
   const ids=info.processInfo.map(p=>p.id).filter(n=>Number.isInteger(n)&&n>0);
   const memory=JSON.parse(ps(`$r=@(${ids.map(n=>`Get-Process -Id ${n} -ErrorAction SilentlyContinue`).join(';')}); $sum=($r|Measure-Object WorkingSet64 -Sum).Sum; if($null-eq$sum){$sum=0}; ConvertTo-Json -Compress @{workingSetBytes=[long]$sum}`));
   snapshots.push({elapsed:(Date.now()-start)/1000,cpu:info.processInfo.reduce((sum,p)=>sum+p.cpuTime,0),
     cpuByPid:Object.fromEntries(info.processInfo.map(p=>[p.id,p.cpuTime])),memoryBytes:memory.workingSetBytes,processes:info.processInfo.length});
   if(index%3===0)console.log(`${arm}: sample ${index+1}/19, elapsed=${snapshots.at(-1).elapsed.toFixed(1)}s`);
   await wait(4000);
 }
 fs.writeFileSync(path.join(root,'result.json'),JSON.stringify({arm,version:version.Browser,pages:pages.length,snapshots},null,2));
 console.log(`${arm}: complete`);
}finally{
 if(ws&&ws.readyState===WebSocket.OPEN){try{await call('Browser.close');}catch{}ws.close();}
 if(child&&!child.killed)child.kill();
 server.close();
 for(const item of pending.values()){clearTimeout(item.timeout);item.reject(new Error('Owned browser closed'));}
}
