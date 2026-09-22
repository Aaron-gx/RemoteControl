// Read-only viewer probe: connects to the relay as a viewer, asks for the software
// list, and reports what actually comes back. Sends no input events.
const target = process.argv[2] || 'WIN-MEQIVMJ5283-4CD5AFD8';
const token = process.argv[3] || '【已脱敏】';
const host = process.argv[4] || '202.60.232.209:8080';
const seconds = Number(process.argv[5] || 20);

const url = `ws://${host}/ws?role=viewer&target=${encodeURIComponent(target)}&token=${encodeURIComponent(token)}`;
const t0 = Date.now();
const ts = () => `[${((Date.now() - t0) / 1000).toFixed(2)}s]`;
const log = (...a) => console.log(ts(), ...a);

const counts = new Map();
let swItems = -1, swBytes = 0, monitor = null, workerStatus = null, textMsgs = [];

const ws = new WebSocket(url);
ws.binaryType = 'arraybuffer';

ws.onopen = () => {
  log('OPEN ' + host + ' target=' + target);
  ws.send(new Uint8Array([0x15, 0, 0, 0, 0]));  // RequestSoftwareList
  log('sent RequestSoftwareList');
  setInterval(() => {
    const buf = new ArrayBuffer(13);
    const dv = new DataView(buf);
    dv.setUint8(0, 0x30);
    dv.setInt32(1, 8, false);
    dv.setBigInt64(5, BigInt(Date.now()), false);
    try { ws.send(new Uint8Array(buf)); } catch (_) {}
  }, 1000);
};

ws.onmessage = (ev) => {
  if (typeof ev.data === 'string') {
    textMsgs.push(ev.data);
    log('TEXT ' + ev.data.slice(0, 400));
    return;
  }
  const u8 = new Uint8Array(ev.data);
  const type = u8[0];
  const len = new DataView(ev.data).getInt32(1, false);
  const key = '0x' + type.toString(16).padStart(2, '0');
  counts.set(key, (counts.get(key) || 0) + 1);

  if (type === 0x10) {                     // SoftwareList
    swBytes = len;
    const json = new TextDecoder().decode(u8.slice(5, 5 + len));
    try {
      const arr = JSON.parse(json);
      swItems = arr.length;
      log('*** SoftwareList items=' + arr.length + ' bytes=' + len);
      console.log(JSON.stringify(arr.slice(0, 3), null, 1).slice(0, 900));
    } catch (e) { log('SoftwareList parse error: ' + e.message + ' head=' + json.slice(0, 200)); }
  } else if (type === 0x14) {              // MonitorInfo
    monitor = new TextDecoder().decode(u8.slice(5, 5 + len));
    log('MonitorInfo ' + monitor);
  } else if (type === 0x41) {              // WorkerStatus
    const j = new TextDecoder().decode(u8.slice(5, 5 + len));
    workerStatus = j;
    log('WorkerStatus ' + j.slice(0, 400));
  } else if (type === 0x50) {              // DirectCandidates
    log('DirectCandidates ' + new TextDecoder().decode(u8.slice(5, 5 + len)).slice(0, 300));
  } else if (type === 0x01) {              // VideoFrame
    if ((counts.get('0x01') || 0) <= 2) log('VideoFrame len=' + len);
  } else if (type === 0x30) {              // Heartbeat echo
  } else if (type === 0xfe) {              // Error
    log('ERROR payload=' + new TextDecoder().decode(u8.slice(5, 5 + len)));
  } else {
    log('BIN ' + key + ' len=' + len + ' head=' + Buffer.from(u8.slice(5, 5 + Math.min(len, 80))).toString('utf8').replace(/[^\x20-\x7e]/g, '.'));
  }
};

ws.onerror = (e) => log('WS ERROR ' + (e.message || e.type || 'unknown'));
ws.onclose = (e) => log('CLOSE code=' + e.code + ' reason=' + (e.reason || ''));

setTimeout(() => {
  console.log('\n===== SUMMARY =====');
  console.log('target            : ' + target);
  console.log('software list     : ' + (swItems >= 0 ? swItems + ' items (' + swBytes + ' bytes)' : 'NOT RECEIVED'));
  console.log('monitor info      : ' + (monitor ?? 'not received'));
  console.log('worker status     : ' + (workerStatus ? 'received' : 'not received'));
  console.log('text messages     : ' + (textMsgs.length ? textMsgs.join(' | ').slice(0, 300) : 'none'));
  console.log('frame counts      : ' + [...counts.entries()].sort().map(([k, v]) => k + '=' + v).join(' '));
  try { ws.close(); } catch (_) {}
  setTimeout(() => process.exit(0), 300);
}, seconds * 1000);
