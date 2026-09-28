(() => {
    const state = {
        startedAt: new Date().toISOString(),
        interactive: false,
        scriptLoaded: typeof window.Blazor !== 'undefined',
        online: navigator.onLine,
        frameworkStatus: null,
        healthStatus: null,
        negotiateStatus: null,
        negotiateContentType: null,
        negotiateBodyLength: null,
        negotiateBodyPreview: null,
        error: null
    };

    let timer;

    const qs = id => document.getElementById(id);

    function detailsText() {
        return [
            'Zeit: ' + new Date().toISOString(),
            'Pfad: ' + location.pathname,
            'Online: ' + navigator.onLine,
            'Blazor global: ' + (typeof window.Blazor !== 'undefined'),
            'Framework-Asset HTTP: ' + (state.frameworkStatus ?? 'nicht geprüft'),
            'Health HTTP: ' + (state.healthStatus ?? 'nicht geprüft'),
            'Blazor negotiate HTTP: ' + (state.negotiateStatus ?? 'nicht geprüft'),
            'Negotiate Content-Type: ' + (state.negotiateContentType ?? 'nicht geprüft'),
            'Negotiate Body-Länge: ' + (state.negotiateBodyLength ?? 'nicht geprüft'),
            state.negotiateBodyPreview ? 'Negotiate Body: ' + state.negotiateBodyPreview : '',
            'Browser: ' + navigator.userAgent,
            state.error ? 'Fehler: ' + state.error : ''
        ].filter(Boolean).join('\n');
    }

    function show(title, message) {
        const panel = qs('dynreport-client-error');
        if (!panel) return;

        const titleNode = qs('dynreport-client-error-title');
        const messageNode = qs('dynreport-client-error-message');
        const detailsNode = qs('dynreport-client-error-details');

        if (titleNode) titleNode.textContent = title;
        if (messageNode) messageNode.textContent = message;
        if (detailsNode) detailsNode.textContent = detailsText();

        panel.hidden = false;
    }

    function hide() {
        const panel = qs('dynreport-client-error');
        if (panel) panel.hidden = true;
    }

    async function probeHttp() {
        try {
            const framework = await fetch('/_framework/blazor.web.js', {
                method: 'HEAD',
                credentials: 'same-origin',
                cache: 'no-store'
            });
            state.frameworkStatus = framework.status;
        } catch (e) {
            state.frameworkStatus = 'Fehler';
            state.error = String(e);
        }

        try {
            const health = await fetch('/health/live', {
                method: 'GET',
                credentials: 'same-origin',
                cache: 'no-store'
            });
            state.healthStatus = health.status;
        } catch (e) {
            state.healthStatus = 'Fehler';
            state.error = String(e);
        }

        try {
            const negotiate = await fetch('/_blazor/negotiate?negotiateVersion=1', {
                method: 'POST',
                credentials: 'same-origin',
                cache: 'no-store'
            });
            state.negotiateStatus = negotiate.status;
            state.negotiateContentType = negotiate.headers.get('content-type') || '(leer)';
            const negotiateBody = await negotiate.text();
            state.negotiateBodyLength = negotiateBody.length;
            state.negotiateBodyPreview = negotiateBody.length > 0
                ? negotiateBody.slice(0, 500)
                : '(leer)';
        } catch (e) {
            state.negotiateStatus = 'Fehler';
            state.error = String(e);
        }
    }

    async function startupTimeout() {
        if (state.interactive) return;

        await probeHttp();
        if (state.interactive) return;

        show(
            'Interaktive Verbindung zu DynReport wurde nicht hergestellt.',
            'Die Seite ist sichtbar, aber Buttons, Register und Speichern sind ohne Blazor-Serververbindung nicht aktiv.'
        );
    }

    window.DynReportDiagnostics = {
        markInteractive() {
            state.interactive = true;
            clearTimeout(timer);
            hide();
        },
        showFailure(title, message, error) {
            state.error = error ? String(error) : state.error;
            show(title, message);
        }
    };

    window.addEventListener('error', event => {
        if (event.target instanceof HTMLScriptElement &&
            event.target.src.includes('/_framework/blazor.web.js')) {
            state.error = 'blazor.web.js konnte nicht geladen werden';
            show(
                'Blazor-Client konnte nicht geladen werden.',
                'Das Framework-Skript /_framework/blazor.web.js ist nicht erreichbar.'
            );
            return;
        }

        if (event.error) {
            state.error = event.error.stack || event.error.message || String(event.error);
        }
    }, true);

    window.addEventListener('unhandledrejection', event => {
        const reason = event.reason;
        state.error = reason?.stack || reason?.message || String(reason ?? 'Unhandled promise rejection');
    });

    window.addEventListener('offline', () => {
        state.online = false;
        show('Netzwerkverbindung unterbrochen.', 'Der Browser ist derzeit offline.');
    });

    document.addEventListener('DOMContentLoaded', () => {
        const retry = qs('dynreport-client-retry');
        if (retry) retry.addEventListener('click', () => location.reload());

        const copy = qs('dynreport-client-copy');
        if (copy) {
            copy.addEventListener('click', async () => {
                const text = detailsText();
                try {
                    await navigator.clipboard.writeText(text);
                    copy.textContent = 'Kopiert';
                } catch {
                    copy.textContent = 'Kopieren nicht möglich';
                }
            });
        }

        timer = window.setTimeout(startupTimeout, 7000);
    });
})();
