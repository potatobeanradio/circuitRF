// `circuitrf sign` — sign a model library, or make the key to sign it with (brief vendorlib-15 R-vl15-3a).
//
// THERE IS NO SIGNING HERE. The sums, the signature, the key and every refusal are LibrarySignature in src/Design —
// the function check and the library rows verify with. What is here is argument parsing, refusals and reporting, on
// Authoring.cs' terms. No GUI command signs: publishing a library is a maker's build step, so this is its one spelling.

using CircuitRF.Diagnostics;
using CircuitRF.Design.VendorLibraries.ModelLibrary;

namespace CircuitRF.Cli;

public static class Sign
{
    public static int Run(string[] args)
    {
        JsonRun.Verb = "sign";
        string? library = null, key = null, newKey = null;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            bool more = i + 1 < args.Length;
            switch (a)
            {
                case "--key" when more:     key = args[++i]; break;
                case "--new-key" when more: newKey = args[++i]; break;
                default:
                    if (a.StartsWith('-')) return Refuse("sign.args.unknown-option", $"sign: '{a}' is not an option of this verb.", usage: true);
                    if (library is not null) return Refuse("sign.args.one-library", "sign: name one library folder.", usage: true);
                    library = a;
                    break;
            }
        }

        if (newKey is not null)
        {
            if (key is not null) return Refuse("sign.args.both-keys", "sign: --new-key makes a key and --key signs with one; pass one of them.", usage: true);
            // A library named beside --new-key is checked for the one mistake that cannot be undone.
            if (library is not null && LibrarySignature.KeyInsideLibrary(newKey, library) is { } inside)
                return Refuse("sign.key.inside-library", inside);
            string? fp = LibrarySignature.NewKey(newKey, out string? why);
            if (fp is null) return Refuse("sign.key.refused", why!);
            string full = Path.GetFullPath(newKey);
            Console.WriteLine(full);
            Console.WriteLine(fp);
            JsonRun.AddOutput("key", full);
            JsonRun.AddOutput("fingerprint", fp);
            Console.Error.WriteLine($"[circuitRF] Wrote a new P-256 key to {full}. Its fingerprint is {fp}. Keep the file private and outside every library.");
            return 0;
        }

        if (library is null) return Refuse("sign.args.library-required", "sign: name the library folder to sign.", usage: true);
        if (key is null) return Refuse("sign.args.key-required", "sign: --key <private.pem> is the key to sign with (make one with --new-key).", usage: true);
        JsonRun.InputPath = library;

        var result = LibrarySignature.Sign(library, key);
        if (!result.Succeeded) return Refuse("sign.refused", result.Refusal!);

        Console.WriteLine(result.SumsPath);
        Console.WriteLine(result.SigPath);
        JsonRun.AddOutput("sums", result.SumsPath!);
        JsonRun.AddOutput("signature", result.SigPath!);
        JsonRun.AddOutput("fingerprint", result.Fingerprint);
        Console.Error.WriteLine($"[circuitRF] Signed {result.Parts} model folder{(result.Parts == 1 ? "" : "s")} with key {result.Fingerprint}.");
        return 0;
    }

    private static int Refuse(string code, string text, bool usage = false)
    {
        JsonRun.Report(Diagnostic.Create(code, DiagnosticSeverity.Error, "{text}", ("text", text)));
        if (usage) Usage();
        return 1;
    }

    private static void Usage() => Console.Error.WriteLine(
        "Usage: circuitrf sign <library> --key <private.pem>   (writes library.sums and library.sig)\n" +
        "       circuitrf sign --new-key <private.pem>         (writes a new P-256 key; prints its fingerprint)\n" +
        "       [--json]");
}
