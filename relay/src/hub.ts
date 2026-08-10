import WebSocket from 'ws';
import { v4 as uuidv4 } from 'uuid';
import {
    PendingSession,
    ServerToClientMessage,
    SessionState,
} from './types';

export interface ConnectedClient {
    id: string;
    displayName: string;
    hardwareId?: string;
    ws: WebSocket;
    registeredAt: Date;
    ipAddress: string;
    // Şifre yanlış girme kilidi
    lockedUntil?: Date;
    failedPasswordAttempts: number;
}

const SESSION_TIMEOUT_MS = parseInt(process.env.SESSION_TIMEOUT_MS ?? '30000', 10);
const MAX_PASSWORD_ATTEMPTS = parseInt(process.env.MAX_PASSWORD_ATTEMPTS ?? '5', 10);
const BRUTE_FORCE_LOCK_MS = parseInt(process.env.BRUTE_FORCE_LOCK_MS ?? '30000', 10);

export class Hub {
    private clients: Map<string, ConnectedClient> = new Map();
    private hardwareIndex: Map<string, string> = new Map(); // hardwareId → machineId
    private sessions: Map<string, PendingSession> = new Map();
    // Presence: hangi istemci hangi ID'leri takip ediyor
    // key = subscriber clientId, value = takip ettiği ID'ler
    private presenceSubscriptions: Map<string, Set<string>> = new Map();

    // ----------------------------------------------------------------
    // İstemci Yönetimi
    // ----------------------------------------------------------------

    register(id: string, displayName: string, ws: WebSocket, ip: string, hardwareId?: string): boolean {
        if (this.clients.has(id)) {
            // ID zaten kayıtlıysa reddet
            return false;
        }
        const normalizedHw = hardwareId ? this.normalizeHardwareId(hardwareId) : undefined;
        this.clients.set(id, {
            id,
            displayName,
            hardwareId: normalizedHw,
            ws,
            registeredAt: new Date(),
            ipAddress: ip,
            failedPasswordAttempts: 0,
        });
        if (normalizedHw) {
            this.hardwareIndex.set(normalizedHw, id);
        }
        console.log(`[Hub] ✅ Kayıt: ${id} (${displayName}) @ ${ip}${normalizedHw ? ` [hw:${normalizedHw.slice(0, 8)}…]` : ''}`);

        // Presence: bu ID'yi takip eden herkese "online" bildir
        this.notifyPresenceChange(id, true, displayName);

        return true;
    }

    unregister(id: string): void {
        const client = this.clients.get(id);
        if (client?.hardwareId) {
            this.hardwareIndex.delete(client.hardwareId);
        }

        // Bu ID'ye ait tüm aktif oturumları kapat
        for (const [sessionId, session] of this.sessions) {
            if (session.requesterId === id || session.targetId === id) {
                this.closeSession(sessionId, 'rejected');
            }
        }

        // Presence: bu ID'yi takip eden herkese "offline" bildir
        this.notifyPresenceChange(id, false);

        // Bu istemcinin aboneliklerini temizle
        this.presenceSubscriptions.delete(id);

        this.clients.delete(id);
        console.log(`[Hub] ❌ Bağlantı kesildi: ${id}`);
    }

    getClient(id: string): ConnectedClient | undefined {
        return this.clients.get(id);
    }

    isOnline(id: string): boolean {
        return this.clients.has(id);
    }

    /** MachineGuid ile çevrimiçi 9 haneli relay ID'sini bulur */
    resolveTargetId(targetId: string): string | null {
        if (/^\d{9}$/.test(targetId)) {
            return this.clients.has(targetId) ? targetId : null;
        }

        const raw = targetId.startsWith('hw:') ? targetId.slice(3) : targetId;
        const normalized = this.normalizeHardwareId(raw);
        if (normalized.length !== 32) return null;

        const machineId = this.hardwareIndex.get(normalized);
        return machineId && this.clients.has(machineId) ? machineId : null;
    }

    private normalizeHardwareId(value: string): string {
        return value.replace(/[^a-fA-F0-9]/g, '').toUpperCase();
    }

    // ----------------------------------------------------------------
    // Oturum Yönetimi
    // ----------------------------------------------------------------

    createSession(requesterId: string, targetId: string, requiresPassword: boolean): PendingSession {
        const sessionId = uuidv4();
        const session: PendingSession = {
            sessionId,
            requesterId,
            targetId,
            createdAt: new Date(),
            state: requiresPassword ? 'awaiting_password' : 'awaiting_acceptance',
            passwordAttempts: 0,
            requiresPassword,
        };

        // Zaman aşımı — 30 saniyede yanıt gelmezse iptal et
        session.timeoutHandle = setTimeout(() => {
            this.closeSession(sessionId, 'timeout');
        }, SESSION_TIMEOUT_MS);

        this.sessions.set(sessionId, session);
        console.log(`[Hub] 🔗 Yeni oturum: ${sessionId} | ${requesterId} → ${targetId}`);
        return session;
    }

