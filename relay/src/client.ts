import WebSocket from 'ws';
import { Hub } from './hub';
import { ClientToServerMessage } from './types';

const RATE_LIMIT_PER_SECOND = parseInt(process.env.RATE_LIMIT_PER_SECOND ?? '10', 10);

// IP başına istek sayacı
const ipRequestCount = new Map<string, { count: number; resetAt: number }>();

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

    private onMessage(raw: WebSocket.RawData): void {
        const buf = Buffer.isBuffer(raw) ? raw : Buffer.from(raw as ArrayBuffer);

        if (buf.length >= 2 && buf[0] === 0xFC && buf[1] === 0x02) {
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

        // Rate limit kontrolü — medya/relay mesajlarına uygulanmaz
        // (Webcam/ses sürekli akan veri gönderir, rate limit keser)
        if (msg.type !== 'relay') {
            if (!checkRateLimit(this.ip)) {
                this.send({ type: 'error', code: 'RATE_LIMITED', message: 'İstek limiti aşıldı.' });
                return;
            }
        }

        this.dispatchMessage(msg);
    }

    private dispatchMessage(msg: ClientToServerMessage): void {

        switch (msg.type) {
            case 'ping':
                this.send({ type: 'pong' });
                break;

            case 'register':
                this.handleRegister(msg.id, msg.display_name, msg.hardware_id);
                break;

            case 'connect_request':
                this.handleConnectRequest(msg.target_id);
                break;

            case 'password_attempt':
                this.handlePasswordAttempt(msg.session_id, msg.password_hash);
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
    private handleRegister(id: string, displayName: string, hardwareId?: string): void {
        // ID formatı: tam olarak 9 rakam
        if (!/^\d{9}$/.test(id)) {
            this.send({ type: 'error', code: 'INVALID_ID', message: 'ID 9 haneli sayı olmalıdır.' });
            return;
        }

        const ok = this.hub.register(id, displayName, this.ws, this.ip, hardwareId);
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

        // Hedef tarafa bildir
        this.hub.sendToClient(resolvedId, {
            type: 'incoming_request',
            from_id: this.clientId,
            from_display_name: requester.displayName,
            session_id: session.sessionId,
            requires_password: false,
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
    private handlePresenceSubscribe(ids: string[]): void {
        if (!this.clientId) {
            this.send({ type: 'error', code: 'NOT_REGISTERED', message: 'Önce kayıt olunmalı.' });
            return;
        }
        this.hub.subscribePresence(this.clientId, ids);

        // Hemen mevcut durumları gönder
        const statuses = this.hub.queryPresence(ids);
        this.send({ type: 'presence_list', statuses });
    }

    private handlePresenceQuery(ids: string[]): void {
        if (!this.clientId) {
            this.send({ type: 'error', code: 'NOT_REGISTERED', message: 'Önce kayıt olunmalı.' });
            return;
        }
        const statuses = this.hub.queryPresence(ids);
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
