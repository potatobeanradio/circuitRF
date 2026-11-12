using Avalonia.Headless;
using Avalonia.Threading;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Ui.ThreeD.Hzn;
using CircuitRF.Ui.Views.Dialogs;

/// <summary>--hzn-save-dialog changed|refused --hzn-open &lt;project.hzn&gt; --hzn-design &lt;name&gt;: the two answers a save of a design held
/// in a Hertzon project can give besides success (HZN-5). The file is never written: a copy beside it in a scratch folder is used.</summary>
internal static class HznSaveSnapshot
{
    public static int Show(string kind, string hzn, string design, string output)
    {
        string scratch = Path.Combine(Path.GetTempPath(), "crf_hznsave_snap_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(scratch);
        try
        {
            string copy = Path.Combine(scratch, Path.GetFileName(hzn));
            File.Copy(hzn, copy);
            var source = new HznDesignSource(copy, design, new TechnologyCache());
            var doc = source.Load();
            Avalonia.Controls.Window dialog;
            if (kind == "changed")
            {
                if (doc.Objects.OfType<C3dBox>().FirstOrDefault() is { } box) box.Size = box.Size with { X = box.Size.X + 1_000_000 };
                doc.Objects.Add(new C3dBox { Name = "Extra", Material = "lam", Size = new C3dPoint3(1_000_000, 1_000_000, 1_000_000) });
                File.AppendAllText(copy, "\n");              // another program saved it
                var why = source.Save(doc);
                if (why is null || !source.LastSaveChangedOnDisk) throw new InvalidOperationException("the save was not refused as changed on disk: " + why);
                dialog = HznChangedOnDiskDialog.Build(Path.GetFileName(hzn), design, source.EditSummary(doc), _ => { });
            }
            else
            {
                doc.AirBoxMaterial = "air";                  // a change no Hertzon key takes
                doc.Objects.Add(new C3dEllipsoid { Name = "Egg", Material = doc.Objects.First().Material, SemiAxes = new C3dPoint3(1_000_000, 2_000_000, 3_000_000) });
                var why = source.Save(doc) ?? throw new InvalidOperationException("the save was not refused");
                dialog = Build(source.Title, why);
            }
            dialog.Show();
            for (int k = 0; k < 6; k++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); dialog.UpdateLayout(); }
            dialog.CaptureRenderedFrame()!.Save(output);
            Console.WriteLine($"wrote {output}");
            return 0;
        }
        finally { try { Directory.Delete(scratch, recursive: true); } catch (IOException) { } }
    }

    private static Avalonia.Controls.Window Build(string title, string text) =>
        TextConfirmDialog.Build(title, CircuitRF.Ui.ViewModels.WorkspaceViewModel.HznNotSaved, text, null, _ => { });
}
