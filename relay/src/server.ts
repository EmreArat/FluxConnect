import 'dotenv/config';
import https from 'https';
import http from 'http';
import fs from 'fs';
import { WebSocketServer, WebSocket } from 'ws';
import { Hub } from './hub';
import { ClientHandler, bantDurumu } from './client';

const PORT = parseInt(process.env.PORT ?? '8765', 10);
const DEV_NO_TLS = process.env.DEV_NO_TLS === 'true';
const TLS_CERT_PATH = process.env.TLS_CERT_PATH;
const TLS_KEY_PATH = process.env.TLS_KEY_PATH;

// ── Kaynak koruma sınırları ──────────────────────────────────────
// Relay paylaşımlı bir sunucuda çalışıyor: sınırsız bırakılan her kaynak,
// yanındaki diğer uygulamaları (DB, web uygulamaları) aç bırakabilir.
// ws kütüphanesinin varsayılan maxPayload'ı 100 MB — birkaç mesajla RAM şişer.
const MAX_PAYLOAD_BYTES = parseInt(process.env.MAX_PAYLOAD_BYTES ?? '1048576', 10); // 1 MB
const MAX_CONNECTIONS = parseInt(process.env.MAX_CONNECTIONS ?? '200', 10);
// Aynı IP'den açılabilecek eşzamanlı bağlantı (tek kullanıcı tüm kotayı yemesin)
const MAX_CONNECTIONS_PER_IP = parseInt(process.env.MAX_CONNECTIONS_PER_IP ?? '10', 10);

const hub = new Hub();

/** Aktif bağlantı sayacı — IP başına ve toplam. */
let toplamBaglanti = 0;
const ipBaglantiSayisi = new Map<string, number>();

// ----------------------------------------------------------------
// HTTP/HTTPS Sunucu Oluştur
// ----------------------------------------------------------------
let server: http.Server | https.Server;

if (DEV_NO_TLS) {
    console.warn('⚠️  [Server] TLS devre dışı — SADECE geliştirme ortamı için!');
    server = http.createServer((req, res) => {
        if (req.url === '/health') {
            res.writeHead(200, { 'Content-Type': 'application/json' });
            res.end(JSON.stringify({
                status: 'ok',
                ...hub.getStats(),
                connections: toplamBaglanti,
                maxConnections: MAX_CONNECTIONS,
                uniqueIps: ipBaglantiSayisi.size,
                bandwidth: bantDurumu(),
            }));
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
            res.end(JSON.stringify({
                status: 'ok',
                ...hub.getStats(),
                connections: toplamBaglanti,
                maxConnections: MAX_CONNECTIONS,
                uniqueIps: ipBaglantiSayisi.size,
                bandwidth: bantDurumu(),
            }));
        } else {
            res.writeHead(404);
            res.end();
        }
    });
}

// ----------------------------------------------------------------
// WebSocket Sunucusu
// ----------------------------------------------------------------
const wss = new WebSocketServer({ server, maxPayload: MAX_PAYLOAD_BYTES });

wss.on('connection', (ws: WebSocket, req) => {
    const ip =
        (req.headers['x-forwarded-for'] as string)?.split(',')[0]?.trim() ??
        req.socket.remoteAddress ??
        'unknown';

    // ── Bağlantı kotaları ────────────────────────────────────────
    if (toplamBaglanti >= MAX_CONNECTIONS) {
        console.warn(`[Server] ⛔ Toplam bağlantı sınırı (${MAX_CONNECTIONS}) doldu, reddedildi: ${ip}`);
        ws.close(1013, 'Sunucu dolu, sonra tekrar deneyin.');
        return;
    }
    const ipSayi = ipBaglantiSayisi.get(ip) ?? 0;
    if (ipSayi >= MAX_CONNECTIONS_PER_IP) {
        console.warn(`[Server] ⛔ IP başına bağlantı sınırı (${MAX_CONNECTIONS_PER_IP}) aşıldı: ${ip}`);
        ws.close(1013, 'Bu adresten çok fazla bağlantı var.');
        return;
    }

    toplamBaglanti++;
    ipBaglantiSayisi.set(ip, ipSayi + 1);

    ws.once('close', () => {
        toplamBaglanti--;
        const kalan = (ipBaglantiSayisi.get(ip) ?? 1) - 1;
        // Sayaç sıfırlanınca kaydı SİL — yoksa Map her yeni IP ile büyür (bellek sızıntısı).
        if (kalan <= 0) ipBaglantiSayisi.delete(ip);
        else ipBaglantiSayisi.set(ip, kalan);
    });

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
    console.log(`   Sağlık     : http${DEV_NO_TLS ? '' : 's'}://localhost:${PORT}/health`);
    console.log(`   Sınırlar   : ${(MAX_PAYLOAD_BYTES / 1024 / 1024).toFixed(1)} MB/mesaj · ` +
        `${MAX_CONNECTIONS} bağlantı (IP başına ${MAX_CONNECTIONS_PER_IP})\n`);
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
