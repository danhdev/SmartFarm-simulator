import Keycloak from 'keycloak-js';
import {HubConnectionBuilder,LogLevel} from '@microsoft/signalr';
import {mergeReading,mergeSnapshot,mergeDeviceState,isStale} from './state.js';
const $=id=>document.getElementById(id);
const readings=new Map();
let devices=[],buffer=[],stateBuffer=[],syncing=true,refreshTask=null,connection=null,keycloak=null,live=false;
const format=value=>value?new Date(value).toLocaleString():'Not received';
function status(message,error=false){ $('status').textContent=message; $('status').className=error?'error':''; }
async function token(){ await keycloak.updateToken(40); return keycloak.token; }
async function api(path){ const response=await fetch(path,{headers:{Authorization:`Bearer ${await token()}`}}); if(!response.ok) throw new Error(`API request failed (${response.status})`); return response.json(); }
function render(){
  const root=$('devices'); root.replaceChildren();
  for(const device of devices.filter(d=>!d.decommissioned)){
    const reading=readings.get(device.device_id),m=reading?.measurements;
    const card=document.createElement('article');
    const title=document.createElement('h2'); title.textContent=device.label; card.append(title);
    const badge=document.createElement('p'); badge.className='badge'; badge.textContent=`${device.device_id} · node ${device.node_id} · ${device.enabled?'enabled':'disabled'}`; card.append(badge);
    for(const [label,value] of [
      ['Air temperature',m?`${m.air_temperature.toFixed(2)} °C`:'—'],['Air humidity',m?`${m.air_humidity.toFixed(2)} %`:'—'],
      ['Soil temperature',m?`${m.soil_temperature.toFixed(2)} °C`:'—'],['Soil humidity',m?`${m.soil_humidity.toFixed(2)} %`:'—'],
      ['pH',m?m.ph.toFixed(2):'—'],['EC / light (dataset scale)',m?`${m.ec.toFixed(2)} / ${m.light.toFixed(2)}`:'—'],
      ['Quality',m?`${m.missing_flag?'Missing observation':'Available'} · ${m.anomaly_label}`:'—'],
      ['Measured',format(reading?.source_timestamp)],['Last received',format(device.last_received_at)],
      ['MQTT observed state',`${device.availability} (${format(device.availability_observed_at)})`],
      ['Delivery freshness',isStale(device.last_received_at,device.reporting_interval_seconds)?'Stale / no telemetry':'Fresh']]){
        const line=document.createElement('p'); const strong=document.createElement('strong');strong.textContent=`${label}: `;line.append(strong,document.createTextNode(String(value)));card.append(line);
      }
    root.append(card);
  }
}
function applyState(event){const device=devices.find(d=>d.device_id===event.device_id);if(device)mergeDeviceState(device,event);}
async function refresh(){
  if(refreshTask)return refreshTask;
  refreshTask=(async()=>{
    syncing=true;buffer=[];stateBuffer=[];
    try{
      const [registered,snapshot,health]=await Promise.all([api('/api/devices'),api('/api/telemetry/latest'),api('/api/status')]);
      devices=registered; mergeSnapshot(readings,snapshot,buffer);
      for(const e of stateBuffer)applyState(e);
      live=health.live_available;
      render();status(`${connection?.state??'Disconnected'} · Redis live delivery ${live?'available':'unavailable; snapshots still work'}`);
      const selected=$('node').value; $('node').replaceChildren();
      for(const device of devices.filter(d=>!d.decommissioned)){const option=document.createElement('option');option.value=device.device_id;option.textContent=device.label;$('node').append(option);}
      if([...$('node').options].some(o=>o.value===selected))$('node').value=selected;
      if(!$('from').value && snapshot.length){const newest=snapshot.reduce((a,b)=>Date.parse(a.source_timestamp)>Date.parse(b.source_timestamp)?a:b);const start=new Date(newest.source_timestamp);start.setUTCHours(0,0,0,0);$('from').value=start.toISOString().slice(0,16);$('to').value=new Date(start.getTime()+86400000).toISOString().slice(0,16);}
    }finally{
      // Events also need applying if snapshot loading failed.
      for(const r of buffer)mergeReading(readings,r);for(const e of stateBuffer)applyState(e);
      buffer=[];stateBuffer=[];syncing=false;render();
    }
  })().finally(()=>refreshTask=null);
  return refreshTask;
}
function draw(points,averages){
  const canvas=$('chart'),ctx=canvas.getContext('2d');ctx.clearRect(0,0,canvas.width,canvas.height);
  const values=[...points.map(p=>p.measurements.air_temperature),...averages.filter(a=>a.air_temperature!==null).map(a=>a.air_temperature)];
  if(!values.length){ctx.fillStyle='#333';ctx.fillText('No available observations in this range',30,40);return;}
  const start=Date.parse($('from').value+'Z'),end=Date.parse($('to').value+'Z'),min=Math.min(...values)-1,max=Math.max(...values)+1;
  const x=t=>55+(Date.parse(t)-start)/(end-start)*(canvas.width-85),y=v=>canvas.height-40-(v-min)/(max-min)*(canvas.height-75);
  ctx.strokeStyle='#ccc';ctx.strokeRect(55,25,canvas.width-85,canvas.height-65);ctx.fillStyle='#333';ctx.fillText(`${max.toFixed(1)} °C`,4,30);ctx.fillText(`${min.toFixed(1)} °C`,4,canvas.height-40);
  for(const [list,time,value,color]of[[points,p=>p.source_timestamp,p=>p.measurements.air_temperature,'#147d69'],[averages.filter(a=>a.air_temperature!==null),p=>p.bucket,p=>p.air_temperature,'#d77818']]){
    ctx.strokeStyle=color;ctx.lineWidth=2;ctx.beginPath();list.forEach((p,i)=>i?ctx.lineTo(x(time(p)),y(value(p))):ctx.moveTo(x(time(p)),y(value(p))));ctx.stroke();for(const p of list){ctx.beginPath();ctx.arc(x(time(p)),y(value(p)),3,0,Math.PI*2);ctx.fillStyle=color;ctx.fill();}
  }
}
async function loadHistory(){
  const params=new URLSearchParams({from:$('from').value+'Z',to:$('to').value+'Z',limit:'1000'}),id=encodeURIComponent($('node').value);
  if(!id||!$('from').value||!$('to').value)return;
  const rows=[];let cursor;
  do{if(cursor)params.set('cursor',cursor);const page=await api(`/api/devices/${id}/telemetry/history?${params}`);rows.push(...page.items);cursor=page.next_cursor;if(rows.length>=10000&&cursor)throw new Error('Select a smaller range (demo chart limit: 10,000 readings)');}while(cursor);
  const aggregates=await api(`/api/devices/${id}/telemetry/aggregates?${new URLSearchParams({from:params.get('from'),to:params.get('to'),interval:'hour'})}`);
  draw(rows.filter(r=>!r.measurements.missing_flag),aggregates);$('history-status').textContent=`${rows.length} measurements · ${aggregates.length} hourly buckets · green: observations, orange: hourly means (missing observations excluded)`;
}
async function main(){
  const config=await fetch('/demo/config.json').then(r=>r.json());keycloak=new Keycloak(config);
  const authenticated=await keycloak.init({onLoad:'check-sso',pkceMethod:'S256',checkLoginIframe:false});
  $('login').onclick=()=>keycloak.login({redirectUri:location.origin+'/demo/'});
  $('logout').onclick=()=>keycloak.logout({redirectUri:location.origin+'/demo/'});
  $('login').hidden=authenticated;$('logout').hidden=!authenticated;
  if(!authenticated){status('Sign in with the farm administrator account.');return;}
  $('user').textContent=keycloak.tokenParsed?.preferred_username??'Administrator';
  connection=new HubConnectionBuilder().withUrl('/hubs/telemetry',{accessTokenFactory:token}).withAutomaticReconnect([0,2000,10000,30000]).configureLogging(LogLevel.Warning).build();
  connection.on('TelemetryPersisted',r=>{if(syncing)buffer.push(r);else if(mergeReading(readings,r))render();});
  connection.on('DeviceStateChanged',e=>{if(syncing)stateBuffer.push(e);else{applyState(e);render();}});
  connection.on('LiveStatus',s=>{live=s.available;status(`Redis live delivery ${live?'available':'unavailable'}`);if(live)refresh().catch(e=>status(e.message,true));});
  connection.onreconnecting(()=>{syncing=true;status('Reconnecting; displaying last known state…');});
  connection.onreconnected(()=>refresh().catch(e=>status(e.message,true)));
  const connect=async()=>{try{await connection.start();await refresh();}catch(e){status(e.message,true);setTimeout(()=>{if(connection.state==='Disconnected')connect();},10000);}};
  connection.onclose(()=>{status('Live connection closed; retrying…',true);setTimeout(()=>{if(connection.state==='Disconnected')connect();},10000);});
  await connect();
  $('refresh').onclick=()=>refresh().catch(e=>status(e.message,true));
  $('load-history').onclick=()=>loadHistory().catch(e=>$('history-status').textContent=e.message);
  setInterval(()=>refresh().catch(e=>status(e.message,true)),60000);setInterval(render,10000);
  keycloak.onTokenExpired=()=>keycloak.updateToken(40).catch(()=>keycloak.login());
}
main().catch(e=>status(e.message,true));
