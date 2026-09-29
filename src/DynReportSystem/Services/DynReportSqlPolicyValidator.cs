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
        @"\b(INSERT|UPDATE|DELETE|MERGE|ALTER|DROP|CREATE|TRUNCATE|GRANT|REVOKE|DENY|" +
        @"BACKUP|RESTORE|DBCC|KILL|SHUTDOWN|RECONFIGURE|BULK\s+INSERT|OPENROWSET|" +
        @"OPENDATASOURCE|OPENQUERY|EXECUTE\s+AS|WAITFOR|XP_[A-Z0-9_]+|SP_OA[A-Z0-9_]*)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SelectInto = new(
        @"\bSELECT\b[\s\S]*?\bINTO\b",
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
        normalized = normalized.TrimStart(';', ' ', '\t', '\r', '\n');

        if (!(normalized.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
              || normalized.StartsWith("WITH", StringComparison.OrdinalIgnoreCase)
              || normalized.StartsWith("DECLARE", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                $"Dataset '{dataSet.Id}' muss als read-only Textabfrage mit SELECT, WITH oder DECLARE beginnen.");
        }

        if (GoBatch.IsMatch(sql))
            throw new InvalidDataException(
                $"Dataset '{dataSet.Id}' enthält mehrere SQL-Batches (GO).");

        if (Forbidden.IsMatch(normalized))
            throw new InvalidDataException(
                $"Dataset '{dataSet.Id}' enthält einen in Reportabfragen nicht erlaubten SQL-Befehl.");

        if (SelectInto.IsMatch(normalized))
            throw new InvalidDataException(
                $"Dataset '{dataSet.Id}' enthält SELECT ... INTO und würde Datenbankobjekte verändern.");

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
