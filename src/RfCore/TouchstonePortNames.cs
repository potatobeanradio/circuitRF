// ================================================================
//  TouchstonePortNames.cs  —  which port number is which named port
//
//  A Touchstone file numbers its ports 1..N and says nothing else
//  about them. A file written from a run whose ports have names (a
//  component port is its part's reference designator) says which is
//  which in header comments, one per port, in the spelling many tools
//  write and read:
//
//      ! Port[1] = P1
//      ! Port[2] = C1
//
//  Written by the EM result writers; read back by the schematic's SnP
//  symbol to name its leads. Reading stops at the option line, so a
//  header is all that is ever read.
// ================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RfCore
{
    public static class TouchstonePortNames
    {
        private static readonly Regex Line = new(@"^\s*Port\[(\d+)\]\s*=\s*(.*?)\s*$", RegexOptions.CultureInvariant);

        /// <summary>The comment text (without the leading <c>!</c>) for each named port, in number order. A name is made
        /// ASCII and single-line, because a Touchstone header is both; a blank name writes nothing for that port.</summary>
        public static IReadOnlyList<string> Lines(IEnumerable<(int Number, string? Name)> ports)
            => [.. ports.OrderBy(p => p.Number)
                        .Select(p => (p.Number, Name: Clean(p.Name)))
                        .Where(p => p.Name.Length > 0)
                        .Select(p => $"Port[{p.Number}] = {p.Name}")];

        /// <summary>
        /// The names <paramref name="comments"/> give ports 1..<paramref name="ports"/> (index 0 is port 1), or null when
        /// they name none. A port with no line, or a number outside the file's ports, reads as null. Comment text is taken
        /// with or without its leading <c>!</c>.
        /// </summary>
        public static string?[]? Parse(IEnumerable<string> comments, int ports)
        {
            if (ports <= 0) return null;
            var names = new string?[ports];
            bool any = false;
            foreach (string raw in comments)
            {
                var m = Line.Match(raw.TrimStart().TrimStart('!'));
                if (!m.Success || !int.TryParse(m.Groups[1].Value, out int n) || n < 1 || n > ports) continue;
                string name = m.Groups[2].Value;
                if (name.Length == 0) continue;
                names[n - 1] = name;
                any = true;
            }
            return any ? names : null;
        }

        private static readonly ConcurrentDictionary<string, (DateTime Stamp, long Length, string?[]? Names)> Cache =
            new(StringComparer.Ordinal);

        /// <summary>
        /// The port names the header of the file at <paramref name="path"/> states, for a file of
        /// <paramref name="ports"/> ports; null when it states none or cannot be read. Only the header is read (it stops
        /// at the option line), and the answer is kept until the file's time or length changes, so asking on every redraw
        /// costs a file-system stat.
        /// </summary>
        public static string?[]? FromFile(string? path, int ports)
        {
            if (string.IsNullOrEmpty(path) || ports <= 0) return null;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return null;
                string key = path + "|" + ports;
                if (Cache.TryGetValue(key, out var hit) && hit.Stamp == info.LastWriteTimeUtc && hit.Length == info.Length)
                    return hit.Names;

                var comments = new List<string>();
                using (var reader = new StreamReader(path, Encoding.ASCII, detectEncodingFromByteOrderMarks: true))
                {
                    for (string? l = reader.ReadLine(); l is not null; l = reader.ReadLine())
                    {
                        string t = l.TrimStart();
                        if (t.Length == 0) continue;
                        if (t[0] == '!') { comments.Add(t); continue; }
                        break;                                      // the option line, a keyword or data: the header is over
                    }
                }
                var names = Parse(comments, ports);
                Cache[key] = (info.LastWriteTimeUtc, info.Length, names);
                return names;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return null;
            }
        }

        private static string Clean(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            var sb = new StringBuilder(name.Length);
            foreach (char c in name.Trim()) sb.Append(c is >= ' ' and < (char)127 ? c : '?');
            return sb.ToString();
        }
    }
}
