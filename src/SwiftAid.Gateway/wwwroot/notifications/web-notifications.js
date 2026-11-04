import { getApps, initializeApp } from 'https://www.gstatic.com/firebasejs/13.1.0/firebase-app.js';
import { getMessaging, getToken, isSupported as isMessagingSupported, onMessage, deleteToken } from 'https://www.gstatic.com/firebasejs/13.1.0/firebase-messaging.js';

const SOUND_KEY = 'swiftaid.notifications.sound.v1';
const PUSH_KEY = 'swiftaid.notifications.push.v1';
const installationKey = 'swiftaid.notifications.installation.v1';
const playedKey = 'swiftaid.notifications.played.v1';
let app;
let messaging;
let configuration;
let workerRegistration;
let pendingClickRegistration;
let stopMessageListener;
const permissionHandlers = new WeakMap();

export async function initialize(publicConfig) {
    configuration = publicConfig;
    if (!configuration?.enabled || !configuration?.vapidKey || !configuration?.apiKey ||
        !configuration?.authDomain || !configuration?.projectId ||
        !configuration?.messagingSenderId || !configuration?.appId) return false;
    const options = {
        apiKey: configuration.apiKey,
        authDomain: configuration.authDomain,
        projectId: configuration.projectId,
        storageBucket: configuration.storageBucket,
        messagingSenderId: configuration.messagingSenderId,
        appId: configuration.appId,
        measurementId: configuration.measurementId
    };
    app = getApps().find(candidate => candidate.name === 'swiftaid-notifications') ||
        initializeApp(options, 'swiftaid-notifications');
    if (!await isMessagingSupported()) return false;

    const workerUrl = new URL('/notifications/firebase-messaging-sw.js', window.location.origin);
    workerUrl.searchParams.set('configuration', JSON.stringify(configuration));
    workerRegistration = await navigator.serviceWorker.register(workerUrl, { scope: '/notifications/' });
    messaging = getMessaging(app);
    return true;
}

export async function isSupported() {
    return Boolean(app && await isMessagingSupported() && 'serviceWorker' in navigator && 'Notification' in window);
}

export async function getState() {
    try {
        const permission = !await isSupported()
            ? 'NotSupported'
            : ({ granted: 'Granted', denied: 'Denied', default: 'Unknown' }[Notification.permission] || 'Unknown');
        return {
            permission,
            pushEnabled: localStorage.getItem(PUSH_KEY) === '1',
            gentleSoundEnabled: localStorage.getItem(SOUND_KEY) === '1'
        };
    } catch {
        return { permission: 'NotSupported', pushEnabled: false, gentleSoundEnabled: false };
    }
}

export async function refreshRegistration(tokenBridge) {
    try {
        if (!await isSupported() || Notification.permission !== 'granted' ||
            localStorage.getItem(PUSH_KEY) !== '1' || !tokenBridge ||
            typeof tokenBridge.invokeMethodAsync !== 'function') return false;
        const registration = workerRegistration || await navigator.serviceWorker.getRegistration('/notifications/');
        if (!registration) return false;
        messaging ||= getMessaging(app);
        const token = await getToken(messaging, { vapidKey: configuration.vapidKey, serviceWorkerRegistration: registration });
        const installationId = getInstallationId();
        return Boolean(token && installationId &&
            await tokenBridge.invokeMethodAsync('RegisterTokenAsync', installationId, token, localStorage.getItem(SOUND_KEY) === '1') === true);
    } catch {
        return false;
    }
}

export function getInstallationId() {
    try {
        let id = localStorage.getItem(installationKey);
        if (!id) {
            id = crypto.randomUUID();
            localStorage.setItem(installationKey, id);
        }
        return id;
    } catch {
        return null;
    }
}

