export function compareReadings(a,b) {
  const source=Date.parse(a.source_timestamp)-Date.parse(b.source_timestamp);
  if(source) return source;
  const emitted=Date.parse(a.emitted_at)-Date.parse(b.emitted_at);
  if(emitted) return emitted;
  return a.message_id===b.message_id?0:a.message_id>b.message_id?1:-1;
}
export function mergeReading(readings,incoming) {
  const previous=readings.get(incoming.device_id);
  if(!previous || compareReadings(incoming,previous)>0) {
    readings.set(incoming.device_id,incoming); return true;
  }
  return false;
}
export function mergeSnapshot(readings,snapshot,buffered=[]) {
  for(const reading of [...snapshot,...buffered]) mergeReading(readings,reading);
  return readings;
}
export function isStale(lastReceivedAt,interval,now=Date.now()) {
  return !lastReceivedAt || now-Date.parse(lastReceivedAt)>interval*2*1000;
}
export function mergeDeviceState(device,event) {
  if(event.availability_observed_at && (!device.availability_observed_at || Date.parse(event.availability_observed_at)>=Date.parse(device.availability_observed_at))) {
    device.availability=event.availability; device.availability_observed_at=event.availability_observed_at;
  }
  if(event.last_received_at && (!device.last_received_at || Date.parse(event.last_received_at)>Date.parse(device.last_received_at))) device.last_received_at=event.last_received_at;
}
