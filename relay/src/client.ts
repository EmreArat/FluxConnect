import WebSocket from 'ws';
import { Hub } from './hub';
import { ClientToServerMessage } from './types';

const RATE_LIMIT_PER_SECOND = parseInt(process.env.RATE_LIMIT_PER_SECOND ?? '10', 10);
// Medya akışı (ekran/webcam) sürekli veri gönderir; kontrol mesajı limiti burada
// işe yaramaz. Onun yerine SANİYEDE BAYT sınırı koyuyoruz — akış çalışır ama tek
// istemci sunucunun bandını/CPU'sunu tüketemez. 0 = sınırsız (önerilmez).
const RELAY_BYTES_PER_SECOND = parseInt(process.env.RELAY_BYTES_PER_SECOND ?? '3145728', 10); // 3 MB/sn
/**
 * TÜM relay trafiği için global tavan (bayt/sn).
 *
 * Bağlantı başına limit tek başına yetmez: 200 bağlantı × 3 MB/s = 600 MB/s,
 * bu sunucunun kapasitesinin kat kat üstü. Global tavan, relay'in sunucunun
 * hattını doldurup yanındaki uygulamaları (DB, web) aç bırakmasını engeller.
 *
 * Varsayılan 36 MB/s (~294 Mbit/s) = ölçülen gerçekçi kapasitenin (~420 Mbit/s,
 * 2026-08-14 Falkenstein testi) %70'i. Kapasitesi farklı bir sunucuya taşınırsa
 * bu değer yeniden ölçülüp güncellenmeli.
 */
const RELAY_TOTAL_BYTES_PER_SECOND = parseInt(process.env.RELAY_TOTAL_BYTES_PER_SECOND ?? '37748736', 10);
// presence_subscribe ile takip edilebilecek azami ID (bellek koruması)
const MAX_PRESENCE_IDS = parseInt(process.env.MAX_PRESENCE_IDS ?? '200', 10);

/** Global bant sayacı — tüm bağlantılar ortak. */
let globalBantPencere = Date.now();
let globalBantSayac = 0;
let globalBantUyari = false;

/** Sağlık endpoint'i için anlık bant kullanımı (izleme). */
export function bantDurumu() {
    return {
        bytesPerSecondNow: globalBantSayac,
        bytesPerSecondLimit: RELAY_TOTAL_BYTES_PER_SECOND,
        throttling: globalBantUyari,
    };
}

function globalBantKontrol(bayt: number): boolean {
    if (RELAY_TOTAL_BYTES_PER_SECOND <= 0) return true;
    const now = Date.now();
    if (now - globalBantPencere >= 1000) {
        globalBantPencere = now;
        globalBantSayac = 0;
        globalBantUyari = false;
    }
    globalBantSayac += bayt;
    if (globalBantSayac > RELAY_TOTAL_BYTES_PER_SECOND) {
        if (!globalBantUyari) {
            globalBantUyari = true;
            console.warn(`[Relay] 🚦 GLOBAL bant tavanı aşıldı (${(RELAY_TOTAL_BYTES_PER_SECOND / 1048576).toFixed(0)} MB/s) — veri düşürülüyor.`);
        }
        return false;
    }
    return true;
}

// IP başına istek sayacı
const ipRequestCount = new Map<string, { count: number; resetAt: number }>();

/**
 * Süresi geçmiş sayaç kayıtlarını temizler.
 *
 * Eskiden kayıtlar hiç silinmiyordu: her yeni IP kalıcı bir girdi bırakıyor,
 * uzun süre çalışan süreçte Map sessizce büyüyordu (bellek sızıntısı).
 */
setInterval(() => {
    const now = Date.now();
    for (const [ip, entry] of ipRequestCount) {
        if (entry.resetAt < now) ipRequestCount.delete(ip);
    }
}, 60_000).unref();

function checkRateLimit(ip: string): boolean {
    const now = Date.now();
    const entry = ipRequestCount.get(ip);

    if (!entry || entry.resetAt < now) {
        ipRequestCount.set(ip, { count: 1, resetAt: now + 1000 });
        return true;
    }

    entry.count++;
    if (entry.count > RATE_LIMIT_PER_SECOND) {
        return false; // Limit aşıldı
    }
    return true;
}

export class ClientHandler {
    private clientId?: string;
    /** Bu bağlantının içinde bulunduğu saniyede aktardığı bayt (bant sınırı için). */
    private bantPencereBaslangic = Date.now();
    private bantSayac = 0;
    private bantUyariVerildi = false;

    constructor(
        private ws: WebSocket,
        private ip: string,
        private hub: Hub
    ) {
        this.ws.on('message', (raw) => this.onMessage(raw));
        this.ws.on('close', () => this.onClose());
        this.ws.on('error', (err) => {
            console.error(`[Client] WebSocket hatası (${ip}):`, err.message);
        });
    }

