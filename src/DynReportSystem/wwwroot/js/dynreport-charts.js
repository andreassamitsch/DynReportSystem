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

    function responsiveOptions(mode, seriesFormats, itemFormat, baseSeries) {
        if (!mode) return [];

        const hideToolbox = { show: false };
        const compactLegend = {
            type: "scroll",
            left: 0,
            right: 0,
            top: 0,
            itemWidth: 12,
            itemHeight: 8,
            textStyle: { fontSize: 10, color: "#61767e" }
        };

        if (mode === "horizontal-bar") {
            return [{
                query: { maxWidth: 520 },
                option: {
                    legend: { show: false },
                    toolbox: hideToolbox,
                    grid: { left: 4, right: 62, top: 8, bottom: 10, containLabel: true },
                    xAxis: { axisLabel: { show: false }, splitNumber: 3 },
                    yAxis: { axisLabel: { width: 128, overflow: "truncate", fontSize: 10 } },
                    series: (baseSeries || []).map(s => ({
                        barMaxWidth: 22,
                        label: {
                            show: true,
                            position: "right",
                            distance: 4,
                            fontSize: 9,
                            color: "#667b83",
                            formatter: p => formatValue(
                                Array.isArray(p.value) ? p.value[p.value.length - 1] : p.value,
                                seriesFormats[s.name] || itemFormat)
                        }
                    }))
                }
            }];
        }

        if (mode === "horizontal-stack") {
            return [{
                query: { maxWidth: 520 },
                option: {
                    legend: compactLegend,
                    toolbox: hideToolbox,
                    grid: { left: 4, right: 8, top: 48, bottom: 10, containLabel: true },
                    xAxis: { axisLabel: { show: false }, splitNumber: 3 },
                    yAxis: { axisLabel: { width: 138, overflow: "truncate", fontSize: 10 } },
                    series: (baseSeries || []).map(() => ({ barMaxWidth: 22 }))
                }
            }];
        }

        if (mode === "primary-stack") {
            return [{
                query: { maxWidth: 520 },
                option: {
                    legend: compactLegend,
                    toolbox: hideToolbox,
                    grid: { left: 4, right: 6, top: 56, bottom: 46, containLabel: true },
                    xAxis: { axisLabel: { rotate: 42, fontSize: 9, interval: "auto" } },
                    yAxis: { axisLabel: { fontSize: 9 }, splitNumber: 4 },
                    dataZoom: [{ type: "inside", start: 0, end: 100 }],
                    series: (baseSeries || []).map(() => ({ barMaxWidth: 24 }))
                }
            }];
        }

        if (mode === "grouped-stack") {
            return [{
                query: { maxWidth: 520 },
                option: {
                    legend: compactLegend,
                    toolbox: hideToolbox,
                    grid: { left: 4, right: 6, top: 56, bottom: 46, containLabel: true },
                    xAxis: { axisLabel: { rotate: 40, fontSize: 9, interval: "auto" } },
                    yAxis: { axisLabel: { fontSize: 9 }, splitNumber: 4 },
                    dataZoom: [{ type: "inside", start: 0, end: 100 }],
                    series: (baseSeries || []).map(s => ({
                        barMaxWidth: s.type === "bar" ? 24 : undefined,
                        symbolSize: s.type === "line" ? 4 : undefined
                    }))
                }
            }];
        }

        if (mode === "donut") {
            return [{
                query: { maxWidth: 520 },
                option: {
                    toolbox: hideToolbox,
                    legend: {
                        type: "scroll",
                        left: 8,
                        right: 8,
                        bottom: 0,
                        itemWidth: 12,
                        itemHeight: 8,
                        textStyle: { fontSize: 10, color: "#61767e" }
                    },
                    series: [{ radius: ["43%", "68%"], center: ["50%", "42%"] }]
                }
            }];
        }

        return [{
            query: { maxWidth: 520 },
            option: {
                legend: compactLegend,
                toolbox: hideToolbox,
                grid: { left: 4, right: 6, top: 52, bottom: 42, containLabel: true },
                xAxis: { axisLabel: { fontSize: 9, hideOverlap: true } },
                yAxis: { axisLabel: { fontSize: 9 } }
            }
        }];
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
        const responsiveMode = option.__dynResponsive || null;
        const stackTotalSeries = option.__dynStackTotalSeries || null;
        const stackTotalFormat = option.__dynStackTotalFormat || "number";

        if (option.__dynMap === "world") delete option.__dynMap;
        delete option.__dynSeriesFormats;
        delete option.__dynItemFormat;
        delete option.__dynResponsive;
        delete option.__dynStackTotalSeries;
        delete option.__dynStackTotalFormat;

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

        if (stackTotalSeries && Array.isArray(option.series)) {
            for (const s of option.series) {
                if (s.name !== stackTotalSeries) continue;
                s.label = {
                    show: true,
                    position: "top",
                    distance: 7,
                    color: "#4d626a",
                    fontSize: 10,
                    fontWeight: 700,
                    formatter: p => {
                        const total = p?.data?.stackTotal;
                        return total == null ? "" : formatValue(total, stackTotalFormat);
                    }
                };
            }
        }

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

        const responsive = responsiveOptions(responsiveMode, seriesFormats, itemFormat, option.series);
        if (responsive.length) option.media = responsive;

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

    function goBack(fallbackUrl) {
        if (window.history.length > 1) {
            window.history.back();
            return;
        }
        if (fallbackUrl) window.location.href = fallbackUrl;
    }

    window.addEventListener("resize", () => {
        for (const chart of charts.values()) chart.resize();
    });

    window.DynReportCharts = { render, dispose, downloadCsv, goBack };
})();
