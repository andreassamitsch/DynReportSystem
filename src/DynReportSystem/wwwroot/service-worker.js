// PWA installability only. Do not cache report pages, credentials or SQL data.
self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', event => event.waitUntil(self.clients.claim()));
