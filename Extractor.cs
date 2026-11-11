using System.Text;
using LibHac;
using LibHac.Common;
using LibHac.Common.Keys;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.FsSystem;
using LibHac.Tools.Es;
using LibHac.Tools.Fs;
using LibHac.Tools.FsSystem;
using LibHac.Tools.FsSystem.NcaUtils;
using LibHac.Spl;
using Path = System.IO.Path;

namespace NxExtractor;

public enum SourceKind { Base, Update, Dlc }

public sealed class ExtractOptions
{
    public string ProdKeys, TitleKeys, OutputDir;
    public string BaseFile, UpdateFile;
    public List<string> DlcFiles = new();
    public bool ExeFs = true, RomFs = true, Nacp = true, DlcRomFs = true;
}

public sealed class Extractor : IDisposable
{
    private sealed class NcaEntry
    {
        public Nca Nca;
        public SourceKind Kind;
        public string Name;
        public ulong TitleId => Nca.Header.TitleId;
        public NcaContentType Type => Nca.Header.ContentType;
    }

    private readonly ExtractOptions _o;
    private readonly Action<string> _log;
    private readonly IProgressReport _progress;
    private readonly KeySet _keys;
    private readonly List<IDisposable> _keepAlive = new();
    private readonly List<string> _tempFiles = new();
    private readonly List<NcaEntry> _ncas = new();
    private string _tempDir;

    public Extractor(ExtractOptions o, Action<string> log, IProgressReport progress)
    {
        _o = o;
        _log = log;
        _progress = progress;
        _keys = KeySet.CreateDefaultKeySet();
        // Los .nup ya vienen descifrados: sin claves se pueden extraer igual.
        if (File.Exists(o.ProdKeys))
            ExternalKeyReader.ReadKeyFile(_keys, o.ProdKeys,
                string.IsNullOrWhiteSpace(o.TitleKeys) ? null : o.TitleKeys, null, (IProgressReport)null);
    }

    public static bool IsNup(string path) => path.EndsWith(".nup", StringComparison.OrdinalIgnoreCase);

    public void Run()
    {
        _tempDir = Path.Combine(_o.OutputDir, ".nxextractor_tmp");
        Directory.CreateDirectory(_tempDir);

        var inputs = new List<(string path, SourceKind kind)>();
        if (!string.IsNullOrEmpty(_o.BaseFile)) inputs.Add((_o.BaseFile, SourceKind.Base));
        if (!string.IsNullOrEmpty(_o.UpdateFile)) inputs.Add((_o.UpdateFile, SourceKind.Update));
        inputs.AddRange(_o.DlcFiles.Select(d => (d, SourceKind.Dlc)));

        foreach (var (path, kind) in inputs)
        {
            if (IsNup(path)) ExtractNup(path);
            else Load(path, kind);
        }

        // Las NCAs de un update llevan el mismo Title ID que el base (sólo el cnmt usa ...800);
        // se distinguen porque su RomFS es un parche BKTR.
        var programs = _ncas.Where(n => n.Type == NcaContentType.Program).ToList();
        var basePrograms = programs.Where(n => !IsPatch(n.Nca)).ToList();
        var patchPrograms = programs.Where(n => IsPatch(n.Nca)).ToList();

        foreach (var bp in basePrograms)
            ExtractProgram(bp, patchPrograms.FirstOrDefault(p => p.TitleId == bp.TitleId));

        // Update sin juego base: sólo se puede sacar el ExeFS (el RomFS es un parche BKTR).
        foreach (var pp in patchPrograms.Where(p => basePrograms.All(b => b.TitleId != p.TitleId)))
        {
            _log(L.F("updateNoBase", pp.TitleId.ToString("X16")));
            if (_o.ExeFs) ExtractSection(pp.Nca, NcaSectionType.Code, Path.Combine(GameDir(pp.TitleId), "exefs"), "ExeFS (update)");
        }

        if (_o.Nacp) ExtractControls();
        if (_o.DlcRomFs) ExtractDlcs();

        _log(L.T("logDone"));
    }

