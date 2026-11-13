import assert from 'node:assert/strict';
import {readFile,writeFile,mkdir} from 'node:fs/promises';
import {createHash} from 'node:crypto';
import QRCode from 'qrcode';
await mkdir(new URL('../.demo-data/',import.meta.url),{recursive:true});
const base='http://127.0.0.1:19180',file=new URL('../.demo-data/restart-proof.json',import.meta.url);
const hash=bytes=>createHash('sha256').update(bytes).digest('hex');
async function call(path,method='GET',body,role='admin') {
  const r=await fetch(base+path,{method,signal:AbortSignal.timeout(30000),headers:{'content-type':'application/json','x-demo-role':role},body:body?JSON.stringify(body):undefined});
  const bytes=Buffer.from(await r.arrayBuffer()); assert.ok(r.ok,bytes.toString());
  return r.headers.get('content-type')?.includes('json')?JSON.parse(bytes):bytes;
}
const api=(s,p,...args)=>call('/api/'+s+p,...args);
async function until(read,predicate){for(let n=0;n<100;n++){const value=await read();if(predicate(value))return value;await new Promise(r=>setTimeout(r,100));}throw Error('Recovery timed out');}
if(process.argv[2]==='before') {
  const game=await call('/demo/game','POST',{name:'Проверка сохранности',system:'bonuses'});
  const blank=(await api('materials','/blanks','POST',{gameId:game.id,teamId:game.teamId,taskId:game.tasks[0].id,count:1})).blanks[0];
  const job=await api('materials','/print-jobs','POST',{gameId:game.id,teamId:game.teamId,blankIds:[blank.id],taskIds:[game.tasks[0].id]});
  await until(()=>api('materials','/print-jobs/'+job.id),j=>j.status==='completed');
  const image=(await QRCode.toDataURL(blank.qrToken)).split(',')[1];
  const receipt=await api('intake','/receipts','POST',{qrToken:blank.qrToken,imageBase64:image,imageMediaType:'image/png',legibilityConfirmed:true},'observer');
  const checks=await until(()=>api('review','/checks?gameId='+game.id),q=>q.items.length===1);
  await writeFile(file,JSON.stringify({gameId:game.id,blank,receipt,checkId:checks.items[0].id,jobId:job.id,
    imageHash:hash(await api('intake','/receipts/'+receipt.id+'/image')),
    pdfHash:hash(await api('materials','/print-jobs/'+job.id+'/artifact'))},null,2));
  console.log('PASS: identities, photo and PDF recorded before restart');
} else if(process.argv[2]==='after') {
  const expected=JSON.parse(await readFile(file,'utf8'));
  assert.ok((await call('/demo/state')).games.some(g=>g.id===expected.gameId));
  assert.deepEqual(await api('materials','/blanks/'+expected.blank.id),expected.blank);
  assert.deepEqual(await api('intake','/receipts/'+expected.receipt.id),expected.receipt);
  assert.equal((await api('review','/checks?gameId='+expected.gameId)).items[0].id,expected.checkId);
  assert.equal(hash(await api('intake','/receipts/'+expected.receipt.id+'/image')),expected.imageHash);
  assert.equal(hash(await api('materials','/print-jobs/'+expected.jobId+'/artifact')),expected.pdfHash);
  console.log('PASS: games, blank, accepted answer, checking queue, photo and PDF survive full restart');
} else throw Error('Use before or after');
