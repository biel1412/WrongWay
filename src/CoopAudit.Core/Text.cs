using System.Globalization;
using System.Text;

namespace CoopAudit.Core;

/// <summary>Normalização de textos (usuários, cooperativas, cabeçalhos).</summary>
public static class Text
{
    /// <summary>Maiúsculas, sem acentos e sem espaços nas pontas. Ex.: " laisa.caetano " → "LAISA.CAETANO".</summary>
    public static string NormUser(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        return RemoveAccents(s.Trim()).ToUpperInvariant();
    }

    /// <summary>Código de cooperativa sem zeros à esquerda. Ex.: "0300" → "300", "3069.0" → "3069".</summary>
    public static string NormCoop(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        var t = s.Trim();
        if (t.EndsWith(".0", StringComparison.Ordinal)) t = t[..^2];
        var allDigits = t.Length > 0 && t.All(char.IsAsciiDigit);
        if (!allDigits) return t.ToUpperInvariant();
        t = t.TrimStart('0');
        return t.Length == 0 ? "0" : t;
    }

    /// <summary>Cabeçalho comparável: maiúsculas, sem acentos e só letras/dígitos.</summary>
    public static string NormHeader(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        var src = RemoveAccents(s).ToUpperInvariant();
        var sb = new StringBuilder(src.Length);
        foreach (var c in src)
            if (char.IsAsciiLetterOrDigit(c)) sb.Append(c);
        return sb.ToString();
    }

    /// <summary>Remove acentos do português sem depender de ICU (funciona no WebAssembly em modo invariante).</summary>
    public static string RemoveAccents(string s)
    {
        var needs = false;
        foreach (var c in s) if (c > 127) { needs = true; break; }
        if (!needs) return s;

        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            sb.Append(c switch
            {
                'á' or 'à' or 'â' or 'ã' or 'ä' => 'a',
                'Á' or 'À' or 'Â' or 'Ã' or 'Ä' => 'A',
                'é' or 'è' or 'ê' or 'ë' => 'e',
                'É' or 'È' or 'Ê' or 'Ë' => 'E',
                'í' or 'ì' or 'î' or 'ï' => 'i',
                'Í' or 'Ì' or 'Î' or 'Ï' => 'I',
                'ó' or 'ò' or 'ô' or 'õ' or 'ö' => 'o',
                'Ó' or 'Ò' or 'Ô' or 'Õ' or 'Ö' => 'O',
                'ú' or 'ù' or 'û' or 'ü' => 'u',
                'Ú' or 'Ù' or 'Û' or 'Ü' => 'U',
                'ç' => 'c',
                'Ç' => 'C',
                'ñ' => 'n',
                'Ñ' => 'N',
                ' ' => ' ',
                _ => c
            });
        }
        return sb.ToString();
    }

    private static readonly string[] DateFormats =
    {
        "dd/MM/yyyy HH:mm:ss", "dd/MM/yyyy HH:mm", "dd/MM/yyyy H:mm:ss", "dd/MM/yyyy H:mm",
        "d/M/yyyy HH:mm:ss", "d/M/yyyy H:mm:ss", "d/M/yyyy HH:mm", "d/M/yyyy H:mm",
        "dd/MM/yyyy", "d/M/yyyy",
        "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm:ss.fff", "yyyy-MM-ddTHH:mm:ss.fff", "yyyy-MM-dd"
    };

    /// <summary>Lê datas no formato brasileiro, ISO ou número serial do Excel.</summary>
    public static DateTime? ParseDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim();
        if (TryFastBr(t, out var fast)) return fast;
        if (DateTime.TryParseExact(t, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d;
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var oa) && oa > 1 && oa < 2958465)
        {
            try { return DateTime.FromOADate(oa); } catch { /* ignora */ }
        }
        return null;
    }

    /// <summary>Caminho rápido para "dd/MM/yyyy HH:mm:ss" (formato do compilado), sem alocações.</summary>
    private static bool TryFastBr(string t, out DateTime d)
    {
        d = default;
        if (t.Length != 19 || t[2] != '/' || t[5] != '/' || t[10] != ' ' || t[13] != ':' || t[16] != ':') return false;
        static int N(string s, int i, int len)
        {
            var v = 0;
            for (var k = i; k < i + len; k++)
            {
                var c = s[k] - '0';
                if ((uint)c > 9) return -1;
                v = v * 10 + c;
            }
            return v;
        }
        int day = N(t, 0, 2), mon = N(t, 3, 2), year = N(t, 6, 4), h = N(t, 11, 2), mi = N(t, 14, 2), sec = N(t, 17, 2);
        if (day < 1 || mon < 1 || mon > 12 || year < 1 || h < 0 || h > 23 || mi < 0 || mi > 59 || sec < 0 || sec > 59) return false;
        if (day > DateTime.DaysInMonth(year, mon)) return false;
        d = new DateTime(year, mon, day, h, mi, sec);
        return true;
    }
}