    /**
     * Saniyede bayt sınırı. Aşılırsa `false` döner ve veri DÜŞÜRÜLÜR (bağlantı
     * kapatılmaz — anlık tepe yapan normal bir akışı koparmak istemiyoruz).
     */
    private bantKontrol(bayt: number): boolean {
        // Önce global tavan: sunucunun hattını hiçbir durumda doldurmasın.
        if (!globalBantKontrol(bayt)) return false;
        if (RELAY_BYTES_PER_SECOND <= 0) return true;
        const now = Date.now();
        if (now - this.bantPencereBaslangic >= 1000) {
            this.bantPencereBaslangic = now;
            this.bantSayac = 0;
            this.bantUyariVerildi = false;
        }
        this.bantSayac += bayt;
        if (this.bantSayac > RELAY_BYTES_PER_SECOND) {
            if (!this.bantUyariVerildi) {
                this.bantUyariVerildi = true;
                console.warn(`[Client] 🚦 Bant sınırı aşıldı, veri düşürülüyor: ${this.clientId ?? this.ip}`);
            }
            return false;
        }
        return true;
    }

    private onMessage(raw: WebSocket.RawData): void {
        const buf = Buffer.isBuffer(raw) ? raw : Buffer.from(raw as ArrayBuffer);

        if (buf.length >= 2 && buf[0] === 0xFC && buf[1] === 0x02) {
            // Binary relay (ekran/webcam akışı) — kontrol mesajı limiti yerine
            // bant sınırına tabi: akış sürer ama sunucuyu boğamaz.
            if (!this.bantKontrol(buf.length)) return;
            this.handleBinaryRelay(buf);
            return;
        }

        let msg: ClientToServerMessage;
        try {
            msg = JSON.parse(buf.toString('utf8')) as ClientToServerMessage;
        } catch {
            this.send({ type: 'error', code: 'INVALID_JSON', message: 'Geçersiz mesaj formatı.' });
            return;
        }

        // Kontrol mesajları: saniyede istek sayısı sınırı.
        // relay (medya akışı): istek sayısı yerine BANT sınırı — akış sürekli veri
        // gönderir, sayı limiti akışı keserdi; ama tamamen sınırsız bırakmak da
        // tek istemcinin sunucuyu boğmasına izin veriyordu.
        if (msg.type !== 'relay') {
            if (!checkRateLimit(this.ip)) {
                this.send({ type: 'error', code: 'RATE_LIMITED', message: 'İstek limiti aşıldı.' });
                return;
            }
        } else if (!this.bantKontrol(buf.length)) {
            return; // sessizce düşür — akışta hata mesajı spam'i istemiyoruz
        }

        this.dispatchMessage(msg);
    }

    private dispatchMessage(msg: ClientToServerMessage): void {

        switch (msg.type) {
            case 'ping':
                this.send({ type: 'pong' });
                break;

            case 'register':
                this.handleRegister(msg.id, msg.display_name, msg.hardware_id, msg.has_session_password ?? false);
                break;

            case 'connect_request':
                this.handleConnectRequest(msg.target_id);
                break;

            case 'password_attempt':
                this.handlePasswordAttempt(msg.session_id, msg.password_hash);
                break;

            case 'password_verify_result':
                this.handlePasswordVerifyResult(msg.session_id, msg.success);
                break;

            case 'connect_response':
                this.handleConnectResponse(msg.session_id, msg.accepted);
                break;

            case 'relay':
                this.handleRelay(msg.session_id, msg.target_id, msg.data);
                break;

            case 'presence_subscribe':
                this.handlePresenceSubscribe(msg.ids);
                break;

            case 'presence_query':
                this.handlePresenceQuery(msg.ids);
                break;

            default:
                this.send({ type: 'error', code: 'UNKNOWN_TYPE', message: 'Bilinmeyen mesaj tipi.' });
        }
    }

    // ----------------------------------------------------------------
    // Kayıt
    // ----------------------------------------------------------------
    private handleRegister(id: string, displayName: string, hardwareId?: string, hasSessionPassword = false): void {
        // ID formatı: tam olarak 9 rakam
        if (!/^\d{9}$/.test(id)) {
            this.send({ type: 'error', code: 'INVALID_ID', message: 'ID 9 haneli sayı olmalıdır.' });
            return;
        }

        const ok = this.hub.register(id, displayName, this.ws, this.ip, hardwareId, hasSessionPassword);
        if (!ok) {
            this.send({ type: 'error', code: 'ID_TAKEN', message: 'Bu ID zaten kullanımda.' });
            return;
        }

        this.clientId = id;
        this.send({ type: 'registered', id });
    }

