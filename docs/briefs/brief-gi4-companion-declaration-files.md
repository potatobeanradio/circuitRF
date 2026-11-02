# Sonnet Brief — Phase GI4: the folder's own declarations

**Series:** `docs/sonnet-briefs/brief-gi-series.md`. Fourth of five.
**Consumes:** L4f's format inference ladder, L4g's classifier.
**Touches:** `src/Design/Layout/Interchange/GerberFileClassifier.cs`,
`src/Design/Layout/Interchange/ExcellonFormat.cs`,
`src/Design/Layout/Interchange/ExcellonReader.Parse.cs`,
`src/Design/Layout/Interchange/GerberImport.cs`, plus one new file for the reader.

**Test loop:**
```
dotnet test tests/Ui.Tests --no-build
dotnet test tests/Firewall.Tests --no-build
```

---

## 1. The shape of the problem

**When a folder contains a plain-text parameter file stating the coordinate format the whole output job
was written with, circuitRF classifies it as "no Gerber or drill content in its head", skips it, and
then infers the same format from the coordinates.** In the same run it reports, in words, that the
digit format was *inferred* — while the file that declares it sits three lines above in the skipped
list.

The declarations in circulation take a `KEYWORD  VALUE` form, one per line, uppercase and hyphenated —
the coordinate format as an integer/decimal digit split, the output units, and one flag per zero class
saying whether that class is suppressed. A companion tool listing takes a comparable form and carries
the column that a drill file most often omits: whether each tool is plated.

`GerberFileClassifier.ClassifyContent` (`GerberFileClassifier.cs:70-84`) has exactly three recognisers —
artwork, drill, job file — and everything else falls through to `Other`. There is no fourth kind, so
there is nowhere for a declaration to go.

**L4f's own reasoning is what makes this worth doing.** `ExcellonFormat.cs`'s header states the risk in
terms this phase does not improve on:

> The failure mode is the worst kind: the file parses cleanly and yields a board a thousand times too
> large.

An inference that is usually right is still an inference. A declaration in the same folder is the
strongest evidence available short of the file speaking for itself, and it is currently thrown away.

## 2. A fourth file kind

**R-gi4-1. Add `GerberFileKind.Declaration`, recognised BY CONTENT.** This is not a new doctrine, it is
the existing one: the classifier's own header states that nothing in it may branch on an extension,
because "renaming a file to a misleading extension does not change what it is classified as, which is
gate 2". A declaration file is recognised by its **keywords**, never by a filename convention — which
also keeps any toolchain's naming out of this repository.

Require at least two recognised keywords before claiming the kind. One keyword is a coincidence in
somebody's unrelated settings file, and the same caution is already written into `LooksLikeJobFile`,
which refuses to call arbitrary JSON a job file.

**R-gi4-2. Recognise a tool listing as the same kind, separately identified.** A drill tool listing is
a table of tool numbers, diameters and a plating column. `DrillEvidence` deliberately refuses to accept
one as a drill file (`GerberFileClassifier.cs:283-289` — it is "a table of prose"), and that refusal is
correct and stays. But refusing to *read* it is a different decision from refusing to *drill* from it.

## 3. Where a declaration ranks, and the trap that inverts it

**R-gi4-3. A declaration outranks every inference and is outranked by the file's own statement.** On
`DrillFormatEvidence`'s existing strongest-first ladder that is a new rung directly below `Override` and
above `FormatComment`… **for a file that states nothing itself**. A drill file carrying its own
`;FILE_FORMAT` or `INCH`/`METRIC` line is authoritative *about itself*; a job-wide declaration is
authoritative about the job. When they disagree, prefer the file, and **report the disagreement** — that
combination is the single most useful thing this phase can produce, because it is the only signal
anyone will ever get that one of the two is stale.

**R-gi4-4. The suppression sense is INVERTED relative to Excellon, and reading one as the other is an
error of orders of magnitude.** `ExcellonFormat.cs`'s header already flags this inversion for the
Gerber/Excellon pair and names it as the one place the conversion may happen:

* Gerber's `%FS<L|T>` names the zeros that are **omitted**.
* Excellon's `LZ`/`TZ` names the zeros that are **kept**.
* A declaration file's suppression flags name the zeros that are **suppressed** — the *Gerber* sense.

So a declaration maps onto `GerberZeroOmission` directly and must **not** go through the Excellon
inversion. Convert in one place, next to the existing conversion, with the same warning attached. A
declaration read through the wrong sense is a board wrong by a factor of ten thousand that parses
perfectly.

**R-gi4-5. A scale factor is never silently ignored.** These files can state a coordinate scale. If one
states anything other than unity, either honour it or refuse — and say which. Silently dropping it
multiplies the whole board.

**R-gi4-6. Scope a declaration to the files of its own kind in its own folder.** An artwork parameter
file speaks for the artwork; a drill parameter file speaks for the drill data. Do not apply either
across the boundary, and do not reach outside the folder — R-L4g-3's rule that an import must never
quietly consume something the user did not point at applies here unchanged.

