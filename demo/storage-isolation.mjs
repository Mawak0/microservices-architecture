import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {Client} from 'pg';
const names=['materials','intake','review','demo'];
const compose=process.argv.includes('--compose');
let passwords,port,s3;
if(compose) {
  const config=Object.fromEntries((await readFile(new URL('../deploy/.env',import.meta.url),'utf8')).split(/\r?\n/).filter(l=>l&&!l.startsWith('#')).map(l=>{const i=l.indexOf('=');return [l.slice(0,i),l.slice(i+1)];}));
  passwords=Object.fromEntries(names.map(n=>[n,config[n.toUpperCase()+'_DATABASE_PASSWORD']]));port=55432;s3=17070;
} else {
  passwords=JSON.parse(await readFile(new URL('../.demo-data/settings.json',import.meta.url),'utf8')).databasePasswords;port=55433;s3=17071;
}
assert.equal(new Set(Object.values(passwords)).size,4);
for(let i=0;i<names.length;i++) {
  const name=names[i],other=names[(i+1)%names.length];
  const options={host:'127.0.0.1',port,user:name,password:passwords[name],database:name};
  const own=new Client(options);try {await own.connect();assert.equal((await own.query('SELECT current_user')).rows[0].current_user,name);}finally{await own.end();}
  for(const attempt of [{...options,database:other},{...options,database:other,user:other}]) {
    const denied=new Client(attempt);try {await assert.rejects(()=>denied.connect(),e=>['42501','28P01'].includes(e.code));}finally{await denied.end();}
  }
}
for(const bucket of ['answer-images','print-materials']) {
  const anonymous=await fetch(`http://127.0.0.1:${s3}/${bucket}`,{signal:AbortSignal.timeout(5000)});
  assert.equal(anonymous.status,403);await anonymous.arrayBuffer();
}
console.log('PASS: four independent credentials, cross-database access denied, anonymous S3 access denied');
