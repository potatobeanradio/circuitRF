# Fixed pictures

The pictures in this folder are **fixed**: DocGen links to them and never draws, writes or deletes them, and they do not
change when the example workspaces change. Every other figure under `assets/` is generated. It is also the only folder that
may hold raster images (`DocsFactoryTests.NoDocumentationImageIsABitmap` exempts it alone): the two Eight Fingers pictures are
PNG. The four Output Wires pictures are SVG — `render` writes a clip-plane field as one flat-shaded path per mesh triangle,
275–615 KB each, no larger than the PNGs they replaced.

Each is **one `circuitrf render --field` command** on a run of its example (brief-em3d-88). DocGen does not run them,
because each needs a thermal solve first (Gmsh), and the Eight Fingers solve is about 9 minutes. To re-make one: copy
the example somewhere writable, run its setup with `circuitrf em <view>.c3d --setup <setup>`, then run the command
below from the example's folder.

| File | What it is | Made from |
|---|---|---|
| `eight-fingers-temperature-top.png` | *Eight Fingers*' steady temperature, an XY cut 1 µm under the surface | `examples/Thermal Channel vs Surface`, setup `Array`: `circuitrf render "Eight Fingers/3d/Eight Fingers.c3d" -o eight-fingers-temperature-top.png --field "Top — 1 µm under the surface" --tight --margin 0.04 --axes --scale-bar` |
| `eight-fingers-temperature-section.png` | the same run, an XZ cut across the fingers at y = 1 µm | as above, with `-o eight-fingers-temperature-section.png --field "Across the fingers — y = 1 µm"` |
| `output-wires-dc-along-wire.svg` | *Thermal Output Wires* at 14 A DC, an XZ cut along wire 4 (y = 75 µm) | `examples/Thermal Output Wires`, setup `DcSweep`: `circuitrf render Output/3d/Output.c3d -o output-wires-dc-along-wire.svg --field "DC 14 A — along wire 4" --labels --tight --axes --scale-bar` |
| `output-wires-dc-across.svg` | the same run, a YZ cut across the six wires at x = 820 µm | as above, with `-o output-wires-dc-across.svg --field "DC 14 A — across the six wires"` |
| `output-wires-rf-across.svg` | the `RfHarmonics` run at 8 A peak, 2 GHz, the same YZ cut: the edge wires hottest | setup `RfHarmonics`, with `-o output-wires-rf-across.svg --field "RF 8 A peak — across the six wires"` |
| `output-wires-rf-plan.svg` | the same run, an XY cut through the wires' loops at z = 330 µm | as above, with `-o output-wires-rf-plan.svg --field "RF 8 A peak — plan through the loops"` |

The plots are the documents' own field plots. The two Eight Fingers ones are kept hidden, so that 3D view still opens
on its Surface plot. Each picture is `render`'s own page: the labels give each solid's material ("Mould compound (generic)"),
and the axis indicator and scale bar are the 3D view's.
