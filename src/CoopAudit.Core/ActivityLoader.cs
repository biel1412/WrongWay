using CoopAudit.Core.Xlsx;

namespace CoopAudit.Core;

/// <summary>Uma linha da planilha de atividades (somente as que tocam cooperativas monitoradas).</summary>
public sealed class Activity
{
    public string Source { get; init; } = "";
    public int Line { get; init; }
    public string ProcessId { get; init; } = "";
    public string ProcessName { get; init; } = "";
    public string OccurrenceId { get; init; } = "";
    public string RecordId { get; init; } = "";
    public string OriginCoop { get; init; } = "";
    public string OriginUnit { get; init; } = "";
    public string DestCoop { get; init; } = "";
    public string DestUnit { get; init; } = "";
    public string User { get; init; } = "";
    public string ActivityName { get; init; } = "";
    public string Procedure { get; init; } = "";
    public string FlowStatus { get; init; } = "";
    public DateTime? Start { get; init; }
    public DateTime? End { get; init; }
    public string Justification { get; init; } = "";

    /// <summary>Momento da atuação: fim da atividade (ou início, se não houver fim).</summary>
    public DateTime? When => End ?? Start;
}

/// <summary>Resultado da leitura de uma ou mais planilhas de atividades.</summary>
public sealed class ActivityDataset
{
    public List<Activity> Relevant { get; } = new();
    public List<string> Files { get; } = new();
    public List<string> Warnings { get; } = new();
    public int TotalRows { get; internal set; }
    public int DuplicateRows { get; internal set; }
    public HashSet<string> AllUsers { get; } = new(StringComparer.Ordinal);
    public HashSet<string> AllCoops { get; } = new(StringComparer.Ordinal);
    public DateTime? MinDate { get; internal set; }
    public DateTime? MaxDate { get; internal set; }
    public Dictionary<DateOnly, int> RowsPerDay { get; } = new();
    public HashSet<string> MonitoredCoops { get; init; } = new(StringComparer.Ordinal);
}

public static class ActivityLoader
{
    private enum Col { ProcessId, ProcessName, Occurrence, Record, OrigCoop, OrigUnit, DestCoop, DestUnit, User, Activity, Procedure, Flow, Start, End, Justification }

    private static readonly Dictionary<string, Col> HeaderMap = new(StringComparer.Ordinal)
    {
        ["IDPROCESSO"] = Col.ProcessId,
        ["NOMEPROCESSO"] = Col.ProcessName,
        ["PROCESSO"] = Col.ProcessName,
        ["IDOCORRENCIAPROCESSO"] = Col.Occurrence,
        ["IDREGISTROCONTROLADO"] = Col.Record,
        ["INSTITUICAOORIGEM"] = Col.OrigCoop,
        ["INSTITUICAORIGEM"] = Col.OrigCoop,
        ["COOPORIGEM"] = Col.OrigCoop,
        ["UNIDADEORIGEM"] = Col.OrigUnit,
        ["INSTITUICADESTINO"] = Col.DestCoop,
        ["INSTITUICAODESTINO"] = Col.DestCoop,
        ["COOPDESTINO"] = Col.DestCoop,
        ["UNIDADEDESTINO"] = Col.DestUnit,
        ["USUARIO"] = Col.User,
        ["LOGIN"] = Col.User,
        ["ATIVIDADE"] = Col.Activity,
        ["PROCEDIMENTO"] = Col.Procedure,
        ["SITUACAOFLUXO"] = Col.Flow,
        ["DATAHORAINICIOATIVIDADE"] = Col.Start,
        ["DATAHORAFIMATIVIDADE"] = Col.End,
        ["JUSTIFICATIVA"] = Col.Justification,
    };

    public static ActivityDataset CreateDataset(IEnumerable<string> monitoredCoops) =>
        new() { MonitoredCoops = new HashSet<string>(monitoredCoops, StringComparer.Ordinal) };

