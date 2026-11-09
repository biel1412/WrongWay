using Nexus.Core;
using Nexus.Core.Xlsx;
using Microsoft.AspNetCore.Components.Forms;

namespace Nexus.Web.Services;

/// <summary>Estado da análise atual. Tudo fica na memória do navegador; nada é enviado a servidores.</summary>
public sealed class AuditSession
{
    public const long MaxFileBytes = 400L * 1024 * 1024; // 400 MB

    public UserDirectory? Directory { get; private set; }
    public ActivityDataset? Dataset { get; private set; }
    public AuditResult? Result { get; private set; }
    public AuditOptions Options { get; private set; } = new();
    public string UsersFileName { get; private set; } = "";

    public bool HasResult => Result != null;

    public async Task LoadAsync(IReadOnlyList<IBrowserFile> activityFiles, IBrowserFile usersFile, AuditOptions options,
        Func<string, double?, Task> report)
    {
        await report("Lendo planilha de usuários…", null);
        UserDirectory dir;
        using (var ms = await ToMemory(usersFile))
        using (var wb = XlsxWorkbook.Open(ms))
            dir = UserDirectory.Parse(wb, AuditScope.Coop);

        var ds = ActivityLoader.CreateDataset(dir.MonitoredCoops);
        var dedupe = activityFiles.Count > 1 ? new HashSet<ulong>() : null;

        for (var i = 0; i < activityFiles.Count; i++)
        {
            var f = activityFiles[i];
            var prefix = activityFiles.Count > 1 ? $"Arquivo {i + 1} de {activityFiles.Count} · " : "";
            await report($"{prefix}Carregando {f.Name} ({FormatBytes(f.Size)})…", null);
            using var ms = await ToMemory(f);
            using var wb = XlsxWorkbook.Open(ms);
            await ActivityLoader.LoadAsync(wb, f.Name, ds, async rows =>
            {
                await report($"{prefix}Processando {f.Name}: {Fmt.N(rows)} linhas lidas", null);
            }, dedupe);
        }

        await report("Cruzando usuários × cooperativas…", null);
        Directory = dir;
        Dataset = ds;
        UsersFileName = usersFile.Name;
        Options = options with { CoopFilter = null, From = null, To = null };
        Result = AuditEngine.Analyze(ds, dir, Options);
    }

    public void Reanalyze(AuditOptions options)
    {
        if (Directory is null || Dataset is null) return;
        Options = options;
        Result = AuditEngine.Analyze(Dataset, Directory, options);
    }

    public void Reset()
    {
        Directory = null;
        Dataset = null;
        Result = null;
        Options = new AuditOptions();
    }

    private static async Task<MemoryStream> ToMemory(IBrowserFile file)
    {
        if (file.Size > MaxFileBytes)
            throw new InvalidDataException($"\"{file.Name}\" tem {FormatBytes(file.Size)}; o limite é {FormatBytes(MaxFileBytes)}.");
        var ms = new MemoryStream(file.Size > 0 && file.Size < int.MaxValue ? (int)file.Size : 0);
        await using (var s = file.OpenReadStream(MaxFileBytes))
            await s.CopyToAsync(ms);
        ms.Position = 0;
        return ms;
    }

    public static string FormatBytes(long b) =>
        b >= 1024 * 1024 ? $"{b / 1024d / 1024d:0.0} MB".Replace('.', ',') : $"{Math.Max(1, b / 1024)} KB";
}
