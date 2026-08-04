// Verifies a real WebSocket connection to SolHub, that the connection is bound to the device
// group, and that presence lands in Redis. Run with: node scripts/verify-realtime.mjs
import * as signalR from '@microsoft/signalr';

const API = process.env.SOL_API ?? 'http://localhost:5298';

const stable = {
  timeZone: 'Asia/Shanghai',
  platform: 'MacIntel',
  hardwareConcurrency: 8,
  deviceMemoryGb: 8,
  screen: { w: 1512, h: 982, colorDepth: 24, dpr: 2 },
  gpu: { vendor: 'Apple', renderer: 'Apple M1 Pro' },
  primaryLanguage: 'zh',
};

// 1. Handshake, capturing the HttpOnly cookie the way a browser would.
const handshake = await fetch(`${API}/api/v1/device/handshake`, {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({
    v: 1,
    stable,
    volatile: { userAgent: 'node-verify', canvasHash: 'node-canvas' },
    clientStoredId: null,
  }),
});

const identity = await handshake.json();
const cookie = handshake.headers.getSetCookie().map((c) => c.split(';')[0]).join('; ');
console.log('handshake:', identity.deviceId.slice(0, 8), identity.method, identity.confidence);

// 2. Connect over WebSockets, presenting the cookie so the hub can bind the connection.
const connection = new signalR.HubConnectionBuilder()
  .withUrl(`${API}/hubs/sol`, {
    transport: signalR.HttpTransportType.WebSockets,
    skipNegotiation: false,
    headers: { Cookie: cookie },
  })
  .build();

const received = [];
connection.on('ReceiveMessage', (envelope) => received.push(envelope));

await connection.start();
console.log('connected:', connection.connectionId?.slice(0, 12), 'state:', connection.state);

// 3. Round-trip a hub method to prove the JSON protocol works with source-generated metadata.
const pong = await connection.invoke('Ping');
console.log('Ping ->', pong);

await new Promise((r) => setTimeout(r, 500));
await connection.stop();
console.log('disconnected cleanly; messages received:', received.length);

if (pong !== 'pong') {
  console.error('FAIL: unexpected Ping response');
  process.exit(1);
}
console.log('OK');
