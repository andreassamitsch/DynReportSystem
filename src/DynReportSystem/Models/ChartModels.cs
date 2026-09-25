using System.Text.Json;

namespace DynReportSystem.Models;

public sealed record ChartDefinition(
    string Key,
    string Title,
    string Subtitle,
    object Options,
    string Height = "340px");

public sealed record ChartPointEvent(
    string ChartKey,
    string? Name,
    string? SeriesName,
    JsonElement Data);

public sealed record MetricSummary(
    decimal? Value,
    decimal? Current,
    decimal? Previous,
    decimal? TrendPercent);
