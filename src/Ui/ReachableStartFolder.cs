using System;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;

namespace CircuitRF.Ui;

/// <summary>
/// The folder a file dialog opens in, looked up WITHOUT the UI thread waiting on a folder that does
/// not answer.
///
/// <para><c>TryGetFolderFromPathAsync</c> is asynchronous in name only: it checks the folder exists
/// synchronously, on the calling thread. A remembered start folder on a disconnected network drive
/// or in a cloud-synced folder whose service is down held the window for the network timeout before
/// the dialog even appeared — and handing the dialog that folder made the native dialog wait again.
/// So the folder is probed off the UI thread first (<see cref="CircuitRF.Design.PathProbe"/>), and
/// one that does not answer in time is simply not suggested: the dialog opens where the platform
/// would put it and the user can navigate from there.</para>
///
/// <para>Every dialog in the application asks through here; a direct
/// <c>TryGetFolderFromPathAsync</c> call is the shape of the stall coming back.</para>
/// </summary>
public static class ReachableStartFolder
{
    /// <summary>Shorter than the probe's default: this is a nicety, and the user is waiting for a
    /// dialog they asked for.</summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    /// <summary>The folder at <paramref name="path"/> when it exists and answers in time; otherwise null.</summary>
    public static async Task<IStorageFolder?> TryGetReachableFolderAsync(this IStorageProvider provider, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            if (await CircuitRF.Design.PathProbe.ProbeAsync(path, Timeout) != CircuitRF.Design.PathState.Present)
                return null;
            return await provider.TryGetFolderFromPathAsync(path);
        }
        catch
        {
            return null;
        }
    }

    /// <inheritdoc cref="TryGetReachableFolderAsync(IStorageProvider, string?)"/>
    public static Task<IStorageFolder?> TryGetReachableFolderAsync(this IStorageProvider provider, Uri? uri)
        => provider.TryGetReachableFolderAsync(uri is { IsFile: true } ? uri.LocalPath : null);
}