    // ----------------------------------------------------------------
    // Bağlantı İsteği
    // ----------------------------------------------------------------
    private handleConnectRequest(targetId: string): void {
        if (!this.clientId) {
            this.send({ type: 'error', code: 'NOT_REGISTERED', message: 'Önce kayıt olunmalı.' });
            return;
        }

        const resolvedId = this.hub.resolveTargetId(targetId);
        if (!resolvedId) {
            this.send({ type: 'error', code: 'TARGET_OFFLINE', message: 'Hedef makine çevrimdışı.' });
            return;
        }

        if (resolvedId === this.clientId) {
            this.send({ type: 'error', code: 'SELF_CONNECT', message: 'Kendinize bağlanamazsınız.' });
            return;
        }

        const session = this.hub.createSession(this.clientId, resolvedId, false);

        const requester = this.hub.getClient(this.clientId)!;

        const target = this.hub.getClient(resolvedId)!;

        // Hedef tarafa bildir
        this.hub.sendToClient(resolvedId, {
            type: 'incoming_request',
            from_id: this.clientId,
            from_display_name: requester.displayName,
            session_id: session.sessionId,
            requires_password: target.hasSessionPassword,
        });

        // İstekte bulunana oturum bilgisi
        this.hub.sendToClient(this.clientId!, {
            type: 'connect_pending',
            session_id: session.sessionId,
            target_has_password: target.hasSessionPassword,
        });

        console.log(`[Handler] 📤 Bağlantı isteği: ${this.clientId} → ${resolvedId} (hedef: ${targetId}) | Oturum: ${session.sessionId}`);
    }

    // ----------------------------------------------------------------
    // Şifre Denemesi
    // ----------------------------------------------------------------
    private handlePasswordAttempt(sessionId: string, passwordHash: string): void {
        const session = this.hub.getSession(sessionId);
        if (!session || session.requesterId !== this.clientId) {
            this.send({ type: 'error', code: 'SESSION_NOT_FOUND', message: 'Oturum bulunamadı.' });
            return;
        }

        // Şifre doğrulamasını hedef tarafa ilet (relay şifreyi bilmez)
        this.hub.sendToClient(session.targetId, {
            type: 'relay',
            session_id: sessionId,
            from_id: this.clientId!,
            data: JSON.stringify({ __internal: 'password_check', hash: passwordHash }),
        });
    }

    // ----------------------------------------------------------------
    // Bağlantı Yanıtı (Kabul / Reddet)
    // ----------------------------------------------------------------
    private handlePasswordVerifyResult(sessionId: string, success: boolean): void {
        if (!this.clientId) return;

        const session = this.hub.getSession(sessionId);
        if (!session || session.targetId !== this.clientId) return;

        this.hub.sendToClient(session.requesterId, {
            type: 'password_result',
            session_id: sessionId,
            success,
        });

        if (!success) {
            this.hub.sendToClient(session.requesterId, {
                type: 'password_result',
                session_id: sessionId,
                success: false,
            });
            return;
        }

        // Başarılı şifre doğrulaması connect_response ile tamamlanır (AcceptAsync)
    }

    private handleConnectResponse(sessionId: string, accepted: boolean): void {
        const session = this.hub.getSession(sessionId);
        if (!session || session.targetId !== this.clientId) {
            this.send({ type: 'error', code: 'SESSION_NOT_FOUND', message: 'Oturum bulunamadı.' });
            return;
        }

        if (!accepted) {
            this.hub.closeSession(sessionId, 'rejected');
            return;
        }

        // Kabul edildi — her iki tarafa da bildir
        const target = this.hub.getClient(this.clientId!);
        const requester = this.hub.getClient(session.requesterId);

        this.hub.setSessionState(sessionId, 'active');

        // Requester'a bildir
        this.hub.sendToClient(session.requesterId, {
            type: 'connect_accepted',
            session_id: sessionId,
            peer_id: this.clientId!,
            peer_display_name: target?.displayName ?? '',
            peer_hardware_id: target?.hardwareId,
        });

        // Target'a da bildir
        this.hub.sendToClient(this.clientId!, {
            type: 'connect_accepted',
            session_id: sessionId,
            peer_id: session.requesterId,
            peer_display_name: requester?.displayName ?? '',
            peer_hardware_id: requester?.hardwareId,
        });

        console.log(`[Handler] ✅ Bağlantı kabul edildi: ${session.requesterId} ↔ ${this.clientId}`);
    }

