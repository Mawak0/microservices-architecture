import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
import {writeFile} from 'node:fs/promises';
const require=createRequire(new URL('./package.json',import.meta.url)),QRCode=require('qrcode');
const base='http://127.0.0.1:19180',results=[];
async function req(p,m='GET',b,role='admin',token){const r=await fetch(base+p,{method:m,signal:AbortSignal.timeout(30000),headers:{'content-type':'application/json','x-demo-role':role,...(token?{'Assignment-Token':token}:{})},body:b===undefined?undefined:JSON.stringify(b)});const bytes=Buffer.from(await r.arrayBuffer());let data;try{data=JSON.parse(bytes);}catch{data=bytes;}return {status:r.status,data};}
async function ok(p,m,b,role,token){const r=await req(p,m,b,role,token);assert.ok(r.status<300,JSON.stringify(r));return r.data;}
function pass(name){results.push(name);console.log('PASS '+name);}
async function until(fn){for(let i=0;i<50;i++){const v=await fn();if(v)return v;await new Promise(r=>setTimeout(r,200));}throw Error('Timeout');}
const health=await ok('/demo/state');assert.equal(health.health.filter(h=>h.healthy).length,3);pass('Три настоящих сервиса доступны');
const g=await ok('/demo/game','POST',{name:'Проверка стенда',system:'bonuses'});
const api=(s,p,m,b,r,t)=>ok('/api/'+s+p,m,b,r,t);
const body={gameId:g.id,teamId:g.teamId,taskId:g.tasks[0].id,count:1,modifier:{kind:'add',value:3}};
assert.equal((await req('/api/materials/blanks','POST',body,'observer')).status,403);pass('Наблюдатель не выпускает дополнительные бланки');
const kit=await api('materials','/kits','POST',{gameId:g.id,teamId:g.teamId});assert.equal(kit.blankIds.length,3);pass('Комплект команды: три бланка');
const issued=await api('materials','/blanks','POST',body);const blank=issued.blanks[0];assert.equal(blank.taskId,g.tasks[0].id);assert.equal(blank.modifier.value,3);pass('Дополнительный бланк связан с задачей и модификатором');
const job=await api('materials','/print-jobs','POST',{gameId:g.id,teamId:g.teamId,blankIds:[blank.id],taskIds:g.tasks.map(t=>t.id)},'observer');
await until(async()=>{const j=await api('materials','/print-jobs/'+job.id);if(j.status==='failed')throw Error(JSON.stringify(j));return j.status==='completed';});
const pdf=await api('materials','/print-jobs/'+job.id+'/artifact');assert.ok(pdf.subarray(0,4).equals(Buffer.from('%PDF')));await writeFile(new URL('../.demo-data/demo-print.pdf',import.meta.url),pdf);pass('Наблюдатель печатает PDF настоящего сервиса');
const image=(await QRCode.toDataURL(blank.qrToken,{margin:4,scale:6})).split(',')[1];
const submission={qrToken:blank.qrToken,imageBase64:image,imageMediaType:'image/png',legibilityConfirmed:true};
assert.equal((await req('/api/intake/receipts','POST',{...submission,legibilityConfirmed:false},'observer')).status,422);pass('Без подтверждения читаемости ответ отклонён');
const forged='forged:local-demo';const badImage=(await QRCode.toDataURL(forged)).split(',')[1];assert.equal((await req('/api/intake/receipts','POST',{...submission,qrToken:forged,imageBase64:badImage},'observer')).status,422);pass('Подделанный QR отклонён сервером');
const receipt=await api('intake','/receipts','POST',submission,'observer');assert.equal(receipt.blankId,blank.id);pass('Сервер распознал QR и принял изображение');
assert.equal((await req('/api/intake/receipts','POST',submission,'observer')).status,409);pass('Повторная сдача бланка запрещена');
const check=await until(async()=>{const q=await api('review','/checks?gameId='+g.id);return q.items[0];});assert.equal(check.receiptId,receipt.id);pass('Событие приёма автоматически доставлено в сервис проверки');
const c=await api('review','/checks/next','POST',{gameId:g.id},'checker');assert.equal(c.referenceAnswer,'5');assert.ok(c.lease.token);pass('Проверяющий видит бланк и эталон');
assert.equal((await req('/api/review/checks/'+c.id+'/claim','POST',undefined,'checker-2')).status,409);pass('Второй проверяющий не забирает занятый ответ');
await api('review','/checks/'+c.id+'/verdict','POST',{verdict:'incorrect'},'checker',c.lease.token);pass('Первичный вердикт сохранён');
const a=await api('review','/appeals','POST',{checkId:c.id,reason:'Ответ совпадает с эталоном'},'observer');let quota=await api('review','/appeal-quotas/'+g.id);assert.equal(quota.remaining,1);pass('Апелляция расходует общую квоту игры');const repeatAppeal=await req('/api/review/appeals','POST',{checkId:c.id,reason:'Повтор'},'observer');assert.equal(repeatAppeal.status,409);assert.equal(repeatAppeal.data.code,'APPEAL_ALREADY_USED');pass('Повторная апелляция отвергается по предметному правилу');
await ok('/demo/deadline','POST',{gameId:g.id});let completion=await api('review','/games/'+g.id+'/completion','GET',undefined,'system');console.log('COMPLETION WITH APPEAL',completion);assert.equal(completion.complete,false);assert.equal(completion.pendingAppealIds.length,1);pass('Истечение сроков не завершает игру с апелляцией');
const assigned=await api('review','/appeals/next','POST',{gameId:g.id},'checker');assert.equal(assigned.id,a.id);assert.equal(assigned.check.originalVerdict,'incorrect');
await api('review','/appeals/'+a.id+'/resolve','POST',{outcome:'jury-error-corrected',verdict:'correct',reason:'Ошибка жюри исправлена'},'checker',assigned.check.lease.token);
quota=await api('review','/appeal-quotas/'+g.id);assert.equal(quota.remaining,2);const changed=await api('review','/checks/'+c.id);assert.equal(changed.verdict,'correct');assert.equal(changed.originalVerdict,'incorrect');pass('Исправление ошибки сохраняет историю и возвращает квоту');await until(async()=> (await api('review','/games/'+g.id+'/completion','GET',undefined,'system')).complete);pass('После закрытия приёма и рассмотрения апелляции все проверки завершены');
await ok('/demo/deadline','POST',{gameId:g.id,reopen:true});assert.equal((await req('/api/review/appeals','POST',{checkId:c.id,reason:'Повтор'},'observer')).status,409);pass('Открытие поздней сдачи не возобновляет срок апелляций');
const extra=(await api('materials','/blanks','POST',{...body,modifier:null})).blanks[0];const lateImage=(await QRCode.toDataURL(extra.qrToken)).split(',')[1];await api('intake','/receipts','POST',{...submission,qrToken:extra.qrToken,imageBase64:lateImage},'observer');pass('Повторное открытие разрешает позднюю сдачу без удаления истории');
const d=await ok('/demo/game','POST',{name:'Проверка Домино',system:'domino'});const db=(await api('materials','/blanks','POST',{gameId:d.id,teamId:d.teamId,taskId:d.tasks[0].id,count:1,modifier:null})).blanks[0];await api('intake','/receipts','POST',{...submission,qrToken:db.qrToken,imageBase64:(await QRCode.toDataURL(db.qrToken)).split(',')[1]},'observer');await until(async()=> (await api('review','/checks?gameId='+d.id)).items.length);const dc=await api('review','/checks/next','POST',{gameId:d.id},'checker');await api('review','/checks/'+dc.id+'/verdict','POST',{verdict:'incorrect'},'checker',dc.lease.token);assert.equal((await req('/api/review/appeals','POST',{checkId:dc.id,reason:'Тест запрета'},'observer')).status,409);pass('Домино запрещает апелляции');
await writeFile(new URL('../.demo-data/smoke-results.json',import.meta.url),JSON.stringify({at:new Date().toISOString(),results},null,2));
console.log('ALL PASSED '+results.length);



