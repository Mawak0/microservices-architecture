import {Pool} from 'pg';
const database=new Pool();
try {
  for(const name of ['materials','intake','review','demo']) {
    const password=process.env[name.toUpperCase()+'_DATABASE_PASSWORD'];
    if(!password)throw Error('Set database password for '+name);
    const literal="'"+password.replaceAll("'","''")+"'";
    if(!(await database.query('SELECT 1 FROM pg_roles WHERE rolname=$1',[name])).rowCount)
      await database.query(`CREATE ROLE ${name} LOGIN PASSWORD ${literal}`);
    else await database.query(`ALTER ROLE ${name} PASSWORD ${literal}`);
    if(!(await database.query('SELECT 1 FROM pg_database WHERE datname=$1',[name])).rowCount)
      await database.query(`CREATE DATABASE ${name} OWNER ${name}`);
    await database.query(`REVOKE CONNECT ON DATABASE ${name} FROM PUBLIC`);
    await database.query(`GRANT CONNECT ON DATABASE ${name} TO ${name}`);
  }
} finally { await database.end(); }