**R-gi4-7. A tool listing is a plating source and nothing else.** Match by tool number and diameter and
take the plating column only. The drill file remains authoritative about every hit, every coordinate and
every diameter — a listing that disagrees about geometry is reported, never preferred. GI1's plating
field is where the answer lands.

## 4. Reporting

**R-gi4-8. A declaration that was read is named, with what it settled.** *"Read the coordinate format
from a parameter file in this folder: mm, 3:4, no zero suppression."* A declaration that was recognised
and **not** used says why — it contradicted a file that speaks for itself, or it covered a file kind not
present.

**R-gi4-9. Skipped files stay reported.** The existing "N file(s) in the set were not artwork or drill
data" summary keeps naming everything genuinely skipped. It gets shorter, which is the point.

## 5. The archive

**R-gi4-10. A folder whose only artwork is inside an archive is currently a dead end, and should be an
offer.** Such an archive is classified "not text" and skipped, which is true and useless. When the
chosen folder yields **no** artwork and **does** hold an archive, offer to look inside.

**An offer, never an action.** This is R-L4g-3's doctrine exactly — *"Never pull them in silently. An
import that quietly reached outside the folder the user pointed at is a surprise, and a surprise in a
file importer is a support question forever."* Extract to a temporary location, import from there, and
leave nothing behind. Headless (`circuitrf convert`, `circuitrf new`) this is a **refusal naming the
flag that answers it**, exactly as an unstated drill coordinate format already is — a dialog's question
becomes a flag, never a guess.

**R-gi4-11. Do not open an archive that the folder does not need.** A set that already yields artwork
imports from the files on disk and never touches an archive sitting beside them, even if it holds the
same thing. Reading both is how two versions of one board get silently merged.

## 6. Gates

1. **Two keywords minimum** — a one-keyword file classifies as `Other`; a genuine declaration
   classifies as `Declaration`. Content-only, no temporary directory, matching `ClassifyContent`'s
   existing test shape.
2. **Extension-blind** — a declaration renamed to a drill extension still classifies as a declaration,
   and a drill file renamed to a declaration extension still classifies as drill data. This is gate 2
   of the classifier's own doctrine and the reason it exists.
3. **The inversion** (R-gi4-4) — a declaration stating leading-zero suppression produces the same
   `GerberZeroOmission` as a Gerber `%FSL`, and the **opposite** of an Excellon `LZ`. Assert all three
   in one test, with the coordinate values that make the difference visible.
4. **Declaration beats inference** — a drill file with no statement of its own, plus a declaration,
   reads at the declared format and reports it as declared rather than inferred.
5. **File beats declaration, and the disagreement is reported** — a drill file with its own
   `;FILE_FORMAT` contradicting a declaration reads at the file's format and produces a message naming
   both.
6. **Scale** (R-gi4-5) — a non-unity scale is either applied or refused, and asserted either way; never
   dropped.
7. **Plating from a listing** (R-gi4-7) — a drill file with no plating statement plus a listing marking
   some tools non-plated yields non-plated holes for exactly those tools, and a via stackup entry that
   is not plated (GI1's field).
8. **Geometry is not taken from a listing** — a listing whose diameters disagree with the drill file
   produces the drill file's geometry and a message.
9. **Scoping** (R-gi4-6) — an artwork declaration does not alter drill reading, and vice versa.
10. **Archive offered, not taken** (R-gi4-10) — a folder with artwork plus an archive imports from the
    artwork and never extracts; a folder with only an archive produces an offer, and the headless path
    produces a refusal naming the flag. Nothing is left on disk in either case.
11. **Bit-identical when absent** — a set with no declaration file imports exactly as it does today.
    Compare the written `.ctech` and `.clay`, not a spot check.

## 7. Out of scope

* Net names, layer spans, and the via/component-hole distinction — GI5.
* Any binary drill format. L4f's R-L4f-3 refusal stands unchanged.
* Guessing a declaration's meaning from a filename. R-gi4-1 forbids it and the classifier's doctrine
  forbade it first.

## 8. On completion

Write **"Phase GI4 — COMPLETE"** at the top of `src/Design/RESOLVED.md` — **not** `CLAUDE.md`. Record:

1. **How often the declaration agreed with the inference** across every set on hand. If it always
   agreed, say so plainly — that is the number that says whether the inference ladder was already good
   enough, and it is worth knowing either way.
2. **Every case where it disagreed**, with the coordinates that differed. These are the sets that were
   importing wrongly and silently.
3. **What the keyword set ended up being**, and whether requiring two was enough to avoid false
   positives on the unrelated text files that live in these folders.
4. **Whether the archive path ever ran** — and if it did, what it cost in wall clock on a real set,
   since it is the only step in this import that unpacks anything.