    /// <summary>
    /// Lê todas as abas com o layout esperado e acumula no dataset.
    /// Guarda em memória apenas as linhas que envolvem cooperativas monitoradas (origem ou destino),
    /// o que permite processar planilhas mensais grandes no navegador.
    /// </summary>
    public static async Task LoadAsync(XlsxWorkbook wb, string fileName, ActivityDataset ds,
        Func<int, Task>? progress = null, HashSet<ulong>? dedupe = null)
    {
        ds.Files.Add(fileName);
        var sheetsRead = 0;

        foreach (var sheet in wb.SheetNames)
        {
            Dictionary<Col, int>? map = null;
            var line = 0;
            var scanned = 0;

            foreach (var row in wb.ReadRows(sheet))
            {
                line++;
                if (map is null)
                {
                    if (++scanned > 15) break;     // aba sem o layout esperado
                    map = TryMapHeader(row);
                    if (map != null) sheetsRead++;
                    continue;
                }

                string Get(Col c) => map.TryGetValue(c, out var i) && i < row.Length ? row[i] ?? "" : "";

                var userRaw = Get(Col.User);
                var origRaw = Get(Col.OrigCoop);
                var destRaw = Get(Col.DestCoop);
                if (userRaw.Length == 0 && origRaw.Length == 0 && destRaw.Length == 0) continue; // linha vazia

                var startRaw = Get(Col.Start);
                var endRaw = Get(Col.End);

                if (dedupe != null)
                {
                    var key = Hash(Get(Col.Occurrence), Get(Col.Record), Get(Col.Activity), userRaw, startRaw, endRaw, Get(Col.Procedure));
                    if (!dedupe.Add(key)) { ds.DuplicateRows++; continue; }
                }

                ds.TotalRows++;
                var user = Text.NormUser(userRaw);
                var orig = Text.NormCoop(origRaw);
                var dest = Text.NormCoop(destRaw);
                if (user.Length > 0) ds.AllUsers.Add(string.Intern(user));
                if (orig.Length > 0) ds.AllCoops.Add(orig);
                if (dest.Length > 0) ds.AllCoops.Add(dest);

                var end = Text.ParseDate(endRaw);
                var start = Text.ParseDate(startRaw);
                var when = end ?? start;
                if (when is { } w)
                {
                    if (ds.MinDate is null || w < ds.MinDate) ds.MinDate = w;
                    if (ds.MaxDate is null || w > ds.MaxDate) ds.MaxDate = w;
                    var day = DateOnly.FromDateTime(w);
                    ds.RowsPerDay[day] = ds.RowsPerDay.GetValueOrDefault(day) + 1;
                }

                if (ds.MonitoredCoops.Contains(orig) || ds.MonitoredCoops.Contains(dest))
                {
                    ds.Relevant.Add(new Activity
                    {
                        Source = fileName,
                        Line = line,
                        ProcessId = Get(Col.ProcessId),
                        ProcessName = Get(Col.ProcessName),
                        OccurrenceId = Get(Col.Occurrence),
                        RecordId = Get(Col.Record),
                        OriginCoop = orig,
                        OriginUnit = Get(Col.OrigUnit),
                        DestCoop = dest,
                        DestUnit = Get(Col.DestUnit),
                        User = string.Intern(user),
                        ActivityName = Get(Col.Activity),
                        Procedure = Get(Col.Procedure),
                        FlowStatus = Get(Col.Flow),
                        Start = start,
                        End = end,
                        Justification = Get(Col.Justification),
                    });
                }

                if (progress != null && ds.TotalRows % 5000 == 0)
                    await progress(ds.TotalRows);
            }
        }

        if (sheetsRead == 0)
            throw new InvalidDataException(
                $"\"{fileName}\": não encontrei as colunas USUARIO, INSTITUICAOORIGEM e INSTITUICADESTINO. " +
                "Confira se é a planilha de atividades (mesmo layout do compilado).");

        if (progress != null) await progress(ds.TotalRows);
    }

    private static Dictionary<Col, int>? TryMapHeader(string?[] row)
    {
        var map = new Dictionary<Col, int>();
        for (var i = 0; i < row.Length; i++)
        {
            var h = Text.NormHeader(row[i]);
            if (h.Length > 0 && HeaderMap.TryGetValue(h, out var c) && !map.ContainsKey(c)) map[c] = i;
        }
        var ok = map.ContainsKey(Col.User) && (map.ContainsKey(Col.OrigCoop) || map.ContainsKey(Col.DestCoop));
        return ok ? map : null;
    }

    /// <summary>Hash FNV-1a de 64 bits (estável) para detectar linhas repetidas entre arquivos.</summary>
    private static ulong Hash(params string[] parts)
    {
        const ulong offset = 14695981039346656037UL, prime = 1099511628211UL;
        var h = offset;
        foreach (var p in parts)
        {
            foreach (var ch in p) { h ^= ch; h *= prime; }
            h ^= 0x1F; h *= prime;
        }
        return h;
    }
}
