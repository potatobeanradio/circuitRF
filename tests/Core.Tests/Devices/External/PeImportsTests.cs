using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CircuitRF.Core.Devices.External;
using Xunit;

namespace CircuitRF.Core.Tests.Devices.External;

/// <summary>
/// A Windows model imports its host callbacks from a NAMED MODULE, and that name is not written
/// down anywhere — it is a property of the model library, so it is read out of the model library.
/// These tests drive that read against PEs BUILT HERE, never against a committed binary: the repo
/// commits no vendor data, and a hand-built image is the only way to cover the malformed cases at
/// all.
///
/// <para>The rule under test is <b>select the descriptor by OUR OWN ABI symbols</b>. Matching a
/// remembered module name would put kit knowledge back in one string at a time, and would silently
/// serve nothing for a kit that names its host module differently.</para>
/// </summary>
public sealed class PeImportsTests
{
    /// <summary>The names this worker actually supplies — the same list the shipped profile carries,
    /// so a test passing here is a test of the real contract rather than of a local copy of it.</summary>
    private static IReadOnlyList<string> AbiSymbols => DeviceLibraryDiscovery.Profiles[0].HostCallbacks;

    // ── the rule ──────────────────────────────────────────────────────────────

    [Fact]
    public void TheDescriptorIsSelectedByOurAbiSymbols_NotByBeingFirst()
    {
        // Two host modules. The first is the ordinary CRT import every real library carries; the
        // second is the one that matters. Taking "the first descriptor" would answer wrongly.
        byte[] image = TestPe.Build(
        [
            new TestPe.Module("api-ms-win-crt-runtime-l1-1-0.dll", ["malloc", "free", "memcpy"]),
            new TestPe.Module("SomeSimulatorHost.dll", ["send_error_to_scn", "load_elements", "add_nl_iq"]),
        ]);

        Assert.Equal("SomeSimulatorHost.dll", PeImports.ModuleSupplying(image, AbiSymbols));
    }

    [Fact]
    public void AnyOneOfTheAbiSymbolsIsEnoughToIdentifyIt()
    {
        // A model need not import all fourteen — it imports what it uses. One is decisive.
        byte[] image = TestPe.Build(
        [
            new TestPe.Module("KERNEL32.dll", ["GetProcAddress"]),
            new TestPe.Module("host.dll",     ["get_delay_v"]),
        ]);

        Assert.Equal("host.dll", PeImports.ModuleSupplying(image, AbiSymbols));
    }

    [Fact]
    public void ALibraryImportingNoneOfThem_ReturnsNothing_RatherThanGuessing()
    {
        // "Not a library this worker can drive" is a clear answer. A fallback guess at a module
        // name would produce a shim nothing binds to, and a much worse failure much later.
        byte[] image = TestPe.Build(
        [
            new TestPe.Module("KERNEL32.dll", ["GetProcAddress", "LoadLibraryW"]),
            new TestPe.Module("USER32.dll",   ["MessageBoxW"]),
        ]);

        Assert.Null(PeImports.ModuleSupplying(image, AbiSymbols));
    }

    [Fact]
    public void ImportsByOrdinal_AreSkipped_AndDoNotMatchAnything()
    {
        // An ordinal entry carries no name at all. Reading its value as an RVA would walk into
        // arbitrary bytes; here the descriptor must simply not match.
        byte[] image = TestPe.Build(
        [
            new TestPe.Module("ordinals.dll", [], OrdinalCount: 4),
            new TestPe.Module("host.dll",     ["send_info_to_scn"]),
        ]);

        Assert.Equal("host.dll", PeImports.ModuleSupplying(image, AbiSymbols));
    }

    [Fact]
    public void Pe32_IsParsedToo_NotOnlyPe32Plus()
    {
        // The two formats differ only in where the data directories start and how wide a thunk is.
        byte[] image = TestPe.Build([new TestPe.Module("host32.dll", ["load_elements"])], pe32Plus: false);

        Assert.Equal("host32.dll", PeImports.ModuleSupplying(image, AbiSymbols));
    }

