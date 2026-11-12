// HZN-5 R-hzn5-2 — one Hertzon object, edited in place: its keys, then its modelling history step by step.

namespace CircuitRF.Design.Interchange.Hzn;

internal sealed partial class HznPatch
{
    /// <summary>
    /// <paramref name="target"/> (the object in the file) takes the keys <paramref name="edited"/> states differently from
    /// <paramref name="opened"/> (both are the writer's own spelling of the object, before and after the edit). A key that reads the
    /// same in both keeps the file's text, and a key neither names (colour, transparency, the keys circuitRF does not model) is not
    /// touched.
    /// </summary>
    private void PatchObject(HznJsonObject target, HznJsonObject opened, HznJsonObject edited, string who)
    {
        foreach (string key in edited.Keys.ToList())
        {
            if (key is "name" or "history") continue;
            if (HznJsonOps.Same(opened[key], edited[key])) continue;
            target.Set(key, HznJsonOps.Clone(edited[key]!));
            _changed = true;
        }
        if (target.GetArray("history") is { } history && opened.GetArray("history") is { } hb && edited.GetArray("history") is { } he)
            PatchHistory(target, history, HznJsonOps.Objects(hb), HznJsonOps.Objects(he), who);
    }

    private static int IndexIn(HznJsonArray array, HznJsonValue item)
    {
        for (int i = 0; i < array.Count; i++)
            if (ReferenceEquals(array[i], item)) return i;
        return -1;
    }

    private static string KindOf(HznJsonObject step) => step.GetString("kind") ?? "";

    private static bool IsSuppressed(HznJsonObject step) => step.GetBool("suppressed") == true;

    /// <summary>The file's steps that line up with the baseline's writing: every step, or the unsuppressed ones, whichever has the
    /// baseline's kinds in the baseline's order. Null when neither does.</summary>
    private static IReadOnlyList<HznJsonObject>? Align(IReadOnlyList<HznJsonObject> file, IReadOnlyList<HznJsonObject> opened)
    {
        bool Matches(IReadOnlyList<HznJsonObject> candidate) =>
            candidate.Count == opened.Count
            && candidate.Zip(opened).All(p => KindOf(p.First) == KindOf(p.Second) && IsSuppressed(p.First) == IsSuppressed(p.Second));
        if (Matches(file)) return file;
        var applied = file.Where(s => !IsSuppressed(s)).ToList();
        return Matches(applied) ? applied : null;
    }

    private void PatchHistory(HznJsonObject target, HznJsonArray history, IReadOnlyList<HznJsonObject> opened,
                              IReadOnlyList<HznJsonObject> edited, string who)
    {
        // Nothing in the history changed in the writer's spelling: the file's steps are not touched.
        if (opened.Count == edited.Count && opened.Zip(edited).All(p => HznJsonOps.Same(p.First, p.Second))) return;

        var file = HznJsonOps.Objects(history).ToList();
        var aligned = Align(file, opened);
        int common = 0;
        while (common < opened.Count && common < edited.Count && KindOf(opened[common]) == KindOf(edited[common])) common++;

        if (aligned is not null)
        {
            for (int i = 0; i < common; i++) PatchStep(aligned[i], opened[i], edited[i]);
            if (common == opened.Count && common == edited.Count) return;
            if (common == opened.Count)
            {
                // The history grew: a boolean added, a placement given. The new steps go after the last one.
                for (int i = common; i < edited.Count; i++) history.Add(HznJsonOps.Clone(edited[i]));
                _changed = true;
                return;
            }
            if (common == edited.Count)
            {
                // The history shrank: a tool removed. Nothing that is not the writer's own may be in the part cut away.
                int firstCut = file.IndexOf(aligned[common]);
                if (file.Skip(firstCut).Any(s => IsSuppressed(s) && !aligned.Contains(s)))
                {
                    Refuse($"Object '{who}': a step the edit removes is followed by a suppressed step that would be left with nothing to act on. " +
                           "Unsuppress or delete it in Hertzon, or Save As a .c3d.");
                    return;
                }
                for (int i = aligned.Count - 1; i >= common; i--)
                    history.RemoveAt(IndexIn(history, aligned[i]));
                _changed = true;
                return;
            }
        }

        // No step edit says it. The history is rewritten as the writer spells it, which is only safe when the file's history holds
        // nothing the writer would lose.
        if (file.Any(IsSuppressed))
        {
            Refuse($"Object '{who}': its history changed shape, and it holds a suppressed step that a rewritten history cannot keep in place. " +
                   "Edit it in Hertzon, or Save As a .c3d.");
            return;
        }
        for (int i = history.Count - 1; i >= 0; i--) history.RemoveAt(i);
        foreach (var step in edited) history.Add(HznJsonOps.Clone(step));
        _changed = true;
    }

    /// <summary>One step of the file, edited: the properties whose written text moved take the new text, the others keep the file's.</summary>
    private void PatchStep(HznJsonObject file, HznJsonObject opened, HznJsonObject edited)
    {
        var fileProps = file.GetObject("props");
        var before = opened.GetObject("props");
        var after = edited.GetObject("props");
        if (after is not null)
        {
            foreach (string key in after.Keys.ToList())
            {
                if (before?.ContainsKey(key) == true && HznJsonOps.Same(before[key], after[key])) continue;
                if (fileProps is null)
                {
                    fileProps = new HznJsonObject();
                    file.Set("props", fileProps);
                }
                fileProps.Set(key, HznJsonOps.Clone(after[key]!));
                _changed = true;
            }
        }
        if (before is not null && fileProps is not null)
            foreach (string key in before.Keys.ToList())
                if (after?.ContainsKey(key) != true && fileProps.Remove(key)) _changed = true;
        if (IsSuppressed(opened) != IsSuppressed(edited))
        {
            file.SetBool("suppressed", IsSuppressed(edited));
            _changed = true;
        }
    }
}
