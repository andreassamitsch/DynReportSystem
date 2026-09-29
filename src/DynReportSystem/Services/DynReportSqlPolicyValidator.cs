using System.Text;
using System.Text.RegularExpressions;
using DynReportSystem.Models;

namespace DynReportSystem.Services;

/// <summary>
/// Defense-in-depth validator for report SQL. This is deliberately conservative,
/// but it is not the primary security boundary. SQL least-privilege permissions
/// remain mandatory.
/// </summary>
public sealed class DynReportSqlPolicyValidator
{
    private static readonly Regex Forbidden = new(
        @"\b(INSERT|UPDATE|DELETE|MERGE|ALTER|TRUNCATE|GRANT|REVOKE|DENY|" +
        @"BACKUP|RESTORE|DBCC|KILL|SHUTDOWN|RECONFIGURE|BULK\s+INSERT|OPENROWSET|" +
        @"OPENDATASOURCE|OPENQUERY|EXECUTE\s+AS|WAITFOR|XP_[A-Z0-9_]+|SP_OA[A-Z0-9_]*)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex DdlToken = new(
        @"\b(?<verb>CREATE|DROP)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SelectIntoTarget = new(
        @"\bINTO\s+(?<target>\[[^\]]+\]|[#A-Za-z_][A-Za-z0-9_#$@]*(?:\.[#A-Za-z_][A-Za-z0-9_#$@]*)*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex AllowedCreateTempTable = new(
        @"^CREATE\s+TABLE\s+(?:\[#(?:[^\]]+)\]|#[A-Za-z_][A-Za-z0-9_]*)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex AllowedCreateTempIndex = new(
        @"^CREATE\s+(?:UNIQUE\s+)?(?:(?:CLUSTERED|NONCLUSTERED)\s+)?INDEX\s+" +
        @"(?:\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_]*)\s+ON\s+(?:\[#(?:[^\]]+)\]|#[A-Za-z_][A-Za-z0-9_]*)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex AllowedDropTempTable = new(
        @"^DROP\s+TABLE\s+(?:IF\s+EXISTS\s+)?(?:\[#(?:[^\]]+)\]|#[A-Za-z_][A-Za-z0-9_]*)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex GoBatch = new(
        @"(?m)^\s*GO\s*(?:--.*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ProcName = new(
        @"^\s*(?:\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_]*)(?:\.(?:\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_]*)){0,3}\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public void Validate(DynReportDataset dataSet, string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            throw new InvalidDataException($"Dataset '{dataSet.Id}' enthält keine Abfrage.");

        if (dataSet.CommandType.Equals("StoredProcedure", StringComparison.OrdinalIgnoreCase))
        {
            if (!ProcName.IsMatch(sql))
                throw new InvalidDataException(
                    $"Dataset '{dataSet.Id}' enthält keinen gültigen Stored-Procedure-Namen.");

            return;
        }

        if (!dataSet.CommandType.Equals("Text", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Dataset '{dataSet.Id}' verwendet unbekannten CommandType '{dataSet.CommandType}'.");

        var normalized = StripCommentsAndStrings(sql).Trim();
        normalized = Regex.Replace(
            normalized,
            @"^\s*SET\s+NOCOUNT\s+ON\s*;?",
            "",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();
        normalized = normalized.TrimStart(';', ' ', '\t', '\r', '\n');

        if (!(normalized.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
              || normalized.StartsWith("WITH", StringComparison.OrdinalIgnoreCase)
              || normalized.StartsWith("DECLARE", StringComparison.OrdinalIgnoreCase)
              || normalized.StartsWith("CREATE TABLE #", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                $"Dataset '{dataSet.Id}' muss als read-only Textabfrage mit SELECT, WITH, DECLARE oder lokaler #Temp-Tabelle beginnen.");
        }

        if (GoBatch.IsMatch(sql))
            throw new InvalidDataException(
                $"Dataset '{dataSet.Id}' enthält mehrere SQL-Batches (GO).");

        if (Forbidden.IsMatch(normalized))
            throw new InvalidDataException(
                $"Dataset '{dataSet.Id}' enthält einen in Reportabfragen nicht erlaubten SQL-Befehl.");

        ValidateLocalTempObjects(dataSet, normalized);

        if (Regex.IsMatch(
                normalized,
                @"\b(EXEC|EXECUTE)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            throw new InvalidDataException(
                $"Dataset '{dataSet.Id}' darf EXEC nicht in einer Textabfrage verwenden. " +
                "Stored Procedures müssen über CommandType=StoredProcedure definiert werden.");
        }
    }

    private static void ValidateLocalTempObjects(
        DynReportDataset dataSet,
        string normalized)
    {
        foreach (Match match in DdlToken.Matches(normalized))
        {
            var tail = normalized[match.Index..];

            var allowed = match.Groups["verb"].Value.Equals(
                    "CREATE",
                    StringComparison.OrdinalIgnoreCase)
                ? AllowedCreateTempTable.IsMatch(tail)
                  || AllowedCreateTempIndex.IsMatch(tail)
                : AllowedDropTempTable.IsMatch(tail);

            if (!allowed)
            {
                throw new InvalidDataException(
                    $"Dataset '{dataSet.Id}' darf CREATE/DROP nur für lokale #Temp-Tabellen bzw. deren Indizes verwenden.");
            }
        }

        foreach (Match match in SelectIntoTarget.Matches(normalized))
        {
            var target = match.Groups["target"].Value.Trim();
            var normalizedTarget = target.StartsWith("[", StringComparison.Ordinal)
                ? target.Trim('[', ']')
                : target;

            if (!normalizedTarget.StartsWith("#", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Dataset '{dataSet.Id}' darf SELECT ... INTO nur für lokale #Temp-Tabellen verwenden.");
            }
        }
    }

    private static string StripCommentsAndStrings(string sql)
    {
        var output = new StringBuilder(sql.Length);
        var i = 0;

        while (i < sql.Length)
        {
            if (i + 1 < sql.Length && sql[i] == '-' && sql[i + 1] == '-')
            {
                i += 2;
                while (i < sql.Length && sql[i] != '\n')
                    i++;

                output.Append(' ');
                continue;
            }

            if (i + 1 < sql.Length && sql[i] == '/' && sql[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < sql.Length && !(sql[i] == '*' && sql[i + 1] == '/'))
                    i++;

                i = Math.Min(sql.Length, i + 2);
                output.Append(' ');
                continue;
            }

            if (sql[i] == '\'')
            {
                output.Append("''");
                i++;

                while (i < sql.Length)
                {
                    if (sql[i] == '\'')
                    {
                        if (i + 1 < sql.Length && sql[i + 1] == '\'')
                        {
                            i += 2;
                            continue;
                        }

                        i++;
                        break;
                    }

                    i++;
                }

                continue;
            }

            output.Append(sql[i]);
            i++;
        }

        return output.ToString();
    }
}
