// ============================================================
// FluxConnect Relay — Mesaj Protokolü Tip Tanımları
// ============================================================

// ---- İstemciden Sunucuya ----

export interface RegisterMessage {
    type: 'register';
    id: string;           // 9 haneli makine ID
    display_name: string; // Görünen ad
    hardware_id?: string; // Windows MachineGuid (32 hex)
    has_session_password?: boolean;
}

export interface ConnectPendingMessage {
    type: 'connect_pending';
    session_id: string;
    target_has_password: boolean;
}

export interface PasswordVerifyResultMessage {
    type: 'password_verify_result';
    session_id: string;
    success: boolean;
}

export interface ConnectRequestMessage {
    type: 'connect_request';
    target_id: string;
}

export interface PasswordAttemptMessage {
    type: 'password_attempt';
    session_id: string;
    password_hash: string; // SHA-256 hash (ham şifre asla gönderilmez)
}

export interface ConnectResponseMessage {
    type: 'connect_response';
    session_id: string;
    accepted: boolean;
}

export interface RelayDataMessage {
    type: 'relay';
    session_id: string;
    target_id: string;
    data: string; // Base64 kodlu, E2EE şifrelenmiş
}

export interface PingMessage {
    type: 'ping';
}

export interface PresenceSubscribeMessage {
    type: 'presence_subscribe';
    ids: string[];  // Takip edilecek makine ID'leri
}

export interface PresenceQueryMessage {
    type: 'presence_query';
    ids: string[];  // Durumu sorgulanacak ID'ler
}

export type ClientToServerMessage =
    | RegisterMessage
    | ConnectRequestMessage
    | PasswordAttemptMessage
    | PasswordVerifyResultMessage
    | ConnectResponseMessage
    | RelayDataMessage
    | PingMessage
    | PresenceSubscribeMessage
    | PresenceQueryMessage;

// ---- Sunucudan İstemciye ----

export interface RegisteredMessage {
    type: 'registered';
    id: string;
}

export interface IncomingRequestMessage {
    type: 'incoming_request';
    from_id: string;
    from_display_name: string;
    session_id: string;
    requires_password: boolean;
}

export interface PasswordRequiredMessage {
    type: 'password_required';
    session_id: string;
}

export interface PasswordResultMessage {
    type: 'password_result';
    session_id: string;
    success: boolean;
    attempts_remaining?: number;
}

export interface ConnectAcceptedMessage {
    type: 'connect_accepted';
    session_id: string;
    peer_id: string;
    peer_display_name: string;
    peer_hardware_id?: string;
}

export interface ConnectRejectedMessage {
    type: 'connect_rejected';
    session_id: string;
    reason: 'rejected' | 'wrong_password' | 'timeout' | 'locked';
}

export interface RelayDataServerMessage {
    type: 'relay';
    session_id: string;
    from_id: string;
    data: string;
}

export interface ErrorMessage {
    type: 'error';
    code: string;
    message: string;
}

export interface PongMessage {
    type: 'pong';
}

export interface PresenceUpdateMessage {
    type: 'presence_update';
    id: string;
    online: boolean;
    display_name?: string;
}

export interface PresenceListMessage {
    type: 'presence_list';
    statuses: Array<{ id: string; online: boolean; display_name?: string }>;
}

export type ServerToClientMessage =
    | RegisteredMessage
    | IncomingRequestMessage
    | ConnectPendingMessage
    | PasswordRequiredMessage
    | PasswordResultMessage
    | ConnectAcceptedMessage
    | ConnectRejectedMessage
    | RelayDataServerMessage
    | ErrorMessage
    | PongMessage
    | PresenceUpdateMessage
    | PresenceListMessage;

// ---- İç Yapılar ----

export type SessionState =
    | 'awaiting_password'
    | 'awaiting_acceptance'
    | 'active'
    | 'closed';

export interface PendingSession {
    sessionId: string;
    requesterId: string;
    targetId: string;
    createdAt: Date;
    state: SessionState;
    passwordAttempts: number;
    requiresPassword: boolean;
    timeoutHandle?: ReturnType<typeof setTimeout>;
}
