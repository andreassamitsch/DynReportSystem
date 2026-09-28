// DynReport browser bootstrap. Blazor itself uses the framework's standard autostart.
// Keep PWA registration separate so a failure here can never prevent Blazor interactivity.
if ('serviceWorker' in navigator) {
    navigator.serviceWorker.register('/service-worker.js').catch(() => {});
}
