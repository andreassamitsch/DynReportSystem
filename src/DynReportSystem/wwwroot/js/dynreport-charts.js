(() => {
    const charts = new Map();
    let worldMapPromise = null;

    const currency = new Intl.NumberFormat("de-AT", {
        style: "currency",
        currency: "EUR",
        maximumFractionDigits: 0
    });

    const number = new Intl.NumberFormat("de-AT", {
        maximumFractionDigits: 1
    });

    const compact = new Intl.NumberFormat("de-AT", {
        notation: "compact",
        maximumFractionDigits: 1
    });

    function formatValue(value, format) {
        const numeric = Number(value ?? 0);
        if (!Number.isFinite(numeric)) return String(value ?? "");
        if (format === "currency") return currency.format(numeric);
        if (format === "percent") return number.format(numeric) + " %";
        if (format === "integer") return Math.round(numeric).toLocaleString("de-AT");
        return number.format(numeric);
    }

    async function ensureWorldMap() {
        if (window.echarts?.getMap("dyn-world")) return;
        if (!worldMapPromise) {
            worldMapPromise = fetch("/vendor/world-map.svg", { credentials: "same-origin" })
                .then(r => {
                    if (!r.ok) throw new Error("World map could not be loaded.");
                    return r.text();
                })
                .then(svg => window.echarts.registerMap("dyn-world", { svg }));
        }
        return worldMapPromise;
    }

    function decorate(option) {
        const seriesFormats = option.__dynSeriesFormats || {};
        const itemFormat = option.__dynItemFormat || null;

        if (option.__dynMap === "world") delete option.__dynMap;
        delete option.__dynSeriesFormats;
        delete option.__dynItemFormat;

        const axes = [];
        if (Array.isArray(option.yAxis)) axes.push(...option.yAxis);
        else if (option.yAxis) axes.push(option.yAxis);
        if (Array.isArray(option.xAxis)) axes.push(...option.xAxis);
        else if (option.xAxis) axes.push(option.xAxis);

        for (const axis of axes) {
            if (!axis || !axis.__dynFormat) continue;
            const f = axis.__dynFormat;
            axis.axisLabel ??= {};
            axis.axisLabel.formatter = v => f === "currency" ? compact.format(v) + " €" : formatValue(v, f);
            delete axis.__dynFormat;
        }

        option.tooltip ??= {};

        if (option.tooltip.trigger === "axis") {
            option.tooltip.formatter = params => {
                const rows = Array.isArray(params) ? params : [params];
                if (!rows.length) return "";
                const header = rows[0].axisValueLabel ?? rows[0].name ?? "";
                const body = rows.map(p => {
                    const format = seriesFormats[p.seriesName] || itemFormat;
                    const raw = Array.isArray(p.value) ? p.value[p.value.length - 1] : p.value;
                    return p.marker + p.seriesName + ": <b>" + formatValue(raw, format) + "</b>";
                }).join("<br/>");
                return "<b>" + header + "</b><br/>" + body;
            };
        } else if (itemFormat || Object.keys(seriesFormats).length) {
            option.tooltip.formatter = p => {
                const format = seriesFormats[p.seriesName] || itemFormat;
                const raw = Array.isArray(p.value) ? p.value[p.value.length - 1] : p.value;
                const pct = typeof p.percent === "number" ? " · " + number.format(p.percent) + " %" : "";
                return p.marker + "<b>" + (p.name || "") + "</b><br/>" + formatValue(raw, format) + pct;
            };
        }

        return option;
    }

    async function render(id, rawOption, dotnetRef) {
        if (!window.echarts) return;

        const option = structuredClone(rawOption || {});
        const needsWorld = option.__dynMap === "world";
        if (needsWorld) await ensureWorldMap();

        decorate(option);

        const el = document.getElementById(id);
        if (!el) return;

        let chart = charts.get(id);
        if (!chart || chart.isDisposed?.()) {
            chart = window.echarts.init(el, null, { renderer: "canvas" });
            charts.set(id, chart);
        }

        chart.off("click");
        chart.setOption(option, { notMerge: true, lazyUpdate: false });

        if (dotnetRef) {
            chart.on("click", p => {
                dotnetRef.invokeMethodAsync("HandleChartClick", {
                    name: p.name ?? null,
                    seriesName: p.seriesName ?? null,
                    data: p.data ?? null
                }).catch(() => {});
            });
        }

        requestAnimationFrame(() => chart.resize());
    }

    function dispose(id) {
        const chart = charts.get(id);
        if (chart) {
            chart.dispose();
            charts.delete(id);
        }
    }

    function downloadCsv(fileName, columns, rows) {
        const esc = value => {
            const s = value == null ? "" : String(value);
            return '"' + s.replaceAll('"', '""') + '"';
        };

        const lines = [columns.map(esc).join(";")];
        for (const row of rows) {
            lines.push(columns.map(c => esc(row[c])).join(";"));
        }

        const blob = new Blob(["\uFEFF" + lines.join("\r\n")], {
            type: "text/csv;charset=utf-8"
        });

        const url = URL.createObjectURL(blob);
        const a = document.createElement("a");
        a.href = url;
        a.download = fileName || "DynReport.csv";
        document.body.appendChild(a);
        a.click();
        a.remove();
        URL.revokeObjectURL(url);
    }

    window.addEventListener("resize", () => {
        for (const chart of charts.values()) chart.resize();
    });

    window.DynReportCharts = { render, dispose, downloadCsv };
})();
