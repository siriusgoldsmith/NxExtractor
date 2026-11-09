using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using LibHac.Common;
using LibHac.Common.Keys;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.FsSystem;
using LibHac.Spl;
using LibHac.Tools.Es;
using LibHac.Tools.Fs;
using LibHac.Tools.FsSystem;
using LibHac.Tools.FsSystem.NcaUtils;
using ZstdSharp;
using ZstdSharp.Unsafe;
using Path = System.IO.Path;

namespace NxExtractor;

public enum ConvertTarget { Nsp, Nsz }

/// <summary>
/// Convierte XCI/XCZ/NSP/NSZ a NSP (NCAs sin comprimir) o NSZ (NCAs comprimidas como NCZ).
/// La salida es un PFS0 con las NCAs/NCZs y los tickets/certificados que hubiera.
/// </summary>
public sealed class Converter
{
    private readonly KeySet _keys;
    private readonly Action<string> _log;
    private readonly IProgressReport _progress;

    public int Level { get; set; } = 18;

    public Converter(string prodKeys, string titleKeys, Action<string> log, IProgressReport progress)
    {
        _keys = KeySet.CreateDefaultKeySet();
        ExternalKeyReader.ReadKeyFile(_keys, prodKeys,
            string.IsNullOrWhiteSpace(titleKeys) ? null : titleKeys, null, (IProgressReport)null);
        _log = log;
        _progress = progress;
    }

