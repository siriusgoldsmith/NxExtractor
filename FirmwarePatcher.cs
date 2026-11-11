using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using LibHac;
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
using Path = System.IO.Path;

namespace NxExtractor;

/// <summary>
/// Parchea un juego de Switch 1 (NSP/NSZ/XCI) para firmware más viejo, como hacen SAK y NSC_Builder
/// (algoritmo de referencia: NSC_Builder, MIT, https://github.com/julesontheroad/NSC_Builder):
///  1. NCAs con titlekey: la titlekey pasa al key area y se borra el rights ID (ya no hace falta ticket).
///  2. Key area re-cifrado con la key-area-key de la generación destino (las claves de contenido no cambian,
///     así que los datos de las secciones quedan intactos; sólo cambia la cabecera).
///  3. Opcional: RequiredSystemVersion del cnmt bajado al firmware destino.
/// A diferencia de NSC_Builder, también se recalculan hashes y nombres de las NCAs modificadas en el cnmt.
/// Necesita sigpatches en la consola (la firma de la cabecera deja de ser válida).
/// </summary>
public sealed class FirmwarePatcher
{
    /// <summary>Generación de claves -> primer firmware que la usa.</summary>
    public static readonly (int KeyGen, string Firmware)[] Targets =
    {
        (0, "1.0.0"), (1, "2.0.0"), (2, "3.0.0"), (3, "3.0.1"), (4, "4.0.0"), (5, "5.0.0"), (6, "6.0.0"),
        (7, "6.2.0"), (8, "7.0.0"), (9, "8.1.0"), (10, "9.0.0"), (11, "9.1.0"), (12, "12.1.0"), (13, "13.0.0"),
        (14, "14.0.0"), (15, "15.0.0"), (16, "16.0.0"), (17, "17.0.0"), (18, "18.0.0"), (19, "19.0.0"), (20, "20.0.0")
    };

    public int TargetKeyGen { get; set; } = 11;
    public bool PatchRequiredSystemVersion { get; set; } = true;

    private readonly KeySet _keys;
    private readonly Action<string> _log;
    private readonly IProgressReport _progress;

    public FirmwarePatcher(string prodKeys, string titleKeys, Action<string> log, IProgressReport progress)
    {
        _keys = KeySet.CreateDefaultKeySet();
        ExternalKeyReader.ReadKeyFile(_keys, prodKeys,
            string.IsNullOrWhiteSpace(titleKeys) ? null : titleKeys, null, (IProgressReport)null);
        _log = log;
        _progress = progress;
    }

    public static uint EncodeVersion(string fw)
    {
        var p = fw.Split('.').Select(uint.Parse).ToArray();
        return (p[0] << 26) | (p[1] << 20) | (p[2] << 16);
    }

    public static string DecodeVersion(uint v) => $"{v >> 26}.{(v >> 20) & 0x3F}.{(v >> 16) & 0xF}";

    private sealed class Patched
    {
        public string OldName, NewName;
        public byte[] OldId, NewHash;
    }

