(() => {
    const nativeFetch = window.fetch.bind(window);
    const NativeWebSocket = window.WebSocket;

    const state = {
        startedAt: new Date().toISOString(),
        interactive: false,
        online: navigator.onLine,
        frameworkStatus: null,
        healthStatus: null,
        negotiateStatus: null,
        negotiateContentType: null,
        negotiateBodyLength: null,
        negotiateBodyPreview: null,
        error: null,
        timeline: []
    };

    let timer;

    const qs = id => document.getElementById(id);

    function sanitize(value) {
        return String(value ?? '')
            .replace(/([?&]id=)[^&\s'"]+/gi, '$1<redacted>')
            .replace(/([?&](?:access_token|token)=)[^&\s'"]+/gi, '$1<redacted>')
            .replace(/(Bearer\s+)[A-Za-z0-9._~+\/-]+/gi, '$1<redacted>');
    }

    function safeUrl(value) {
        try {
            const url = new URL(value, document.baseURI);
            return sanitize(url.pathname + url.search);
        } catch {
            return sanitize(value);
        }
    }

    function isBlazorUrl(value) {
        try {
            return new URL(value, document.baseURI).pathname.includes('/_blazor');
        } catch {
            return String(value).includes('_blazor');
        }
    }

    function addTimeline(kind, message) {
        const elapsed = Math.round(performance.now());
        state.timeline.push(`${elapsed} ms [${kind}] ${sanitize(message)}`);
        if (state.timeline.length > 60)
            state.timeline.splice(0, state.timeline.length - 60);
    }

    function detailsText() {
        const timeline = state.timeline.length
            ? '\n\nBlazor/SignalR Ablauf:\n' + state.timeline.join('\n')
            : '';

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
            state.negotiateBodyPreview ? 'Negotiate Body: ' + sanitize(state.negotiateBodyPreview) : '',
            'Browser: ' + navigator.userAgent,
            state.error ? 'Fehler: ' + sanitize(state.error) : ''
        ].filter(Boolean).join('\n') + timeline;
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

    async function captureBlazorResponse(response, method, url, started) {
        if (!isBlazorUrl(url))
            return;

        let body = '';
        try {
            body = await response.clone().text();
        } catch (e) {
            body = '<Body nicht lesbar: ' + e + '>';
        }

        const contentType = response.headers.get('content-type') || '(leer)';
        const elapsed = Math.round(performance.now() - started);
        const preview = body.length > 0 ? sanitize(body.slice(0, 350)) : '(leer)';

        addTimeline(
            'FETCH',
            `${method} ${safeUrl(url)} -> ${response.status}; ${contentType}; ${body.length} Zeichen; ${elapsed} ms; Body=${preview}`
        );
    }

    window.fetch = async function(input, init) {
        const request = input instanceof Request ? input : null;
        const url = request?.url || String(input);
        const method = String(init?.method || request?.method || 'GET').toUpperCase();
        const started = performance.now();

        try {
            const response = await nativeFetch(input, init);
            await captureBlazorResponse(response, method, url, started);
            return response;
        } catch (e) {
            if (isBlazorUrl(url)) {
                addTimeline(
                    'FETCH-ERROR',
                    `${method} ${safeUrl(url)} -> ${e?.stack || e}`
                );
            }
            throw e;
        }
    };

    if (NativeWebSocket) {
        window.WebSocket = new Proxy(NativeWebSocket, {
            construct(Target, args) {
                const url = safeUrl(args[0]);
                addTimeline('WS', 'create ' + url);
                const socket = Reflect.construct(Target, args);

                socket.addEventListener('open', () =>
                    addTimeline('WS', 'open ' + url));

                socket.addEventListener('error', () =>
                    addTimeline('WS', 'error ' + url));

                socket.addEventListener('close', event =>
                    addTimeline(
                        'WS',
                        `close ${url}; code=${event.code}; clean=${event.wasClean}; reason=${event.reason || '(leer)'}`
                    ));

                return socket;
            }
        });
    }

    async function probeHttp() {
        try {
            const framework = await nativeFetch('/_framework/blazor.web.js', {
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
            const health = await nativeFetch('/health/live', {
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
            const negotiate = await nativeFetch('/_blazor/negotiate?negotiateVersion=1', {
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
            addTimeline('CIRCUIT', 'Interactive Server aktiv');
            hide();
        },
        markStart() {
            addTimeline('BLAZOR', 'Blazor.start() wird aufgerufen');
        },
        markStarted() {
            addTimeline('BLAZOR', 'Blazor.start() Promise erfolgreich');
        },
        logSignalR(level, message) {
            addTimeline('SIGNALR-' + level, message);
        },
        showFailure(title, message, error) {
            state.error = error?.stack || error?.message || (error ? String(error) : state.error);
            addTimeline('FAIL', state.error || message);
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
            addTimeline('WINDOW-ERROR', state.error);
        }
    }, true);

    window.addEventListener('unhandledrejection', event => {
        const reason = event.reason;
        state.error = reason?.stack || reason?.message || String(reason ?? 'Unhandled promise rejection');
        addTimeline('PROMISE-ERROR', state.error);
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
