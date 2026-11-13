import http from 'node:http';
import {spawn} from 'node:child_process';
import {readFile,writeFile,mkdir,open} from 'node:fs/promises';
import {resolve,dirname,join} from 'node:path';
import {fileURLToPath} from 'node:url';
import {randomUUID} from 'node:crypto';
import {createRequire} from 'node:module';
const root=resolve(dirname(fileURLToPath(import.meta.url)),'..');
const require=createRequire(join(root,'demo/package.json'));
const QRCode=require('qrcode');
const base='http://127.0.0.1:19180';
const dependencies=process.env.DEMO_INTERNAL_BASE_URL||base;
const serviceUrls=Object.fromEntries(['materials','intake','review'].map((s,i)=>[s,process.env['DEMO_'+s.toUpperCase()+'_URL']||'http://127.0.0.1:'+(19181+i)]));
const ports={materials:19181,intake:19182,review:19183};
const data=join(root,'.demo-data'); await mkdir(data,{recursive:true});
if(!process.env.PGHOST||!process.env.PGDATABASE||!process.env.PGUSER) throw Error('Configure PGHOST, PGDATABASE and PGUSER for the demonstration database');
const {Pool}=require('pg'); const database=new Pool();
await database.query('CREATE TABLE IF NOT EXISTS demo_state(id INTEGER PRIMARY KEY CHECK(id=1),document JSONB NOT NULL)');
let state=(await database.query('SELECT document FROM demo_state WHERE id=1')).rows[0]?.document||{games:[],events:[],requests:[]};
let saving=Promise.resolve(); const save=()=>{const json=JSON.stringify(state);saving=saving.then(()=>database.query('INSERT INTO demo_state VALUES(1,$1::jsonb) ON CONFLICT(id) DO UPDATE SET document=EXCLUDED.document',[json]));return saving;};
const children=[];let stopping=false;
const send=(r,s,b)=>{r.writeHead(s,{'content-type':'application/json; charset=utf-8','cache-control':'no-store'});r.end(JSON.stringify(b));};
const roles={'demo-admin':['admin'],'demo-observer':['observer'],'demo-checker':['checker'],'demo-checker-2':['checker'],'demo-organizer':['organizer'],'demo-system':['integration']};
const game=id=>state.games.find(g=>g.id===id);
async function call(service,path,method='GET',body,role='system',extra={}){
 const headers={authorization:'Bearer demo-'+role,...extra}; if(body!==undefined)headers['content-type']='application/json';if(method!=='GET')headers['Idempotency-Key']??=randomUUID();
 const start=Date.now(); let r;
 do {
  r=await fetch(serviceUrls[service]+path,{method,headers,body:body===undefined?undefined:JSON.stringify(body),signal:AbortSignal.timeout(25000)});
  if(r.status!==202||!['Accepted','Running'].includes(r.headers.get('Operation-Status'))||Date.now()-start>30000)break;
  await r.arrayBuffer(); await new Promise(resolve=>setTimeout(resolve,100));
 }while(true);
 const bytes=Buffer.from(await r.arrayBuffer());state.requests.unshift({time:new Date().toISOString(),service,method,path,status:r.status,ms:Date.now()-start,role});state.requests=state.requests.slice(0,100);
 return {status:r.status,type:r.headers.get('content-type')||'',operation:r.headers.get('Operation-Id'),operationStatus:r.headers.get('Operation-Status'),operationLocation:r.headers.get('Operation-Location'),bytes,json:()=>bytes.length?JSON.parse(bytes):null};
}
async function ok(...args){const r=await call(...args);if(r.status>=400)throw Object.assign(Error(r.bytes.toString()),{status:r.status});return r.json();}
function gameProfile(g){return {...g,taskIds:g.tasks.map(t=>t.id),taskAssignments:g.tasks.map(t=>({taskId:t.id,taskNumber:t.number})),materialSettings:{answerBlankCount:3,taskCardCount:3,taskCardsShared:g.system==='domino',templateKey:g.system+'/v1',templateParameters:{heading:'Олимпиада · '+g.name}},capabilities:{appealsAllowed:g.system!=='domino',modifiersAllowed:g.system==='bonuses'}};}
async function route(req,res){
 const url=new URL(req.url,base),p=url.pathname;let raw='';for await(const c of req){raw+=c;if(raw.length>25000000)throw Error('Image too large');}const b=raw?JSON.parse(raw):{};
 if(p==='/health')return send(res,200,{ready:true});
 if(p==='/vendor/jsqr.js'){res.writeHead(200,{'content-type':'text/javascript'});return res.end(await readFile(join(root,'demo/node_modules/jsqr/dist/jsQR.js')));}
 if(p==='/auth/introspect')return send(res,200,{active:!!roles[b.token],userId:b.token,roles:roles[b.token]||[]});
 if(p==='/clock')return send(res,200,{now:new Date().toISOString()});
 if(p.startsWith('/games/')){const g=game(p.split('/')[2]);if(g)return send(res,200,gameProfile(g));}
 if(p.startsWith('/teams/')){const g=state.games.find(g=>g.teamId===p.split('/')[2]);if(g)return send(res,200,{id:g.teamId,gameId:g.id,admitted:true});}
 if(p.startsWith('/tasks/')){const t=state.games.flatMap(g=>g.tasks).find(t=>t.id===p.split('/')[2]);if(t)return send(res,200,t);}
 if(p.startsWith('/material-templates/'))return send(res,200,{key:decodeURIComponent(p.slice(20)),format:'declarative-v1',answerSheet:[{kind:'field',field:'heading'},{kind:'field',field:'taskNumber'},{kind:'field',field:'blankNumber'},{kind:'field',field:'modifierLabel'},{kind:'qr'},{kind:'answer-area'}],taskSheet:[{kind:'field',field:'taskNumber'},{kind:'field',field:'statement'}]});
 if(p.startsWith('/receipts/')){const r=await call('intake',p);res.writeHead(r.status,{'content-type':r.type});return res.end(r.bytes);}
 if(p==='/events'){
  if(!state.events.some(e=>e.eventId===b.eventId)){state.events.unshift(b);await save();}
  return send(res,202,{accepted:true});
 }
 if(p==='/demo/state'){
  const health=await Promise.all(Object.keys(ports).map(async s=>{try{return {service:s,port:ports[s],healthy:(await call(s,'/health')).status===200};}catch{return {service:s,port:ports[s],healthy:false};}}));
  return send(res,200,{...state,health});
 }
 if(p==='/demo/game'&&req.method==='POST'){
  if(!['bonuses','domino','tic-tac-toe'].includes(b.system))return send(res,422,{message:'Выберите игру'});
  const g={id:randomUUID(),teamId:randomUUID(),name:b.name||'Живая демонстрация',teamName:'Команда «Интеграл»',system:b.system,status:'running',tournamentId:randomUUID(),admissionVersion:1,lateSubmissionAllowed:false,submissionDeadline:new Date(Date.now()+86400000).toISOString(),appealDeadline:new Date(Date.now()+86400000).toISOString(),finishDeadline:new Date(Date.now()+86400000).toISOString(),tasks:[1,2,3].map((n)=>({id:randomUUID(),number:String(n),statement:`Вычислите ${n+1} + ${n+2}`,referenceAnswer:String(2*n+3)}))};
  state.games.unshift(g);await save();await ok('intake','/intake-sessions/'+g.id,'PUT',{admissionVersion:1});await ok('review','/appeal-quotas/'+g.id,'PUT',{limit:2},'admin');return send(res,201,g);
 }
 if(p==='/demo/deadline'&&req.method==='POST'){
  const g=game(b.gameId);if(!g)return send(res,404,{});
  if(b.reopen){g.admissionVersion++;g.lateSubmissionAllowed=true;g.status='running';await save();await ok('intake','/intake-sessions/'+g.id,'PUT',{admissionVersion:g.admissionVersion});}
  else {g.submissionDeadline=new Date(Date.now()-1000).toISOString();g.appealDeadline=g.submissionDeadline;g.finishDeadline=g.submissionDeadline;g.lateSubmissionAllowed=false;g.status='submission-closed';await save();await ok('intake','/intake-sessions/'+g.id+'/close','POST',{admissionVersion:g.admissionVersion});}
  return send(res,200,g);
 }
 if(p==='/demo/qr'&&req.method==='POST')return send(res,200,{image:await QRCode.toDataURL(b.token,{errorCorrectionLevel:'M',margin:4,scale:6})});
 if(p.startsWith('/api/')){
  const [, ,service,...rest]=p.split('/');if(!ports[service])return send(res,404,{});
  const path='/'+rest.join('/')+url.search;const r=await call(service,path,req.method,raw?b:undefined,req.headers['x-demo-role']||'admin',{...(req.headers['assignment-token']?{'Assignment-Token':req.headers['assignment-token']}:{}),...(req.headers['idempotency-key']?{'Idempotency-Key':req.headers['idempotency-key']}:{})});
  res.writeHead(r.status,{'content-type':r.type,'cache-control':'no-store',...(r.operation?{'Operation-Id':r.operation,'Operation-Status':r.operationStatus,...(r.operationLocation?{'Operation-Location':'/api/'+service+r.operationLocation}:{})}:{})});return res.end(r.bytes);
 }
 if(p==='/'||p==='/app.js'||p==='/style.css'){const name=p==='/'?'index.html':p.slice(1);res.writeHead(200,{'content-type':name.endsWith('.html')?'text/html; charset=utf-8':name.endsWith('.js')?'text/javascript; charset=utf-8':'text/css; charset=utf-8'});return res.end(await readFile(join(root,'demo/public',name)));}
 return send(res,404,{message:'Не найдено'});
}
const server=http.createServer((q,r)=>route(q,r).catch(e=>{console.error(e);if(!r.headersSent)send(r,e.status||500,{message:e.message});else r.end();}));
await new Promise((r,j)=>{server.once('error',j);server.listen(19180,process.env.DEMO_BIND||'127.0.0.1',r);});
if(process.env.DEMO_EXTERNAL_SERVICES!=='true') for(const [s,port]of Object.entries(ports)){
 const title=s[0].toUpperCase()+s.slice(1),cwd=join(root,'services',s);const log=await open(join(data,s+'.log'),'a');
 const c=spawn('dotnet',[`src/Olympiad.${title}.Api/bin/Release/net8.0/Olympiad.${title}.Api.dll`],{cwd,windowsHide:true,stdio:['ignore',log.fd,log.fd],env:{...process.env,ASPNETCORE_URLS:'http://127.0.0.1:'+port,ASPNETCORE_ENVIRONMENT:'Development',Dependencies__BaseUrl:dependencies,Dependencies__TemplatesBaseUrl:dependencies,Dependencies__MaterialsBaseUrl:serviceUrls.materials,Dependencies__ReviewBaseUrl:serviceUrls.review,Dependencies__ReceiptsBaseUrl:serviceUrls.intake,Dependencies__AccessToken:'demo-system',Storage__ConnectionString:`Host=${process.env.PGHOST};Port=${process.env.PGPORT||5432};Database=${s};Username=${s};Password=${process.env[s.toUpperCase()+'_DATABASE_PASSWORD']}`,S3__Endpoint:process.env.S3_ENDPOINT,S3__Region:'us-east-1',S3__AccessKey:process.env.S3_ACCESS_KEY,S3__SecretKey:process.env.S3_SECRET_KEY,Runtime__LeaseSeconds:'1800',Logging__LogLevel__Default:'Warning'}});
 children.push(c);c.on('error',console.error);c.on('exit',code=>{if(!stopping)console.error(s+' exited '+code);});await log.close();
}
await writeFile(join(data,'processes.json'),JSON.stringify({launcher:process.pid,services:children.map(c=>c.pid)}));
async function shutdown(){if(stopping)return;stopping=true;for(const c of children)c.kill();await save();await database.end();server.close();setTimeout(()=>process.exit(),1000).unref();}
process.on('SIGINT',shutdown);process.on('SIGTERM',shutdown);
console.log('Демонстрационный стенд: '+base);
