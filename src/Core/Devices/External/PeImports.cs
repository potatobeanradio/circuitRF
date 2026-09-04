using System.Runtime.InteropServices;

namespace CircuitRF.Core.Devices.External;

/// <summary>
/// Reads which module a Windows library imports a given set of symbols FROM, straight out of its
/// PE import table.
///
/// <para><b>Why this exists.</b> A Linux model leaves its host callbacks <i>undefined</i> and the
/// loader resolves them against whatever process loaded it. A Windows model instead <i>imports</i>
/// them by name from a NAMED MODULE — and an executable's exports are never consulted for a DLL's
/// import-by-name, so a module under that name has to exist at load time. The name is not written
/// down anywhere: it is a property of the model library, so it is read from the model library.</para>
///
/// <para><b>The descriptor is identified by OUR OWN ABI symbols, never by a remembered module
/// name.</b> Matching a name we happen to have seen before would put kit knowledge back into this
/// repository one string at a time, and would silently serve nothing for a kit that names its host
/// module differently. This is the third instance of a principle already load-bearing here — the
/// ELF symbol-table scan instead of a compiled-in name list, and the runtime alias map instead of a
/// compiled-in table — and it is what keeps the code kit-agnostic rather than merely tidy.</para>
///
/// <para><b>There is a second implementation, in C, and that is deliberate.</b> The worker's launcher
/// stub (<c>derive_host_module</c> in <c>tools/senior-worker/senior_worker.c</c>) does the same walk
/// at run time, because it has to: it runs before any managed code in its own process, and staging
/// the shim is its whole job. This one exists so the RULE can be exercised on every platform — the
/// stub's copy can only be run on Windows — and so an importer can say plainly whether a kit's
/// Windows build is one circuitRF's worker can drive. Keep them in step; the shape is small enough
/// that the two are read side by side.</para>
///
/// <para>Nothing here allocates on behalf of a caller or trusts a field it has not bounds-checked:
/// a malformed image is refused, never read past its end.</para>
/// </summary>
public static class PeImports
{
    /// <summary>A descriptor table longer than this is a corrupt image, not a large one.</summary>
    private const int MaxDescriptors = 4096;

    /// <summary>An import list longer than this is likewise not credible.</summary>
    private const int MaxThunks = 65536;

    /// <summary>
    /// The module name <paramref name="image"/> imports any of <paramref name="symbols"/> from, or
    /// null when it imports none of them (or is not a readable PE at all).
    ///
    /// <para>Null is a clear answer, not a failure to look: this library is not one whose host
    /// callbacks we supply. Do not fall back to guessing a name from it.</para>
    /// </summary>
    public static string? ModuleSupplying(ReadOnlySpan<byte> image, IReadOnlyCollection<string> symbols)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        if (symbols.Count == 0) return null;
        if (!TryParseHeaders(image, out var pe)) return null;
        if (pe.ImportRva == 0) return null;
        if (!TryRvaToOffset(pe, image.Length, pe.ImportRva, out int descOffset)) return null;

        for (int d = 0; d < MaxDescriptors; d++)
        {
            int e = descOffset + d * 20;
            if (!TryU32(image, e, out uint origThunk)) return null;
            if (!TryU32(image, e + 12, out uint nameRva)) return null;
            if (!TryU32(image, e + 16, out uint firstThunk)) return null;
            if (origThunk == 0 && nameRva == 0 && firstThunk == 0) break;   // terminating descriptor

            uint thunkRva = origThunk != 0 ? origThunk : firstThunk;
            if (thunkRva == 0 || nameRva == 0) continue;
            if (!TryRvaToOffset(pe, image.Length, thunkRva, out int thunkOffset)) continue;
            if (!ImportsAny(image, pe, thunkOffset, symbols)) continue;

            if (!TryRvaToOffset(pe, image.Length, nameRva, out int nameOffset)) continue;
            string? module = ReadAscii(image, nameOffset);
            if (!string.IsNullOrEmpty(module)) return module;
        }

