using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Ui.Schematic;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Gate tests for the SDD "Add Equation…" picker's contents (owner report, 2026-09-02).
///
/// <para><b>What was wrong.</b> The parameter editor's generic "+" added <c>Name[n]</c> for an
/// ever-increasing n. An SDD's slots are two-dimensional (<c>I[p,w]</c>) and bounded by the port
/// count, so the generic index parser read neither: the seeded <c>I[1,0]</c> looked unindexed, the
/// first press offered <c>I[1]</c> — valid sugar for the SAME slot, which silently replaced the
/// seeded equation — and a few presses later <c>I[3]</c> on a 2-port, which the factory refuses at
/// Run. Every slot the picker offers must instead be one the component can use, at the value it is
/// created with; the last test here proves that against the real elaborator rather than against
/// this class's own idea of the grammar.</para>
/// </summary>
public class SddEquationSlotsTests(ITestOutputHelper output)
{
    /// <summary>What ComponentTypeRegistry seeds an N-port SDD with: one I[x,0] per port.</summary>
    private static List<(string, string)> Seeded(int n)
        => [.. Enumerable.Range(1, n).Select(x => ($"I[{x},0]", $"_v{x}/50"))];

    private static string[] Names(IEnumerable<SddEquationSlot> slots) => [.. slots.Select(s => s.Name)];

    // ── Nothing already present is offered ────────────────────────────────────

    [Fact]
    public void FreshTwoPortSdd_DoesNotOfferACurrentSlotItAlreadyCarries()
    {
        var names = Names(SddEquationSlots.Available(2, Seeded(2)));

        Assert.DoesNotContain("I[1,0]", names);
        Assert.DoesNotContain("I[2,0]", names);
        // The whole defect in one assertion: I[1] is the SAME slot as the seeded I[1,0], and the
        // old "+" offered it first.
        Assert.DoesNotContain("I[1]", names);
    }

    /// <summary>Both sugars occupy the two-index slot they abbreviate — offering the long spelling
    /// beside them would create the duplicate that silently replaced a seeded equation.</summary>
    [Theory]
    [InlineData("I[1]", "I[1,0]")]
    [InlineData("Q[1]", "I[1,1]")]
    public void SingleIndexSugar_OccupiesTheSlotItAbbreviates(string sugar, string longForm)
    {
        var names = Names(SddEquationSlots.Available(2, [(sugar, "0")]));
        Assert.DoesNotContain(longForm, names);
    }

    // ── No port beyond the port count is ever named ───────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void NoSlotNamesAPortTheDeviceDoesNotHave(int portCount)
    {
        var slots = SddEquationSlots.Available(portCount, Seeded(portCount));

        foreach (var s in slots)
        {
            foreach (var name in new[] { s.Name, s.CompanionName })
            {
                if (name.Length == 0) continue;
                // A control reference indexes OTHER instances, not this device's ports.
                if (name.StartsWith("C[", StringComparison.Ordinal) ||
                    name.StartsWith("Cport[", StringComparison.Ordinal)) continue;

                var m = System.Text.RegularExpressions.Regex.Match(name, @"^[IQV]\[(\d+)");
                if (!m.Success) continue;
                int p = int.Parse(m.Groups[1].Value);
                Assert.True(p <= portCount,
                    $"{name} names port {p} on a {portCount}-port SDD");
            }
        }
    }

    // ── The categories a fresh SDD actually gets ──────────────────────────────

    [Fact]
    public void FreshTwoPortSdd_OffersChargeWeightedAndAConstant()
    {
        var slots = SddEquationSlots.Available(2, Seeded(2));
        var names = Names(slots);
        output.WriteLine(string.Join("\n", slots.Select(s => $"{s.Category,-9} {s.DisplayName}")));

        Assert.Contains("I[1,1]", names);   // charge at port 1
        Assert.Contains("I[2,1]", names);
        Assert.Contains("I[1,2]", names);   // weighted, with its new H[2]
        Assert.Contains($"{SddEquationSlots.ConstantPrefix}1", names);
    }

    // ── V[p] and a current equation are mutually exclusive ────────────────────
    //
    // The factory refuses a port that states both, by name. A freshly placed SDD carries an I[p,0]
    // on every port, so the branch equation is offered on NO port until one is removed — and that,
    // unexplained, reads as a missing feature, which is what Notes() is for.

