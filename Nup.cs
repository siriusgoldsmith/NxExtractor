using System.Buffers.Binary;
using System.Text;
using ZstdSharp;

namespace NxExtractor;

/// <summary>
/// Lector de paquetes .nup (dumps ya descifrados de Switch 2: uno o más títulos, cada uno con un árbol exefs/ y romfs/).
/// Formato (little-endian):
///   0x000  "NUP\x01"
///   0x080  "TOC\x01", u32 títulos, u64 dirs, u64 archivos, u64 offset de strings (desde 0x80), u64 tamaño de strings
///   0x0b0  títulos, 0x40 c/u: u64 title id, u32 dir raíz, u32 dir exefs, u32 dir romfs, u32 versión, ...
///   luego  dirs, 24 bytes c/u: u32 offset nombre, u32 largo nombre, u32 primer hijo, u32 nº hijos, u64 0
///   luego  archivos, 80 bytes c/u: u32 offset nombre, u32 largo nombre, u64 tamaño, u64 offset de datos, u64 tamaño, hash[32], 0[16]
///   luego  lista de hijos, u32 c/u: índice &lt; dirs es un dir, si no (índice - dirs) es un archivo
///   luego  los nombres (sin terminador)
/// Los datos de un archivo están tal cual, o (dumpers nuevos) en un frame zstd cuyo contenido es el archivo.
/// </summary>
public sealed class NupPackage : IDisposable
{
    public sealed record Title(int Index, ulong Id, uint Root, uint ExeFs, uint RomFs, uint Version);
    public sealed record FileRec(uint NameOff, uint NameLen, long Size, long Offset);

    private static readonly byte[] ZstdMagic = { 0x28, 0xB5, 0x2F, 0xFD };

    private readonly FileStream _f;
    private readonly byte[] _strings;
    private readonly (uint nameOff, uint nameLen, uint first, uint count)[] _dirs;
    private readonly FileRec[] _files;
    private readonly uint[] _kids;
    private readonly long _dirCount;

    public List<Title> Titles { get; } = new();
    public long FileCount => _files.Length;

    public NupPackage(string path)
    {
        _f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        var head = new byte[0xB0];
        _f.ReadExactly(head);
        if (Encoding.ASCII.GetString(head, 0, 4) != "NUP\x01" || Encoding.ASCII.GetString(head, 0x80, 4) != "TOC\x01")
            throw new InvalidDataException(L.T("badNup"));

        var toc = head.AsSpan(0x84);
        uint titleCount = BinaryPrimitives.ReadUInt32LittleEndian(toc);
        long nd = (long)BinaryPrimitives.ReadUInt64LittleEndian(toc[4..]);
        long nf = (long)BinaryPrimitives.ReadUInt64LittleEndian(toc[12..]);
        long stringsAt = 0x80 + (long)BinaryPrimitives.ReadUInt64LittleEndian(toc[20..]);
        long stringsSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(toc[28..]);

        long dirsAt = 0xB0 + 0x40L * titleCount;
        long filesAt = dirsAt + 24 * nd;
        long kidsAt = filesAt + 80 * nf;
        if (titleCount == 0 || kidsAt > stringsAt || stringsAt + stringsSize > _f.Length || stringsAt + stringsSize > int.MaxValue)
            throw new InvalidDataException(L.T("badNup"));

        var meta = new byte[stringsAt + stringsSize];
        _f.Position = 0;
        _f.ReadExactly(meta);
        var m = meta.AsSpan();

        for (int i = 0; i < titleCount; i++)
        {
            var t = m[(0xB0 + 0x40 * i)..];
            Titles.Add(new Title(i,
                BinaryPrimitives.ReadUInt64LittleEndian(t),
                BinaryPrimitives.ReadUInt32LittleEndian(t[8..]),
                BinaryPrimitives.ReadUInt32LittleEndian(t[12..]),
                BinaryPrimitives.ReadUInt32LittleEndian(t[16..]),
                BinaryPrimitives.ReadUInt32LittleEndian(t[20..])));
        }

        _dirCount = nd;
        _dirs = new (uint, uint, uint, uint)[nd];
        for (long i = 0; i < nd; i++)
        {
            var d = m[(int)(dirsAt + 24 * i)..];
            _dirs[i] = (BinaryPrimitives.ReadUInt32LittleEndian(d), BinaryPrimitives.ReadUInt32LittleEndian(d[4..]),
                BinaryPrimitives.ReadUInt32LittleEndian(d[8..]), BinaryPrimitives.ReadUInt32LittleEndian(d[12..]));
        }

        _files = new FileRec[nf];
        for (long i = 0; i < nf; i++)
        {
            var r = m[(int)(filesAt + 80 * i)..];
            _files[i] = new FileRec(BinaryPrimitives.ReadUInt32LittleEndian(r), BinaryPrimitives.ReadUInt32LittleEndian(r[4..]),
                (long)BinaryPrimitives.ReadUInt64LittleEndian(r[8..]), (long)BinaryPrimitives.ReadUInt64LittleEndian(r[16..]));
        }

        long nk = (stringsAt - kidsAt) / 4;
        _kids = new uint[nk];
        for (long i = 0; i < nk; i++) _kids[i] = BinaryPrimitives.ReadUInt32LittleEndian(m[(int)(kidsAt + 4 * i)..]);

        _strings = meta[(int)stringsAt..];
    }