        return null;
    }

    /// <summary>Convenience overload for a whole file already in memory.</summary>
    public static string? ModuleSupplying(byte[] image, IReadOnlyCollection<string> symbols)
    {
        ArgumentNullException.ThrowIfNull(image);
        return ModuleSupplying(image.AsSpan(), symbols);
    }

    // ── one descriptor's import list ─────────────────────────────────────────

    private static bool ImportsAny(
        ReadOnlySpan<byte> image, PeHeaders pe, int thunkOffset, IReadOnlyCollection<string> symbols)
    {
        int stride = pe.Is64 ? 8 : 4;

        for (int t = 0; t < MaxThunks; t++)
        {
            int at = thunkOffset + t * stride;
            ulong entry;

            if (pe.Is64)
            {
                if (!TryU64(image, at, out entry)) return false;
                if (entry == 0) return false;
                if ((entry & 0x8000_0000_0000_0000UL) != 0) continue;   // imported by ordinal
            }
            else
            {
                if (!TryU32(image, at, out uint e32)) return false;
                if (e32 == 0) return false;
                if ((e32 & 0x8000_0000u) != 0) continue;
                entry = e32;
            }

            if (entry > uint.MaxValue) continue;
            if (!TryRvaToOffset(pe, image.Length, (uint)entry, out int hintOffset)) continue;

            // IMAGE_IMPORT_BY_NAME: a 2-byte hint, then the NUL-terminated name.
            string? name = ReadAscii(image, hintOffset + 2);
            if (name is null) continue;

            foreach (string wanted in symbols)
                if (string.Equals(name, wanted, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    // ── headers ──────────────────────────────────────────────────────────────

    private readonly record struct Section(uint Rva, uint VirtualSize, uint RawOffset, uint RawSize);

    private sealed class PeHeaders
    {
        public bool Is64;
        public uint ImportRva;
        public uint ImportSize;
        public List<Section> Sections { get; } = [];
    }

    private static bool TryParseHeaders(ReadOnlySpan<byte> image, out PeHeaders pe)
    {
        pe = new PeHeaders();

        if (!TryU16(image, 0, out ushort mz) || mz != 0x5A4D) return false;          // "MZ"
        if (!TryU32(image, 0x3C, out uint peOffset)) return false;
        if (peOffset > int.MaxValue - 24) return false;

        int coff = (int)peOffset;
        if (!TryU32(image, coff, out uint sig) || sig != 0x0000_4550) return false;  // "PE\0\0"
        coff += 4;

        if (!TryU16(image, coff + 2, out ushort sectionCount)) return false;
        if (!TryU16(image, coff + 16, out ushort optionalSize)) return false;

        int opt = coff + 20;
        // An unrecognised optional-header magic is refused outright rather than defaulted to one of
        // the two layouts — guessing would read the data directories at the wrong offset.
        if (!TryU16(image, opt, out ushort magic)) return false;
        if (magic is not (0x20B or 0x10B)) return false;
        pe.Is64 = magic == 0x20B;

        // DataDirectory[1] is the import table. Its offset inside the optional header differs by
        // format only because PE32+ widens four of the preceding fields.
        int dd = opt + (pe.Is64 ? 112 : 96);
        if (!TryU32(image, dd + 8, out uint importRva)) return false;
        if (!TryU32(image, dd + 12, out uint importSize)) return false;
        pe.ImportRva = importRva;
        pe.ImportSize = importSize;

        if (sectionCount > 96) return false;                 // not a plausible section count
        int sh = opt + optionalSize;
        for (int i = 0; i < sectionCount; i++)
        {
            int s = sh + i * 40;
            if (!TryU32(image, s + 8,  out uint vsize)) return false;
            if (!TryU32(image, s + 12, out uint rva)) return false;
            if (!TryU32(image, s + 16, out uint rawSize)) return false;
            if (!TryU32(image, s + 20, out uint rawOffset)) return false;
            pe.Sections.Add(new Section(rva, vsize != 0 ? vsize : rawSize, rawOffset, rawSize));
        }

        return pe.Sections.Count > 0;
    }

    /// <summary>
    /// A file on disk is NOT RVA-addressable the way a loaded image is — every RVA has to go
    /// through the section table. An RVA landing in a section's virtual tail (past its raw size) is
    /// refused rather than mapped to the wrong bytes.
    /// </summary>
    private static bool TryRvaToOffset(PeHeaders pe, int length, uint rva, out int offset)
    {
        foreach (var s in pe.Sections)
        {
            if (rva < s.Rva || rva >= s.Rva + s.VirtualSize) continue;
            uint delta = rva - s.Rva;
            if (delta >= s.RawSize) { offset = 0; return false; }
            ulong o = (ulong)s.RawOffset + delta;
            if (o >= (ulong)length) { offset = 0; return false; }
            offset = (int)o;
            return true;
        }

        offset = 0;
        return false;
    }

    // ── which machine a PE was built for ─────────────────────────────────────

    /// <summary>
    /// The processor architecture <paramref name="image"/> was built for, or null when it is not a
    /// readable PE at all (a Mach-O, an ELF, a text file, a truncated download).
    ///
    /// <para><b>Why a caller wants this.</b> A worker that loads a library into its own process must
    /// be built for the same architecture, because a process holds exactly one instruction set. On
    /// Windows that is not a formality: an arm64 machine routinely runs a translated x64 toolchain,
    /// so the model a user just compiled there is x64 while the machine is not. Reading the answer
    /// out of the file is the only way to be right in both directions; assuming either the machine's
    /// architecture or circuitRF's own produces a load that fails with the operating system's own
    /// wording about a bad image, which names nothing a user can act on.</para>
    ///
    /// <para>Only the COFF header is consulted, so a prefix of the file is enough — see
    /// <see cref="HeaderPrefixBytes"/>. Null for a machine value this build has no name for, which
    /// is a clear answer and not a failure to look: the caller then has no architecture to match on
    /// and should fall back rather than refuse.</para>
    /// </summary>
    public static Architecture? MachineOf(ReadOnlySpan<byte> image)
    {
        if (!TryU16(image, 0, out ushort mz) || mz != 0x5A4D) return null;           // "MZ"
        if (!TryU32(image, 0x3C, out uint peOffset)) return null;
        if (peOffset > int.MaxValue - 8) return null;

        int coff = (int)peOffset;
        if (!TryU32(image, coff, out uint sig) || sig != 0x0000_4550) return null;   // "PE\0\0"
        if (!TryU16(image, coff + 4, out ushort machine)) return null;

        return machine switch
        {
            0x8664 => Architecture.X64,
            0xAA64 => Architecture.Arm64,
            0x014C => Architecture.X86,
            0x01C4 => Architecture.Arm,
            _      => null,
        };
    }

    /// <summary>
    /// How much of a file <see cref="MachineOf"/> needs. The COFF header sits at the offset written
    /// at 0x3C, and a linker puts that within the first page; a value past this is refused rather
    /// than chased, so identifying an architecture never reads a hundred-megabyte model library.
    /// </summary>
    public const int HeaderPrefixBytes = 4096;

    // ── bounds-checked primitives ────────────────────────────────────────────

    private static bool TryU16(ReadOnlySpan<byte> b, int off, out ushort value)
    {
        if (off < 0 || off + 2 > b.Length) { value = 0; return false; }
        value = (ushort)(b[off] | (b[off + 1] << 8));
        return true;
    }

    private static bool TryU32(ReadOnlySpan<byte> b, int off, out uint value)
    {
        if (off < 0 || off + 4 > b.Length) { value = 0; return false; }
        value = (uint)(b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24));
        return true;
    }

    private static bool TryU64(ReadOnlySpan<byte> b, int off, out ulong value)
    {
        if (off < 0 || off + 8 > b.Length) { value = 0; return false; }
        if (!TryU32(b, off, out uint lo) || !TryU32(b, off + 4, out uint hi)) { value = 0; return false; }
        value = lo | ((ulong)hi << 32);
        return true;
    }

    /// <summary>A NUL-terminated ASCII string that must terminate inside the image.</summary>
    private static string? ReadAscii(ReadOnlySpan<byte> b, int off)
    {
        if (off < 0 || off >= b.Length) return null;

        int end = off;
        while (end < b.Length && b[end] != 0) end++;
        if (end >= b.Length) return null;                    // ran off the end: not a valid string
        if (end == off) return null;

        var chars = new char[end - off];
        for (int i = 0; i < chars.Length; i++)
        {
            byte c = b[off + i];
            if (c is < 0x20 or > 0x7E) return null;          // a module/symbol name is printable ASCII
            chars[i] = (char)c;
        }
        return new string(chars);
    }
}
