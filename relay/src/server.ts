import 'dotenv/config';
import https from 'https';
import http from 'http';
import fs from 'fs';
import { WebSocketServer, WebSocket } from 'ws';
import { Hub } from './hub';
import { ClientHandler } from './client';

const PORT = parseInt(process.env.PORT ?? '8765', 10);
const DEV_NO_TLS = process.env.DEV_NO_TLS === 'true';
const TLS_CERT_PATH = process.env.TLS_CERT_PATH;
const TLS_KEY_PATH = process.env.TLS_KEY_PATH;

const hub = new Hub();

// ----------------------------------------------------------------
// HTTP/HTTPS Sunucu Oluştur
// ----------------------------------------------------------------
let server: http.Server | https.Server;

if (DEV_NO_TLS) {
    console.warn('⚠️  [Server] TLS devre dışı — SADECE geliştirme ortamı için!');
    server = http.createServer((req, res) => {
        if (req.url === '/health') {
            res.writeHead(200, { 'Content-Type': 'application/json' });
            res.end(JSON.stringify({ status: 'ok', ...hub.getStats() }));
        } else {
            res.writeHead(404);
            res.end();
        }
    });
} else {
    if (!TLS_CERT_PATH || !TLS_KEY_PATH) {
        console.error('❌ [Server] TLS sertifika yolları eksik! .env dosyasını kontrol edin.');
        process.exit(1);
    }

    const tlsOptions = {
        cert: fs.readFileSync(TLS_CERT_PATH),
        key: fs.readFileSync(TLS_KEY_PATH),
        minVersion: 'TLSv1.3' as const,
    };

    server = https.createServer(tlsOptions, (req, res) => {
        if (req.url === '/health') {
            res.writeHead(200, { 'Content-Type': 'application/json' });
            res.end(JSON.stringify({ status: 'ok', ...hub.getStats() }));
        } else {
            res.writeHead(404);
            res.end();
        }
    });
}

// ----------------------------------------------------------------
// WebSocket Sunucusu
// ----------------------------------------------------------------
const wss = new WebSocketServer({ server });

wss.on('connection', (ws: WebSocket, req) => {
    const ip =
        (req.headers['x-forwarded-for'] as string)?.split(',')[0]?.trim() ??
        req.socket.remoteAddress ??
        'unknown';

    console.log(`[Server] 🔌 Yeni bağlantı: ${ip}`);

    // Her bağlantı için bir handler oluştur
    new ClientHandler(ws, ip, hub);
});

// ----------------------------------------------------------------
// Başlat
// ----------------------------------------------------------------
server.listen(PORT, () => {
    const protocol = DEV_NO_TLS ? 'ws' : 'wss';
    console.log(`\n🚀 FluxConnect Relay Sunucu`);
    console.log(`   Dinleniyor : ${protocol}://0.0.0.0:${PORT}`);
    console.log(`   TLS        : ${DEV_NO_TLS ? '❌ Devre dışı (dev)' : '✅ Aktif'}`);
    console.log(`   Sağlık     : http${DEV_NO_TLS ? '' : 's'}://localhost:${PORT}/health\n`);
});

// ----------------------------------------------------------------
// Graceful Shutdown
// ----------------------------------------------------------------
process.on('SIGTERM', () => {
    console.log('\n[Server] SIGTERM alındı, kapatılıyor...');
    wss.close(() => {
        server.close(() => {
            console.log('[Server] ✅ Kapatıldı.');
            process.exit(0);
        });
    });
});

process.on('uncaughtException', (err) => {
    console.error('[Server] Yakalanmamış hata:', err);
});