    getSession(sessionId: string): PendingSession | undefined {
        return this.sessions.get(sessionId);
    }

    setSessionState(sessionId: string, state: SessionState): void {
        const session = this.sessions.get(sessionId);
        if (session) {
            session.state = state;
            if (state === 'active' && session.timeoutHandle) {
                clearTimeout(session.timeoutHandle);
                session.timeoutHandle = undefined;
            }
        }
    }

    // Şifre denemesini işle — true: doğru, false: yanlış, null: kilitli
    handlePasswordAttempt(sessionId: string, passwordHash: string, storedHash: string): boolean | null {
        const session = this.sessions.get(sessionId);
        if (!session) return false;

        const target = this.clients.get(session.targetId);
        if (!target) return false;

        // Brute-force kilidi kontrolü
        if (target.lockedUntil && target.lockedUntil > new Date()) {
            return null; // Kilitli
        }

        const isCorrect = passwordHash === storedHash;

        if (!isCorrect) {
            session.passwordAttempts++;
            target.failedPasswordAttempts++;

            if (session.passwordAttempts >= MAX_PASSWORD_ATTEMPTS) {
                // Kilitle
                target.lockedUntil = new Date(Date.now() + BRUTE_FORCE_LOCK_MS);
                target.failedPasswordAttempts = 0;
                console.log(`[Hub] 🔒 Brute-force kilidi: ${session.targetId} (${BRUTE_FORCE_LOCK_MS / 1000}s)`);
                this.closeSession(sessionId, 'locked');
                return null;
            }
        } else {
            target.failedPasswordAttempts = 0;
            target.lockedUntil = undefined;
        }

        return isCorrect;
    }

    closeSession(sessionId: string, reason: 'rejected' | 'timeout' | 'locked'): void {
        const session = this.sessions.get(sessionId);
        if (!session) return;

        if (session.timeoutHandle) {
            clearTimeout(session.timeoutHandle);
        }

        // Taraflara bildir
        this.sendToClient(session.requesterId, {
            type: 'connect_rejected',
            session_id: sessionId,
            reason,
        });

        session.state = 'closed';
        this.sessions.delete(sessionId);
        console.log(`[Hub] 🚫 Oturum kapatıldı: ${sessionId} (${reason})`);
    }

    // ----------------------------------------------------------------
    // Mesaj Gönderme
    // ----------------------------------------------------------------

    sendToClient(clientId: string, message: ServerToClientMessage): boolean {
        const client = this.clients.get(clientId);
        if (!client || client.ws.readyState !== WebSocket.OPEN) return false;
        client.ws.send(JSON.stringify(message));
        return true;
    }

    sendBinaryToClient(clientId: string, payload: Buffer): boolean {
        const client = this.clients.get(clientId);
        if (!client || client.ws.readyState !== WebSocket.OPEN) return false;
        client.ws.send(payload);
        return true;
    }

    // ----------------------------------------------------------------
    // İstatistik
    // ----------------------------------------------------------------

    getStats() {
        return {
            connectedClients: this.clients.size,
            activeSessions: this.sessions.size,
        };
    }

    // ----------------------------------------------------------------
    // Presence (ÇevrimDurum Takibi)
    // ----------------------------------------------------------------

    /** Bir istemci, belirli ID'lerin durumunu takip etmek istiyor */
    subscribePresence(subscriberId: string, targetIds: string[]): void {
        let subs = this.presenceSubscriptions.get(subscriberId);
        if (!subs) {
            subs = new Set();
            this.presenceSubscriptions.set(subscriberId, subs);
        }
        for (const id of targetIds) {
            subs.add(id);
        }
    }

    /** Belirli ID'lerin mevcut durumunu döndür */
    queryPresence(ids: string[]): Array<{ id: string; online: boolean; display_name?: string }> {
        return ids.map(id => {
            const client = this.clients.get(id);
            return {
                id,
                online: !!client,
                display_name: client?.displayName,
            };
        });
    }

    /** Bir ID'nin durumu değiştiğinde, onu takip eden tüm istemcilere bildir */
    private notifyPresenceChange(changedId: string, online: boolean, displayName?: string): void {
        for (const [subscriberId, watchedIds] of this.presenceSubscriptions) {
            if (watchedIds.has(changedId)) {
                this.sendToClient(subscriberId, {
                    type: 'presence_update',
                    id: changedId,
                    online,
                    display_name: displayName,
                });
            }
        }
    }
}
