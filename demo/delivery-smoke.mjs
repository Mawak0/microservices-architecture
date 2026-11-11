import assert from 'node:assert/strict';
import {readFile,writeFile} from 'node:fs/promises';
import {createHash,randomUUID} from 'node:crypto';
import QRCode from 'qrcode';
const base='http://127.0.0.1:19180',file=new URL('../.demo-data/delivery-proof.json',import.meta.url);
const hash=bytes=>createHash('sha256').update(bytes).digest('hex');
async function call(path,method='GET',body,role='admin') {
  const response=await fetch(base+path,{method,signal:AbortSignal.timeout(30000),headers:{'content-type':'application/json','x-demo-role':role,'Idempotency-Key':randomUUID()},body:body?JSON.stringify(body):undefined});
  const bytes=Buffer.from(await response.arrayBuffer()); assert.ok(response.ok,bytes.toString());
  return {data:response.headers.get('content-type')?.includes('json')?JSON.parse(bytes):bytes,operation:response.headers.get('Operation-Id')};
}
const api=async(s,p,...args)=>(await call('/api/'+s+p,...args)).data;
const mode=process.argv[2];
if(mode==='prepare') {
  const game=(await call('/demo/game','POST',{name:'Восстановление доставки',system:'bonuses'})).data;
  const blank=(await api('materials','/blanks','POST',{gameId:game.id,teamId:game.teamId,taskId:game.tasks[0].id,count:1})).blanks[0];
  await writeFile(file,JSON.stringify({gameId:game.id,blank}));
  console.log('PASS: delivery recovery scenario prepared');
} else {
  const proof=JSON.parse(await readFile(file,'utf8'));
  if(mode==='submit') {
    const png=Buffer.from((await QRCode.toDataURL(proof.blank.qrToken)).split(',')[1],'base64');
    const response=await call('/api/intake/receipts','POST',{qrToken:proof.blank.qrToken,imageBase64:png.toString('base64'),imageMediaType:'image/png',legibilityConfirmed:true},'observer');
    proof.receipt=response.data;proof.operation=response.operation;proof.imageHash=hash(png);
    assert.equal((await api('intake','/operations/'+proof.operation)).status,'AwaitingCheckpoint');
    assert.equal((await api('intake','/receipts/'+proof.receipt.id)).id,proof.receipt.id);
    await writeFile(file,JSON.stringify(proof));
    console.log('PASS: accepted answer and photo retained while checking service is stopped');
  } else if(mode==='verify') {
    let status;const deadline=Date.now()+30000;
    do {status=(await api('intake','/operations/'+proof.operation)).status;if(status==='Completed')break;await new Promise(r=>setTimeout(r,100));}while(Date.now()<deadline);
    assert.equal(status,'Completed');
    const checks=(await api('review','/checks?gameId='+proof.gameId)).items;
    assert.equal(checks.length,1);assert.equal(checks[0].receiptId,proof.receipt.id);
    assert.equal(hash(await api('intake','/receipts/'+proof.receipt.id+'/image')),proof.imageHash);
    console.log('PASS: saved request, outbox and photo survive restart; exactly one check acknowledges the original operation');
  } else throw Error('Use prepare, submit or verify');
}