    public void Convert(string input, string outputDir, ConvertTarget target)
    {
        if (Extractor.IsNup(input)) throw new NotSupportedException(L.F("nupNoConvert", Path.GetFileName(input)));
        string ext = target == ConvertTarget.Nsp ? ".nsp" : ".nsz";
        string output = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(input) + ext);
        if (Path.GetFullPath(output).Equals(Path.GetFullPath(input), StringComparison.OrdinalIgnoreCase))
            output = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(input) + $" ({L.T("converted")})" + ext);

        _log($"{Path.GetFileName(input)} -> {Path.GetFileName(output)}");
        string tempDir = Path.Combine(outputDir, ".nxconvert_tmp");
        Directory.CreateDirectory(tempDir);
        var keepAlive = new List<IDisposable>();
        try
        {
            var storage = new LocalStorage(input, FileAccess.Read);
            keepAlive.Add(storage);
            IFileSystem fs;
            string inExt = Path.GetExtension(input).ToLowerInvariant();
            if (inExt is ".xci" or ".xcz")
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
            keepAlive.Add(fs);

            IFile Open(string path)
            {
                using var f = new UniqueRef<IFile>();
                fs.OpenFile(ref f.Ref, path.ToU8Span(), OpenMode.Read).ThrowIfFailure();
                IFile r = f.Release();
                keepAlive.Add(r);
                return r;
            }

            var entries = fs.EnumerateEntries("/", "*").Where(e => e.Type == DirectoryEntryType.File).ToList();

            // Los tickets hacen falta para descifrar NCAs con rights ID al comprimir.
            foreach (var e in entries.Where(e => e.Name.EndsWith(".tik", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    var t = new Ticket(Open(e.FullPath).AsStream());
                    byte[] tk = t.GetTitleKey(_keys);
                    if (tk != null) _keys.ExternalKeySet.Add(new RightsId(t.RightsId), new AccessKey(tk));
                }
                catch (Exception ex) { _log($"  [!] Ticket {e.Name}: {ex.Message}"); }
            }

            // Orden: NCAs/NCZs primero, luego tickets y certificados (como hacen los dumpers).
            var ordered = entries.OrderBy(e => IsContent(e.Name) ? 0 : 1).ThenBy(e => e.Name).ToList();

            var pfsOut = new Pfs0Writer(output);
            foreach (var e in ordered)
            {
                string lower = e.Name.ToLowerInvariant();
                if (target == ConvertTarget.Nsp && lower.EndsWith(".ncz"))
                {
                    _log(L.F("logDecompressing", e.Name));
                    string tmp = Path.Combine(tempDir, Path.ChangeExtension(e.Name, ".nca"));
                    Ncz.Decompress(Open(e.FullPath).AsStream(), tmp, _ => { });
                    using (var s = File.OpenRead(tmp)) pfsOut.AddFile(Path.GetFileName(tmp), s.CopyTo);
                    File.Delete(tmp);
                }
                else if (target == ConvertTarget.Nsz && lower.EndsWith(".nca") && !lower.EndsWith(".cnmt.nca")
                         && TryPrepareCompress(Open(e.FullPath), out var nca, out var sections))
                {
                    _log(L.F("logCompressing", e.Name, Level));
                    var raw = Open(e.FullPath).AsStream();
                    pfsOut.AddFile(Path.ChangeExtension(e.Name, ".ncz"), dst => CompressNca(raw, sections, dst));
                }
                else
                {
                    _log(L.F("logCopying", e.Name));
                    var src = Open(e.FullPath).AsStream();
                    pfsOut.AddFile(e.Name, dst => CopyWithProgress(src, dst, e.Size));
                }
            }
            pfsOut.Finish();
            _log($"  OK ({new FileInfo(output).Length / (1024 * 1024)} MiB)");
        }
        catch
        {
            try { File.Delete(output); } catch { }
            throw;
        }
        finally
        {
            foreach (var d in Enumerable.Reverse(keepAlive)) try { d.Dispose(); } catch { }
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private static bool IsContent(string name) =>
        name.EndsWith(".nca", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".ncz", StringComparison.OrdinalIgnoreCase);

    // ---------- NCA -> NCZ ----------

    private sealed class Sec
    {
        public long Offset, Size, CryptoType;
        public byte[] Key = new byte[16], Counter = new byte[16];
    }

    private bool TryPrepareCompress(IFile file, out Nca nca, out List<Sec> sections)
    {
        sections = new List<Sec>();
        nca = new Nca(_keys, file.AsStorage());
        byte[] key = null;
        for (int i = 0; i < 4; i++)
        {
            if (!nca.Header.IsSectionEnabled(i)) continue;
            var fsh = nca.Header.GetFsHeader(i);
            long type = (long)fsh.EncryptionType;
            // Sólo AES-CTR plano o sin cifrar; BKTR/XTS se dejan como NCA sin comprimir.
            if (type != (long)NcaEncryptionType.None && type != (long)NcaEncryptionType.AesCtr)
                return false;
            if (type == (long)NcaEncryptionType.AesCtr && key == null)
                key = nca.Header.HasRightsId ? nca.GetDecryptedTitleKey() : nca.GetDecryptedKey(2);

            var s = new Sec
            {
                Offset = nca.Header.GetSectionStartOffset(i),
                Size = nca.Header.GetSectionSize(i),
                CryptoType = type
            };
            if (type == (long)NcaEncryptionType.AesCtr)
            {
                s.Key = (byte[])key.Clone();
                BinaryPrimitives.WriteUInt64BigEndian(s.Counter, fsh.Counter);
            }
            sections.Add(s);
        }
        return sections.Count > 0;
    }

    private void CompressNca(Stream raw, List<Sec> sections, Stream dst)
    {
        const long headerSize = 0x4000;
        long total = raw.Length;
        raw.Position = 0;
        var head = new byte[headerSize];
        raw.ReadExactly(head);
        dst.Write(head);

        var bw = new BinaryWriter(dst, Encoding.ASCII, leaveOpen: true);
        bw.Write(Encoding.ASCII.GetBytes("NCZSECTN"));
        bw.Write((long)sections.Count);
        foreach (var s in sections)
        {
            bw.Write(s.Offset); bw.Write(s.Size); bw.Write(s.CryptoType); bw.Write(0L);
            bw.Write(s.Key); bw.Write(s.Counter);
        }
        bw.Flush();

        _progress?.SetTotal(total);
        using var zs = new CompressionStream(dst, Level, 1 << 20, leaveOpen: true);
        zs.SetParameter(ZSTD_cParameter.ZSTD_c_nbWorkers, Math.Max(1, Environment.ProcessorCount - 1));

        const int chunk = 4 << 20;
        var buf = new byte[chunk];
        long pos = headerSize;
        using var aes = Aes.Create();
        while (pos < total)
        {
            int len = (int)Math.Min(chunk, total - pos);
            raw.Position = pos;
            raw.ReadExactly(buf, 0, len);
            foreach (var s in sections.Where(s => s.CryptoType == (long)NcaEncryptionType.AesCtr))
            {
                long a = Math.Max(pos, s.Offset), b = Math.Min(pos + len, s.Offset + s.Size);
                if (b > a) Ncz.CtrXor(aes, s.Key, s.Counter, buf.AsSpan((int)(a - pos), (int)(b - a)), a);
            }
            zs.Write(buf, 0, len);
            pos += len;
            _progress?.Report(pos);
        }
    }

    private void CopyWithProgress(Stream src, Stream dst, long size)
    {
        _progress?.SetTotal(size);
        var buf = new byte[4 << 20];
        long done = 0;
        src.Position = 0;
        int n;
        while ((n = src.Read(buf, 0, buf.Length)) > 0)
        {
            dst.Write(buf, 0, n);
            done += n;
            _progress?.Report(done);
        }
    }
}

/// <summary>Escribe un PFS0 (NSP/NSZ). La cabecera se reserva al principio y se rellena al final.</summary>
public sealed class Pfs0Writer
{
    private readonly FileStream _out;
    private readonly List<(string name, long offset, long size)> _files = new();
    private long _dataStart = -1;

    public Pfs0Writer(string path)
    {
        _out = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20);
    }

    // Los tamaños no se conocen hasta escribir, así que se reserva una cabecera fija
    // y la tabla de nombres se rellena con ceros hasta el inicio de los datos.
    private const int ReservedHeader = 0x8000;

    public void AddFile(string name, Action<Stream> writeData)
    {
        if (_dataStart < 0)
        {
            _dataStart = ReservedHeader;
            _out.Position = _dataStart;
        }
        long start = _out.Position;
        writeData(_out);
        _files.Add((name, start - _dataStart, _out.Position - start));
    }

    public void Finish()
    {
        if (_dataStart < 0) { _dataStart = ReservedHeader; }
        var strings = new MemoryStream();
        var nameOffsets = new List<int>();
        foreach (var f in _files)
        {
            nameOffsets.Add((int)strings.Length);
            var b = Encoding.UTF8.GetBytes(f.name);
            strings.Write(b); strings.WriteByte(0);
        }

        int baseSize = 0x10 + 0x18 * _files.Count;
        int headerSize = (int)ReservedHeader;
        int stringTableSize = headerSize - baseSize;
        if (stringTableSize < strings.Length)
            throw new InvalidOperationException(L.T("tooManyFiles"));
        // El hueco hasta el inicio de datos se absorbe en la tabla de strings (relleno con ceros).
        strings.SetLength(stringTableSize);

        var header = new MemoryStream();
        var bw = new BinaryWriter(header);
        bw.Write(Encoding.ASCII.GetBytes("PFS0"));
        bw.Write(_files.Count);
        bw.Write(stringTableSize);
        bw.Write(0);
        for (int i = 0; i < _files.Count; i++)
        {
            bw.Write(_files[i].offset);
            bw.Write(_files[i].size);
            bw.Write(nameOffsets[i]);
            bw.Write(0);
        }
        bw.Write(strings.ToArray());
        bw.Flush();

        _out.Position = 0;
        header.Position = 0;
        header.CopyTo(_out);
        _out.Flush();
        _out.Dispose();
    }
}
