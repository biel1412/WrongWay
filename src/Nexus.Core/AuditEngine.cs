namespace Nexus.Core;

/// <summary>Qual coluna define em qual cooperativa o usuário atuou.</summary>
public enum CoopMatchMode
{
    OrigemOuDestino,
    Origem,
    Destino
}

public sealed record AuditOptions
{
    public CoopMatchMode Mode { get; init; } = CoopMatchMode.OrigemOuDestino;
    public string? CoopFilter { get; init; }
    public DateOnly? From { get; init; }
    public DateOnly? To { get; init; }
}

/// <summary>Uma atividade vinculada a uma cooperativa monitorada, com a situação do usuário nela.</summary>
public sealed record Hit(Activity Activity, string Coop, AccessStatus Status, bool Irregular);

public sealed record StatusCounts(int Autorizado, int NaoAutorizado)
{
    public int Total => Autorizado + NaoAutorizado;
    public int Of(AccessStatus s) => s == AccessStatus.Autorizado ? Autorizado : NaoAutorizado;
}

public sealed class CoopSummary
{
    public required string Coop { get; init; }
    public required string Label { get; init; }
    public int ListedAuthorized { get; init; }
    public required StatusCounts Volume { get; init; }
    public int Irregular { get; init; }
    public int UsersActing { get; init; }
    public int AuthorizedActing { get; init; }
    public List<UserSummary> FlaggedUsers { get; init; } = new();
}

public sealed class UserSummary
{
    public required string Coop { get; init; }
    public required string User { get; init; }
    public AccessStatus Status { get; init; }
    public bool Irregular { get; init; }
    public int Count { get; init; }
    public DateTime? First { get; init; }
    public DateTime? Last { get; init; }
    public Dictionary<string, int> Procedures { get; init; } = new();
    public string TopProcedure => Procedures.Count == 0 ? "-" : Procedures.MaxBy(p => p.Value).Key;
}

public sealed record TimeBucket(string Label, string Tooltip, StatusCounts Counts);

public sealed record NamedCount(string Name, int Count);

public sealed class AuditResult
{
    public required AuditOptions Options { get; init; }
    public List<Hit> Hits { get; init; } = new();
    public List<Hit> IrregularHits { get; init; } = new();
    public int FileRows { get; init; }
    public int FileUsers { get; init; }
    public int MonitoredActivities { get; init; }
    public int IrregularActivities { get; init; }
    public required StatusCounts StatusTotals { get; init; }
    public List<CoopSummary> Coops { get; init; } = new();
    public List<UserSummary> Users { get; init; } = new();
    public List<TimeBucket> Timeline { get; init; } = new();
    public bool TimelineIsHourly { get; init; }
    public List<NamedCount> IrregularProcedures { get; init; } = new();
    public List<NamedCount> IrregularProcesses { get; init; } = new();
    public int IrregularUsers => Users.Where(u => u.Irregular).Select(u => u.User).Distinct().Count();
    public double IrregularShare => MonitoredActivities == 0 ? 0 : (double)IrregularActivities / MonitoredActivities;
}

