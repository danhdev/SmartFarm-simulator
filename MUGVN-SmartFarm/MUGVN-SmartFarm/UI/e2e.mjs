// Explicit integration check: isolated database/topics/channels; never stops existing containers.
import assert from 'node:assert/strict';
import {spawn,execFileSync} from 'node:child_process';
import {readFileSync,openSync,mkdtempSync,closeSync} from 'node:fs';
import {tmpdir} from 'node:os';import {join,resolve} from 'node:path';import {fileURLToPath} from 'node:url';
import {randomUUID,randomBytes,createHash} from 'node:crypto';import net from 'node:net';
import mqtt from 'mqtt';import {HubConnectionBuilder,HttpTransportType,LogLevel} from '@microsoft/signalr';
process.env.NODE_TLS_REJECT_UNAUTHORIZED='0'; // Only the localhost ASP.NET development certificate in this test process.
const root=resolve(fileURLToPath(new URL('..',import.meta.url)));
const run=randomUUID().replaceAll('-',''),database='smartfarm_test_e2e_'+run,prefix='smartfarm-test/'+run+'/farm';
const scratch=mkdtempSync(join(tmpdir(),'smartfarm-e2e-'));
const secrets=Object.fromEntries(readFileSync(join(root,'.env'),'utf8').trim().split('\n').map(l=>{const i=l.indexOf('=');return[l.slice(0,i),l.slice(i+1)];}));
const delay=ms=>new Promise(r=>setTimeout(r,ms));
async function until(fn,label,timeout=45000){const end=Date.now()+timeout;let last;while(Date.now()<end){try{const r=await fn();if(r)return r;}catch(e){last=e;}await delay(150);}throw new Error(`Timed out: ${label}${last?' ('+last.message+')':''}`);}
async function proxy(target){
  let server,port;const sockets=new Set();
  async function start(){server=net.createServer(socket=>{const upstream=net.connect(target,'127.0.0.1');sockets.add(socket);sockets.add(upstream);socket.on('error',()=>upstream.destroy());upstream.on('error',()=>socket.destroy());socket.on('close',()=>{sockets.delete(socket);upstream.destroy();});upstream.on('close',()=>{sockets.delete(upstream);socket.destroy();});socket.pipe(upstream).pipe(socket);});await new Promise((r,j)=>{server.once('error',j);server.listen(port??0,'127.0.0.1',r);});port=server.address().port;}
  async function stop(){for(const s of sockets)s.destroy();sockets.clear();await new Promise(r=>server.close(r));}
  await start();return {get port(){return port;},start,stop};
}
let apiProcess,workerProcess,publisher,hub,redisProxy,mongoProxy,token;
const logs=[];
function child(name,dll,env){const path=join(scratch,name+'.log'),fd=openSync(path,'a');logs.push(path);const p=spawn('dotnet',[dll],{cwd:root,env:{...process.env,...env,DD_SERVICE:name==='worker'?'smartfarm-ingestion':'smartfarm-api'},stdio:['ignore',fd,fd]});closeSync(fd);return p;}
async function stop(p){if(!p||p.exitCode!==null||p.signalCode!==null)return;await new Promise(r=>{p.once('exit',r);p.kill('SIGTERM');setTimeout(()=>p.kill('SIGKILL'),8000).unref();});}
async function login(){
  const verifier=randomBytes(32).toString('base64url'),challenge=createHash('sha256').update(verifier).digest('base64url');
  const auth='http://localhost:8085/realms/smartfarm/protocol/openid-connect';
  const params=new URLSearchParams({client_id:'smartfarm-demo',redirect_uri:'https://localhost:7017/demo/',response_type:'code',scope:'openid',code_challenge:challenge,code_challenge_method:'S256'});
  const response=await fetch(auth+'/auth?'+params,{redirect:'manual'});const html=await response.text();
  const action=html.match(/<form[^>]*action="([^"]+)"/)?.[1]?.replaceAll('&amp;','&');assert.ok(action,'Keycloak login form available');
  const cookie=response.headers.getSetCookie().map(c=>c.split(';')[0]).join('; ');
  const signed=await fetch(action,{method:'POST',redirect:'manual',headers:{'content-type':'application/x-www-form-urlencoded',cookie},body:new URLSearchParams({username:secrets.FARM_ADMIN_USERNAME,password:secrets.FARM_ADMIN_PASSWORD,credentialId:''})});
  assert.equal(signed.status,302,'Keycloak accepts administrator password');const location=new URL(signed.headers.get('location'));
  assert.ok(location.searchParams.get('code'),'Authorization code returned');
  const tokens=await fetch(auth+'/token',{method:'POST',headers:{'content-type':'application/x-www-form-urlencoded'},body:new URLSearchParams({grant_type:'authorization_code',client_id:'smartfarm-demo',code:location.searchParams.get('code'),redirect_uri:'https://localhost:7017/demo/',code_verifier:verifier})});
  assert.equal(tokens.status,200,'PKCE code exchange');token=(await tokens.json()).access_token;assert.ok(token);
}
async function api(path,options={}){const response=await fetch('https://localhost:7017'+path,{...options,headers:{Authorization:'Bearer '+token,'content-type':'application/json',...options.headers}});if(!response.ok)throw new Error(`API ${path}: ${response.status}`);return response.status===204?null:response.json();}
const events=[];
function payload(device,node,time){return {message_id:randomUUID(),device_id:device,node_id:node,timestamp:time,emitted_at:new Date().toISOString(),air_temperature:29.5,air_humidity:70,soil_temperature:25,soil_humidity:60,ph:6.5,ec:1.2,light:20000,missing_flag:0,anomaly_label:'normal',violated_variable_count:0};}
async function send(reading,expectLive=true){const before=events.length;await publisher.publishAsync(`${prefix}/${reading.device_id}/env`,JSON.stringify(reading),{qos:1});if(expectLive)await until(()=>events.slice(before).some(r=>r.message_id===reading.message_id),'SignalR event '+reading.device_id);}
try{
  await login();console.log('PASS Keycloak password login, authorization code + PKCE, API audience');
  mongoProxy=await proxy(27018);redisProxy=await proxy(6379);
  const env={ASPNETCORE_ENVIRONMENT:'Development',DOTNET_ENVIRONMENT:'Development',ASPNETCORE_URLS:`https://localhost:7017;http://localhost:${process.env.SMARTFARM_E2E_HTTP_PORT??5198}`,Mongo__Database:database,Mongo__ConnectionString:`mongodb://127.0.0.1:${mongoProxy.port}/?directConnection=true`,Redis__ChannelPrefix:'smartfarm_e2e_'+run,Redis__ConnectionString:`127.0.0.1:${redisProxy.port},abortConnect=false,connectTimeout=500,asyncTimeout=500,configCheckSeconds=1`,Mqtt__TopicPrefix:prefix,Mqtt__ClientId:'smartfarm-e2e-'+run};
  apiProcess=child('api',join(root,'bin/Debug/net10.0/MUGVN-SmartFarm.dll'),env);
  await until(async()=>{if(apiProcess.exitCode!==null)throw new Error('API exited');return(await api('/api/status')).storage_ready;},'API initialized');
  assert.equal((await api('/api/devices')).length,4);
  const demo=await fetch('https://localhost:7017/demo/');assert.equal(demo.status,200);assert.match(await demo.text(),/SmartFarm telemetry/);
  const bundle=await fetch('https://localhost:7017/demo/app.js');assert.equal(bundle.status,200);assert.match(await bundle.text(),/S256/);
  const noAuth=await fetch('https://localhost:7017/api/devices');assert.equal(noAuth.status,401);
  const created=await api('/api/devices',{method:'POST',body:JSON.stringify({device_id:'test-extra',node_id:99,label:'Extra'})});assert.equal(created.node_id,99);
  assert.equal((await api('/api/devices/test-extra',{method:'PATCH',body:JSON.stringify({label:'Edited'})})).label,'Edited');await api('/api/devices/test-extra',{method:'DELETE'});
  assert.equal((await api('/api/devices/test-extra')).decommissioned,true);console.log('PASS authenticated API, device CRUD, decommission');
  hub=new HubConnectionBuilder().withUrl('https://localhost:7017/hubs/telemetry',{accessTokenFactory:()=>token,transport:HttpTransportType.WebSockets,skipNegotiation:true}).withAutomaticReconnect([0,500,1000]).configureLogging(LogLevel.Error).build();
  hub.on('TelemetryPersisted',reading=>{events.push(reading);} );await hub.start();
  workerProcess=child('worker',join(root,'Ingestion/bin/Debug/net10.0/SmartFarm.Ingestion.dll'),env);
  await until(()=>readFileSync(join(scratch,'worker.log'),'utf8').includes('MQTT connected'),'worker MQTT subscription');
  await until(async()=>(await api('/api/status')).live_available,'Redis subscription');
  publisher=await mqtt.connectAsync('mqtt://127.0.0.1:1883',{clientId:'smartfarm-e2e-publisher-'+run});
  const rows=[payload('pi4-1',6,'2025-07-24T08:00:00Z'),payload('pi4-2',18,'2025-07-24T08:01:00Z'),payload('pi4-3',24,'2025-07-24T08:03:00Z'),payload('pi4-4',38,'2025-07-24T08:03:30Z')];
  for(const reading of rows)await send(reading);assert.equal(events.slice(0,4).map(r=>r.device_id).join(','),'pi4-1,pi4-2,pi4-3,pi4-4');
  const receipt=(await api('/api/devices/pi4-1')).last_received_at;await send(rows[0]);assert.equal((await api('/api/devices/pi4-1')).last_received_at,receipt);
  const query='?from=2025-07-24T00:00:00Z&to=2025-07-25T00:00:00Z';assert.equal((await api('/api/devices/pi4-1/telemetry/history'+query)).items.length,1);
  const late=payload('pi4-1',6,'2025-07-24T07:00:00Z');await send(late);assert.equal((await api('/api/telemetry/latest?deviceIds=pi4-1'))[0].message_id,rows[0].message_id);
  assert.equal((await api('/api/devices/pi4-1/telemetry/aggregates'+query+'&interval=hour')).length,2);
  console.log('PASS MQTT → MongoDB → Redis → SignalR, four staggered nodes, duplicate suppression, late source ordering');
  await redisProxy.stop();await until(async()=>!(await api('/api/status')).live_available,'Redis outage detected');
  const offline=payload('pi4-1',6,'2025-07-24T08:05:00Z');await send(offline,false);
  await until(async()=>(await api('/api/telemetry/latest?deviceIds=pi4-1'))[0].message_id===offline.message_id,'storage continues during Redis outage');
  await redisProxy.start();await until(async()=>(await api('/api/status')).live_available,'Redis subscription recovery');
  await until(()=>readFileSync(join(scratch,'worker.log'),'utf8').includes('Redis connection restored'),'ingestion Redis publisher recovery');
  await send(payload('pi4-2',18,'2025-07-24T08:06:00Z'));console.log('PASS Redis outage preserves storage and live delivery recovers');
  await mongoProxy.stop();const pending=payload('pi4-3',24,'2025-07-24T08:08:00Z');await send(pending,false);
  await until(()=>readFileSync(join(scratch,'worker.log'),'utf8').includes('Ingestion retry'),'Mongo failure triggers unacknowledged retry');await mongoProxy.start();
  await until(()=>events.some(e=>e.message_id===pending.message_id),'Mongo recovery persists pending MQTT reading',90000);console.log('PASS MongoDB connection interruption and retry recovery');
  await stop(workerProcess);const queued=payload('pi4-4',38,'2025-07-24T08:09:00Z');await send(queued,false);workerProcess=child('worker',join(root,'Ingestion/bin/Debug/net10.0/SmartFarm.Ingestion.dll'),env);
  await until(()=>events.some(e=>e.message_id===queued.message_id),'persistent MQTT session after worker restart',90000);console.log('PASS worker restart and offline MQTT session queue');
  await stop(apiProcess);apiProcess=child('api',join(root,'bin/Debug/net10.0/MUGVN-SmartFarm.dll'),env);await until(async()=>(await api('/api/status')).storage_ready,'API restart');
  assert.equal((await api('/api/telemetry/latest?deviceIds=pi4-4'))[0].message_id,queued.message_id);
  if(hub.state==='Disconnected')await hub.start();await until(()=>hub.state==='Connected','SignalR reconnect');
  await until(async()=>(await api('/api/status')).live_available,'new API Redis subscription');
  await send(payload('pi4-1',6,'2025-07-24T09:00:00Z'));console.log('PASS API restart restores latest state and resumes SignalR delivery');
  console.log('End-to-end checks passed. Logs: '+scratch);
}finally{
  if(hub)await hub.stop().catch(()=>{});if(publisher)await publisher.endAsync().catch(()=>{});
  await stop(workerProcess);await stop(apiProcess);
  if(workerProcess)try{const cleanup=await mqtt.connectAsync('mqtt://127.0.0.1:1883',{clientId:'smartfarm-e2e-'+run,protocolVersion:5,clean:true,reconnectPeriod:0,connectTimeout:3000,properties:{sessionExpiryInterval:0}});await cleanup.endAsync();}catch{console.warn('Test MQTT session cleanup unavailable; broker expiry still applies');}
  if(redisProxy)await redisProxy.stop().catch(()=>{});if(mongoProxy)await mongoProxy.stop().catch(()=>{});
  assert.match(database,/^smartfarm_test_e2e_[a-f0-9]+$/);
  execFileSync('mongosh',['mongodb://127.0.0.1:27018/?directConnection=true','--quiet','--eval',`db.getSiblingDB('${database}').dropDatabase()`],{stdio:'ignore'});
}
