using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace CircuitRF.Ui.ViewModels.Dock;

/// <summary>
/// One item of the palette's Type menu (brief vendorlib-2 R-vl2-1): a selectable category, a sub-menu,
/// a separator, or a command. The view turns these into menu items; nothing here knows Avalonia.
/// </summary>
public sealed class PaletteMenuNode
{
    public string Header { get; init; } = "";

    /// <summary>The category this item selects, or null for a sub-menu, a separator or a command.</summary>
    public PaletteCategoryEntry? Entry { get; init; }

    /// <summary>The sub-menu; empty for a leaf.</summary>
    public IReadOnlyList<PaletteMenuNode> Children { get; init; } = [];

    public bool IsSeparator { get; init; }

    public bool IsEnabled { get; init; } = true;

    /// <summary>A command item (Add Vendor Library…) rather than a category.</summary>
    public ICommand? Command { get; init; }

    public static PaletteMenuNode Separator { get; } = new() { IsSeparator = true };

    public override string ToString() => IsSeparator ? "—" : Header;
}

/// <summary>
/// Builds a vendor library's sub-menu from its own folder tree (brief vendorlib-2 R-vl2-1a/1b): one
/// item per folder, one leaf per file carrying its part count, and an "All in …" item opening every
/// sub-menu, so a folder can be selected as well as opened.
///
/// <para><b>No level that offers no choice.</b> A folder with exactly one child collapses into it
/// (<c>Folder ▸ Child</c>), and a tree that starts with a single folder skips it. The breadcrumb of
/// whatever is selected still shows the full path, collapsed levels included.</para>
/// </summary>
public static class VendorMenuTree
{
    /// <summary>The separator between breadcrumb levels, and between collapsed levels in a header.</summary>
    public const string Step = " ▸ ";

    private sealed class Folder(string name, string path)
    {
        public string Name { get; } = name;
        public string Path { get; } = path;
        public SortedDictionary<string, Folder> Subs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public SortedDictionary<string, (string Stem, int Parts)> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Model folders directly here (brief vendorlib-9), by path, with their names. A folder of models
        /// and nothing else is one leaf listing them; beside files or sub-folders, each model is a leaf of its own.</summary>
        public SortedDictionary<string, string> ModelFolders { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int Models => ModelFolders.Count;
        public int ChildCount => Subs.Count + Files.Count;
    }

    /// <summary>The menu item for the library <paramref name="library"/>, whose index is <paramref name="build"/>.</summary>
    public static PaletteMenuNode Build(string library, VendorLibraryBuild build)
    {
        var root = new Folder(library, "");
        foreach (var file in build.Entries.GroupBy(e => e.File, StringComparer.Ordinal))
        {
            var e = file.First();
            var at = root;
            foreach (string seg in e.Folders)
            {
                if (!at.Subs.TryGetValue(seg, out var sub))
                    at.Subs[seg] = sub = new Folder(seg, at.Path.Length == 0 ? seg : at.Path + "/" + seg);
                at = sub;
            }
            // A model folder is ONE part, and a category of models is the menu's unit: the folder it sits in lists
            // it, as a SPICE file lists its parts, rather than each model becoming a sub-menu of one.
            if (e.IsModel) at.ModelFolders[file.Key] = e.FileStem;
            else at.Files[file.Key] = (e.FileStem, file.Count());
        }

        // A tree that starts with a single folder skips it: the library itself is that folder.
        var top = root;
        while (top.Files.Count == 0 && top.Models == 0 && top.Subs.Count == 1) top = top.Subs.Values.First();

        return new PaletteMenuNode
        {
            Header   = library,
            Children = [AllIn(library, library, ""), .. ChildrenOf(library, top)],
        };
    }

    private static IEnumerable<PaletteMenuNode> ChildrenOf(string library, Folder f)
    {
        foreach (var sub in f.Subs.Values) yield return FolderNode(library, sub, sub.Name);
        foreach (var (path, file) in f.Files.OrderBy(kv => kv.Value.Stem, StringComparer.OrdinalIgnoreCase))
            yield return Leaf(library, path, file.Stem, file.Parts);
        // Models beside files or sub-folders are leaves too, so the menu shows they are there.
        if (f.ChildCount > 0)
            foreach (var (path, name) in f.ModelFolders.OrderBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase))
                yield return Leaf(library, path, name, 1);
    }

    private static PaletteMenuNode FolderNode(string library, Folder f, string header)
    {
        // A folder of models and nothing else is a leaf: choosing it lists its models.
        if (f.ChildCount == 0)
            return new PaletteMenuNode
            {
                Header = $"{header} ({f.Models:N0})",
                Entry  = PaletteCategoryEntry.ForVendor(library, f.Path, BreadcrumbFor(library, f.Path)),
            };
        if (f.ChildCount == 1 && f.Models == 0)
        {
            if (f.Subs.Count == 1)
            {
                var only = f.Subs.Values.First();
                return FolderNode(library, only, header + Step + only.Name);
            }
            var (path, file) = f.Files.First();
            return Leaf(library, path, header + Step + file.Stem, file.Parts);
        }
        return new PaletteMenuNode
        {
            Header   = header,
            Children = [AllIn(library, header, f.Path), .. ChildrenOf(library, f)],
        };
    }

    private static PaletteMenuNode Leaf(string library, string file, string header, int parts)
        => new()
        {
            Header = $"{header} ({parts:N0})",
            Entry  = PaletteCategoryEntry.ForVendor(library, file, BreadcrumbFor(library, file, isFile: true)),
        };

    private static PaletteMenuNode AllIn(string library, string header, string path)
        => new()
        {
            Header = $"All in {header}",
            Entry  = PaletteCategoryEntry.ForVendor(library, path, BreadcrumbFor(library, path)),
        };

    /// <summary>
    /// <c>Library ▸ folder ▸ … ▸ file stem</c> for a path inside the library (<c>""</c> is the whole
    /// library). Every level, collapsed and skipped ones included, so the selection always says where
    /// in the library it is.
    /// </summary>
    public static string BreadcrumbFor(string library, string path, bool isFile = false)
    {
        if (path.Length == 0) return library;
        var segs = path.Split('/');
        // A file is named by its stem, as the menu names it.
        if (isFile) segs[^1] = System.IO.Path.GetFileNameWithoutExtension(segs[^1]);
        return library + Step + string.Join(Step, segs);
    }
}