    [Fact]
    public void VoltageSlot_IsNotOfferedForAPortThatStatesACurrent()
    {
        var names = Names(SddEquationSlots.Available(2, Seeded(2)));
        Assert.DoesNotContain("V[1]", names);
        Assert.DoesNotContain("V[2]", names);
    }

    [Fact]
    public void VoltageSlot_IsOfferedForAPortWithNoCurrentEquation()
    {
        // Port 2's current equation removed — the branch equation becomes available there only.
        var names = Names(SddEquationSlots.Available(2, [("I[1,0]", "_v1/50")]));
        Assert.Contains("V[2]",   names);
        Assert.DoesNotContain("V[1]", names);
    }

    [Fact]
    public void CurrentSlots_AreNotOfferedForAPortWhoseVoltageIsHeld()
    {
        var names = Names(SddEquationSlots.Available(2, [("V[1]", "0.5*_v2"), ("I[2,0]", "_v2/50")]));

        Assert.DoesNotContain("I[1,0]", names);
        Assert.DoesNotContain("I[1,1]", names);
        Assert.DoesNotContain("I[1,2]", names);
        Assert.Contains("I[2,1]", names);   // the other port is unaffected
    }

    [Fact]
    public void Notes_ExplainASuppressedBranchEquation()
    {
        var notes = SddEquationSlots.Notes(2, Seeded(2));
        Assert.Single(notes);
        Assert.Contains("V[1]", notes[0], StringComparison.Ordinal);
        Assert.Contains("V[2]", notes[0], StringComparison.Ordinal);

        // Nothing to explain once every port could take one.
        Assert.Empty(SddEquationSlots.Notes(2, []));
    }

    /// <summary>A weighted current is never offered without the weighting it needs: the factory
    /// refuses an <c>I[p,w]</c> whose <c>H[w]</c> is not defined.</summary>
    [Fact]
    public void AWeightedCurrentWithANewWeighting_CarriesTheWeightingWithIt()
    {
        var slot = SddEquationSlots.Available(2, Seeded(2)).Single(s => s.Name == "I[1,2]");

        Assert.Equal("H[2]", slot.CompanionName);
        Assert.NotEqual("", slot.CompanionExpression);
        Assert.Equal("I[1,2] + H[2]", slot.DisplayName);
    }

    /// <summary>Once H[2] exists, the weighted slot for it stands on its own and the "new
    /// weighting" pair moves on to H[3].</summary>
    [Fact]
    public void AnExistingWeighting_IsOfferedWithoutACompanion()
    {
        var slots = SddEquationSlots.Available(2, [.. Seeded(2), ("H[2]", "1")]);

        var plain = slots.Single(s => s.Name == "I[1,2]");
        Assert.Equal("", plain.CompanionName);
        Assert.Contains(slots, s => s.CompanionName == "H[3]");
    }

    // ── Control references: offered only where one is already demanded ─────────

    [Fact]
    public void ControlReference_IsNotOfferedSpeculatively()
    {
        var names = Names(SddEquationSlots.Available(2, Seeded(2)));
        Assert.DoesNotContain("C[1]", names);
    }

    /// <summary>An equation reading <c>_c1</c> with no <c>C[1]</c> is refused at Run — so that is
    /// exactly when the slot is worth offering.</summary>
    [Fact]
    public void ControlReference_IsOfferedOnceAnEquationReadsIt()
    {
        var names = Names(SddEquationSlots.Available(2, [("I[1,0]", "_c1*2"), ("I[2,0]", "0")]));
        Assert.Contains("C[1]", names);
    }

    [Fact]
    public void CportIsOfferedOnceAControlReferenceExists()
    {
        var names = Names(SddEquationSlots.Available(2, [.. Seeded(2), ("C[1]", "VS")]));
        Assert.Contains("Cport[1]", names);
        Assert.DoesNotContain("C[1]", names);     // already carried
    }

    // ── The constant's placeholder never collides ─────────────────────────────

    [Fact]
    public void TheConstantPlaceholder_StepsPastOnesAlreadyThere()
    {
        var names = Names(SddEquationSlots.Available(2,
            [.. Seeded(2), ($"{SddEquationSlots.ConstantPrefix}1", "1e-3")]));

        Assert.Contains($"{SddEquationSlots.ConstantPrefix}2", names);
        Assert.DoesNotContain($"{SddEquationSlots.ConstantPrefix}1", names);
    }

    // ── No slot is ever seeded blank ──────────────────────────────────────────

