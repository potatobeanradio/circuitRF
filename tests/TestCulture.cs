using System.Globalization;
using System.Runtime.CompilerServices;

namespace CircuitRF.Tests.Support;

/// <summary>
/// Pins every test assembly in this repo to <c>en-US</c> before a single test runs.
///
/// <para>Nothing in <c>src/</c> sets a thread culture, so the application — and, until this file
/// existed, the test suite too — runs at whatever locale the OS provides. The suite formats numbers
/// into a large share of its assertions (thousands of <c>Assert…Contains</c> calls), so an unknown
/// number of them were quietly asserting against the developer's or CI runner's locale. Windows,
/// macOS and Linux happen to agree today; nothing made them.</para>
///
/// <para>A <see cref="ModuleInitializerAttribute"/> is the right hook: it runs once per assembly,
/// before any test and before xunit's parallelism starts, and costs nothing per test. Setting the
/// <c>DefaultThreadCurrent*</c> pair rather than <c>CurrentCulture</c> is what makes it stick on the
/// worker threads xunit creates afterwards.</para>
///
/// <para>This pins the <em>default</em>. A test that deliberately exercises another locale — the
/// <c>de-DE</c> format round-trip and the expression-language gate — still sets and restores culture
/// around itself, and must disable parallelization while it does, because culture is process-wide
/// state (see <c>docs/sonnet-briefs/brief-localization-groundwork.md</c> §5, §10).</para>
///
/// <para>This is emphatically NOT <c>InvariantGlobalization=true</c>: that would break a future
/// localization, change collation, and make the format-invariance gate pass for the wrong reason.</para>
/// </summary>
internal static class TestCulture
{
    [ModuleInitializer]
    internal static void PinEnUs()
    {
        var enUs = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentCulture = enUs;
        CultureInfo.DefaultThreadCurrentUICulture = enUs;
        CultureInfo.CurrentCulture = enUs;
        CultureInfo.CurrentUICulture = enUs;
    }
}