    public void Patch(string input, string outputDir)
    {
        if (Extractor.IsNup(input)) throw new NotSupportedException(L.F("nupNoConvert", Path.GetFileName(input)));
        string target = Targets.First(t => t.KeyGen == TargetKeyGen).Firmware;
        string output = Path.Combine(outputDir, $"{Path.GetFileNameWithoutExtension(input)} [FW {target}].nsp");
        _log($"{Path.GetFileName(input)} -> {Path.GetFileName(output)}");
        CheckTargetKeys();

        string tempDir = Path.Combine(outputDir, ".nxfirmware_tmp");
        Directory.CreateDirectory(tempDir);
        var keepAlive = new List<IDisposable>();
        try
        {
            var storage = new LocalStorage(input, FileAccess.Read);
            keepAlive.Add(storage);
            IFileSystem fs;
            if (Path.GetExtension(input).ToLowerInvariant() is ".xci" or ".xcz")
            {
                var xci = new Xci(_keys, storage);
                if (!xci.HasPartition(XciPartitionType.Secure)) throw new InvalidDataException(L.T("noSecure"));
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

            // NCZ -> NCA en temporal; el resto se lee directamente del contenedor.
            var ncas = new List<(string name, Func<Stream> open, long size)>();
            foreach (var e in entries.Where(e => e.Name.EndsWith(".nca", StringComparison.OrdinalIgnoreCase) || e.Name.EndsWith(".ncz", StringComparison.OrdinalIgnoreCase)))
            {
                if (e.Name.EndsWith(".ncz", StringComparison.OrdinalIgnoreCase))
                {
                    _log(L.F("logDecompressing", e.Name));
                    string tmp = Path.Combine(tempDir, Path.ChangeExtension(e.Name, ".nca"));
                    Ncz.Decompress(Open(e.FullPath).AsStream(), tmp, _ => { });
                    ncas.Add((Path.GetFileName(tmp), () => File.OpenRead(tmp), new FileInfo(tmp).Length));
                }
                else
                {
                    string path = e.FullPath;
                    ncas.Add((e.Name, () => Open(path).AsStream(), e.Size));
                }
            }

            var writer = new Pfs0Writer(output);
            var patched = new List<Patched>();
            bool rightsRemoved = false;
            _progress?.SetTotal(Math.Max(1, ncas.Sum(n => n.size)));
            long done = 0;

            // Primero el contenido; el Meta va al final porque su cnmt lleva los hashes de los demás.
            var metas = new List<(string name, Func<Stream> open, long size)>();
            foreach (var n in ncas)
            {
                using var src = n.open();
                var header = ReadHeader(src);
                if (header[0x205] == (byte)NcaContentType.Meta) { metas.Add(n); continue; }

                var (newHeader, changed, hadRights) = PatchHeader(header, src, n.name, isMeta: false);
                rightsRemoved |= hadRights;
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                int idx = writer.AddFile(n.name, dst =>
                {
                    var enc = EncryptHeader(newHeader);
                    sha.AppendData(enc);
                    dst.Write(enc);
                    src.Position = 0xC00;
                    var buf = new byte[4 << 20];
                    int r;
                    while ((r = src.Read(buf, 0, buf.Length)) > 0)
                    {
                        sha.AppendData(buf, 0, r);
                        dst.Write(buf, 0, r);
                        done += r;
                        _progress?.Report(done);
                    }
                });
                var hash = sha.GetHashAndReset();
                if (changed)
                {
                    string newName = Convert.ToHexString(hash, 0, 16).ToLowerInvariant() + ".nca";
                    writer.Rename(idx, newName);
                    patched.Add(new Patched { OldName = n.name, NewName = newName, OldId = IdFromName(n.name), NewHash = hash });
                }
            }

            foreach (var m in metas)
            {
                using var src = m.open();
                var raw = new byte[src.Length];
                src.Position = 0;
                src.ReadExactly(raw);
                var header = DecryptHeader(raw.AsSpan(0, 0xC00));
                var (newHeader, _, hadRights) = PatchHeader(header, new MemoryStream(raw), m.name, isMeta: true);
                rightsRemoved |= hadRights;
                PatchMeta(raw, newHeader, patched);
                Array.Copy(EncryptHeader(newHeader), raw, 0xC00);
                var hash = SHA256.HashData(raw);
                string newName = Convert.ToHexString(hash, 0, 16).ToLowerInvariant() + ".cnmt.nca";
                writer.AddFile(newName, dst => dst.Write(raw));
                done += raw.Length;
                _progress?.Report(done);
            }

            // Sin rights ID ya no hacen falta tickets ni certificados; el .cnmt.xml quedaría desactualizado.
            foreach (var e in entries)
            {
                string lower = e.Name.ToLowerInvariant();
                bool keep = lower.EndsWith(".tik") || lower.EndsWith(".cert") ? !rightsRemoved : !lower.EndsWith(".nca") && !lower.EndsWith(".ncz") && !lower.EndsWith(".xml");
                if (!keep) continue;
                var s = Open(e.FullPath).AsStream();
                writer.AddFile(e.Name, dst => s.CopyTo(dst));
            }
            writer.Finish();
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

    private void CheckTargetKeys()
    {
        int idx = TargetKeyGen > 0 ? TargetKeyGen - 1 : 0;
        if (_keys.KeyAreaKeys[idx][0].IsZeros() || _keys.HeaderKey.IsZeros())
            throw new InvalidDataException(L.F("fwMissingKeys", TargetKeyGen));
    }

    private static byte[] IdFromName(string name) => Convert.FromHexString(name[..32]);

    // ---------- Cabecera NCA ----------

    private byte[] ReadHeader(Stream src)
    {
        var raw = new byte[0xC00];
        src.Position = 0;
        src.ReadExactly(raw);
        return DecryptHeader(raw);
    }

    private byte[] DecryptHeader(ReadOnlySpan<byte> raw) => Xts(raw.ToArray(), decrypt: true);
    private byte[] EncryptHeader(byte[] plain) => Xts((byte[])plain.Clone(), decrypt: false);

    /// <summary>
    /// Cambia generación y key area. Devuelve la cabecera nueva, si cambió algo y si tenía rights ID.
    /// </summary>
    private (byte[] header, bool changed, bool hadRights) PatchHeader(byte[] h, Stream src, string name, bool isMeta)
    {
        if (Encoding.ASCII.GetString(h, 0x200, 4) != "NCA3") { _log(L.F("fwSkipNca", name)); return (h, false, false); }

        int curGen = Math.Max(h[0x206], h[0x220]);
        bool hasRights = h.AsSpan(0x230, 16).IndexOfAnyExcept((byte)0) >= 0;
        int newGen = Math.Min(curGen, TargetKeyGen);
        if (newGen == curGen && !hasRights) return (h, false, false);

        // Claves de contenido en claro (LibHac ya sabe sacarlas, con ticket o key area).
        src.Position = 0;
        var nca = new Nca(_keys, src.AsStorage());
        var plainKeys = new byte[4][];
        if (hasRights)
        {
            var tk = nca.GetDecryptedTitleKey() ?? throw new InvalidDataException(L.F("fwNoTitleKey", name));
            for (int i = 0; i < 4; i++) plainKeys[i] = tk;
        }
        else
        {
            for (int i = 0; i < 4; i++) plainKeys[i] = nca.GetDecryptedKey(i);
        }

        var n = (byte[])h.Clone();
        int keyIndex = n[0x207];
        var kaek = _keys.KeyAreaKeys[newGen > 0 ? newGen - 1 : 0][keyIndex].DataRo.ToArray();
        using (var aes = Aes.Create())
        {
            aes.Key = kaek;
            for (int i = 0; i < 4; i++)
                aes.EncryptEcb(plainKeys[i], PaddingMode.None).CopyTo(n, 0x300 + 0x10 * i);
        }

        // Campos de generación como los escribe NSC_Builder (0x206 = antigua, 0x220 = nueva).
        if (newGen >= 3) { n[0x206] = 2; n[0x220] = (byte)newGen; }
        else if (newGen == 2) { n[0x206] = 2; n[0x220] = 0; }
        else { n[0x206] = (byte)newGen; n[0x220] = 0; }
        n.AsSpan(0x230, 16).Clear();

        _log(L.F("fwLogNca", name, curGen, newGen, hasRights ? L.T("fwRightsRemoved") : ""));
        return (n, true, hasRights);
    }

    // ---------- Meta NCA (cnmt) ----------

    private void PatchMeta(byte[] raw, byte[] header, List<Patched> patched)
    {
        const int section = 0;
        long start = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x240)) * 0x200L;
        long end = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x244)) * 0x200L;
        int fsh = 0x400 + 0x200 * section;
        byte encType = header[fsh + 0x04];
        if (header[fsh + 0x03] != 2 || end > raw.Length) throw new InvalidDataException(L.T("fwBadMeta"));

