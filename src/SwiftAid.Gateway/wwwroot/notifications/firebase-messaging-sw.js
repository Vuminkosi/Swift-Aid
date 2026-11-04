importScripts('https://www.gstatic.com/firebasejs/13.1.0/firebase-app-compat.js');
importScripts('https://www.gstatic.com/firebasejs/13.1.0/firebase-messaging-compat.js');

const params = new URL(self.location.href).searchParams;
let messagingReady = false;
try {
    const configuration = JSON.parse(params.get('configuration') || '{}');
    if (configuration.enabled && configuration.apiKey && configuration.projectId && configuration.messagingSenderId && configuration.appId) {
        firebase.initializeApp(configuration);
        const messaging = firebase.messaging();
        messaging.onBackgroundMessage(() => showGenericNotification());
        messagingReady = true;
    }
} catch {
    // Push remains disabled when public Firebase configuration is incomplete or invalid.
}

function showGenericNotification() {
    return self.registration.showNotification('SwiftAid', {
        body: 'You have an update. Open SwiftAid to review it.',
        icon: '/favicon.png',
        tag: 'swiftaid-generic-update',
        renotify: false,
        data: { url: '/notifications' }
    });
}

self.addEventListener('push', event => {
    if (!messagingReady && event.data) event.waitUntil(showGenericNotification());
});

self.addEventListener('notificationclick', event => {
    event.notification.close();
    event.waitUntil((async () => {
        const target = new URL('/', self.location.origin).href;
        const clients = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
        const existing = clients.find(client => client.url.startsWith(self.location.origin));
        if (existing) {
            await existing.focus();
            if ('navigate' in existing) await existing.navigate(target);
            return;
        }
        await self.clients.openWindow(target);
    })());
});