    // ---------- NUP (Switch 2) ----------

    private void ExtractNup(string path)
    {
        _log(L.F("logOpening", "NUP", Path.GetFileName(path)));
        using var nup = new NupPackage(path);
        foreach (var t in nup.Titles)
            _log(L.F("logNupTitle", t.Index, t.Id.ToString("X16"), t.Version));

        // Igual que el emulador: el ExeFS es el del título más nuevo que lo tenga, y el RomFS es el
        // del base con los updates encima (cada archivo, el de la versión más nueva).
        var titles = nup.Titles.Where(t => t.Kind != NupFormat.KindAddon)
            .OrderBy(t => t.Kind).ThenBy(t => t.Version).ToList();
        if (titles.Count == 0) throw new InvalidDataException(L.T("badNup"));
        var newest = titles.Last();
        string dir = GameDir(newest.Id);
        _log(L.F("logNupExtracting", newest.Id.ToString("X16"), newest.Version));

        var parts = new List<(string name, List<(string path, NupPackage.FileRec rec)> files)>();
        if (_o.ExeFs)
        {
            var exeTitle = titles.LastOrDefault(t => t.ExeFs != NupFormat.None);
            if (exeTitle != null) parts.Add(("exefs", nup.Walk(exeTitle.ExeFs).ToList()));
        }
        if (_o.RomFs)
        {
            var merged = new Dictionary<string, NupPackage.FileRec>(StringComparer.Ordinal);
            foreach (var t in titles)
                foreach (var (p, r) in nup.Walk(t.RomFs)) merged[p] = r;
            if (merged.Count > 0) parts.Add(("romfs", merged.Select(kv => (kv.Key, kv.Value)).ToList()));
        }

        foreach (var (part, files) in parts)
        {
            string outRoot = Path.Combine(dir, part);
            _log(L.F("logExtracting", $"{part} ({files.Count})", outRoot));
            _progress?.SetTotal(Math.Max(1, files.Sum(f => f.rec.Size)));
            long done = 0;
            foreach (var (rel, rec) in files)
            {
                string dst = SafeJoin(outRoot, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                using (var fs = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                    nup.CopyTo(rec, rel, fs, n => { done += n; _progress?.Report(done); });
            }
        }
    }

    private static readonly HashSet<string> WindowsDevices = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>Une una ruta del paquete bajo root, rechazando "..", nombres inválidos o dispositivos de Windows.</summary>
    private static string SafeJoin(string root, string rel)
    {
        var parts = rel.Split('/');
        foreach (var p in parts)
        {
            if (p.Length == 0 || p == "." || p == ".." || p.EndsWith(' ') || p.EndsWith('.') ||
                p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || WindowsDevices.Contains(p.Split('.')[0].TrimEnd()))
                throw new InvalidDataException(L.F("unsafePath", rel));
        }
        string full = Path.GetFullPath(Path.Combine(root, Path.Combine(parts)));
        string rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(L.F("unsafePath", rel));
        return full;
    }

    private static bool IsPatch(Nca nca)
    {
        for (int i = 0; i < 4; i++)
            if (nca.Header.IsSectionEnabled(i) && nca.Header.GetFsHeader(i).IsPatchSection()) return true;
        return false;
    }

    private string GameDir(ulong programId) => Path.Combine(_o.OutputDir, programId.ToString("X16"));

    // ---------- Carga de contenedores ----------

    private void Load(string path, SourceKind kind)
    {
        _log(L.F("logOpening", L.T("kind" + kind), Path.GetFileName(path)));
        var storage = new LocalStorage(path, FileAccess.Read);
        _keepAlive.Add(storage);

        IFileSystem fs;
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".xci" or ".xcz")
        {
            var xci = new Xci(_keys, storage);
            if (!xci.HasPartition(XciPartitionType.Secure))
                throw new InvalidDataException(L.T("noSecure"));
            fs = xci.OpenPartition(XciPartitionType.Secure);
        }
        else
        {
            var pfs = new PartitionFileSystem();
            pfs.Initialize(storage).ThrowIfFailure();
            fs = pfs;
        }
        _keepAlive.Add(fs);

        var entries = fs.EnumerateEntries("/", "*").Where(e => e.Type == DirectoryEntryType.File).ToList();

        foreach (var e in entries.Where(e => e.Name.EndsWith(".tik", StringComparison.OrdinalIgnoreCase)))
            ImportTicket(fs, e.FullPath);

        foreach (var e in entries)
        {
            bool isNca = e.Name.EndsWith(".nca", StringComparison.OrdinalIgnoreCase);
            bool isNcz = e.Name.EndsWith(".ncz", StringComparison.OrdinalIgnoreCase);
            if (!isNca && !isNcz) continue;

            try
            {
                IStorage ncaStorage = isNcz ? DecompressNcz(fs, e) : OpenFile(fs, e.FullPath).AsStorage();
                var nca = new Nca(_keys, ncaStorage);
                _ncas.Add(new NcaEntry { Nca = nca, Kind = kind, Name = e.Name });
                _log($"  {e.Name}: {nca.Header.ContentType} {nca.Header.TitleId:X16}");
            }
            catch (Exception ex)
            {
                _log($"  [!] {e.Name}: {ex.Message}");
            }
        }
    }

    private IFile OpenFile(IFileSystem fs, string path)
    {
        using var file = new UniqueRef<IFile>();
        fs.OpenFile(ref file.Ref, path.ToU8Span(), OpenMode.Read).ThrowIfFailure();
        IFile f = file.Release();
        _keepAlive.Add(f);
        return f;
    }

    private void ImportTicket(IFileSystem fs, string path)
    {
        try
        {
            var t = new Ticket(OpenFile(fs, path).AsStream());
            byte[] tk = t.GetTitleKey(_keys);
            if (tk != null)
                _keys.ExternalKeySet.Add(new RightsId(t.RightsId), new AccessKey(tk)).ThrowIfFailure();
        }
        catch (Exception ex)
        {
            _log($"  [!] Ticket {path}: {ex.Message}");
        }
    }

    private IStorage DecompressNcz(IFileSystem fs, DirectoryEntryEx e)
    {
        _log(L.F("logDecompressing", e.Name));
        string tmp = Path.Combine(_tempDir, Path.ChangeExtension(e.Name, ".nca"));
        Ncz.Decompress(OpenFile(fs, e.FullPath).AsStream(), tmp, _log);
        _tempFiles.Add(tmp);
        var st = new LocalStorage(tmp, FileAccess.Read);
        _keepAlive.Add(st);
        return st;
    }

    // ---------- Extracción ----------

    private void ExtractProgram(NcaEntry baseNca, NcaEntry patch)
    {
        string dir = GameDir(baseNca.TitleId);
        _log(L.F("logGame", baseNca.TitleId.ToString("X16")) + (patch != null ? L.T("logPlusUpdate") : ""));

        if (_o.ExeFs)
        {
            // El ExeFS de un update es completo; reemplaza al del base.
            var src = patch != null && patch.Nca.CanOpenSection(NcaSectionType.Code) ? patch.Nca : baseNca.Nca;
            ExtractSection(src, NcaSectionType.Code, Path.Combine(dir, "exefs"), "ExeFS");
        }

        if (_o.RomFs && baseNca.Nca.CanOpenSection(NcaSectionType.Data))
        {
            string outDir = Path.Combine(dir, "romfs");
            try
            {
                IFileSystem romfs = patch != null
                    ? baseNca.Nca.OpenFileSystemWithPatch(patch.Nca, NcaSectionType.Data, IntegrityCheckLevel.None)
                    : baseNca.Nca.OpenFileSystem(NcaSectionType.Data, IntegrityCheckLevel.None);
                _log(L.F("logExtracting", patch != null ? L.T("romfsWithUpdate") : "RomFS", outDir));
                romfs.Extract(outDir, _progress);
            }
            catch (Exception ex)
            {
                _log($"  [!] RomFS: {ex.Message}");
            }
        }
    }

    private void ExtractSection(Nca nca, NcaSectionType type, string outDir, string label)
    {
        try
        {
            if (!nca.CanOpenSection(type)) { _log(L.F("noSection", label)); return; }
            _log(L.F("logExtracting", label, outDir));
            nca.OpenFileSystem(type, IntegrityCheckLevel.None).Extract(outDir, _progress);
        }
        catch (Exception ex)
        {
            _log($"  [!] {label}: {ex.Message}");
        }
    }

    private void ExtractControls()
    {
        var controls = _ncas.Where(n => n.Type == NcaContentType.Control).ToList();
        // Agrupar por juego: el control del update (más nuevo) gana sobre el del base.
        foreach (var group in controls.GroupBy(c => c.TitleId))
        {
            var ctrl = group.OrderByDescending(c => c.Kind == SourceKind.Update).First();
            string dir = GameDir(group.Key);
            try
            {
                var fs = ctrl.Nca.OpenFileSystem(NcaSectionType.Data, IntegrityCheckLevel.None);
                string ctrlDir = Path.Combine(dir, "control");
                fs.Extract(ctrlDir, _progress);
                string nacp = Path.Combine(ctrlDir, "control.nacp");
                if (File.Exists(nacp))
                {
                    File.Copy(nacp, Path.Combine(dir, "control.nacp"), true);
                    var (name, ver) = ReadNacp(nacp);
                    _log($"  control.nacp -> {dir}  ({name} v{ver})");
                    File.WriteAllText(Path.Combine(dir, "info.txt"),
                        $"Title ID: {group.Key:X16}\r\n{L.T("infoName")}: {name}\r\n{L.T("infoVersion")}: {ver}\r\n");
                }
            }
            catch (Exception ex)
            {
                _log($"  [!] Control {ctrl.TitleId:X16}: {ex.Message}");
            }
        }
    }

    private void ExtractDlcs()
    {
        foreach (var dlc in _ncas.Where(n => n.Type == NcaContentType.PublicData))
        {
            // ID de app del DLC: (dlcId - 0x1000) con los 12 bits bajos a cero.
            ulong appId = (dlc.TitleId - 0x1000) & ~0xFFFUL;
            string dir = Path.Combine(GameDir(appId), "dlc", dlc.TitleId.ToString("X16"), "romfs");
            ExtractSection(dlc.Nca, NcaSectionType.Data, dir, $"DLC {dlc.TitleId:X16}");
        }
    }

    private static (string name, string version) ReadNacp(string path)
    {
        byte[] d = File.ReadAllBytes(path);
        string name = "?";
        for (int i = 0; i < 16; i++)
        {
            string n = CString(d, i * 0x300, 0x200);
            if (n.Length > 0) { name = n; break; }
        }
        return (name, CString(d, 0x3060, 0x10));
    }

    private static string CString(byte[] d, int off, int max)
    {
        if (off + max > d.Length) return "";
        int len = Array.IndexOf(d, (byte)0, off, max);
        return Encoding.UTF8.GetString(d, off, (len < 0 ? off + max : len) - off);
    }

    public void Dispose()
    {
        foreach (var d in Enumerable.Reverse(_keepAlive))
            try { d.Dispose(); } catch { }
        foreach (var f in _tempFiles)
            try { File.Delete(f); } catch { }
        try { if (_tempDir != null) Directory.Delete(_tempDir, true); } catch { }
    }
}

