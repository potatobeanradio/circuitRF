// ================================================================
//  PropagationModels.cs  —  the one registry of the large-scale models
//
//  Every model is described once: its name, its document, and its
//  parameters (name, unit, default, range, help, group). The Chain
//  Budget's editor (PCH-3), the Propagation Channel component (PCH-6) and
//  `reference` all read this table, so a parameter is never described
//  twice. A model whose evaluation reads a parameter the table does not
//  name fails a scan test.
//
//  Values arrive as text in SI units (the unit each parameter states): a
//  number, a word from the parameter's list, a comma-separated list of
//  numbers, or a file path. Blank means the stated default; a default of
//  "" means the model works it out (and says so). The geometry every
//  model shares — frequency, horizontal distance, the two antenna
//  heights, and a terrain profile where one is needed — is on
//  ModelInputs itself.
//
//  The added losses (AddedLosses.cs) are parameters of every model: set,
//  each adds its own line to the breakdown.
// ================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RfCore.Propagation
{
    public enum ParameterKind { Number, Word, List, File }

    /// <param name="Name">The parameter's name, as written in a document.</param>
    /// <param name="Unit">Its SI unit ("" for a pure number or a word).</param>
    /// <param name="Default">Its default as text; "" means the model works it out or none is needed.</param>
    /// <param name="Help">One line for an editor or `reference`.</param>
    /// <param name="Group">The editor group: "Large scale" or "Added losses".</param>
    public sealed record ModelParameter(string Name, string Unit, string Default, string Help, ParameterKind Kind = ParameterKind.Number,
                                        double Min = double.NegativeInfinity, double Max = double.PositiveInfinity,
                                        IReadOnlyList<string>? Words = null, string Group = "Large scale");

    /// <param name="Validity">Where the model holds, as it refuses outside it: one line for the user documentation and
    /// <c>reference</c>, beside the parameters' own ranges.</param>
    public sealed record ModelDescriptor(string Name, string Title, string Document, IReadOnlyList<ModelParameter> Parameters,
                                         bool UsesHeights, bool UsesProfile, Func<ModelArgs, PathLossResult> Run, string Validity = "");

    /// <summary>The geometry every model shares, and the parameter values as text.</summary>
    public sealed class ModelInputs
    {
        public string Model { get; init; } = "free";
        public double FrequencyHz { get; init; }
        /// <summary>The horizontal distance between the antennas, m (the profile's length when a profile is given).</summary>
        public double DistanceM { get; init; } = double.NaN;
        public double TxHeightM { get; init; } = 1.5;
        public double RxHeightM { get; init; } = 1.5;
        public TerrainProfile? Profile { get; init; }
        /// <summary>Parameter values by name, as text in each parameter's SI unit.</summary>
        public IReadOnlyDictionary<string, string> Values { get; init; } = new Dictionary<string, string>();
        /// <summary>brief-pch-10 R-pch10-1 — <c>Rain=drawn</c>: the time percentage this drop drew (0–100), the rain attenuation then
        /// the one exceeded for it. Null outside a drop, and <c>Rain=drawn</c> is then refused.</summary>
        public double? RainTimePercent { get; init; }

        public double Distance3DM => Math.Sqrt(DistanceM * DistanceM + (TxHeightM - RxHeightM) * (TxHeightM - RxHeightM));
    }

    /// <summary>A model's inputs with its parameters checked against the registry and defaults filled in.</summary>
    public sealed class ModelArgs
    {
        private readonly ModelDescriptor _model;
        private readonly Dictionary<string, ModelParameter> _byName;
        private readonly IReadOnlyDictionary<string, string> _values;
        private readonly HashSet<string> _read = new(StringComparer.OrdinalIgnoreCase);

        public ModelInputs Inputs { get; }
        public double F => Inputs.FrequencyHz;
        public double D => Inputs.DistanceM;
        public double D3 => Inputs.Distance3DM;
        public double Htx => Inputs.TxHeightM;
        public double Hrx => Inputs.RxHeightM;
        public TerrainProfile? Profile { get; }

        internal ModelArgs(ModelDescriptor model, ModelInputs inputs, TerrainProfile? profile)
        {
            _model = model;
            Inputs = inputs;
            Profile = profile;
            _byName = model.Parameters.Concat(AddedLosses.Parameters).ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
            _values = inputs.Values;
        }

        /// <summary>The names this evaluation read (the registry scan test compares them with the table).</summary>
        public IReadOnlyCollection<string> Read => _read;

        private ModelParameter Param(string name)
            => _byName.TryGetValue(name, out var p) ? p
               : throw new InvalidOperationException($"{_model.Name} reads the parameter '{name}', which its registry entry does not name.");

        /// <summary>The raw text (the default when blank or absent).</summary>
        public string Text(string name)
        {
            var p = Param(name);
            _read.Add(p.Name);
            return _values.TryGetValue(p.Name, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : p.Default;
        }

        /// <summary>True when the parameter was given (not blank).</summary>
        public bool Given(string name)
        {
            var p = Param(name);
            _read.Add(p.Name);
            return _values.TryGetValue(p.Name, out var v) && !string.IsNullOrWhiteSpace(v);
        }

        public double Number(string name)
        {
            var p = Param(name);
            string t = Text(name);
            if (t.Length == 0) return double.NaN;
            if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                throw new PropagationRefusal($"{_model.Name}: {p.Name} must be a number{(p.Unit.Length > 0 ? " in " + p.Unit : "")}; it is '{t}'.");
            if (double.IsNaN(v) || v < p.Min || v > p.Max)
                throw new PropagationRefusal(PropagationDiagnostics.OutOfRange(_model.Name, p.Name, v, p.Min, p.Max, p.Unit));
            return v;
        }

        public string Word(string name)
        {
            var p = Param(name);
            string t = Text(name).ToLowerInvariant();
            if (p.Words is not null && !p.Words.Contains(t, StringComparer.OrdinalIgnoreCase))
                throw new PropagationRefusal($"{_model.Name}: {p.Name} is one of {string.Join(", ", p.Words)}; it is '{t}'.");
            return t;
        }

        public double[] List(string name)
        {
            var p = Param(name);
            string t = Text(name);
            if (t.Length == 0) return Array.Empty<double>();
            return t.Split(',').Select(s =>
                double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v)
                    ? v
                    : throw new PropagationRefusal($"{_model.Name}: {p.Name} is a comma-separated list of numbers; '{s.Trim()}' is not one.")).ToArray();
        }

        public TerrainProfile RequireProfile()
            => Profile ?? throw new PropagationRefusal($"{_model.Name}: this model needs a terrain profile (Profile, a CSV of distance and height).");
    }

    public static class PropagationModels
    {
        private static readonly string[] LosWords = { "auto", "los", "nlos" };
        private static readonly string[] LosOnlyWords = { "los", "nlos" };
        private static readonly string[] PolWords = { "vertical", "horizontal" };

        private static ModelParameter N(string name, string unit, string def, string help, double min = double.NegativeInfinity, double max = double.PositiveInfinity)
            => new(name, unit, def, help, ParameterKind.Number, min, max);
        private static ModelParameter W(string name, string def, string help, params string[] words)
            => new(name, "", def, help, ParameterKind.Word, Words: words);

        private static readonly ModelParameter ProfileParam =
            new("Profile", "", "", "a terrain profile CSV: distance and height columns with units, optional clutter, surface and zone", ParameterKind.File);

        public static readonly IReadOnlyList<ModelDescriptor> All = new List<ModelDescriptor>
        {
            new("free", "Free space", FreeSpaceModel.Document, Array.Empty<ModelParameter>(), true, false,
                a => FreeSpaceModel.Evaluate(a.F, a.D3), "any frequency and distance: λ/4πd between isotropic antennas"),

            new("logdist", "Log-distance", LogDistanceModel.Document, new[]
            {
                N("n", "", "2", "the distance exponent beyond d0", 0.1, 10),
                N("d0", "m", "1", "the reference distance: free space up to it", 1e-3, 1e6),
                N("Sigma", "dB", "0", "the shadowing standard deviation", 0, 30),
            }, true, false, a => LogDistanceModel.LogDistance(a.F, a.D3, a.Number("n"), a.Number("d0"), a.Number("Sigma")),
                "any frequency; free space to d0, the stated exponent beyond: the numbers are yours, from a measurement or a reference"),

            new("multislope", "Multi-slope log-distance", LogDistanceModel.Document, new[]
            {
                new ModelParameter("Breaks", "m", "", "the breakpoints, increasing, comma-separated", ParameterKind.List),
                new ModelParameter("Exponents", "", "2", "one exponent per segment (one more than the breakpoints)", ParameterKind.List),
                N("d0", "m", "1", "the reference distance: free space up to it", 1e-3, 1e6),
                N("Sigma", "dB", "0", "the shadowing standard deviation", 0, 30),
            }, true, false, a => LogDistanceModel.MultiSlope("multislope", a.F, a.D3, a.Number("d0"), a.List("Breaks"), a.List("Exponents"), a.Number("Sigma")),
                "any frequency; free space to d0, one stated exponent per segment between increasing breakpoints"),

            new("tworay", "Two-ray ground reflection", TwoRayModel.Document, new[]
            {
                N("GroundEr", "", "15", "the ground's relative permittivity", 1, 100),
                N("GroundSigma", "S/m", "0.005", "the ground's conductivity", 0, 100),
                W("Polarization", "vertical", "the polarization", PolWords),
            }, true, false, a => TwoRayModel.Evaluate(a.F, a.D, a.Htx, a.Hrx, a.Number("GroundEr"), a.Number("GroundSigma"),
                    a.Word("Polarization") == "horizontal" ? TwoRayModel.Polarization.Horizontal : TwoRayModel.Polarization.Vertical),
                "line of sight over flat ground, any frequency; both heights above 0"),

            new("38901", "3GPP TR 38.901 scenarios", Tr38901.Document, new[]
            {
                W("Scenario", "UMa", "the scenario", "uma", "umi", "rma", "inh-mixed", "inh-open", "inf-sl", "inf-dl", "inf-sh", "inf-dh", "inf-hh"),
                W("Los", "auto", "the propagation state: auto gives the more probable one, with its probability", LosWords),
                N("StreetWidth", "m", "20", "RMa: the average street width W", 5, 50),
                N("BuildingHeight", "m", "5", "RMa: the average building height h", 5, 50),
                N("ClutterDensity", "", "", "InF: the fraction of the hall's area occupied by clutter (blank: the sub-scenario's)", 1e-6, 0.999999),
                N("ClutterHeight", "m", "", "InF: the effective clutter height (blank: the sub-scenario's)", 0, 25),
                N("ClutterSize", "m", "", "InF: the typical clutter size (blank: the sub-scenario's)", 0.01, 100),
                N("HallLength", "m", "", "InF: the hall's length (blank: the sub-scenario's)", 1, 1000),
                N("HallWidth", "m", "", "InF: the hall's width (blank: the sub-scenario's)", 1, 1000),
                N("HallHeight", "m", "", "InF: the hall's height (blank: the sub-scenario's)", 1, 100),
            }, true, false, Run38901,
                "0.5 to 100 GHz (RMa to 30 GHz); UMa and UMi 10 m to 5 km, RMa 10 m to 10 km in line of sight and 5 km without, InH 1 to 150 m, InF 1 to 600 m; the report's base station and terminal heights"),

            new("P1238", "ITU-R P.1238 indoor", ItuP1238.Document, new[]
            {
                W("Environment", "office", "the indoor environment", "office", "corridor", "industrial", "conference"),
                W("Los", "los", "line of sight or not", LosOnlyWords),
                N("Floors", "", "0", "floors between the stations", 0, 50),
                W("Building", "office", "the building type for the floor penetration loss", "office", "commercial", "residential"),
                N("FloorLoss", "dB", "", "a stated floor penetration loss (blank: the recommendation's table)", 0, 200),
            }, true, false, a =>
            {
                double floors = a.Number("Floors");
                if (floors != Math.Floor(floors)) throw new PropagationRefusal($"P1238: Floors is a whole number; it is {PropagationMath.F(floors)}.");
                return ItuP1238.Evaluate(a.Word("Environment") switch
                    {
                        "corridor" => IndoorEnvironment.Corridor,
                        "industrial" => IndoorEnvironment.Industrial,
                        "conference" => IndoorEnvironment.Conference,
                        _ => IndoorEnvironment.Office,
                    }, a.Word("Los") == "los", a.F, a.D3, (int)floors,
                    a.Word("Building") switch { "commercial" => FloorBuilding.Commercial, "residential" => FloorBuilding.Residential, _ => FloorBuilding.Office },
                    a.Number("FloorLoss"));
            }, "0.3 to 300 GHz and 2 to 160 m, each environment and state over its own row of the recommendation's Table 2; floor losses at 0.9, 1.8 to 2, 2.4, 3.5, 5.2 and 5.8 GHz, or stated"),

            new("P1411", "ITU-R P.1411 short-range outdoor", ItuP1411.Document, new[]
            {
                W("Mode", "belowroof", "which of the recommendation's models", "belowroof", "aboveroof", "lowheight", "corner"),
                W("Environment", "urbanlowrise", "belowroof, aboveroof and corner: the environment", "urbanhighrise", "urbanlowrise", "suburban", "residential"),
                W("Los", "los", "belowroof, aboveroof: line of sight or not", LosOnlyWords),
                W("Urban", "urban", "lowheight: the urban category", "suburban", "urban", "denseurban"),
                N("Locations", "%", "50", "lowheight: the percentage of locations the loss is not exceeded at", 1, 99),
                N("CornerDistance", "m", "", "lowheight: the corner distance when known (blank: the recommendation's statistics)", 0, 3000),
                N("X1", "m", "", "corner: distance along the line-of-sight street to the crossing", 0, 10000),
                N("X2", "m", "", "corner: distance from the crossing along the second street", 0, 10000),
                N("W1", "m", "20", "corner: width of the line-of-sight street", 1, 200),
                N("W2", "m", "20", "corner: width of the second street", 1, 200),
                N("CornerAngle", "deg", "90", "corner: the angle between the streets", 0, 180),
                W("Chamfered", "no", "corner, above 2 GHz: a chamfered corner building", "no", "yes"),
                N("RoadHeight", "m", "0", "corner, up to 15 GHz: the effective road height hs (traffic)", 0, 10),
                N("n", "", "2", "corner, above 15 GHz: the line-of-sight street's loss exponent", 1, 5),
            }, true, false, RunP1411,
                "below and above roof: 0.45 to 300 GHz over the distances of each environment's row; terminals near street level: 300 MHz to 3 GHz, to 3 km; around a corner: 0.8 to 38 GHz"),

            new("P526", "ITU-R P.526 diffraction", ItuP526.Document, new[]
            {
                W("Mode", "profile", "knifeedge: one edge; sphere: a smooth Earth; profile: a general path over a profile", "knifeedge", "sphere", "profile"),
                N("EdgeHeight", "m", "0", "knifeedge: the edge's height above the line between the antennas (negative below)", -1e5, 1e5),
                N("EdgeDistance", "m", "", "knifeedge: the edge's distance from the transmitter", 0, 1e8),
                N("EarthRadiusFactor", "", "1.333333333", "sphere, profile: the effective Earth-radius factor k", 0.1, 100),
                N("GroundEr", "", "22", "sphere, profile: the ground's relative permittivity", 1, 100),
                N("GroundSigma", "S/m", "0.003", "sphere, profile: the ground's conductivity", 0, 100),
                W("Polarization", "vertical", "the polarization", PolWords),
                ProfileParam,
            }, true, true, RunP526,
                "knife edge and a general path over a profile from 30 MHz, a smooth spherical Earth from 10 MHz, any distance"),

            new("satellite", "Satellite path", "circular-orbit geometry; " + Tr38811.Document, new[]
            {
                N("Altitude", "km", "600", "the satellite's altitude (a circular orbit; 35786 is geostationary)", 1, 1e6),
                N("Elevation", "deg", "30", "the satellite's elevation seen from the earth station", 0, 90),
                W("Pass", "setting", "for the Doppler: the satellite rising (approaching) or setting (receding)", "rising", "setting"),
                W("Scenario", "none", "TR 38.811's terminal environment for line of sight, shadowing and clutter (none: open, line of sight)",
                  "none", "denseurban", "urban", "suburban", "rural"),
                W("Los", "auto", "with a scenario: the propagation state; auto gives the more probable one", LosWords),
            }, false, false, RunSatellite,
                "a circular orbit of any altitude, elevation 0 to 90° (10° and up with a TR 38.811 scenario); the atmospheric terms each over their own recommendation's range"),

            new("P1812", "ITU-R P.1812 point-to-area over a profile", ItuP1812.Document, new[]
            {
                ProfileParam,
                N("Time", "%", "50", "the percentage of time the loss is not exceeded", 1, 50),
                N("Locations", "%", "50", "the percentage of locations the loss is not exceeded at", 1, 99),
                N("DeltaN", "N-units/km", "45", "the average refractivity lapse rate through the lowest 1 km", 0, 156.9),
                N("N0", "N-units", "325", "the sea-level surface refractivity", 200, 450),
                N("Latitude", "deg", "45", "the path centre's latitude", -80, 80),
                N("CoastTx", "km", "500", "the transmitter's distance from the coast (paths over sea)", 0, 1e4),
                N("CoastRx", "km", "500", "the receiver's distance from the coast (paths over sea)", 0, 1e4),
                N("SigmaL", "dB", "", "the location variability's standard deviation (blank: from the resolution)", 0, 30),
                N("Resolution", "m", "100", "the prediction resolution the location variability applies over", 1, 1e5),
                W("Polarization", "vertical", "the polarization", PolWords),
            }, true, true, a => ItuP1812.Evaluate(a.RequireProfile(), new P1812Inputs(
                a.F, a.Number("Time"), a.Number("Locations"), a.Htx, a.Hrx, a.Word("Polarization") == "vertical",
                a.Number("DeltaN"), a.Number("N0"), a.Number("Latitude"), a.Number("CoastTx"), a.Number("CoastRx"),
                a.Number("SigmaL"), a.Number("Resolution"))),
                "30 MHz to 6 GHz, paths 0.25 to 3000 km over a terrain profile, antennas 1 to 3000 m above ground, 1 to 50 % of time, 1 to 99 % of locations, latitude within ±80°"),
        };

        public static ModelDescriptor? Find(string name)
            => All.FirstOrDefault(m => string.Equals(m.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));

        /// <summary>Evaluates the model <see cref="ModelInputs.Model"/> names, its added losses included.</summary>
        public static PathLossResult Evaluate(ModelInputs inputs) => Evaluate(inputs, out _);

        /// <summary>As <see cref="Evaluate(ModelInputs)"/>, also giving the parameter names the evaluation read.</summary>
        public static PathLossResult Evaluate(ModelInputs inputs, out IReadOnlyCollection<string> read)
        {
            var model = Find(inputs.Model)
                        ?? throw new PropagationRefusal($"There is no propagation model '{inputs.Model}'; the models are {string.Join(", ", All.Select(m => m.Name))}.");
            var known = model.Parameters.Concat(AddedLosses.Parameters).Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var key in inputs.Values.Keys)
                if (!known.Contains(key))
                    throw new PropagationRefusal($"{model.Name}: there is no parameter '{key}'; it takes {string.Join(", ", known)}.");

            TerrainProfile? profile = inputs.Profile;
            if (model.UsesProfile && profile is null && inputs.Values.TryGetValue("Profile", out var path) && !string.IsNullOrWhiteSpace(path))
                profile = TerrainProfile.Read(path.Trim());
            if (profile is not null && !double.IsNaN(inputs.DistanceM)) profile.RequireLength(inputs.DistanceM, model.Name);
            if (profile is not null && double.IsNaN(inputs.DistanceM))
                inputs = new ModelInputs
                {
                    Model = inputs.Model, FrequencyHz = inputs.FrequencyHz, DistanceM = profile.LengthKm * 1000,
                    TxHeightM = inputs.TxHeightM, RxHeightM = inputs.RxHeightM, Profile = profile, Values = inputs.Values,
                    RainTimePercent = inputs.RainTimePercent,
                };

            var args = new ModelArgs(model, inputs, profile);
            var result = model.Run(args);
            var (terms, notes) = AddedLosses.Evaluate(args, result);
            read = args.Read;
            return result.With(terms, notes);
        }

        // ---------------------------------------------------------------- adapters

        private static PathLossResult Run38901(ModelArgs a)
        {
            string word = a.Word("Scenario");
            if (!Tr38901.TryParse(word, out var s)) throw new PropagationRefusal($"38901: there is no scenario '{word}'.");
            var los = a.Word("Los") switch { "los" => LosState.Los, "nlos" => LosState.Nlos, _ => LosState.Auto };
            InfClutter? clutter = null;
            if (s is Scenario38901.InFSL or Scenario38901.InFDL or Scenario38901.InFSH or Scenario38901.InFDH or Scenario38901.InFHH)
            {
                var c = InfClutter.Default(s);
                double Or(string n, double d) { double v = a.Number(n); return double.IsNaN(v) ? d : v; }
                clutter = new InfClutter(Or("ClutterDensity", c.Density), Or("ClutterHeight", c.HeightM), Or("ClutterSize", c.SizeM),
                                         Or("HallLength", c.HallLengthM), Or("HallWidth", c.HallWidthM), Or("HallHeight", c.HallHeightM));
            }
            return Tr38901.Evaluate(s, los, a.F, a.D, a.Htx, a.Hrx, a.Number("StreetWidth"), a.Number("BuildingHeight"), clutter);
        }

        private static PathLossResult RunP1411(ModelArgs a)
        {
            var env = a.Word("Environment") switch
            {
                "urbanhighrise" => StreetEnvironment.UrbanHighRise,
                "suburban" => StreetEnvironment.Suburban,
                "residential" => StreetEnvironment.Residential,
                _ => StreetEnvironment.UrbanLowRise,
            };
            switch (a.Word("Mode"))
            {
                case "aboveroof": return ItuP1411.SiteGeneral(true, env, a.Word("Los") == "los", a.F, a.D3);
                case "lowheight":
                    return ItuP1411.LowHeight(a.Word("Urban") switch { "suburban" => UrbanCategory.Suburban, "denseurban" => UrbanCategory.DenseUrban, _ => UrbanCategory.Urban },
                                              a.F, a.D3, a.Number("Locations"), a.Number("CornerDistance"));
                case "corner":
                {
                    if (!a.Given("X1") || !a.Given("X2")) throw new PropagationRefusal("P1411: the corner model needs X1 and X2.");
                    var r = ItuP1411.Corner(a.F, a.Number("X1"), a.Number("X2"), a.Number("W1"), a.Number("W2"), a.Number("CornerAngle"),
                                            env == StreetEnvironment.Residential, a.Word("Chamfered") == "yes", a.Htx, a.Hrx, a.Number("RoadHeight"), a.Number("n"));
                    return r with { Notes = r.NoteList.Append("The corner model's path is X1 then X2 along the streets; the straight-line distance is not used.").ToList() };
                }
                default: return ItuP1411.SiteGeneral(false, env, a.Word("Los") == "los", a.F, a.D3);
            }
        }

        private static PathLossResult RunSatellite(ModelArgs a)
        {
            var geo = SatelliteGeometry.At(a.Number("Altitude"), a.Number("Elevation"), a.F, a.Word("Pass") == "rising");
            double fs = PropagationMath.FreeSpaceDb(a.F, geo.SlantRangeKm * 1000);
            var terms = new List<LossTerm> { new("free space (slant range)", fs) };
            var notes = new List<string>
            {
                $"Slant range {PropagationMath.F(geo.SlantRangeKm)} km, latency {PropagationMath.F(geo.LatencyS * 1e3)} ms; " +
                $"Doppler {PropagationMath.F(geo.DopplerHz)} Hz drifting {PropagationMath.F(geo.DopplerRateHzps)} Hz/s (a pass through the zenith; Earth's rotation not included).",
            };
            if (!double.IsNaN(a.D)) notes.Add("The satellite model's distance is the slant range from altitude and elevation; the stated distance is not used.");
            double sigma = 0;
            double? pLos = null;
            string sc = a.Word("Scenario");
            if (sc != "none")
            {
                var s = sc switch { "denseurban" => NtnScenario.DenseUrban, "urban" => NtnScenario.Urban, "suburban" => NtnScenario.Suburban, _ => NtnScenario.Rural };
                pLos = Tr38811.LosProbability(s, geo.ElevationDeg);
                bool los = a.Word("Los") switch { "los" => true, "nlos" => false, _ => pLos >= 0.5 };
                if (a.Word("Los") == "auto")
                    notes.Add($"Los=auto: the line-of-sight probability is {PropagationMath.F(pLos.Value)}, so the {(los ? "line-of-sight" : "non-line-of-sight")} loss is given.");
                var (sig, cl) = Tr38811.LargeScale(s, los, a.F / 1e9, geo.ElevationDeg);
                sigma = sig;
                if (cl > 0) terms.Add(new("38.811 clutter loss", cl));
            }
            double total = terms.Sum(t => t.Db);
            return new PathLossResult("satellite", Tr38811.Document, total, terms, sigma, pLos, Notes: notes) { Geometry = geo };
        }

        private static PathLossResult RunP526(ModelArgs a)
        {
            bool vertical = a.Word("Polarization") == "vertical";
            switch (a.Word("Mode"))
            {
                case "knifeedge":
                {
                    if (!a.Given("EdgeDistance")) throw new PropagationRefusal("P526: the knife-edge model needs EdgeDistance, the edge's distance from the transmitter.");
                    double d1 = a.Number("EdgeDistance");
                    if (!(d1 > 0 && d1 < a.D))
                        throw new PropagationRefusal($"P526: the edge must lie between the antennas: EdgeDistance is {PropagationMath.F(d1)} m on a {PropagationMath.F(a.D)} m path.");
                    return ItuP526.KnifeEdge(a.F, a.Number("EdgeHeight"), d1, a.D - d1);
                }
                case "sphere":
                    return ItuP526.Sphere(a.F, a.D, a.Htx, a.Hrx, a.Number("EarthRadiusFactor"), a.Number("GroundEr"), a.Number("GroundSigma"), vertical);
                default:
                    return ItuP526.Profile(a.F, a.RequireProfile(), a.Htx, a.Hrx, a.Number("EarthRadiusFactor"), a.Number("GroundEr"), a.Number("GroundSigma"), vertical);
            }
        }
    }
}