        // Clave CTR de la sección: misma que antes (el key area sólo cambió de envoltorio).
        byte[] ctrKey = null, counter = new byte[16];
        if (encType == 3)
        {
            using var aes = Aes.Create();
            int gen = Math.Max(header[0x206], header[0x220]);
            aes.Key = _keys.KeyAreaKeys[gen > 0 ? gen - 1 : 0][header[0x207]].DataRo.ToArray();
            ctrKey = aes.DecryptEcb(header.AsSpan(0x300 + 0x20, 16), PaddingMode.None);
            BinaryPrimitives.WriteUInt64BigEndian(counter, BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(fsh + 0x140)));
        }
        else if (encType != 1) throw new InvalidDataException(L.T("fwBadMeta"));

        var sec = raw.AsSpan((int)start, (int)(end - start));
        using var ctr = Aes.Create();
        if (ctrKey != null) Ncz.CtrXor(ctr, ctrKey, counter, sec, start);

        // HierarchicalSha256: tabla de hashes + PFS0 con el .cnmt.
        int blockSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(fsh + 0x28));
        long htOff = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(fsh + 0x30));
        long htSize = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(fsh + 0x38));
        long pfsOff = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(fsh + 0x40));
        long pfsSize = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(fsh + 0x48));
        var pfs = sec.Slice((int)pfsOff, (int)pfsSize);
        if (Encoding.ASCII.GetString(pfs[..4]) != "PFS0") throw new InvalidDataException(L.T("fwBadMeta"));
        int count = BinaryPrimitives.ReadInt32LittleEndian(pfs[4..]);
        int strSize = BinaryPrimitives.ReadInt32LittleEndian(pfs[8..]);
        int dataStart = 0x10 + 0x18 * count + strSize;
        Span<byte> cnmt = default;
        for (int i = 0; i < count; i++)
        {
            var e = pfs[(0x10 + 0x18 * i)..];
            string fname = ReadCString(pfs[(0x10 + 0x18 * count + BinaryPrimitives.ReadInt32LittleEndian(e[16..]))..]);
            if (!fname.EndsWith(".cnmt", StringComparison.OrdinalIgnoreCase)) continue;
            cnmt = pfs.Slice(dataStart + (int)BinaryPrimitives.ReadInt64LittleEndian(e), (int)BinaryPrimitives.ReadInt64LittleEndian(e[8..]));
        }
        if (cnmt.IsEmpty) throw new InvalidDataException(L.T("fwBadMeta"));

        byte type = cnmt[0x0C];
        int extSize = BinaryPrimitives.ReadUInt16LittleEndian(cnmt[0x0E..]);
        int contentCount = BinaryPrimitives.ReadUInt16LittleEndian(cnmt[0x10..]);

        // RequiredSystemVersion (aplicación 0x80 y parche 0x81): sólo se baja, nunca se sube.
        if (PatchRequiredSystemVersion && (type == 0x80 || type == 0x81))
        {
            uint rsv = BinaryPrimitives.ReadUInt32LittleEndian(cnmt[0x28..]);
            uint target = EncodeVersion(Targets.First(t => t.KeyGen == TargetKeyGen).Firmware);
            if (rsv > target)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(cnmt[0x28..], target);
                _log(L.F("fwLogRsv", DecodeVersion(rsv), DecodeVersion(target)));
            }
        }

        // Registros de contenido: hash (0x20) + content id (0x10) de cada NCA modificada.
        for (int i = 0; i < contentCount; i++)
        {
            var rec = cnmt.Slice(0x20 + extSize + 0x38 * i, 0x38);
            var id = rec.Slice(0x20, 16).ToArray();
            var p = patched.FirstOrDefault(x => id.AsSpan().SequenceEqual(x.OldId));
            if (p == null) continue;
            p.NewHash.CopyTo(rec);
            p.NewHash.AsSpan(0, 16).CopyTo(rec[0x20..]);
        }

        // Rehacer la tabla de hashes por bloques, el hash maestro y el hash de la cabecera de sección.
        var table = sec.Slice((int)htOff, (int)htSize);
        table.Clear();
        int blocks = (int)((pfsSize + blockSize - 1) / blockSize);
        for (int b = 0; b < blocks; b++)
        {
            var block = pfs.Slice(b * blockSize, (int)Math.Min(blockSize, pfsSize - (long)b * blockSize));
            SHA256.HashData(block).CopyTo(table[(b * 32)..]);
        }
        SHA256.HashData(table).CopyTo(header, fsh + 0x08);
        SHA256.HashData(header.AsSpan(fsh, 0x200)).CopyTo(header, 0x280 + 0x20 * section);

        if (ctrKey != null) Ncz.CtrXor(ctr, ctrKey, counter, sec, start);
    }

    private static string ReadCString(ReadOnlySpan<byte> s)
    {
        int n = s.IndexOf((byte)0);
        return Encoding.ASCII.GetString(n < 0 ? s : s[..n]);
    }

    // ---------- AES-XTS de la cabecera (sectores de 0x200, tweak en big-endian como hace Nintendo) ----------

    private byte[] Xts(byte[] data, bool decrypt)
    {
        var key = _keys.HeaderKey;
        using var k1 = Aes.Create();
        using var k2 = Aes.Create();
        k1.Key = key.DataKey.DataRo.ToArray();
        k2.Key = key.TweakKey.DataRo.ToArray();
        var block = new byte[16];
        for (int s = 0; s < data.Length / 0x200; s++)
        {
            var tweakIn = new byte[16];
            BinaryPrimitives.WriteUInt64BigEndian(tweakIn.AsSpan(8), (ulong)s);
            var t = k2.EncryptEcb(tweakIn, PaddingMode.None);
            for (int b = 0; b < 0x200; b += 16)
            {
                var span = data.AsSpan(s * 0x200 + b, 16);
                for (int i = 0; i < 16; i++) block[i] = (byte)(span[i] ^ t[i]);
                var r = decrypt ? k1.DecryptEcb(block, PaddingMode.None) : k1.EncryptEcb(block, PaddingMode.None);
                for (int i = 0; i < 16; i++) span[i] = (byte)(r[i] ^ t[i]);
                // T *= alfa en GF(2^128), little-endian.
                int carry = t[15] >> 7;
                for (int i = 15; i > 0; i--) t[i] = (byte)((t[i] << 1) | (t[i - 1] >> 7));
                t[0] = (byte)((t[0] << 1) ^ (carry != 0 ? 0x87 : 0));
            }
        }
        return data;
    }
}