    /// <summary>
    /// An SDD parameter reaches the factory verbatim and is PARSED there, so a blank expression is a
    /// ParseException at Run — which is what the old "+" created every time. The one exception is
    /// C[n], whose value is an instance NAME rather than an expression and which is only offered
    /// when an equation already demands it.
    /// </summary>
    [Fact]
    public void EverySlotIsSeededWithSomethingThatParses()
    {
        foreach (var s in SddEquationSlots.Available(3, Seeded(3)))
        {
            if (s.Name.StartsWith("C[", StringComparison.Ordinal)) continue;
            Assert.False(string.IsNullOrWhiteSpace(s.DefaultExpression),
                $"{s.Name} would be created blank");
            if (s.CompanionName.Length > 0)
                Assert.False(string.IsNullOrWhiteSpace(s.CompanionExpression),
                    $"{s.CompanionName} would be created blank");
        }
    }

    // ── The gate: every offered slot ELABORATES ───────────────────────────────

    /// <summary>
    /// <b>The property the picker exists for, checked against the real factory rather than against
    /// this class's own reading of the grammar.</b> Each offered slot is added to a 2-port SDD at
    /// the value it would be created with, and the netlist is elaborated — which is where
    /// <c>ComponentModelFactory.CreateSddModel</c> parses every equation, validates every port index
    /// and cross-checks that each <c>I[p,w≥2]</c> has its <c>H[w]</c>. The old "+" fails this on its
    /// first press (<c>I[1]</c> blank → a parse error) and again on its third (<c>I[3]</c> → the
    /// port-count refusal), which is what makes it a gate and not a restatement.
    /// </summary>
    [Fact]
    public void EveryOfferedSlot_ElaboratesOnTheDeviceItWasOfferedFor()
    {
        var seeded = Seeded(2);
        var slots  = SddEquationSlots.Available(2, seeded);
        Assert.NotEmpty(slots);

        foreach (var slot in slots)
        {
            // C[n] carries an instance name; the fixture below has a Vdc called VS to point at.
            string expr = slot.Name.StartsWith("C[", StringComparison.Ordinal)
                ? "VS" : slot.DefaultExpression;

            var parms = new List<(string Name, string Expr)>([.. seeded.Select(t => (t.Item1, t.Item2))])
            {
                (slot.Name, expr),
            };
            if (slot.CompanionName.Length > 0)
                parms.Add((slot.CompanionName, slot.CompanionExpression));

            string cnl = BuildTwoPortSddNetlist(parms);
            output.WriteLine($"── {slot.Category}: {slot.DisplayName}");

            var ex = Record.Exception(() =>
            {
                var (lib, tb) = new CnlReader().Read(cnl);
                _ = new Elaborator(lib).Elaborate(tb);
            });

            Assert.True(ex is null,
                $"Slot '{slot.DisplayName}' was offered but the device cannot use it: {ex?.Message}\n{cnl}");
        }
    }

    /// <summary>The counter-case, so the gate above is known to be capable of failing: the two
    /// parameters the old "+" actually produced.</summary>
    [Theory]
    [InlineData("I[1]", "",  "a blank equation is a parse error")]
    [InlineData("I[3]", "0", "port 3 does not exist on a 2-port")]
    public void TheParametersTheOldPlusButtonProduced_DoNotElaborate(string name, string expr, string why)
    {
        string cnl = BuildTwoPortSddNetlist([.. Seeded(2).Select(t => (t.Item1, t.Item2)), (name, expr)]);

        var ex = Record.Exception(() =>
        {
            var (lib, tb) = new CnlReader().Read(cnl);
            _ = new Elaborator(lib).Elaborate(tb);
        });

        output.WriteLine($"{name}={expr}  →  {ex?.GetType().Name}: {ex?.Message}");
        Assert.True(ex is not null, $"Expected a refusal ({why}) but elaboration succeeded");
    }

    private static string BuildTwoPortSddNetlist(IEnumerable<(string Name, string Expr)> parameters)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Vdc:VS  n1 0  Vdc=1 V");
        sb.AppendLine("R:R1    n1 n2  R=100 Ohm");
        sb.Append("SDD:X1  n2 0  n1 0");
        foreach (var (name, expr) in parameters) sb.Append($"  {name}={expr}");
        sb.AppendLine();
        sb.AppendLine("analysis DC1 type=dc");
        return sb.ToString();
    }
}
