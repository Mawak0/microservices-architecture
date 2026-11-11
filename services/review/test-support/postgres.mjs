import pg from 'pg';
import { createHash } from 'node:crypto';

export async function postgresConnection(identity) {
  const value = process.env.TEST_POSTGRES_CONNECTION;
  if (!value) throw new Error('Set TEST_POSTGRES_CONNECTION to a dedicated PostgreSQL test database.');
  const settings = Object.fromEntries(value.split(';').filter(Boolean).map(x => { const i=x.indexOf('='); return [x.slice(0,i).trim().toLowerCase(),x.slice(i+1)]; }));
  const schema='test_'+createHash('sha256').update(identity).digest('hex').slice(0,24);
  const client = new pg.Client({host:settings.host,port:Number(settings.port||5432),database:settings.database,user:settings.username,password:settings.password});
  await client.connect();
  try { await client.query('CREATE SCHEMA IF NOT EXISTS '+schema); } finally { await client.end(); }
  return value+';Search Path='+schema+';Pooling=false';
}