    [Fact]
    public void TheDerivedNameIsTheModulesOwnDeclaredName_ByteForByte()
    {
        // End to end: what comes back is exactly what the library declared, including case and
        // extension — this string becomes a filename on the user's machine, so it must not be
        // normalised on the way through.
        const string declared = "CRF_Test_Host-v1.DLL";
        byte[] image = TestPe.Build([new TestPe.Module(declared, [.. AbiSymbols])]);

        Assert.Equal(declared, PeImports.ModuleSupplying(image, AbiSymbols));
    }

    // ── refusal, never a read past the end ────────────────────────────────────

    [Theory]
    [InlineData(0)]      // empty
    [InlineData(2)]      // "MZ" and nothing else
    [InlineData(0x40)]   // a DOS header pointing past the end
    [InlineData(0x90)]   // a PE signature with a truncated COFF header
    [InlineData(0x200)]  // headers present, section data missing
    public void ATruncatedImage_IsRefused_NotReadPastItsEnd(int keep)
    {
        byte[] full = TestPe.Build([new TestPe.Module("host.dll", ["load_elements"])]);
        byte[] cut  = full.Take(Math.Min(keep, full.Length)).ToArray();

        // The contract is "returns null and does not throw" — a malformed image is a normal thing
        // to be handed, not an exceptional one.
        Assert.Null(PeImports.ModuleSupplying(cut, AbiSymbols));
    }

