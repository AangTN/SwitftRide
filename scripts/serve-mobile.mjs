// Dependency-free local preview. Each app has its own origin and session storage.
import http from 'node:http';
import https from 'node:https';
import { createReadStream, existsSync, statSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../apps/mobile/build');
const backend = new URL(process.env.SWIFTRIDE_API_URL || 'http://127.0.0.1:5000');
if (!['http:', 'https:'].includes(backend.protocol)) throw new Error('API must use HTTP(S).');
const mime = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.json': 'application/json', '.wasm': 'application/wasm', '.png': 'image/png', '.ttf': 'font/ttf', '.otf': 'font/otf', '.ico': 'image/x-icon' };
const servers = [];
for (const [app, port] of [['rider', 5173], ['driver', 5174]]) {
  const directory = path.join(root, `web-${app}`);
  if (!existsSync(path.join(directory, 'index.html'))) throw new Error(`Build ${app} first (scripts/Start-Mobile.ps1).`);
  const server = http.createServer((req, res) => {
    const pathname = new URL(req.url, 'http://localhost').pathname;
    if (pathname.startsWith('/api/') || pathname.startsWith('/seed/')) {
      const transport = backend.protocol === 'https:' ? https : http;
      const upstream = transport.request(new URL(req.url, backend), { method: req.method, headers: { ...req.headers, host: backend.host } }, response => {
        res.writeHead(response.statusCode, { ...response.headers, 'cache-control': 'no-store' });
        response.pipe(res);
      });
      upstream.setTimeout(25000, () => upstream.destroy(new Error('Backend timeout')));
      upstream.on('error', () => {
        if (!res.headersSent) res.writeHead(502, { 'content-type': 'application/problem+json' });
        res.end(JSON.stringify({ title: 'backend_unavailable', status: 502 }));
      });
      req.pipe(upstream);
      return;
    }
    let decoded;
    try { decoded = decodeURIComponent(pathname); } catch { res.writeHead(400); res.end(); return; }
    let file = path.resolve(directory, `.${decoded}`);
    if (file !== directory && !file.startsWith(directory + path.sep)) { res.writeHead(403); res.end(); return; }
    if (!existsSync(file) || statSync(file).isDirectory()) file = path.join(directory, 'index.html');
    res.writeHead(200, { 'content-type': mime[path.extname(file)] || 'application/octet-stream', 'cache-control': 'no-cache', 'x-content-type-options': 'nosniff' });
    createReadStream(file).on('error', () => res.destroy()).pipe(res);
  });
  server.on('error', e => { console.error(`${app}: ${e.message}`); for (const s of servers) s.close(); process.exitCode = 1; });
  server.listen(port, '127.0.0.1', () => console.log(`SwiftRide ${app}: http://localhost:${port}`));
  servers.push(server);
}
process.on('SIGINT', () => { for (const server of servers) server.close(); });
