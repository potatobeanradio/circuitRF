using System;
using System.IO;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist.Spice;
using Xunit;

namespace CircuitRF.Core.Tests.Netlist;

/// <summary>
/// Reading a netlist written in the SPICE dialect into ordinary circuitRF cells.
///
/// <para><b>Every fixture here is synthetic.</b> This is a format reader, and the repository commits
/// no third-party kit data — so nothing in this file names a supplier, a product or a part, and the
/// files are written to exercise a rule rather than to resemble any particular kit.</para>
/// </summary>
public sealed class SpiceNetlistReaderTests
{
    private static SpiceNetlistResult Read(string text) => SpiceNetlistReader.Read(text);

    private static Cell Cell(SpiceNetlistResult r, string name)
        => Assert.Single(r.Library.Cells, c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string Override(Instance i, string name)
        => i.Overrides.Single(o => o.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Expression;

    // ── S1 — subcircuits become ordinary cells ────────────────────────────────

    [Fact]
    public void S1_ASubcircuitBecomesACellWithPortsAndParameters()
    {
        var r = Read("""
            .subckt divider in out gnd  rtop=1k rbot=2k
            R1 in  out rtop
            R2 out gnd rbot
            .ends divider
            """);

        var cell = Cell(r, "divider");
        Assert.Equal(["in", "out", "gnd"], cell.Ports);
        Assert.Equal(["rtop", "rbot"], cell.Parameters.Select(p => p.Name));
        Assert.Equal("1000", cell.Parameters[0].DefaultExpression);

        var r1 = cell.Instances[0];
        Assert.Equal("R1", r1.InstanceName);
        Assert.Equal("R", r1.Reference);
        Assert.Equal(["in", "out"], r1.NetBindings);
        Assert.Equal("rtop", Override(r1, "R"));

        Assert.Empty(r.IncompleteCells);
    }

    /// <summary>The dialect permits nesting, and a reader that does not track it attributes every inner instance to the outer cell.</summary>
    [Fact]
    public void S2_SubcircuitsNest()
    {
        var r = Read("""
            .subckt outer a b
            .subckt inner c d
            R9 c d 1
            .ends inner
            X1 a b inner
            .ends outer
            """);

        Assert.Equal(2, r.Library.Cells.Count);
        Assert.Equal("R9", Assert.Single(Cell(r, "inner").Instances).InstanceName);

        var x1 = Assert.Single(Cell(r, "outer").Instances);
        Assert.Equal("inner", x1.Reference);
        Assert.Equal(["a", "b"], x1.NetBindings);
    }

    // ── S3 — line assembly ────────────────────────────────────────────────────

    /// <summary>
    /// Continuation is a leading <c>+</c> on the FOLLOWING line — the opposite way round from a
    /// trailing marker — and a full-line comment between a line and its continuation does not break
    /// it, because that is where people put them.
    /// </summary>
    [Fact]
    public void S3_ContinuationAndComments()
    {
        var r = Read("""
            * a whole-line comment
            .subckt part a b
            R1 a b 1k  $ a trailing comment
            X1 a b sub
            * a comment between a line and its continuation
            + w=2u
            + l=4u   ; a trailing comment in the other spelling
            .ends
            """);

        var cell = Cell(r, "part");
        var x1 = cell.Instances[1];

        Assert.Equal("2E-06", Override(x1, "w"));
        Assert.Equal("4E-06", Override(x1, "l"));
        Assert.Equal("1000",  Override(cell.Instances[0], "R"));
        Assert.Empty(r.Notes);
    }

    /// <summary>
    /// A note is reported at the line the logical line STARTED on, which is where a reader of the
    /// file would look for it — not at the last continuation.
    /// </summary>
    [Fact]
    public void S4_ANoteNamesTheLineTheStatementStartedOn()
    {
        var r = Read("""
            .subckt part a b
            R1 a b 1k
            .nonsense something
            + continued
            .ends
            """);

        var note = Assert.Single(r.Notes);
        Assert.Equal(3, note.Line);
        Assert.Contains("nonsense", note.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── S5/S6 — what "incomplete" means ───────────────────────────────────────

    /// <summary>
    /// A line of the DEFINITION that could not be read makes the cell incomplete: what is left is a
    /// plausible-looking different circuit, and building it would be worse than refusing.
    /// </summary>
    [Fact]
    public void S5_AnUnreadableLineMarksTheCellIncomplete()
    {
        var r = Read("""
            .subckt part a b
            R1 a b 1k
            Z9 a b something
            .ends
            """);

        Assert.Equal("part", Assert.Single(r.IncompleteCells));
        Assert.Contains(r.Notes, n => n.Line == 3);

        // The rest of the cell is still read — an incomplete cell is a cell that was read as far as
        // it could be, not one that was abandoned.
        Assert.Single(Cell(r, "part").Instances);
    }

    /// <summary>
    /// …but a simulator directive does NOT. The circuit is still exactly the one the file wrote, and
    /// reporting the working case as broken is the failure this distinction exists to prevent.
    /// </summary>
    [Fact]
    public void S6_ASimulatorDirectiveIsSkippedWithoutMarkingTheCellIncomplete()
    {
        var r = Read("""
            .title a deck
            .options savecurrents
            .subckt part a b
            R1 a b 1k
            .ic v(a)=0
            .ends
            .ac dec 10 1 1e9
            .print ac v(a)
            .end
            """);

        Assert.Empty(r.IncompleteCells);
        Assert.Equal(6, r.Notes.Count);
        Assert.All(r.Notes, n => Assert.Contains("simulator directive", n.Message, StringComparison.Ordinal));
    }

    // ── S7 — elements ─────────────────────────────────────────────────────────

    [Fact]
    public void S7_TheElementsThatAreRead()
    {
        var r = Read("""
            .subckt part a b c d
            R1 a b 1k
            C1 a b 2p
            L1 a b 3n
            D1 a b dmod
            Q1 a b c qmod
            Q2 a b c d qmod
            M1 a b c d nmos w=1u l=0.13u
            X1 a b sub
            .ends
            """);

        var i = Cell(r, "part").Instances;
        Assert.Empty(r.IncompleteCells);

        Assert.Equal("R",    i[0].Reference);
        Assert.Equal("C",    i[1].Reference);
        Assert.Equal("L",    i[2].Reference);
        Assert.Equal("dmod", i[3].Reference);
        Assert.Equal("qmod", i[4].Reference);
        Assert.Equal("qmod", i[5].Reference);
        Assert.Equal("nmos", i[6].Reference);
        Assert.Equal("sub",  i[7].Reference);

        Assert.Equal("2E-12", Override(i[1], "C"));
        Assert.Equal("3E-09", Override(i[2], "L"));

        // The name of what implements a device is taken from the END of the bare words, which is
        // what lets one rule cover a three- and a four-terminal transistor without guessing.
        Assert.Equal(["a", "b", "c"],      i[4].NetBindings);
        Assert.Equal(["a", "b", "c", "d"], i[5].NetBindings);
        Assert.Equal("1E-06", Override(i[6], "w"));
    }

    /// <summary>
    /// A passive's third word is its VALUE when it reads as one and the name of a model card when it
    /// does not. Both spellings are ordinary, and nothing but the word itself distinguishes them.
    /// </summary>
    [Fact]
    public void S8_APassiveNamingAModelIsNotReadAsAValue()
    {
        var r = Read("""
            .subckt part a b
            R1 a b 1k
            R2 a b rhigh w=1u
            .ends
            """);

        var i = Cell(r, "part").Instances;

        Assert.Equal("R", i[0].Reference);
        Assert.Equal("1000", Override(i[0], "R"));

        Assert.Equal("rhigh", i[1].Reference);
        Assert.DoesNotContain(i[1].Overrides, o => o.Name == "R");
        Assert.Equal("1E-06", Override(i[1], "w"));
    }

    /// <summary>The multiplier, area and temperature rise are carried faithfully, positionally as well as by name.</summary>
    [Fact]
    public void S9_MultiplierAreaAndTemperatureRiseAreCarried()
    {
        var r = Read("""
            .subckt part a b c
            D1 a b dmod 4
            D2 a b dmod area=2 m=8 dtemp=15
            Q1 a b c qmod 3
            .ends
            """);

        var i = Cell(r, "part").Instances;

        Assert.Equal("4", Override(i[0], "area"));
        Assert.Equal("dmod", i[0].Reference);

        Assert.Equal("2",  Override(i[1], "area"));
        Assert.Equal("8",  Override(i[1], "m"));
        Assert.Equal("15", Override(i[1], "dtemp"));

        Assert.Equal("3", Override(i[2], "area"));
        Assert.Equal(["a", "b", "c"], i[2].NetBindings);
    }

    /// <summary>
    /// The multiplier's spelling is normalised at this boundary, and it is a genuine trap. This
    /// dialect is case-insensitive, so <c>M=4</c> on an instance means four copies in parallel;
    /// circuitRF compares parameter names ordinally and reserves upper-case <c>M</c> for the
    /// junction diode's grading coefficient — on a component that can carry both. Passed through
    /// verbatim, a diode written <c>M=4</c> would get a grading coefficient of 4, no multiplier at
    /// all, and would simulate.
    /// </summary>
    [Fact]
    public void S9b_TheMultiplierIsSpelledTheWayCircuitRfSpellsIt()
    {
        var r = Read("""
            .subckt part a b
            D1 a b dmod M=4
            D2 a b dmod m=8
            .ends
            """);

        var i = Cell(r, "part").Instances;

        Assert.Equal("4", Override(i[0], "m"));
        Assert.DoesNotContain(i[0].Overrides, o => o.Name == "M");
        Assert.Equal("8", Override(i[1], "m"));

        // A model card is NOT normalised: there, M is the grading coefficient and means exactly
        // what circuitRF means by it.
        var card = Read(".model dmod d (m=0.4)");
        Assert.Equal("0.4", Assert.Single(card.ModelCards).Parameters["m"]);
    }

    [Fact]
    public void S10_AnElementWithTooFewNetsIsRefusedRatherThanPadded()
    {
        var r = Read("""
            .subckt part a b
            M1 a b nmos
            .ends
            """);

        Assert.Equal("part", Assert.Single(r.IncompleteCells));
        Assert.Empty(Cell(r, "part").Instances);
        Assert.Contains(r.Notes, n => n.Message.Contains("net", StringComparison.Ordinal));
    }

    // ── S11 — parameters and functions ────────────────────────────────────────

    [Fact]
    public void S11_ParametersLandInTheScopeThatDeclaredThem()
    {
        var r = Read("""
            .param vdd=1.2 tox=2.5n
            .subckt part a b
            .param wmin={tox*4}
            R1 a b {wmin*1k}
            .ends
            """);

        Assert.Equal(["vdd", "tox"], r.Variables.Select(v => v.Name));
        Assert.Equal("1.2", r.Variables[0].Expression);
        Assert.Equal("2.5E-09", r.Variables[1].Expression);

        var cell = Cell(r, "part");
        Assert.Equal("wmin", Assert.Single(cell.Variables).Name);
        Assert.Equal("tox*4", cell.Variables[0].Expression);
        Assert.Equal("wmin*1000", Override(cell.Instances[0], "R"));
    }

    [Fact]
    public void S12_FunctionsAreDeclarations_NotBindings()
    {
        var r = Read("""
            .func square(x) = {x*x}
            .param double(y) = {y*2}
            .subckt part a b
            R1 a b {square(3)}
            .ends
            """);

        Assert.Equal(["square", "double"], r.Functions.Select(f => f.Name));
        Assert.Empty(r.Variables);              // neither is a variable named "square(x)"
        Assert.Equal("x*x", r.Functions[0].Body);
    }

    /// <summary>
    /// <c>name =value</c> — the <c>=</c> glued to the VALUE rather than to the name. Two words, one
    /// binding. This is how a kit writes every one of its statistical parameters, 75 of them,
    /// and without it both halves fall through as bare words: the file reads cleanly, reports
    /// nothing, and declares none of them.
    /// </summary>
    [Fact]
    public void S11b_TheEqualsSignMayBeGluedToTheValueInstead()
    {
        var r = Read("""
            .param a =1.5  b= 2.5  c = 3.5  d=4.5
            .params e =5.5
            """);

        Assert.Equal(["a", "b", "c", "d", "e"], r.Variables.Select(v => v.Name));
        Assert.Equal("1.5", r.Variables[0].Expression);
        Assert.Equal("5.5", r.Variables[4].Expression);
        Assert.Empty(r.Notes);
    }

    /// <summary>
    /// How a device backed by a COMPILED model is instantiated. Its terminal count is the model's,
    /// not the letter's, which is exactly the case the take-the-name-from-the-END rule was written
    /// for. Observed on a kit as a four-terminal resistor with a thermal node.
    /// </summary>
    [Fact]
    public void S7b_ACompiledModelInstanceIsRead()
    {
        var r = Read("""
            .subckt part a b bn dt
            NR1 a bn b dt rmod_kind L=1u W=2u m=1
            .ends
            """);

        var n = Assert.Single(Cell(r, "part").Instances);

        Assert.Equal("rmod_kind", n.Reference);
        Assert.Equal(["a", "bn", "b", "dt"], n.NetBindings);
        Assert.Equal("1E-06", Override(n, "L"));
        Assert.Empty(r.IncompleteCells);
    }

    // ── S13 — model cards ─────────────────────────────────────────────────────

    /// <summary>
    /// The parameter block's bracket is routinely glued to the type. Reading the card off the word
    /// list — where a bracketed run is deliberately kept whole — spells the type <c>nmos(level</c>
    /// on every such card.
    /// </summary>
    [Fact]
    public void S13_ModelCardsAreReadWithOrWithoutBrackets()
    {
        var r = Read("""
            .model dmod d (is=1e-14 n=1.05 rs=0.1)
            .model nfet nmos(level=54 vth0=0.4)
            .model rhigh r rsh=1k tc1=1.2e-3
            """);

        Assert.Equal(3, r.ModelCards.Count);

        Assert.Equal("d", r.ModelCards[0].ModelType);
        Assert.Equal("1E-14", r.ModelCards[0].Parameters["is"]);
        Assert.Equal("0.1",   r.ModelCards[0].Parameters["rs"]);

        Assert.Equal("nmos", r.ModelCards[1].ModelType);
        Assert.Equal("54",   r.ModelCards[1].Parameters["level"]);

        Assert.Equal("r",    r.ModelCards[2].ModelType);
        Assert.Equal("1000", r.ModelCards[2].Parameters["rsh"]);
    }

    /// <summary>Two cards under one name are not necessarily the same parameter set, so the collision is reported.</summary>
    [Fact]
    public void S14_ARedefinedModelIsReported()
    {
        var r = Read("""
            .model dmod d is=1e-14
            .model dmod d is=2e-14
            """);

        Assert.Equal("2E-14", Assert.Single(r.ModelCards).Parameters["is"]);
        Assert.Contains(r.Notes, n => n.Message.Contains("already defined", StringComparison.Ordinal));
    }

    // ── S15 — conditionals ────────────────────────────────────────────────────

    [Fact]
    public void S15_AConditionalTakesExactlyOneBranch()
    {
        var r = Read("""
            .param corner=2
            .subckt part a b
            .if (corner == 1)
            R1 a b 1k
            .elseif (corner == 2)
            R1 a b 2k
            .else
            R1 a b 3k
            .endif
            .ends
            """);

        var i = Assert.Single(Cell(r, "part").Instances);
        Assert.Equal("2000", Override(i, "R"));
        Assert.Empty(r.IncompleteCells);
    }

    [Fact]
    public void S16_TheElseBranchIsTakenWhenNoneOfTheOthersAre()
    {
        var r = Read("""
            .param corner=9
            .subckt part a b
            .if (corner == 1)
            R1 a b 1k
            .else
            R1 a b 3k
            .endif
            .ends
            """);

        Assert.Equal("3000", Override(Assert.Single(Cell(r, "part").Instances), "R"));
    }

    /// <summary>
    /// A condition that cannot be evaluated takes NO branch, and the cell is marked incomplete.
    ///
    /// <para>Reading it as false would silently delete the guarded block and leave a cell that builds
    /// and is wrong. The outcome here is "circuitRF could not read this", which is true, rather than
    /// "the file said no", which is a claim nothing in the reader can make.</para>
    /// </summary>
    [Fact]
    public void S17_AnUnevaluableConditionTakesNoBranchAndIsReported()
    {
        var r = Read("""
            .subckt part a b
            .if (whatever == 1)
            R1 a b 1k
            .else
            R1 a b 3k
            .endif
            .ends
            """);

        Assert.Empty(Cell(r, "part").Instances);
        Assert.Equal("part", Assert.Single(r.IncompleteCells));
        Assert.Contains(r.Notes, n => n.Message.Contains("no branch", StringComparison.Ordinal));
    }

    /// <summary>
    /// A condition is written glued to its directive as readily as spaced. The tokeniser
    /// deliberately keeps a bracketed run whole, so the first whitespace-separated "word" of the
    /// glued spelling is the WHOLE directive — which matches no case and falls through to whatever
    /// the last arm of the switch happens to be. Here that arm is <c>.endif</c>, so every
    /// conditional in a file written that way would silently unwind the wrong construct.
    /// </summary>
    [Fact]
    public void S17b_AConditionGluedToItsDirectiveIsReadTheSameWay()
    {
        var r = Read("""
            .param corner=2
            .subckt part a b
            .if(corner==1)
            R1 a b 1k
            .elseif(corner==2)
            R1 a b 2k
            .else
            R1 a b 3k
            .endif
            .ends
            """);

        Assert.Equal("2000", Override(Assert.Single(Cell(r, "part").Instances), "R"));
        Assert.Empty(r.IncompleteCells);
        Assert.Empty(r.Notes);
    }

    [Fact]
    public void S18_ConditionalsNest()
    {
        var r = Read("""
            .param outer=1 inner=0
            .subckt part a b
            .if (outer)
              .if (inner)
            R1 a b 1k
              .else
            R1 a b 2k
              .endif
            .else
            R1 a b 3k
            .endif
            .ends
            """);

        Assert.Equal("2000", Override(Assert.Single(Cell(r, "part").Instances), "R"));
    }

    // ── S19 — statistics ──────────────────────────────────────────────────────

    /// <summary>
    /// The numbers from a card carrying a distribution are a nominal run, and the caller has to be
    /// able to say so. Silently returning the nominal value while a card asked for a distribution is
    /// the bad outcome — it is indistinguishable from a card that asked for nothing.
    /// </summary>
    [Fact]
    public void S19_AStatisticalCardIsReadAtNominalAndReportsThatItWas()
    {
        var r = Read("""
            .model nfet nmos (vth0=agauss(0.4, 0.02, 3) tox=2.5n)
            """);

        Assert.Equal("0.4", Assert.Single(r.ModelCards).Parameters["vth0"]);

        var use = Assert.Single(r.Statistics);
        Assert.Equal("agauss", use.Function);
        Assert.Equal("0.4", use.Nominal);
    }

    // ── S20 — structural errors that must not be read past ────────────────────

    [Fact]
    public void S20_AnUnclosedSubcircuitIsAnError()
    {
        var ex = Assert.Throws<SpiceNetlistException>(() => Read(".subckt part a b\nR1 a b 1k\n"));
        Assert.Contains("part", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A named close whose name does not match is REPORTED, not refused — and this was a hard error
    /// until a kit disagreed.
    ///
    /// <para><c>.ends</c> closes the innermost open subcircuit whatever name follows it: that is the
    /// dialect's own rule, every simulator reads it that way, so the nesting is never in doubt and
    /// the name is decoration. Refusing threw away an entire model library over one stray suffix —
    /// on a kit whose own <c>.ends diodevdd_4kv_mod</c> closes <c>diodevdd_4kv</c>. The kit is wrong
    /// and nothing had ever noticed, because nothing else reads that name either.</para>
    /// </summary>
    [Fact]
    public void S21_AMismatchedCloseIsReported_NotRefused()
    {
        var r = Read("""
            .subckt part a b
            R1 a b 1k
            .ends part_mod
            .subckt other c d
            R2 c d 2k
            .ends other
            """);

        // Both subcircuits survive, and the second is NOT swallowed by the first.
        Assert.Equal(["part", "other"], r.Library.Cells.Select(c => c.Name));
        Assert.Single(Cell(r, "part").Instances);
        Assert.Single(Cell(r, "other").Instances);

        Assert.Contains(r.Notes, n => n.Message.Contains("carries no meaning", StringComparison.Ordinal));

        // …and it is not treated as damage: the definition itself was read completely.
        Assert.Empty(r.IncompleteCells);
    }

    // ── S22 — inclusion ───────────────────────────────────────────────────────

    [Fact]
    public void S22_AnIncludedFileIsSplicedAndItsNotesNameIt()
    {
        using var dir = new TempDirectory();
        dir.Write("models.inc", """
            .model dmod d is=1e-14
            .nonsense
            """);
        string top = dir.Write("top.sp", """
            .include models.inc
            .subckt part a b
            D1 a b dmod
            .ends
            """);

        var r = SpiceNetlistReader.ReadFile(top);

        Assert.Equal("dmod", Assert.Single(r.ModelCards).Name);
        Assert.Equal("dmod", Assert.Single(Cell(r, "part").Instances).Reference);

        // The note is attributed to the file it is actually in. An included file's line 2 and the
        // including file's line 2 are different lines.
        var note = Assert.Single(r.Notes);
        Assert.EndsWith("models.inc", note.File, StringComparison.Ordinal);
        Assert.Equal(2, note.Line);
    }

    [Fact]
    public void S23_AnInclusionCycleIsReportedRatherThanFollowed()
    {
        using var dir = new TempDirectory();
        dir.Write("b.inc", ".include a.inc\n.param fromb=2\n");
        string a = dir.Write("a.inc", ".include b.inc\n.param froma=1\n");

        var r = SpiceNetlistReader.ReadFile(a);

        Assert.Equal(["fromb", "froma"], r.Variables.Select(v => v.Name));
        Assert.Contains(r.Notes, n => n.Message.Contains("includes itself", StringComparison.Ordinal));
    }

    [Fact]
    public void S24_AMissingIncludeIsReported_AndDoesNotStopTheRead()
    {
        using var dir = new TempDirectory();
        string top = dir.Write("top.sp", """
            .include nowhere.inc
            .subckt part a b
            R1 a b 1k
            .ends
            """);

        var r = SpiceNetlistReader.ReadFile(top);

        Assert.Single(Cell(r, "part").Instances);
        Assert.Contains(r.Notes, n => n.Message.Contains("not found", StringComparison.Ordinal));
    }

    /// <summary>Reading from text has no directory to resolve against, and says so rather than guessing one.</summary>
    [Fact]
    public void S25_AnInclusionFromTextIsReportedRatherThanResolvedAgainstTheWorkingDirectory()
    {
        var r = Read(".include models.inc\n");
        Assert.Contains(r.Notes, n => n.Message.Contains("read from text", StringComparison.Ordinal));
    }

    // ── S26 — library sections ────────────────────────────────────────────────

    [Fact]
    public void S26_OnlyTheRequestedSectionIsRead()
    {
        using var dir = new TempDirectory();
        dir.Write("corners.lib", """
            .lib typical
            .param vth=0.40
            .endl typical
            .lib slow
            .param vth=0.45
            .endl slow
            """);
        string top = dir.Write("top.sp", ".lib corners.lib slow\n");

        var r = SpiceNetlistReader.ReadFile(top);

        Assert.Equal("0.45", Assert.Single(r.Variables).Expression);
    }

    /// <summary>
    /// Sections are ALTERNATIVES. Reading a library file whole would define the same parameters
    /// several times over, so an unrequested section is skipped — and named, because picking one
    /// nobody asked for is a guess and skipping silently is a different circuit.
    /// </summary>
    [Fact]
    public void S27_SectionsAreSkippedWhenNoneWasRequested()
    {
        var r = Read("""
            .param shared=1
            .lib typical
            .param vth=0.40
            .endl
            .lib slow
            .param vth=0.45
            .endl
            """);

        Assert.Equal("shared", Assert.Single(r.Variables).Name);
        Assert.Equal(2, r.Notes.Count(n => n.Message.Contains("alternatives", StringComparison.Ordinal)));
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"crf-spice-{Guid.NewGuid():N}");

        public TempDirectory() => Directory.CreateDirectory(Path);

        public string Write(string name, string text)
        {
            string full = System.IO.Path.Combine(Path, name);
            File.WriteAllText(full, text);
            return full;
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
        }
    }
}
