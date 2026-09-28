// DynReport browser bootstrap.
// Blazor is started explicitly so startup/SignalR failures can be surfaced in the UI.
(async () => {
    if ('serviceWorker' in navigator) {
        navigator.serviceWorker.register('/service-worker.js').catch(() => {});
    }

    const diagnostics = window.DynReportDiagnostics;

    try {
        diagnostics?.markStart();

        await Blazor.start({
            circuit: {
                configureSignalR: builder => {
                    builder.configureLogging({
                        log: (level, message) => diagnostics?.logSignalR(level, message)
                    });
                }
            }
        });

        diagnostics?.markStarted();
    } catch (error) {
        diagnostics?.showFailure(
            'Blazor konnte nicht gestartet werden.',
            'Der Interactive-Server-Circuit konnte nicht aufgebaut werden.',
            error
        );
    }
})();
