// .gbrjob job file (docs/sonnet-briefs/brief-L4c-gerber-export.md §2, R-L4c-2) — the X2 answer to
// "which files belong together," §8's own stated hard part of Gerber. A minimal but valid job-file JSON
// (Ucamco's own extension) listing every file in the export set with its FileFunction/FilePolarity.
// Serialized via System.Text.Json rather than hand-built strings so path escaping (backslashes on
// Windows, quotes) is never a source of a malformed file.

using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CircuitRF.Ui.Layout.Interchange;

public static class GerberJobFile
{
    public sealed record FileAttribute(string Path, string? FileFunction, string FilePolarity = "Positive");

    private sealed class JobFileModel
    {
        public JobFileHeader Header { get; set; } = new();
        public List<JobFileEntry> FilesAttributes { get; set; } = [];
    }

    private sealed class JobFileHeader
    {
        public JobFileGenSoftware GenerationSoftware { get; set; } = new();
        public string CreationDate { get; set; } = "";
    }

    private sealed class JobFileGenSoftware
    {
        public string Vendor { get; set; } = "circuitRF";
        public string Application { get; set; } = "circuitRF";
        public string Version { get; set; } = "";
    }

    private sealed class JobFileEntry
    {
        public string Path { get; set; } = "";

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? FileFunction { get; set; }

        public string FilePolarity { get; set; } = "Positive";
    }

    public static void Write(Stream stream, IReadOnlyList<FileAttribute> files, DateTime creationTimeUtc, string version)
    {
        var model = new JobFileModel
        {
            Header = new JobFileHeader
            {
                GenerationSoftware = new JobFileGenSoftware { Version = version },
                CreationDate = creationTimeUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            },
            FilesAttributes = files.Select(f => new JobFileEntry
            {
                Path = f.Path,
                FileFunction = f.FileFunction,
                FilePolarity = f.FilePolarity,
            }).ToList(),
        };

        JsonSerializer.Serialize(stream, model, new JsonSerializerOptions { WriteIndented = true });
    }
}