public static class AuditEngine
{
    public static AuditResult Analyze(ActivityDataset ds, UserDirectory dir, AuditOptions opt)
    {
        var hits = new List<Hit>();
        var seenRows = new HashSet<Activity>(ReferenceEqualityComparer.Instance);
        var irregularRows = new HashSet<Activity>(ReferenceEqualityComparer.Instance);

        foreach (var a in ds.Relevant)
        {
            if (opt.From.HasValue || opt.To.HasValue)
            {
                if (a.When is not { } w) continue;
                var d = DateOnly.FromDateTime(w);
                if (opt.From is { } f && d < f) continue;
                if (opt.To is { } t && d > t) continue;
            }

            void Add(string coop)
            {
                if (!dir.Coops.ContainsKey(coop)) return;
                if (opt.CoopFilter is { } cf && cf != coop) return;
                var st = dir.StatusOf(coop, a.User);
                var irregular = st == AccessStatus.NaoAutorizado;
                hits.Add(new Hit(a, coop, st, irregular));
                seenRows.Add(a);
                if (irregular) irregularRows.Add(a);
            }

            switch (opt.Mode)
            {
                case CoopMatchMode.Origem:
                    Add(a.OriginCoop);
                    break;
                case CoopMatchMode.Destino:
                    Add(a.DestCoop);
                    break;
                default:
                    Add(a.OriginCoop);
                    if (a.DestCoop != a.OriginCoop) Add(a.DestCoop);
                    break;
            }
        }

        // Usuários por coop
        var users = hits
            .GroupBy(h => (h.Coop, h.Activity.User))
            .Select(g =>
            {
                var whens = g.Select(h => h.Activity.When).Where(w => w.HasValue).Select(w => w!.Value).ToList();
                return new UserSummary
                {
                    Coop = g.Key.Coop,
                    User = g.Key.User,
                    Status = g.First().Status,
                    Irregular = g.First().Irregular,
                    Count = g.Count(),
                    First = whens.Count > 0 ? whens.Min() : null,
                    Last = whens.Count > 0 ? whens.Max() : null,
                    Procedures = g.GroupBy(h => Blank(h.Activity.Procedure)).ToDictionary(p => p.Key, p => p.Count()),
                };
            })
            .OrderByDescending(u => u.Irregular)
            .ThenBy(u => u.Status)
            .ThenByDescending(u => u.Count)
            .ToList();

        // Resumo por coop (inclui coops monitoradas sem nenhuma atividade)
        var coops = dir.Coops.Values
            .Where(c => opt.CoopFilter is null || c.Coop == opt.CoopFilter)
            .Select(c =>
            {
                var ch = hits.Where(h => h.Coop == c.Coop).ToList();
                var cu = users.Where(u => u.Coop == c.Coop).ToList();
                return new CoopSummary
                {
                    Coop = c.Coop,
                    Label = c.Label,
                    ListedAuthorized = c.Authorized.Count,
                    Volume = Count(ch),
                    Irregular = ch.Count(h => h.Irregular),
                    UsersActing = cu.Count,
                    AuthorizedActing = cu.Count(u => u.Status == AccessStatus.Autorizado),
                    FlaggedUsers = cu.Where(u => u.Irregular).OrderByDescending(u => u.Count).ToList(),
                };
            })
            .OrderByDescending(c => c.Irregular)
            .ThenByDescending(c => c.Volume.Total)
            .ToList();

        // Linha do tempo: por hora se o período for de um único dia, senão por dia
        var dated = hits.Where(h => h.Activity.When.HasValue).ToList();
        var days = dated.Select(h => DateOnly.FromDateTime(h.Activity.When!.Value)).Distinct().Count();
        var hourly = days <= 1;
        List<TimeBucket> timeline;
        if (hourly)
        {
            timeline = dated
                .GroupBy(h => h.Activity.When!.Value.Hour)
                .OrderBy(g => g.Key)
                .Select(g => new TimeBucket($"{g.Key:00}h", $"{g.Key:00}:00 – {g.Key:00}:59", Count(g)))
                .ToList();
            timeline = FillHours(timeline);
        }
        else
        {
            timeline = dated
                .GroupBy(h => DateOnly.FromDateTime(h.Activity.When!.Value))
                .OrderBy(g => g.Key)
                .Select(g => new TimeBucket(g.Key.ToString("dd/MM"), g.Key.ToString("dd/MM/yyyy") + " · " + DiaSemana(g.Key.DayOfWeek), Count(g)))
                .ToList();
        }

        var irregularHits = hits.Where(h => h.Irregular).OrderByDescending(h => h.Activity.When).ToList();

        return new AuditResult
        {
            Options = opt,
            Hits = hits,
            IrregularHits = irregularHits,
            FileRows = ds.TotalRows,
            FileUsers = ds.AllUsers.Count,
            MonitoredActivities = seenRows.Count,
            IrregularActivities = irregularRows.Count,
            StatusTotals = Count(hits),
            Coops = coops,
            Users = users,
            Timeline = timeline,
            TimelineIsHourly = hourly,
            IrregularProcedures = irregularHits.GroupBy(h => Blank(h.Activity.Procedure))
                .Select(g => new NamedCount(g.Key, g.Count())).OrderByDescending(n => n.Count).ToList(),
            IrregularProcesses = irregularHits.GroupBy(h => Blank(h.Activity.ProcessName))
                .Select(g => new NamedCount(g.Key, g.Count())).OrderByDescending(n => n.Count).ToList(),
        };
    }

    private static string Blank(string s) => string.IsNullOrWhiteSpace(s) ? "(sem informação)" : s;

    private static StatusCounts Count(IEnumerable<Hit> hits)
    {
        int a = 0, n = 0;
        foreach (var h in hits)
        {
            if (h.Status == AccessStatus.Autorizado) a++; else n++;
        }
        return new StatusCounts(a, n);
    }

    private static List<TimeBucket> FillHours(List<TimeBucket> buckets)
    {
        if (buckets.Count == 0) return buckets;
        var byHour = buckets.ToDictionary(b => int.Parse(b.Label[..2]));
        var min = byHour.Keys.Min();
        var max = byHour.Keys.Max();
        var list = new List<TimeBucket>();
        for (var h = min; h <= max; h++)
            list.Add(byHour.TryGetValue(h, out var b) ? b : new TimeBucket($"{h:00}h", $"{h:00}:00 – {h:00}:59", new StatusCounts(0, 0)));
        return list;
    }

    private static string DiaSemana(DayOfWeek d) => d switch
    {
        DayOfWeek.Monday => "segunda",
        DayOfWeek.Tuesday => "terça",
        DayOfWeek.Wednesday => "quarta",
        DayOfWeek.Thursday => "quinta",
        DayOfWeek.Friday => "sexta",
        DayOfWeek.Saturday => "sábado",
        _ => "domingo"
    };
}