    private string Name(uint off, uint len) => Encoding.UTF8.GetString(_strings, (int)off, (int)len);

    /// <summary>(ruta relativa con '/', registro) de cada archivo bajo el dir indicado.</summary>
    public IEnumerable<(string path, FileRec rec)> Walk(uint dir, string prefix = "", int depth = 0)
    {
        if (dir >= _dirCount || depth > 64) throw new InvalidDataException(L.T("badNup"));
        var (_, _, first, count) = _dirs[dir];
        for (long k = first; k < (long)first + count; k++)
        {
            uint node = _kids[k];
            if (node < _dirCount)
            {
                var d = _dirs[node];
                foreach (var x in Walk(node, prefix + Name(d.nameOff, d.nameLen) + "/", depth + 1)) yield return x;
            }
            else
            {
                var r = _files[node - _dirCount];
                yield return (prefix + Name(r.NameOff, r.NameLen), r);
            }
        }
    }

    // ---------- Lectura de datos ----------

    private Stream Slice(long offset, long length)
    {
        _f.Position = offset;
        return new BoundedStream(_f, offset, Math.Min(length, _f.Length - offset));
    }

    private static long? FrameContentSize(ReadOnlySpan<byte> head)
    {
        if (head.Length < 18 || !head[..4].SequenceEqual(ZstdMagic)) return null;
        int fhd = head[4];
        int single = (fhd >> 5) & 1;
        int at = 5 + (single == 1 ? 0 : 1) + new[] { 0, 1, 2, 4 }[fhd & 3];
        int n = new[] { single == 1 ? 1 : 0, 2, 4, 8 }[fhd >> 6];
        if (n == 0) return null;
        ulong size = 0;
        for (int i = 0; i < n; i++) size |= (ulong)head[at + i] << (8 * i);
        return n == 2 ? (long)size + 256 : (long)size;
    }

    /// <summary>Descomprime el frame en offset hasta 'limit' bytes de salida; devuelve cuántos salieron (o -1 si no es zstd válido).</summary>
    private long TryDecompress(FileRec r, long limit, Stream sink, byte[] firstBytes = null, long? inputLen = null)
    {
        try
        {
            using var zs = new DecompressionStream(Slice(r.Offset, inputLen ?? r.Size + (1 << 16)), leaveOpen: false);
            var buf = new byte[1 << 20];
            long got = 0;
            while (got < limit)
            {
                int n = zs.Read(buf, 0, (int)Math.Min(buf.Length, limit - got));
                if (n == 0) break;
                if (firstBytes != null && got == 0) Array.Copy(buf, firstBytes, Math.Min(n, firstBytes.Length));
                sink?.Write(buf, 0, n);
                got += n;
            }
            return got;
        }
        catch (ZstdException) { return -1; }
        catch (IOException) { return -1; }
    }

    /// <summary>Si el paquete guarda este archivo comprimido (frame zstd cuyo contenido es el archivo).</summary>
    private bool IsPacked(FileRec r, string name)
    {
        var head = new byte[18];
        _f.Position = r.Offset;
        if (_f.Read(head, 0, 18) < 18 || !head.AsSpan(0, 4).SequenceEqual(ZstdMagic)) return false;
        long? size = FrameContentSize(head);

        // Los .zs ya son zstd dentro del juego: sólo están "empaquetados" si lo de dentro vuelve a ser zstd.
        if (name.EndsWith(".zs", StringComparison.OrdinalIgnoreCase) && (size == null || size == r.Size))
        {
            if (size != r.Size) return false;
            var first = new byte[4];
            return TryDecompress(r, 4, null, first) == 4 && first.AsSpan().SequenceEqual(ZstdMagic);
        }
        if (size != null) return size == r.Size;
        // Sin tamaño declarado: es el archivo si, leyendo sólo su tamaño de entrada, sale exactamente ese tamaño.
        return TryDecompress(r, r.Size + 1, null, null, r.Size) == r.Size;
    }

    public void CopyTo(FileRec r, string name, Stream dst, Action<long> progress)
    {
        if (IsPacked(r, name))
        {
            long got = TryDecompress(r, r.Size, new ProgressStream(dst, progress));
            if (got != r.Size) throw new InvalidDataException(L.F("nupBadFrame", name, got, r.Size));
            return;
        }
        using var src = Slice(r.Offset, r.Size);
        if (src.Length < r.Size) throw new EndOfStreamException(name);
        src.CopyTo(new ProgressStream(dst, progress), 4 << 20);
    }

    public void Dispose() => _f.Dispose();

    /// <summary>Vista de sólo lectura de un rango del archivo.</summary>
    private sealed class BoundedStream : Stream
    {
        private readonly Stream _s;
        private readonly long _start, _len;
        private long _pos;
        public BoundedStream(Stream s, long start, long len) { _s = s; _start = start; _len = Math.Max(0, len); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= _len) return 0;
            _s.Position = _start + _pos;
            int n = _s.Read(buffer, offset, (int)Math.Min(count, _len - _pos));
            _pos += n;
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _len;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin so) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    private sealed class ProgressStream : Stream
    {
        private readonly Stream _s;
        private readonly Action<long> _p;
        public ProgressStream(Stream s, Action<long> p) { _s = s; _p = p; }
        public override void Write(byte[] b, int o, int c) { _s.Write(b, o, c); _p?.Invoke(c); }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _s.Flush();
        public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin so) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
    }
}
