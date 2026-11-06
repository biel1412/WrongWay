using System.Globalization;
using System.Text;
using CoopAudit.Core;

namespace CoopAudit.Web.Services;

/// <summary>Formatação no padrão brasileiro (sem depender de dados de cultura no navegador).</summary>
public static class Fmt
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string N(int n) => n.ToString("#,0", Inv).Replace(',', '.');

    public static string Pct(double v, int decimals = 1) =>
        (v * 100).ToString(decimals > 0 ? "0." + new string('0', decimals) : "0", Inv).Replace('.', ',') + "%";

    public static string Pct(int part, int total, int decimals = 1) => total == 0 ? "0%" : Pct((double)part / total, decimals);

    public static string DateTime(DateTime? d) => d?.ToString("dd/MM/yyyy HH:mm", Inv) ?? "-";
    public static string Date(DateTime? d) => d?.ToString("dd/MM/yyyy", Inv) ?? "-";
    public static string Time(DateTime? d) => d?.ToString("HH:mm", Inv) ?? "-";

    public static string StatusLabel(AccessStatus s) => s switch
    {
        AccessStatus.Autorizado => "Autorizado",
        _ => "Não autorizado"
    };

    public static string StatusCss(AccessStatus s) => s switch
    {
        AccessStatus.Autorizado => "ok",
        _ => "bad"
    };

    public static string ModeLabel(CoopMatchMode m) => m switch
    {
        CoopMatchMode.Origem => "Instituição de origem",
        CoopMatchMode.Destino => "Instituição de destino",
        _ => "Origem ou destino"
    };

    /// <summary>CSV com ponto e vírgula e BOM — abre direto no Excel em português.</summary>
    public static string Csv(IEnumerable<string> header, IEnumerable<IEnumerable<string?>> rows)
    {
        var sb = new StringBuilder();
        sb.Append('﻿');
        sb.AppendLine(string.Join(';', header.Select(Esc)));
        foreach (var r in rows) sb.AppendLine(string.Join(';', r.Select(Esc)));
        return sb.ToString();

        static string Esc(string? v)
        {
            v ??= "";
            return v.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
        }
    }
}
