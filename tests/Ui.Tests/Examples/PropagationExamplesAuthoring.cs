// ================================================================
//  PropagationExamplesAuthoring.cs — the three Propagation Channel examples (brief PCH-12 R-pch12-3), BUILT from their
//  netlists with the function `circuitrf netlist --to-schematic` calls, rather than drawn by hand. Skipped unless
//  CRF_AUTHOR_PCH_EXAMPLES=1, because it WRITES under examples/: each README is kept, and so is everything of the
//  mmWave Array Link example that is not a bench (its Patch Element cell, technology and element record, and the two
//  channel cells, which were copied from the Phased Array example and edited).
//
//  Indoor IoT Link      — 1 Coverage (received power and outage over 2,000 drops, S-parameter) and 2 Bit Errors (FSK
//                         bit errors over 50 drops, envelope), P.1238 with partitions, a tapped delay line from a file.
//  LEO Satellite Downlink — Downlink: 16APSK through a transponder (IMUX, TWTA, OMUX) and a 600 km pass at 30°, P.618 at
//                         99.5 %, P.681 states, its Doppler; envelope over 40 drops.
//  mmWave Array Link    — Array Link: a 16 × 16 transmit array to an 8 × 8 receive array, TR 38.901 UMi through CDL-C,
//                         the beam pair found per drop, NR 64-QAM; envelope over 20 drops.
//
//  PropagationExamplesTests holds every bench to `check` and to its netlist here.
// ================================================================

using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Tests.Em3d;
using Xunit;

namespace CircuitRF.Ui.Tests.Examples;

public sealed class PropagationExamplesAuthoring
{
    public const string IotFolder = "Indoor IoT Link", LeoFolder = "LEO Satellite Downlink", MmFolder = "mmWave Array Link";

