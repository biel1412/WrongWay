using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace CoopAudit.Core.Xlsx;

/// <summary>
/// Leitor .xlsx enxuto e em streaming, sem dependências externas.
/// Lê apenas valores (não fórmulas/estilos), suficiente para planilhas de dados.
/// Funciona no navegador (Blazor WebAssembly).
/// </summary>
public sealed class XlsxWorkbook : IDisposable
{
    private const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    private readonly ZipArchive _zip;
    private readonly Dictionary<string, ZipArchiveEntry> _entries;
    private readonly List<(string Name, string Path)> _sheets = new();
    private string[] _shared = Array.Empty<string>();

    public IReadOnlyList<string> SheetNames => _sheets.Select(s => s.Name).ToList();

    private XlsxWorkbook(Stream stream)
    {
        try
        {
            _zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        }
        catch (InvalidDataException)
        {
            throw new InvalidDataException("O arquivo não é um .xlsx válido. Salve a planilha como 'Pasta de Trabalho do Excel (.xlsx)'.");
        }
        _entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in _zip.Entries)
            _entries[e.FullName.Replace('\\', '/').TrimStart('/')] = e;
    }

    /// <summary>Abre o workbook. O stream precisa permitir Seek (use MemoryStream no navegador).</summary>
    public static XlsxWorkbook Open(Stream stream)
    {
        var wb = new XlsxWorkbook(stream);
        wb.LoadStructure();
        return wb;
    }

    private void LoadStructure()
    {
        var workbookPath = FindWorkbookPath();
        var baseDir = workbookPath.Contains('/') ? workbookPath[..(workbookPath.LastIndexOf('/') + 1)] : string.Empty;
        var relsPath = baseDir + "_rels/" + workbookPath[(workbookPath.LastIndexOf('/') + 1)..] + ".rels";

        var rels = new Dictionary<string, string>();
        string? sharedPath = null;
        if (_entries.TryGetValue(relsPath, out var relsEntry))
        {
            using var rs = relsEntry.Open();
            var rx = XDocument.Load(rs);
            foreach (var r in rx.Root!.Elements().Where(e => e.Name.LocalName == "Relationship"))
            {
                var id = (string?)r.Attribute("Id");
                var target = (string?)r.Attribute("Target");
                var type = (string?)r.Attribute("Type") ?? string.Empty;
                if (id is null || target is null) continue;
                var full = ResolvePath(baseDir, target);
                rels[id] = full;
                if (type.EndsWith("/sharedStrings", StringComparison.OrdinalIgnoreCase)) sharedPath = full;
            }
        }

        using (var ws = Entry(workbookPath).Open())
        {
            var wx = XDocument.Load(ws);
            var sheetEls = wx.Descendants().Where(e => e.Name.LocalName == "sheet");
            var idx = 1;
            foreach (var s in sheetEls)
            {
                var name = (string?)s.Attribute("name") ?? $"Planilha{idx}";
                var rid = (string?)s.Attribute(XName.Get("id", RelNs));
                string? path = null;
                if (rid != null && rels.TryGetValue(rid, out var p)) path = p;
                path ??= $"{baseDir}worksheets/sheet{idx}.xml";
                if (_entries.ContainsKey(path)) _sheets.Add((name, path));
                idx++;
            }
        }

        sharedPath ??= baseDir + "sharedStrings.xml";
        if (_entries.TryGetValue(sharedPath, out var sharedEntry))
            _shared = ReadSharedStrings(sharedEntry);
    }

    private string FindWorkbookPath()
    {
        if (_entries.TryGetValue("_rels/.rels", out var root))
        {
            using var s = root.Open();
            var x = XDocument.Load(s);
            var target = x.Root!.Elements()
                .Where(e => ((string?)e.Attribute("Type") ?? "").EndsWith("/officeDocument", StringComparison.OrdinalIgnoreCase))
                .Select(e => (string?)e.Attribute("Target"))
                .FirstOrDefault();
            if (target != null)
            {
                var p = ResolvePath(string.Empty, target);
                if (_entries.ContainsKey(p)) return p;
            }
        }
        if (_entries.ContainsKey("xl/workbook.xml")) return "xl/workbook.xml";
        throw new InvalidDataException("Estrutura do .xlsx não reconhecida (workbook.xml ausente).");
    }

    private static string ResolvePath(string baseDir, string target)
    {
        target = target.Replace('\\', '/');
        if (target.StartsWith('/')) return target.TrimStart('/');
        var parts = new List<string>((baseDir + target).Split('/', StringSplitOptions.RemoveEmptyEntries));
        for (var i = 0; i < parts.Count; i++)
        {
            if (parts[i] == "..") { parts.RemoveAt(i); if (i > 0) { parts.RemoveAt(i - 1); i--; } i--; }
            else if (parts[i] == ".") { parts.RemoveAt(i); i--; }
        }
        return string.Join('/', parts);
    }

    private ZipArchiveEntry Entry(string path) =>
        _entries.TryGetValue(path, out var e) ? e : throw new InvalidDataException($"Parte ausente no .xlsx: {path}");

    private static XmlReaderSettings ReaderSettings() => new()
    {
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = false,
        DtdProcessing = DtdProcessing.Prohibit,
        CloseInput = true,
    };

    private static string[] ReadSharedStrings(ZipArchiveEntry entry)
    {
        var list = new List<string>();
        using var xr = XmlReader.Create(entry.Open(), ReaderSettings());
        StringBuilder? sb = null;
        var inT = false;
        var inPhonetic = false;
        while (xr.Read())
        {
            switch (xr.NodeType)
            {
                case XmlNodeType.Element:
                    switch (xr.LocalName)
                    {
                        case "si":
                            if (xr.IsEmptyElement) list.Add(string.Empty);
                            else sb = new StringBuilder();
                            break;
                        case "rPh":
                            if (!xr.IsEmptyElement) inPhonetic = true;
                            break;
                        case "t":
                            inT = !xr.IsEmptyElement;
                            break;
                    }
                    break;
                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                case XmlNodeType.Whitespace:
                case XmlNodeType.SignificantWhitespace:
                    if (inT && !inPhonetic) sb?.Append(xr.Value);
                    break;
                case XmlNodeType.EndElement:
                    switch (xr.LocalName)
                    {
                        case "t": inT = false; break;
                        case "rPh": inPhonetic = false; break;
                        case "si":
                            list.Add(sb?.ToString() ?? string.Empty);
                            sb = null;
                            break;
                    }
                    break;
            }
        }
        return list.ToArray();
    }

    /// <summary>Lê as linhas de uma aba como vetores de texto (células vazias = null).</summary>
    public IEnumerable<string?[]> ReadRows(string sheetName)
    {
        var sheet = _sheets.FirstOrDefault(s => s.Name == sheetName);
        if (sheet.Path is null) throw new ArgumentException($"Aba '{sheetName}' não encontrada.");
        return ReadRowsFromPath(sheet.Path);
    }

    private IEnumerable<string?[]> ReadRowsFromPath(string path)
    {
        using var xr = XmlReader.Create(Entry(path).Open(), ReaderSettings());
        var cells = new List<string?>(32);
        var value = new StringBuilder();
        var inRow = false;
        var col = -1;
        string? cellType = null;
        var capture = false;   // dentro de <v> ou <is><t>
        var inIs = false;
        var hasValue = false;
        var phonetic = false;

        while (xr.Read())
        {
            switch (xr.NodeType)
            {
                case XmlNodeType.Element:
                    switch (xr.LocalName)
                    {
                        case "row":
                            cells.Clear();
                            col = -1;
                            inRow = true;
                            if (xr.IsEmptyElement) { inRow = false; }
                            break;
                        case "c" when inRow:
                            var r = xr.GetAttribute("r");
                            col = r != null ? ColumnIndex(r) : col + 1;
                            cellType = xr.GetAttribute("t");
                            value.Clear();
                            hasValue = false;
                            if (xr.IsEmptyElement) SetCell(cells, col, null);
                            break;
                        case "v":
                            capture = !xr.IsEmptyElement;
                            hasValue = true;
                            break;
                        case "is":
                            inIs = true;
                            hasValue = true;
                            break;
                        case "t" when inIs:
                            capture = !xr.IsEmptyElement;
                            break;
                        case "rPh":
                            if (!xr.IsEmptyElement) phonetic = true; // ignora texto fonético
                            break;
                    }
                    break;
                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                case XmlNodeType.Whitespace:
                case XmlNodeType.SignificantWhitespace:
                    if (capture && !phonetic) value.Append(xr.Value);
                    break;
                case XmlNodeType.EndElement:
                    switch (xr.LocalName)
                    {
                        case "rPh":
                            phonetic = false;
                            break;
                        case "v":
                        case "t":
                            capture = false;
                            break;
                        case "is":
                            inIs = false;
                            break;
                        case "c" when inRow:
                            SetCell(cells, col, hasValue ? Resolve(cellType, value.ToString()) : null);
                            break;
                        case "row":
                            inRow = false;
                            yield return cells.ToArray();
                            break;
                    }
                    break;
            }
        }
    }

    private string? Resolve(string? type, string raw)
    {
        switch (type)
        {
            case "s":
                return int.TryParse(raw, out var i) && i >= 0 && i < _shared.Length ? _shared[i] : null;
            case "b":
                return raw == "1" ? "TRUE" : "FALSE";
            default:
                return raw.Length == 0 ? null : raw;
        }
    }

    private static void SetCell(List<string?> cells, int col, string? v)
    {
        if (col < 0) return;
        while (cells.Count <= col) cells.Add(null);
        cells[col] = v;
    }

    /// <summary>"A1" → 0, "AB12" → 27.</summary>
    public static int ColumnIndex(string cellRef)
    {
        var n = 0;
        foreach (var ch in cellRef)
        {
            var c = char.ToUpperInvariant(ch);
            if (c < 'A' || c > 'Z') break;
            n = n * 26 + (c - 'A' + 1);
        }
        return n - 1;
    }

    public void Dispose() => _zip.Dispose();
}
