using System.Reflection;

namespace DynReportSystem.Services;

/// <summary>
/// Single source of truth for the deployed runtime version.
/// Automatically follows the assembly version emitted by dotnet publish.
/// </summary>
public static class DynReportVersion
{
    public static string Current { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var assembly = typeof(DynReportVersion).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
            return informational.Split('+', 2)[0];

        var version = assembly.GetName().Version;
        return version is null
            ? "unbekannt"
            : $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