    public sealed class AuthorFactAttribute : FactAttribute
    {
        public AuthorFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("CRF_AUTHOR_PCH_EXAMPLES") != "1")
                Skip = "re-builds the three Propagation Channel examples only with CRF_AUTHOR_PCH_EXAMPLES=1";
        }
    }

    public static string Examples => Path.Combine(PalaceBackendTests.RepoRoot(), "examples");

    /// <summary>Every bench: its workspace, its cell, its netlist and the files its schematic names.</summary>
    public static readonly (string Folder, string Cell, string Netlist, string[] Files)[] Benches =
    [
        (IotFolder, "1 Coverage", IotCoverage, [IotProfile]),
        (IotFolder, "2 Bit Errors", IotBits, [IotProfile]),
        (LeoFolder, "Downlink", LeoDownlink, []),
        (MmFolder, "Array Link", MmLink, [MmRecord]),
    ];

    // ── Indoor IoT Link ─────────────────────────────────────────────────────────────────────────────────────────────

    public const string IotProfile = "profiles/office_pdp.csv";

    /// <summary>The office's power-delay profile: six taps over 110 ns, 18 ns RMS. A measured profile is written the same way.</summary>
    public const string IotProfileCsv = """
        delay (ns), power (dB)
        0, 0
        10, -2.5
        25, -5.8
        45, -9.1
        70, -13.0
        110, -18.4
        """;

    /// <summary>The sensor, the path and the gateway, shared by both IoT benches: a 10 dBm FSK sensor at 868.95 MHz, 28 m
    /// away two floors down through a concrete wall and two partitions (P.1238 office, no line of sight), the office's
    /// tapped delay line fading as Rayleigh at walking pace, and the gateway's 2 dBi antenna and 6 dB NF front end.</summary>
    private const string IotLink = """
        drop = 1
        Ptx = 10
        Fc = 868.95 MHz
        Sens = -98
        MOD_Source:SENSOR tx 0 Fc=Fc Pavg=Ptx dBm Z=50 Ohm Carrier[1]="FSK;Rb=100kbps;Dev=45kHz;Data=PN9;Off=0;P=0" Record=5.11e-3
        PropagationChannel:CH1 tx rx f0=Fc PathLoss=P1238 Environment=office Los=nlos Distance=28 TxHeight=1 RxHeight=2.5 Floors=2 Partitions="10,6,6" Multipath=file File="profiles/office_pdp.csv" Fading=rayleigh Velocity=1 TxGain=0 RxGain=2 Seed=1 Drop=drop
        Amp:LNA rx 0 lna 0 Gain=20 NF=6 IP3=200
        IProbe:Irx lna gw
        Port:GW gw 0 Num=1 Z=50 Ohm
        """;

    public const string IotCoverage = IotLink + "\n" + """
        analysis SP1 type=sparam start=868.95 startUnit=MHz stop=868.95 stopUnit=MHz npts=1
        analysis DROPS type=parametric_sweep Var=drop Start=1 Stop=2000 Step=1 Inner=SP1
        measure Prx_dBm = Ptx + SP1.link("CH1", "Gain_dB")
        measure PrxMedian_dBm = median_over(Prx_dBm, "drop")
        measure Outage = outage_over(Prx_dBm, Sens, "drop")
        measure Prx_cdf = cdf(Prx_dBm)
        """;

    public const string IotBits = IotLink + "\n" + """
        analysis ENV1 type=env Stop=5.11e-3 Accuracy=draft MaxHarm=1 Noise=yes SeedPerPoint=yes SaveWaveforms=no
        analysis BITS type=parametric_sweep Var=drop Start=1 Stop=50 Step=1 Inner=ENV1
        measure Prx_dBm = Ptx + ENV1.link("CH1", "Gain_dB")
        measure BER = ENV1.BER[:, "Irx", 0]
        measure BERmean = mean_over(BER, "drop")
        measure Outage = outage_over(Prx_dBm, Sens, "drop")
        """;

    // ── LEO Satellite Downlink ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The transponder (an IMUX filter, a TWTA at its output intercept 48 dBm, an OMUX filter) sending 16APSK at
    /// 10 Msym/s from 600 km, seen at 30° on a setting pass, to a vehicle's 30 dBi flat panel and 1.5 dB LNA: P.618 rain, gas,
    /// clouds and scintillation at 99.5 %, P.681's suburban states at 10 m/s, each drop one state.</summary>
    public const string LeoDownlink = """
        drop = 1
        Fc = 20 GHz
        Pin = -3
        MerReq = 15
        MOD_Source:UPLINK up 0 Fc=Fc Pavg=Pin dBm Z=50 Ohm Carrier[1]="SC;Rs=10Msps;Mod=16APSK;RollOff=0.2;Data=PN9;Preamble=64;PilotBlock=36/1440;Off=0;P=0"
        Filter:IMUX up 0 a 0 Response=Chebyshev Form=Bandpass Order=4 F1=19.99 GHz F2=20.01 GHz Ripple=0.1 dB Zin=50 Ohm Zout=50 Ohm
        Amp:TWTA a 0 b 0 Gain=40 IP3=48 dBm IP3Ref=Output
        Filter:OMUX b 0 c 0 Response=Chebyshev Form=Bandpass Order=4 F1=19.985 GHz F2=20.015 GHz Ripple=0.1 dB Zin=50 Ohm Zout=50 Ohm
        IProbe:Itx c tx
        PropagationChannel:CH1 tx rx f0=Fc PathLoss=satellite Altitude=600 Elevation=30 Pass=setting Availability=99.5 Rain=fixed Gas=on Clouds=on Scintillation=on AntennaDiameter=0.3 States=p681 StateEnvironment=suburban Velocity=10 TxGain=36 RxGain=30 Seed=1 Drop=drop
        Amp:LNA rx 0 lna 0 Gain=30 NF=1.5 IP3=200
        IProbe:Irx lna term
        Port:TERM term 0 Num=1 Z=50 Ohm
        analysis ENV1 type=env Stop=0.2e-3 Accuracy=draft MaxHarm=3 Noise=yes SeedPerPoint=yes SaveWaveforms=no Equalizer=pilot
        analysis PASS type=parametric_sweep Var=drop Start=1 Stop=40 Step=1 Inner=ENV1
        measure State = ENV1.link("CH1", "State")
        measure Doppler_Hz = ENV1.link("CH1", "Doppler0_Hz")
        measure Drift_Hzps = ENV1.link("CH1", "Drift0_Hzps")
        measure CFO_Hz = ENV1.CFO_Hz[:, "Irx", 0]
        measure EVM_pct = ENV1.EVM_pct[:, "Irx", 0]
        measure MER_dB = ENV1.MER_dB[:, "Irx", 0]
        measure BER = ENV1.BER[:, "Irx", 0]
        measure BERest = ENV1.BERest[:, "Irx", 0]
        measure EVMtx_pct = ENV1.EVM_pct[:, "Itx", 0]
        measure Outage = outage_over(MER_dB, MerReq, "drop")
        measure GoodFraction = mean_over(State, "drop")
        """;

    // ── mmWave Array Link ───────────────────────────────────────────────────────────────────────────────────────────

    public const string MmRecord = "results/Patch Element.cemr";

    /// <summary>A pre-driver that compresses, the 16 × 16 transmit array (each element through TxChannel: a 12 dB driver and
    /// a 20 dB amplifier), TR 38.901 UMi without line of sight over 400 m through CDL-C, and the 8 × 8 receive array (each
    /// element through RxChannel: a 20 dB, 1.5 dB NF LNA and a 3 dB attenuator) into its feed. Both arrays steer by
    /// steer(), so the channel picks their beams per drop. NR 20 MHz at 60 kHz, four symbols.</summary>
    public const string MmLink = """
        drop = 1
        f0 = 28 GHz
        Pin = -32
        MerReq = 20
        MOD_Source:GNB s 0 Fc=f0 Pavg=Pin dBm Z=50 Ohm Carrier[1]="NR;BW=20MHz;SCS=60kHz;Mod=64QAM;Off=0;P=0;Seed=1"
        Amp:PRE s 0 d 0 Gain=30 NF=6 IP3=40 dBm IP3Ref=Output
        PhasedArray:PT d Record="results/Patch Element.cemr" dx=0.5*lambda(f0) dy=0.5*lambda(f0) Mode=feed Rows=16 Cols=16 Channel=TxChannel Phase=steer(0,0,f0)
        PropagationChannel:CH1 Tx=PT Rx=PR f0=f0 Distance=400 TxHeight=10 RxHeight=1.5 PathLoss=38901 Scenario=umi Los=nlos Multipath=cdl Profile=CDL-C DelaySpread=100e-9 Seed=1 Drop=drop
        PhasedArray:PR r Record="results/Patch Element.cemr" dx=0.5*lambda(f0) dy=0.5*lambda(f0) Mode=feed Rows=8 Cols=8 Channel=RxChannel Phase=steer(0,0,f0)
        IProbe:Irx r ue
        Port:UE ue 0 Num=1 Z=50 Ohm
        analysis ENV1 type=env Stop=72e-6 Accuracy=draft MaxHarm=1 Noise=yes SeedPerPoint=yes SaveWaveforms=no Equalizer=pilot
        analysis DROPS type=parametric_sweep Var=drop Start=1 Stop=20 Step=1 Inner=ENV1
        measure Beam_dB = ENV1.link("CH1", "BeamChosen_dB")
        measure BoundLoss_dB = ENV1.link("CH1", "BeamCodebookLoss_dB")
        measure EVM_pct = ENV1.EVM_pct[:, "Irx", 0]
        measure MER_dB = ENV1.MER_dB[:, "Irx", 0]
        measure BER = ENV1.BER[:, "Irx", 0]
        measure BERest = ENV1.BERest[:, "Irx", 0]
        measure Outage = outage_over(MER_dB, MerReq, "drop")
        """;

    // ── building ────────────────────────────────────────────────────────────────────────────────────────────────────

    [AuthorFact]
    public void BuildTheWorkspaces()
    {
        foreach (string folder in new[] { IotFolder, LeoFolder })
        {
            string ws = Path.Combine(Examples, folder);
            string readme = Path.Combine(ws, "README.md");
            string? kept = File.Exists(readme) ? File.ReadAllText(readme) : null;
            if (Directory.Exists(ws)) Directory.Delete(ws, true);
            WorkspaceCreate.Create(Examples, folder, null);
            if (kept is not null) File.WriteAllText(readme, kept);
        }
        string profile = Path.Combine(Examples, IotFolder, IotProfile);
        Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
        File.WriteAllText(profile, IotProfileCsv.ReplaceLineEndings("\n") + "\n");

        foreach (var (folder, cell, _, _) in Benches.Where(b => b.Folder == MmFolder))
            if (Directory.Exists(Path.Combine(Examples, folder, cell))) Directory.Delete(Path.Combine(Examples, folder, cell), true);
        foreach (var (folder, cell, netlist, files) in Benches)
            ModelComposerExampleAuthoring.Draw(Path.Combine(Examples, folder), cell, netlist, null, files);
    }
}