/// <summary>Bind this to the real Enable button so both permission and FCM token APIs start within the user click.</summary>
export function bindPermissionRequest(element) {
    if (!element || permissionHandlers.has(element)) return false;
    const handler = () => {
        if (!app || !workerRegistration || !messaging || !window.isSecureContext ||
            !('serviceWorker' in navigator) || !('Notification' in window)) {
            pendingClickRegistration = Promise.resolve({ permission: 'unsupported', token: null });
            return;
        }

        const permission = Notification.requestPermission().catch(() => 'denied');
        const token = getToken(messaging, {
            vapidKey: configuration.vapidKey,
            serviceWorkerRegistration: workerRegistration
        }).catch(() => null);
        pendingClickRegistration = Promise.all([permission, token]).then(([permissionState, tokenValue]) => ({
            permission: permissionState,
            token: permissionState === 'granted' ? tokenValue : null
        }));
    };
    element.addEventListener('click', handler, true);
    permissionHandlers.set(element, handler);
    return true;
}

export function unbindPermissionRequest(element) {
    const handler = element && permissionHandlers.get(element);
    if (!handler) return false;
    element.removeEventListener('click', handler, true);
    permissionHandlers.delete(element);
    return true;
}

export async function enable(installationId, soundOptIn, tokenBridge) {
    try {
        if (!tokenBridge || typeof tokenBridge.invokeMethodAsync !== 'function' || !pendingClickRegistration) return false;
        const registration = pendingClickRegistration;
        pendingClickRegistration = undefined;
        const clickResult = await registration;
        if (clickResult.permission !== 'granted' || !clickResult.token || !await isSupported()) return false;

        const stableId = installationId || getInstallationId();
        if (!stableId) return false;
        const registered = await tokenBridge.invokeMethodAsync(
            'RegisterTokenAsync', stableId, clickResult.token, Boolean(soundOptIn));
        if (registered !== true) return false;
        localStorage.setItem(installationKey, stableId);
        localStorage.setItem(PUSH_KEY, '1');
        localStorage.setItem(SOUND_KEY, soundOptIn ? '1' : '0');
        stopMessageListener?.();
        stopMessageListener = onMessage(messaging, () => dispatchEvent(new Event('swiftaid-notification-changed')));
        return true;
    } catch {
        return false;
    }
}

export async function disable() {
    try {
        const registration = workerRegistration || await navigator.serviceWorker?.getRegistration('/notifications/');
        if (app) {
            messaging ||= getMessaging(app);
            await deleteToken(messaging);
        }
        await registration?.unregister();
        workerRegistration = undefined;
        stopMessageListener?.();
        stopMessageListener = undefined;
        localStorage.setItem(PUSH_KEY, '0');
        localStorage.setItem(SOUND_KEY, '0');
        pendingClickRegistration = undefined;
        return true;
    } catch {
        return false;
    }
}

export async function setGentleSound(enabled, tokenBridge) {
    try {
        if (localStorage.getItem(PUSH_KEY) !== '1' || !tokenBridge ||
            typeof tokenBridge.invokeMethodAsync !== 'function') return false;
        messaging ||= getMessaging(app);
        const registration = workerRegistration || await navigator.serviceWorker.getRegistration('/notifications/');
        if (!registration) return false;
        const token = await getToken(messaging, { vapidKey: configuration.vapidKey, serviceWorkerRegistration: registration });
        const installationId = getInstallationId();
        if (!token || !installationId || await tokenBridge.invokeMethodAsync(
            'RegisterTokenAsync', installationId, token, Boolean(enabled)) !== true) return false;
        localStorage.setItem(SOUND_KEY, enabled ? '1' : '0');
        return true;
    } catch {
        return false;
    }
}

export async function tryPlayForegroundSound(occurrenceId) {
    let played;
    try {
        if (!occurrenceId || localStorage.getItem(PUSH_KEY) !== '1' ||
            localStorage.getItem(SOUND_KEY) !== '1' || document.visibilityState !== 'visible') return false;
        played = JSON.parse(localStorage.getItem(playedKey) || '[]');
    } catch {
        return false;
    }
    if (!Array.isArray(played) || played.includes(occurrenceId)) return false;
    try {
        const audio = new Audio('/notifications/swiftaid_gentle_notification.wav');
        audio.volume = 0.35;
        await audio.play();
        played.push(occurrenceId);
        localStorage.setItem(playedKey, JSON.stringify(played.slice(-100)));
        return true;
    } catch {
        return false;
    }
}