using System.Diagnostics.Metrics;

namespace DynReportSystem.Services;

/// <summary>
/// Built-in .NET metrics. These instruments can be collected by an
/// OpenTelemetry-compatible exporter later without changing report code.
/// </summary>
public sealed class DynReportMetrics : IDisposable
{
    private readonly Meter _meter = new("DynReportSystem", "0.19.1");

    public Counter<long> ReportExecutions { get; }
    public Counter<long> DatasetExecutions { get; }
    public Counter<long> DatasetErrors { get; }
    public Counter<long> PackagePublishes { get; }
    public Histogram<double> DatasetDurationMs { get; }
    public Histogram<long> DatasetRows { get; }
    public Histogram<double> ReportDurationMs { get; }
    public Counter<long> InteractiveTableQueries { get; }
    public Histogram<double> InteractiveTableDurationMs { get; }
    public Histogram<long> InteractiveTableRows { get; }

    public DynReportMetrics()
    {
        ReportExecutions = _meter.CreateCounter<long>(
            "dynreport.report.executions",
            unit: "{execution}",
            description: "Number of native report executions.");

        DatasetExecutions = _meter.CreateCounter<long>(
            "dynreport.dataset.executions",
            unit: "{execution}",
            description: "Number of native dataset executions.");

        DatasetErrors = _meter.CreateCounter<long>(
            "dynreport.dataset.errors",
            unit: "{error}",
            description: "Number of native dataset execution failures.");

        PackagePublishes = _meter.CreateCounter<long>(
            "dynreport.package.publishes",
            unit: "{publish}",
            description: "Number of validated package publishes.");

        DatasetDurationMs = _meter.CreateHistogram<double>(
            "dynreport.dataset.duration",
            unit: "ms",
            description: "Native dataset execution duration.");

        DatasetRows = _meter.CreateHistogram<long>(
            "dynreport.dataset.rows",
            unit: "{row}",
            description: "Rows materialized by a native dataset execution.");

        ReportDurationMs = _meter.CreateHistogram<double>(
            "dynreport.report.duration",
            unit: "ms",
            description: "End-to-end native report execution duration.");

        InteractiveTableQueries = _meter.CreateCounter<long>(
            "dynreport.table.queries",
            unit: "{query}",
            description: "Number of server-side interactive table queries.");

        InteractiveTableDurationMs = _meter.CreateHistogram<double>(
            "dynreport.table.query.duration",
            unit: "ms",
            description: "Server-side interactive table query duration.");

        InteractiveTableRows = _meter.CreateHistogram<long>(
            "dynreport.table.query.rows",
            unit: "{row}",
            description: "Rows returned by server-side interactive table queries.");
    }

    public void Dispose() => _meter.Dispose();
}