    // ----------------------------------------------------------------
    // E2EE Veri Röleleme
    // ----------------------------------------------------------------
    private handleBinaryRelay(buf: Buffer): void {
        if (!this.clientId) return;

        let offset = 2;
        if (buf.length < offset + 4) return;

        const sessionLen = buf.readUInt16LE(offset);
        offset += 2;
        if (buf.length < offset + sessionLen + 2) return;

        const sessionId = buf.subarray(offset, offset + sessionLen).toString('utf8');
        offset += sessionLen;

        const peerLen = buf.readUInt16LE(offset);
        offset += 2;
        if (buf.length < offset + peerLen) return;

        offset += peerLen; // target_id (client→server) — routing oturumdan yapılır
        const framePayload = buf.subarray(offset);

        const session = this.hub.getSession(sessionId);
        if (!session || session.state !== 'active') return;

        if (session.requesterId !== this.clientId && session.targetId !== this.clientId) {
            this.send({ type: 'error', code: 'UNAUTHORIZED', message: 'Bu oturuma erişim yetkiniz yok.' });
            return;
        }

        const actualTargetId = session.requesterId === this.clientId
            ? session.targetId
            : session.requesterId;

        const sessionStart = 4;
        const sessionIdSlice = buf.subarray(sessionStart, sessionStart + sessionLen);
        const fromIdBytes = Buffer.from(this.clientId, 'utf8');
        const out = Buffer.alloc(2 + 2 + sessionLen + 2 + fromIdBytes.length + framePayload.length);
        let o = 0;
        out[o++] = 0xFC;
        out[o++] = 0x02;
        out.writeUInt16LE(sessionLen, o);
        o += 2;
        sessionIdSlice.copy(out, o);
        o += sessionLen;
        out.writeUInt16LE(fromIdBytes.length, o);
        o += 2;
        fromIdBytes.copy(out, o);
        o += fromIdBytes.length;
        framePayload.copy(out, o);

        this.hub.sendBinaryToClient(actualTargetId, out);
    }

    private handleRelay(sessionId: string, targetId: string, data: string): void {
        if (!this.clientId) return;

        const session = this.hub.getSession(sessionId);
        if (!session || session.state !== 'active') return;

        // Güvenlik: Bu istemci, bu oturumun tarafı mı?
        if (session.requesterId !== this.clientId && session.targetId !== this.clientId) {
            this.send({ type: 'error', code: 'UNAUTHORIZED', message: 'Bu oturuma erişim yetkiniz yok.' });
            return;
        }

        // targetId parametresine güvenmek yerine oturum bilgisinden karşı tarafı belirle
        // Bu sayede yanlış targetId gönderilse bile veri doğru yere gider
        const actualTargetId = session.requesterId === this.clientId
            ? session.targetId
            : session.requesterId;

        // Şifrelenmiş veriyi körü körüne karşı tarafa ilet (relay içeriği görmez)
        this.hub.sendToClient(actualTargetId, {
            type: 'relay',
            session_id: sessionId,
            from_id: this.clientId,
            data,
        });
    }

    // ----------------------------------------------------------------
    // Bağlantı Kapatıldı
    // ----------------------------------------------------------------
    private onClose(): void {
        if (this.clientId) {
            this.hub.unregister(this.clientId);
        }
    }

    // ----------------------------------------------------------------
    // Presence (ÇevrimDurum)
    // ----------------------------------------------------------------
    /** Liste boyutunu sınırla — sınırsız ID listesi bellek şişirme aracı olurdu. */
    private kirpIdListesi(ids: unknown): string[] {
        if (!Array.isArray(ids)) return [];
        return ids.filter((x): x is string => typeof x === 'string').slice(0, MAX_PRESENCE_IDS);
    }

    private handlePresenceSubscribe(ids: string[]): void {
        if (!this.clientId) {
            this.send({ type: 'error', code: 'NOT_REGISTERED', message: 'Önce kayıt olunmalı.' });
            return;
        }
        const guvenli = this.kirpIdListesi(ids);
        this.hub.subscribePresence(this.clientId, guvenli);

        // Hemen mevcut durumları gönder
        const statuses = this.hub.queryPresence(guvenli);
        this.send({ type: 'presence_list', statuses });
    }

    private handlePresenceQuery(ids: string[]): void {
        if (!this.clientId) {
            this.send({ type: 'error', code: 'NOT_REGISTERED', message: 'Önce kayıt olunmalı.' });
            return;
        }
        const statuses = this.hub.queryPresence(this.kirpIdListesi(ids));
        this.send({ type: 'presence_list', statuses });
    }

    // ----------------------------------------------------------------
    // Yardımcı
    // ----------------------------------------------------------------
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    private send(msg: any): void {
        if (this.ws.readyState === WebSocket.OPEN) {
            this.ws.send(JSON.stringify(msg));
        }
    }
}
