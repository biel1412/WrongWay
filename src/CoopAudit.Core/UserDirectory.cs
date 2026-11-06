using System.Text.RegularExpressions;
using CoopAudit.Core.Xlsx;

namespace CoopAudit.Core;

public enum AccessStatus
{
    Autorizado,
    NaoAutorizado
}

/// <summary>Listas de acesso de uma cooperativa.</summary>
public sealed class CoopAccessList
{
    public required string Coop { get; init; }
    /// <summary>Código como escrito na planilha (ex.: "0300").</summary>
    public required string Label { get; init; }
    public HashSet<string> Authorized { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Unauthorized { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Lista de usuários autorizados por cooperativa (modelo "lista branca").
/// Quem não estiver na lista de autorizados da coop é NÃO AUTORIZADO nela.
///
/// Formato principal (uma aba por coop):
///   "3069_autorizados"  → únicos usuários liberados para a coop 3069
///   "0300_autorizados"  → únicos usuários liberados para a coop 300 (aba vazia = ninguém liberado)
///
/// Abas "COOP_não_autorizados" ainda são aceitas, mas são redundantes: só servem para
/// garantir que um nome também presente na lista de autorizados seja tratado como não autorizado.
///
/// Formato alternativo (uma única aba com colunas):
///   COOPERATIVA | USUARIO | STATUS (Autorizado / Não autorizado)
/// </summary>
public sealed partial class UserDirectory
{
    private static readonly HashSet<string> HeaderWords = new(StringComparer.Ordinal)
    {
        "LISTA", "USUARIO", "USUARIOS", "LOGIN", "LOGINS", "NOME", "NOMES", "MATRICULA", "USER", "USERS",
        "AUTORIZADOS", "NAOAUTORIZADOS", "COLABORADOR", "COLABORADORES"
    };

    public Dictionary<string, CoopAccessList> Coops { get; } = new(StringComparer.Ordinal);
    public List<string> Warnings { get; } = new();

    public IEnumerable<string> MonitoredCoops => Coops.Keys;

    [GeneratedRegex(@"^\s*(\d+)\s*[_\-\s.]+\s*(.+?)\s*$")]
    private static partial Regex SheetNameRegex();

    public AccessStatus StatusOf(string coop, string user)
    {
        if (!Coops.TryGetValue(coop, out var list)) return AccessStatus.NaoAutorizado;
        return list.Authorized.Contains(user) ? AccessStatus.Autorizado : AccessStatus.NaoAutorizado;
    }

    public static UserDirectory Parse(XlsxWorkbook wb)
    {
        var dir = new UserDirectory();
        var recognized = 0;

        foreach (var sheet in wb.SheetNames)
        {
            var m = SheetNameRegex().Match(Text.RemoveAccents(sheet).ToUpperInvariant());
            if (m.Success && ClassifyKind(m.Groups[2].Value) is { } authorized)
            {
                recognized++;
                var code = m.Groups[1].Value;
                var list = dir.GetOrAdd(code);
                foreach (var user in ReadUserColumn(wb.ReadRows(sheet)))
                    (authorized ? list.Authorized : list.Unauthorized).Add(user);
                continue;
            }

            if (TryParseLongFormat(wb.ReadRows(sheet), dir)) recognized++;
        }

        if (recognized == 0)
            throw new InvalidDataException(
                "Não encontrei listas de usuários. Nomeie as abas como \"3069_autorizados\", \"0300_autorizados\"... " +
                "ou use uma aba com as colunas COOPERATIVA, USUARIO e STATUS.");

        foreach (var c in dir.Coops.Values)
        {
            var both = c.Authorized.Intersect(c.Unauthorized).ToList();
            foreach (var u in both)
            {
                c.Authorized.Remove(u);
                dir.Warnings.Add($"Coop {c.Label}: {u} aparece nas duas listas — considerado NÃO autorizado.");
            }
            if (c.Authorized.Count == 0)
                dir.Warnings.Add($"Coop {c.Label}: lista de autorizados vazia — todo usuário que atuar nela será considerado não autorizado.");
        }

        return dir;
    }

    private CoopAccessList GetOrAdd(string rawCode)
    {
        var key = Text.NormCoop(rawCode);
        if (!Coops.TryGetValue(key, out var list))
        {
            list = new CoopAccessList { Coop = key, Label = rawCode.Trim() };
            Coops[key] = list;
        }
        return list;
    }

    /// <summary>true = autorizados, false = não autorizados, null = aba não reconhecida.</summary>
    private static bool? ClassifyKind(string kind)
    {
        var k = Text.NormHeader(kind);
        if (k.Contains("NAOAUTORIZ") || k.StartsWith("NAO") || k.Contains("BLOQUE") || k.Contains("PROIBID") || k.Contains("NEGAD"))
            return false;
        if (k.Contains("AUTORIZ") || k.Contains("PERMITID") || k.Contains("LIBERAD"))
            return true;
        return null;
    }

    private static IEnumerable<string> ReadUserColumn(IEnumerable<string?[]> rows)
    {
        var col = 0;
        var first = true;
        foreach (var row in rows)
        {
            if (first)
            {
                first = false;
                // Se a primeira linha for um cabeçalho reconhecível, usa a coluna dele.
                for (var i = 0; i < row.Length; i++)
                {
                    if (row[i] is { } h && HeaderWords.Contains(Text.NormHeader(h))) { col = i; break; }
                }
                // primeira coluna preenchida, se A estiver vazia
                if (col == 0 && (row.Length == 0 || string.IsNullOrWhiteSpace(row[0])))
                {
                    var firstFilled = Array.FindIndex(row, v => !string.IsNullOrWhiteSpace(v));
                    if (firstFilled > 0) col = firstFilled;
                }
            }

            if (col >= row.Length) continue;
            var v = row[col];
            if (string.IsNullOrWhiteSpace(v)) continue;
            if (HeaderWords.Contains(Text.NormHeader(v))) continue;
            yield return Text.NormUser(v);
        }
    }

    private static bool TryParseLongFormat(IEnumerable<string?[]> rows, UserDirectory dir)
    {
        int coopCol = -1, userCol = -1, statusCol = -1;
        var headerFound = false;
        var any = false;
        var scanned = 0;

        foreach (var row in rows)
        {
            if (!headerFound)
            {
                if (++scanned > 10) return false;
                for (var i = 0; i < row.Length; i++)
                {
                    var h = Text.NormHeader(row[i]);
                    if (h.Length == 0) continue;
                    if (coopCol < 0 && (h.Contains("COOP") || h.StartsWith("INSTITUICAO") || h == "AGENCIA")) coopCol = i;
                    else if (userCol < 0 && (h.Contains("USUARIO") || h.Contains("LOGIN"))) userCol = i;
                    else if (statusCol < 0 && (h.Contains("STATUS") || h.Contains("AUTORIZ") || h.Contains("SITUACAO") || h.Contains("PERMISSAO"))) statusCol = i;
                }
                if (coopCol >= 0 && userCol >= 0 && statusCol >= 0) headerFound = true;
                else { coopCol = userCol = statusCol = -1; }
                continue;
            }

            string? Get(int i) => i < row.Length ? row[i] : null;
            var coop = Get(coopCol);
            var user = Get(userCol);
            var status = Text.NormHeader(Get(statusCol));
            if (string.IsNullOrWhiteSpace(coop) || string.IsNullOrWhiteSpace(user) || status.Length == 0) continue;

            bool? authorized =
                status.StartsWith("NAO") || status == "N" || status.Contains("BLOQUE") || status.Contains("PROIBID") || status.Contains("NEGAD") ? false :
                status.Contains("AUTORIZ") || status is "SIM" or "S" or "OK" or "LIBERADO" or "PERMITIDO" ? true : null;
            if (authorized is null) continue;

            var list = dir.GetOrAdd(coop);
            (authorized.Value ? list.Authorized : list.Unauthorized).Add(Text.NormUser(user));
            any = true;
        }
        return headerFound && any;
    }
}
