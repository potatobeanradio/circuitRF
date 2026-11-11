using System;
using System.IO;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// A test class whose answers depend on the per-user preferences — Settings ▸ General ▸ Units seeds every NEW frequency
/// in the preferred unit (brief-dfr22-1), so a 1 GHz tone opens as "1000" "MHz" on a machine where MHz is preferred — runs
/// against a first-launch installation: the per-user state directory moved to an empty folder for the class's lifetime.
/// A deriving class must also carry <c>[Collection(UserStateDirectoryCollection.Name)]</c> itself — xUnit reads the
/// attribute off the class, not its base — which runs it alone, because the directory is one process-global slot.
/// </summary>
public abstract class FirstLaunchUserState : IDisposable
{
    private readonly string _stateRoot =
        Path.Combine(Path.GetTempPath(), "crf-first-launch-" + Guid.NewGuid().ToString("N")[..8]);

    protected FirstLaunchUserState() => AppDataRoot.RedirectTo(_stateRoot);

    public void Dispose()
    {
        AppDataRoot.RedirectTo(null);
        try { Directory.Delete(_stateRoot, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }
}
