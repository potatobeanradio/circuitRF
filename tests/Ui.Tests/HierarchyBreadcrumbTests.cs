using System.ComponentModel;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Gate tests for hier4: breadcrumb bar data projection on <see cref="SchematicDocument"/>.
/// All tests are framework-free: no Avalonia, no disk I/O.
/// </summary>
public class HierarchyBreadcrumbTests
{
    private static SchematicViewModel MakeVm() => new(new SchematicEditModel());
    private static SchematicDocument  MakeDoc(string title, SchematicViewModel vm) => new(title, vm);

    // ── Breadcrumbs at base ───────────────────────────────────────────────────

    [Fact]
    public void AtBase_Breadcrumbs_HasOneCrumb_IsCurrent()
    {
        var vm  = MakeVm();
        var doc = MakeDoc("Top", vm);

        var crumbs = doc.Breadcrumbs;

        Assert.Single(crumbs);
        Assert.Equal(0,     crumbs[0].FrameIndex);
        Assert.Equal("Top", crumbs[0].Text);
        Assert.True(crumbs[0].IsCurrent);
        Assert.False(crumbs[0].IsNotFirst);
    }

    [Fact]
    public void AtBase_CanPopOut_IsFalse()
    {
        var vm  = MakeVm();
        var doc = MakeDoc("Top", vm);

        Assert.False(doc.CanPopOut);
    }

    // ── Breadcrumbs after two push-ins ────────────────────────────────────────

    [Fact]
    public void AfterTwoPushIns_Breadcrumbs_HasThreeCrumbs_CorrectState()
    {
        var vmA = MakeVm();
        var vmB = MakeVm();
        var vmC = MakeVm();
        var doc = MakeDoc("Top", vmA);

        doc.PushIn(vmB, "X1");
        doc.PushIn(vmC, "X2");

        var crumbs = doc.Breadcrumbs;

        Assert.Equal(3, crumbs.Count);

        // Base crumb
        Assert.Equal(0,     crumbs[0].FrameIndex);
        Assert.Equal("Top", crumbs[0].Text);
        Assert.False(crumbs[0].IsCurrent);
        Assert.False(crumbs[0].IsNotFirst);

        // First pushed crumb
        Assert.Equal(1,    crumbs[1].FrameIndex);
        Assert.Equal("X1", crumbs[1].Text);
        Assert.False(crumbs[1].IsCurrent);
        Assert.True(crumbs[1].IsNotFirst);

        // Active (current) crumb
        Assert.Equal(2,    crumbs[2].FrameIndex);
        Assert.Equal("X2", crumbs[2].Text);
        Assert.True(crumbs[2].IsCurrent);
        Assert.True(crumbs[2].IsNotFirst);
    }

    // ── PopToLevel leaves correct breadcrumbs ─────────────────────────────────

    [Fact]
    public void PopToFrameIndex1_LeavesTwoCrumbs_Index1IsCurrent()
    {
        var vmA = MakeVm();
        var vmB = MakeVm();
        var vmC = MakeVm();
        var doc = MakeDoc("Top", vmA);

        doc.PushIn(vmB, "X1");
        doc.PushIn(vmC, "X2");
        doc.PopTo(1);

        var crumbs = doc.Breadcrumbs;

        Assert.Equal(2,    crumbs.Count);
        Assert.Equal("X1", crumbs[1].Text);
        Assert.True(crumbs[1].IsCurrent);
        Assert.False(crumbs[0].IsCurrent);
    }

    [Fact]
    public void PopToZero_LeavesOneCrumb_BaseCrumbIsCurrent()
    {
        var vmA = MakeVm();
        var vmB = MakeVm();
        var doc = MakeDoc("Top", vmA);

        doc.PushIn(vmB, "X1");
        doc.PopTo(0);

        var crumbs = doc.Breadcrumbs;

        Assert.Single(crumbs);
        Assert.Equal("Top", crumbs[0].Text);
        Assert.True(crumbs[0].IsCurrent);
    }

    // ── PropertyChanged fires for Breadcrumbs ─────────────────────────────────

    [Fact]
    public void PushIn_RaisesBreadcrumbsPropertyChanged()
    {
        var vmA = MakeVm();
        var vmB = MakeVm();
        var doc = MakeDoc("Top", vmA);

        int changeCount = 0;
        doc.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SchematicDocument.Breadcrumbs))
                changeCount++;
        };

        doc.PushIn(vmB, "X1");

        Assert.True(changeCount > 0);
    }

    [Fact]
    public void PopOut_RaisesBreadcrumbsPropertyChanged()
    {
        var vmA = MakeVm();
        var vmB = MakeVm();
        var doc = MakeDoc("Top", vmA);
        doc.PushIn(vmB, "X1");

        int changeCount = 0;
        doc.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SchematicDocument.Breadcrumbs))
                changeCount++;
        };

        doc.PopOut();

        Assert.True(changeCount > 0);
    }

    // ── BreadcrumbItem IsNotFirst ─────────────────────────────────────────────

    [Fact]
    public void BreadcrumbItem_IsNotFirst_FalseOnlyForFrameZero()
    {
        var vmA = MakeVm();
        var vmB = MakeVm();
        var vmC = MakeVm();
        var doc = MakeDoc("Base", vmA);
        doc.PushIn(vmB, "X1");
        doc.PushIn(vmC, "X2");

        var crumbs = doc.Breadcrumbs;

        Assert.False(crumbs[0].IsNotFirst);
        Assert.True(crumbs[1].IsNotFirst);
        Assert.True(crumbs[2].IsNotFirst);
    }
}