    [Fact]
    public void SomethingThatIsNotAPeAtAll_IsRefused()
    {
        Assert.Null(PeImports.ModuleSupplying(Encoding.ASCII.GetBytes("#!/bin/sh\necho hello\n"), AbiSymbols));
        Assert.Null(PeImports.ModuleSupplying(new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F' }, AbiSymbols));
    }

    [Fact]
    public void AnImportTableRvaOutsideEverySection_IsRefused()
    {
        byte[] image = TestPe.Build([new TestPe.Module("host.dll", ["load_elements"])], importRvaOverride: 0x7F00_0000);
        Assert.Null(PeImports.ModuleSupplying(image, AbiSymbols));
    }

    [Fact]
    public void ALibraryWithNoImportTableAtAll_IsRefused()
    {
        byte[] image = TestPe.Build([], importRvaOverride: 0);
        Assert.Null(PeImports.ModuleSupplying(image, AbiSymbols));
    }

    [Fact]
    public void AnEmptySymbolSet_MatchesNothing()
    {
        // Guard against the degenerate call answering "the first module".
        byte[] image = TestPe.Build([new TestPe.Module("host.dll", ["load_elements"])]);
        Assert.Null(PeImports.ModuleSupplying(image, []));
    }

    // ─────────────────────────────────────────────────────────────────────────
    /// <summary>
    /// A minimal PE writer: enough of the format to carry an import table, and nothing else. Built
    /// here rather than committed so the malformed cases above can exist at all.
    /// </summary>
    private static class TestPe
    {
        internal sealed record Module(string Name, string[] Symbols, int OrdinalCount = 0);

        private const uint SectionRva = 0x1000;
        private const int  SectionRaw = 0x400;
        private const int  PeOffset   = 0x80;

        internal static byte[] Build(
            IReadOnlyList<Module> modules, bool pe32Plus = true, uint? importRvaOverride = null)
        {
            var content = new List<byte>();
            int stride  = pe32Plus ? 8 : 4;

            // Reserve the descriptor array (one per module plus the terminating null descriptor).
            int descCount = modules.Count + 1;
            content.AddRange(new byte[descCount * 20]);

            var placed = new List<(uint ThunkRva, uint NameRva)>();

            foreach (var m in modules)
            {
                // Each imported name: a 2-byte hint then the NUL-terminated name.
                var symbolRvas = new List<uint>();
                foreach (string s in m.Symbols)
                {
                    Align(content, 2);
                    symbolRvas.Add(Rva(content));
                    content.AddRange(new byte[2]);
                    content.AddRange(Encoding.ASCII.GetBytes(s));
                    content.Add(0);
                }

                Align(content, 8);
                uint thunkRva = Rva(content);
                foreach (uint r in symbolRvas) content.AddRange(Thunk(r, pe32Plus));
                for (int i = 0; i < m.OrdinalCount; i++)
                    content.AddRange(OrdinalThunk((uint)(i + 1), pe32Plus));
                content.AddRange(new byte[stride]);                 // terminating null thunk

                uint nameRva = Rva(content);
                content.AddRange(Encoding.ASCII.GetBytes(m.Name));
                content.Add(0);

                placed.Add((thunkRva, nameRva));
            }

            for (int i = 0; i < modules.Count; i++)
            {
                int at = i * 20;
                WriteU32(content, at + 0,  placed[i].ThunkRva);      // OriginalFirstThunk
                WriteU32(content, at + 12, placed[i].NameRva);       // Name
                WriteU32(content, at + 16, placed[i].ThunkRva);      // FirstThunk
            }

            uint importRva  = importRvaOverride ?? SectionRva;
            uint importSize = (uint)(descCount * 20);
            int  optSize    = pe32Plus ? 240 : 224;

            var file = new List<byte>();
            file.AddRange(new byte[PeOffset]);
            file[0] = (byte)'M'; file[1] = (byte)'Z';
            WriteU32(file, 0x3C, PeOffset);

            file.AddRange("PE\0\0"u8.ToArray());

            // COFF header.
            var coff = new byte[20];
            WriteU16(coff, 0, pe32Plus ? (ushort)0x8664 : (ushort)0x014C);   // Machine
            WriteU16(coff, 2, 1);                                            // NumberOfSections
            WriteU16(coff, 16, (ushort)optSize);
            WriteU16(coff, 18, 0x2000);                                      // DLL
            file.AddRange(coff);

            // Optional header: only the magic and DataDirectory[1] are load-bearing here.
            var opt = new byte[optSize];
            WriteU16(opt, 0, pe32Plus ? (ushort)0x20B : (ushort)0x10B);
            int dd = pe32Plus ? 112 : 96;
            WriteU32(opt, dd + 8, importRva);
            WriteU32(opt, dd + 12, importSize);
            file.AddRange(opt);

            // One section holding everything.
            var sec = new byte[40];
            Encoding.ASCII.GetBytes(".idata").CopyTo(sec, 0);
            WriteU32(sec, 8,  (uint)content.Count);       // VirtualSize
            WriteU32(sec, 12, SectionRva);                // VirtualAddress
            WriteU32(sec, 16, (uint)content.Count);       // SizeOfRawData
            WriteU32(sec, 20, SectionRaw);                // PointerToRawData
            file.AddRange(sec);

            while (file.Count < SectionRaw) file.Add(0);
            file.AddRange(content);
            return [.. file];
        }

        private static uint Rva(List<byte> content) => SectionRva + (uint)content.Count;

        private static void Align(List<byte> content, int to)
        {
            while (content.Count % to != 0) content.Add(0);
        }

        private static byte[] Thunk(uint rva, bool pe32Plus)
            => pe32Plus ? BitConverter.GetBytes((ulong)rva) : BitConverter.GetBytes(rva);

        private static byte[] OrdinalThunk(uint ordinal, bool pe32Plus)
            => pe32Plus
                ? BitConverter.GetBytes(0x8000_0000_0000_0000UL | ordinal)
                : BitConverter.GetBytes(0x8000_0000u | ordinal);

        private static void WriteU16(IList<byte> b, int at, ushort v)
        {
            b[at] = (byte)v; b[at + 1] = (byte)(v >> 8);
        }

        private static void WriteU32(IList<byte> b, int at, uint v)
        {
            b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); b[at + 2] = (byte)(v >> 16); b[at + 3] = (byte)(v >> 24);
        }
    }
}
