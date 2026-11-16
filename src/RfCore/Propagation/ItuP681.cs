// ================================================================
//  ItuP681.cs  —  ITU-R P.681's land mobile-satellite state model (brief PCH-10 R-pch10-3, design note §4.4)
//
//  Transcribed from Recommendation ITU-R P.681-11 (08/2019), Annex 1 §6 (the enhanced 2-state model) and Annex 2
//  (Tables 7 to 10 and the parameter sets of §§2-4). The current edition's state model has TWO states, GOOD (line of
//  sight and slightly shadowed) and BAD (more severely shadowed, blockage included); there is no third state in it.
//
//  The model (§6, §6.2):
//    · Events alternate GOOD, BAD, GOOD, … ; an event's length (m) is lognormal, ln x ~ N(µ, σ), at least durmin (a
//      shorter draw is redrawn: here the truncated distribution is sampled by its inverse, one draw, the same law).
//    · Each event draws its Loo triplet: MA ~ N(µMA, σMA) within [µ − 1.645σ, µ + 1.645σ] (GOOD) or within the
//      [pB,min, pB,max] quantiles (BAD), again redrawn when outside (sampled by the inverse); ΣA = g1·MA + g2 (dB),
//      MP = h1·MA + h2 (dB).
//    · Between two events a transition of Ltrans = f1·|ΔMA| + f2 (m, eq. 26) over which MA, ΣA and MP go linearly
//      in dB from one event's to the next's.
//    · Inside: the direct signal 10^(A/20), A ~ N(MA, ΣA) correlated over Lcorr (the exponential correlation of
//      eq. 29's filter: here a sum of sinusoids at spatial frequencies drawn from that correlation's spectrum, so it is
//      stateless), plus diffuse multipath of power 10^(MP/10), Rayleigh with the classical (Jakes, eq. 28) spectrum at
//      f_m = v·f/c. Levels are relative to the unobstructed line of sight (0 dB).
//    · The state averages of §6.1: <dur> (eq. 17a, the mean of the truncated lognormal), <dur>T (eq. 17b), and the
//      occupancies pG, pB (eq. 19), a transition counted half to each side.
//
//  A drop starts at a uniformly placed point of the stationary series: in a GOOD event, a BAD event or a transition with
//  probabilities in proportion to <dur>G, <dur>B and 2<dur>T, an event's length then length-biased (ln x ~ N(µ + σ², σ),
//  durmin as before) and the point uniform within it. A transition's state is the one it leaves over its first half,
//  the one it enters over its second (eq. 19's convention), so the state a drop starts in is GOOD with probability pG.
//
//  Table selection (§6.1 Step 0): of the environment's parameter sets, the nearest frequency, then the nearest
//  elevation. Validity: 1.5-20 GHz (§6.1: "the suggested parameter values ... limit the applicable frequency range of
//  1.5 GHz to 20 GHz") and elevations 20°-90°; outside them refused.
//
//  As printed (src/RfCore/RESOLVED.md): the 2.2 GHz Village 30° and 3.8 GHz Suburban 30° sets label their MA, h and g rows
//  "h, g, h" — read by position, as Table 7's format orders them; the 2.2 GHz Suburban 70° set repeats the Urban 70°
//  numbers exactly, transcribed as printed.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using RfCore.Statistics;

namespace RfCore.Propagation
{
    public enum P681Environment { Urban, Suburban, Village, RuralWooded, Residential, Rural, Highway, Railway }

    /// <summary>One state's parameters (Annex 2, Table 7): event lengths ln x ~ N(Mu, Sigma) in metres, at least DurMin; MA ~ N(MuMa,
    /// SigmaMa) dB; MP = H1·MA + H2 dB; ΣA = G1·MA + G2 dB; the direct signal's correlation length Lcorr (m).</summary>
    public sealed record P681State(double Mu, double Sigma, double DurMin, double MuMa, double SigmaMa, double H1, double H2,
                                   double G1, double G2, double Lcorr);

    /// <summary>One parameter set of Annex 2: frequency, environment, elevation; the two states; the transition length f1·|ΔMA| + f2 (m);
    /// the BAD state's MA probability range.</summary>
    public sealed record P681Set(double FrequencyHz, P681Environment Environment, int ElevationDeg, P681State Good, P681State Bad,
                                 double F1, double F2, double PbMin, double PbMax)
    {
        public string Label => $"{FrequencyHz / 1e9:0.###} GHz / {ItuP681.Name(Environment)} / {ElevationDeg}°";
    }

    public static partial class ItuP681
    {
        public const string Document = "ITU-R P.681-11 (08/2019)";
        private const string M = "P681";

        public static string Name(P681Environment e) => e switch
        {
            P681Environment.RuralWooded => "rural wooded",
            _ => e.ToString().ToLowerInvariant(),
        };

        public static readonly string[] Words = ["urban", "suburban", "village", "ruralwooded", "residential", "rural", "highway", "railway"];

        public static bool TryParse(string word, out P681Environment e)
        {
            switch (word.Trim().ToLowerInvariant())
            {
                case "urban": e = P681Environment.Urban; return true;
                case "suburban": e = P681Environment.Suburban; return true;
                case "village": e = P681Environment.Village; return true;
                case "ruralwooded": e = P681Environment.RuralWooded; return true;
                case "residential": e = P681Environment.Residential; return true;
                case "rural": e = P681Environment.Rural; return true;
                case "highway": e = P681Environment.Highway; return true;
                case "railway": e = P681Environment.Railway; return true;
                default: e = default; return false;
            }
        }

        /// <summary>Every parameter set of Annex 2, as printed (§§2-4).</summary>
        public static readonly IReadOnlyList<P681Set> Sets =
        [
            new(2.2e9, P681Environment.Urban, 20, new(2.0042, 1.2049, 3.9889, -3.3681, 3.3226, 0.1739, -11.5966, 0.0036, 1.323, 0.968), new(3.689, 0.9796, 10.3114, -18.1771, 3.2672, 1.1411, 4.0581, -0.2502, -1.2528, 0.968), 0.087, 2.8469, 0.1, 0.9),
            new(2.2e9, P681Environment.Urban, 30, new(2.7332, 1.103, 7.3174, -2.3773, 2.1222, 0.0941, -13.1679, -0.2811, 0.9323, 1.4731), new(2.7582, 1.221, 5.7276, -17.4276, 3.9532, 0.9175, -0.8009, -0.1484, 0.591, 1.4731), 0.1378, 3.3733, 0.1, 0.9),
            new(2.2e9, P681Environment.Urban, 45, new(3.0639, 1.698, 10.0, -1.8225, 1.1317, -0.0481, -14.745, -0.4643, 0.3334, 1.791), new(2.9108, 1.2602, 6.0, -15.4844, 3.3245, 0.9434, -1.7555, -0.0798, 2.8101, 1.791), 0.0744, 2.1423, 0.1, 0.9),
            new(2.2e9, P681Environment.Urban, 60, new(2.8135, 1.5962, 10.0, -1.5872, 1.2446, -0.5168, -17.406, -0.1953, 0.5353, 1.7977), new(2.0211, 0.6568, 1.9126, -14.1435, 3.2706, 0.6975, -7.5383, 0.0422, 3.203, 1.7977), -0.1285, 5.4991, 0.1, 0.9),
            new(2.2e9, P681Environment.Urban, 70, new(4.2919, 2.4703, 118.3312, -1.8434, 0.537, -4.7301, -26.5687, 0.5192, 1.9583, 2.0963), new(2.1012, 1.0341, 4.8569, -12.9383, 1.7588, 2.5318, 16.8468, 0.3768, 8.4377, 2.0963), -0.0826, 2.8824, 0.1, 0.9),
            new(2.2e9, P681Environment.Suburban, 20, new(2.2201, 1.2767, 2.2914, -2.7191, 1.384, -0.3037, -13.0719, -0.1254, 0.7894, 0.929), new(2.2657, 1.3812, 2.5585, -13.8808, 2.583, 1.0136, 0.5158, -0.1441, 0.7757, 0.929), 0.2904, 1.0324, 0.1, 0.9),
            new(2.2e9, P681Environment.Suburban, 30, new(3.0138, 1.4161, 8.3214, -0.7018, 1.2107, -0.6543, -14.6457, -0.1333, 0.8992, 1.7135), new(2.4521, 0.7637, 5.9087, -11.9823, 3.4728, 0.62, -7.5485, -0.1644, 0.2762, 1.7135), 0.1091, 3.3, 0.1, 0.9),
            new(2.2e9, P681Environment.Suburban, 45, new(4.5857, 1.3918, 126.8375, -1.1496, 1.0369, 0.2148, -17.8462, 0.0729, 1.0303, 3.2293), new(2.2414, 0.7884, 4.3132, -10.3806, 2.3543, 0.0344, -14.2087, 0.0662, 3.5043, 3.2293), 0.5766, 0.7163, 0.1, 0.9),
            new(2.2e9, P681Environment.Suburban, 60, new(3.4124, 1.4331, 19.5431, -0.7811, 0.7979, -2.1102, -19.7954, -0.2284, 0.2796, 2.0215), new(1.9922, 0.7132, 3.1213, -12.1436, 3.1798, 0.4372, -8.3651, -0.2903, -0.6001, 2.0215), -0.4097, 8.744, 0.1, 0.9),
            new(2.2e9, P681Environment.Suburban, 70, new(4.2919, 2.4703, 118.3312, -1.8434, 0.537, -4.7301, -26.5687, 0.5192, 1.9583, 2.0963), new(2.1012, 1.0341, 4.8569, -12.9383, 1.7588, 2.5318, 16.8468, 0.3768, 8.4377, 2.0963), -0.0826, 2.8824, 0.1, 0.9),
            new(2.2e9, P681Environment.Village, 20, new(2.7663, 1.1211, 6.5373, -2.5017, 2.3059, 0.0238, -11.4824, -0.2735, 1.3898, 0.8574), new(2.2328, 1.3788, 2.8174, -15.23, 5.0919, 0.9971, 0.897, -0.0568, 1.9253, 0.8574), 0.0644, 2.674, 0.1, 0.9),
            new(2.2e9, P681Environment.Village, 30, new(2.4246, 1.3025, 5.4326, -2.2284, 1.4984, -0.3431, -14.0798, -0.2215, 1.0077, 0.8264), new(1.898, 1.0505, 2.4696, -15.1583, 4.0987, 0.9614, 0.3719, -0.0961, 1.3123, 0.8264), -0.0576, 3.3977, 0.1, 0.9),   // printed with its MA, h and g rows labelled h, g, h
            new(2.2e9, P681Environment.Village, 45, new(2.8402, 1.4563, 10.4906, -1.2871, 0.6346, -0.0222, -16.7316, -0.3905, 0.488, 1.4256), new(1.8509, 0.8736, 2.6515, -12.6718, 3.1722, 0.8329, -3.9947, -0.098, 1.3381, 1.4256), -0.0493, 5.3952, 0.1, 0.9),
            new(2.2e9, P681Environment.Village, 60, new(3.763, 1.2854, 17.6726, -0.5364, 0.6115, -0.1418, -17.8032, -0.212, 0.7819, 0.883), new(1.7192, 1.142, 2.5981, -9.5399, 2.0732, -0.4454, -16.8201, 0.0609, 2.5925, 0.883), -0.8818, 10.161, 0.1, 0.9),
            new(2.2e9, P681Environment.Village, 70, new(4.0717, 1.2475, 30.8829, -0.334, 0.6279, -1.6253, -19.7558, -0.4438, 0.6355, 1.5633), new(1.5673, 0.5948, 2.1609, -8.3686, 2.5603, 0.1788, -9.5153, -0.0779, 1.1209, 1.5633), -0.3483, 5.1244, 0.1, 0.9),
            new(2.2e9, P681Environment.RuralWooded, 20, new(2.1597, 1.3766, 2.0744, -0.8065, 1.5635, -0.917, -12.1228, -0.0348, 0.9571, 0.8845), new(1.9587, 1.5465, 1.3934, -10.6615, 2.617, 0.844, -1.4804, -0.1069, 1.6141, 0.8845), 0.055, 2.6383, 0.1, 0.9),
            new(2.2e9, P681Environment.RuralWooded, 30, new(2.5579, 1.2444, 3.5947, -1.3214, 1.6645, -1.0445, -14.3176, -0.1656, 0.718, 1.0942), new(2.3791, 1.1778, 2.28, -10.424, 2.4446, 0.6278, -4.8146, -0.0451, 2.2327, 1.0942), 0.0256, 3.8527, 0.1, 0.9),
            new(2.2e9, P681Environment.RuralWooded, 45, new(3.1803, 1.3427, 6.7673, -0.9902, 1.0348, -0.4235, -16.838, -0.1095, 0.6893, 2.3956), new(2.5382, 1.1291, 3.3683, -10.2891, 2.309, 0.3386, -9.7118, -0.046, 2.131, 2.3956), 0.2803, 4.0004, 0.1, 0.9),
            new(2.2e9, P681Environment.RuralWooded, 60, new(2.9322, 1.3234, 5.7209, -0.6153, 1.1723, -1.4024, -16.9664, -0.2516, 0.5353, 1.7586), new(2.1955, 1.1115, 1.6512, -9.9595, 2.2188, 0.2666, -9.0046, -0.0907, 1.473, 1.7586), 0.1099, 4.2183, 0.1, 0.9),
            new(2.2e9, P681Environment.RuralWooded, 70, new(3.8768, 1.4738, 16.0855, -0.7818, 0.7044, -2.9566, -20.0326, -0.2874, 0.405, 1.6546), new(1.8445, 0.8874, 2.9629, -6.7769, 2.1339, -0.3723, -14.9638, -0.1822, 0.1163, 1.6546), -0.3914, 6.6931, 0.1, 0.9),
            new(2.2e9, P681Environment.Residential, 20, new(2.5818, 1.731, 9.2291, -0.8449, 1.305, -0.3977, -12.3714, 0.0984, 1.3138, 1.1578), new(1.7136, 1.1421, 1.6385, -10.8315, 2.2642, 0.8589, -2.4054, -0.1804, 0.8553, 1.1578), 0.0994, 2.42, 0.1, 0.9),
            new(2.2e9, P681Environment.Residential, 30, new(3.281, 1.42, 14.4825, -1.3799, 1.001, -0.8893, -16.4615, -0.2432, 0.6519, 1.9053), new(1.8414, 0.9697, 2.7681, -11.1669, 2.4724, -0.103, -13.7102, -0.1025, 1.7671, 1.9053), 0.0196, 3.9374, 0.1, 0.9),
            new(2.2e9, P681Environment.Residential, 60, new(3.255, 1.287, 6.47, 0.0, 0.3, -2.024, -19.454, 0.273, 0.403, 3.84), new(3.277, 1.26, 7.81, -2.32, 2.06, -1.496, -22.894, -0.361, -0.119, 3.84), -1.591, 12.274, 0.1, 0.9),
            new(2.2e9, P681Environment.Residential, 70, new(4.3291, 0.7249, 27.3637, -0.1625, 0.3249, 0.6321, -21.5594, 0.1764, 0.4135, 1.6854), new(3.4534, 0.9763, 8.9481, -1.6084, 0.5817, -0.3976, -22.7905, -0.0796, 0.1939, 1.6854), 3.0127, 6.2345, 0.1, 0.9),
            new(3.8e9, P681Environment.Urban, 20, new(2.5467, 1.0431, 5.261, -2.7844, 2.6841, 0.1757, -12.9417, -0.2044, 1.5866, 1.4243), new(3.689, 0.9796, 10.3114, -19.4022, 3.2428, 0.9638, -0.9382, 0.0537, 4.567, 1.4243), 0.1073, 1.9199, 0.1, 0.9),
            new(3.8e9, P681Environment.Urban, 30, new(2.0158, 1.2348, 4.5491, -3.7749, 2.2381, -0.1564, -15.1531, -0.0343, 1.0602, 0.8999), new(2.2627, 1.4901, 2.0749, -17.9098, 2.9828, 0.825, -2.5833, -0.0741, 2.1406, 0.8999), 0.2707, -0.0287, 0.1, 0.9),
            new(3.8e9, P681Environment.Urban, 45, new(2.3005, 1.696, 10.0, -1.4466, 1.1472, 0.155, -13.6861, 0.1666, 1.2558, 1.6424), new(2.6314, 1.121, 6.0, -15.3926, 3.2527, 0.9509, -1.2462, 0.0363, 4.4356, 1.6424), 0.2517, -0.3512, 0.1, 0.9),
            new(3.8e9, P681Environment.Urban, 60, new(2.4546, 1.9595, 10.0, -1.6655, 0.8244, -0.4887, -17.2505, -0.3373, 0.3285, 2.3036), new(1.8892, 0.8982, 1.9126, -14.4922, 3.4941, 0.4501, -9.6935, 0.1202, 4.8329, 2.3036), 0.0025, 1.4949, 0.1, 0.9),
            new(3.8e9, P681Environment.Urban, 70, new(2.8354, 2.4631, 67.5721, -1.0455, 0.2934, -3.0973, -20.7862, 0.0808, 0.8952, 2.2062), new(1.517, 1.1057, 3.6673, -14.2294, 5.4444, 0.0908, -15.8022, 0.0065, 3.152, 2.2062), 0.0755, 2.1426, 0.1, 0.9),
            new(3.8e9, P681Environment.Suburban, 20, new(2.8194, 1.6507, 11.1083, -4.8136, 1.9133, -0.45, -17.9227, -0.1763, 0.8244, 1.2571), new(2.5873, 1.3919, 4.4393, -17.097, 2.935, 0.8991, -2.4082, 0.0582, 4.0347, 1.2571), 0.0727, 2.8177, 0.1, 0.9),
            new(3.8e9, P681Environment.Suburban, 30, new(2.9226, 1.384, 6.7899, -1.9611, 1.846, 0.2329, -15.0063, 0.0334, 1.3323, 1.6156), new(2.7375, 0.689, 7.7356, -15.3022, 2.9379, 0.5146, -8.9987, 0.088, 4.4692, 1.6156), 0.1281, 2.3949, 0.1, 0.9),   // printed with its MA, h and g rows labelled h, g, h
            new(3.8e9, P681Environment.Suburban, 45, new(4.3019, 0.853, 36.1277, -1.273, 0.9286, 0.205, -17.567, 0.0074, 0.749, 1.1191), new(2.3715, 1.3435, 9.5511, -5.6373, 2.9302, -0.7188, -21.0513, -0.2896, -0.3951, 1.1191), -0.9586, 10.8084, 0.1, 0.9),
            new(3.8e9, P681Environment.Suburban, 60, new(2.8958, 1.7061, 13.9133, -1.1987, 1.0492, -1.6501, -18.9375, -0.1369, 0.4477, 3.0619), new(1.9128, 0.6869, 2.9398, -13.1811, 2.6228, 0.6911, -6.0721, 0.0598, 3.722, 3.0619), -0.0419, 5.892, 0.1, 0.9),
            new(3.8e9, P681Environment.Suburban, 70, new(4.1684, 1.0766, 42.0185, 0.16, 0.5082, -3.4369, -18.1632, -1.1144, 0.9703, 2.5817), new(1.4778, 0.7033, 1.8473, -10.2225, 1.8417, 0.3934, -9.6284, -0.1331, 0.7223, 2.5817), -0.1129, 4.0555, 0.1, 0.9),
            new(3.8e9, P681Environment.Village, 20, new(2.0262, 1.2355, 2.2401, -3.1324, 1.8929, -0.4368, -15.1009, -0.0423, 1.2532, 0.838), new(1.9451, 1.4293, 1.9624, -16.5697, 4.0368, 1.0921, 1.644, -0.0325, 2.4452, 0.838), 0.059, 1.5623, 0.1, 0.9),
            new(3.8e9, P681Environment.Village, 30, new(2.4504, 1.1061, 2.3941, -1.8384, 1.796, -0.5582, -14.4416, -0.4545, 0.8188, 0.9268), new(1.7813, 1.2802, 2.1484, -15.4143, 4.5579, 0.8549, -2.2415, -0.0761, 1.6768, 0.9268), -0.033, 2.7056, 0.1, 0.9),
            new(3.8e9, P681Environment.Village, 45, new(2.291, 1.4229, 2.8605, -0.0018, 1.1193, -1.2023, -14.0732, -0.1033, 0.9299, 0.9288), new(1.2738, 1.1539, 0.7797, -12.1063, 2.9814, 0.6537, -4.5948, -0.0815, 1.6693, 0.9288), 0.0002, 1.9694, 0.1, 0.9),
            new(3.8e9, P681Environment.Village, 60, new(3.0956, 1.3725, 8.1516, -0.522, 1.095, 0.0831, -16.8546, 0.0411, 1.1482, 1.2251), new(1.092, 1.208, 0.7934, -12.1817, 3.3604, 1.1006, 0.5381, -0.0098, 2.4287, 1.2251), -0.053, 2.7165, 0.1, 0.9),
            new(3.8e9, P681Environment.Village, 70, new(3.9982, 1.332, 28.322, -1.3403, 0.7793, -0.4861, -19.5316, -0.2356, 0.7178, 1.4378), new(1.4165, 0.4685, 2.5168, -11.956, 1.5654, 0.5663, -6.8615, -0.2903, -1.2715, 1.4378), -0.0983, 3.9005, 0.1, 0.9),
            new(3.8e9, P681Environment.RuralWooded, 20, new(2.0294, 1.428, 1.7836, -3.2536, 1.6159, -0.5718, -16.1382, -0.0805, 0.943, 1.0863), new(2.029, 1.5493, 1.5269, -14.3363, 2.7753, 0.8186, -2.9963, -0.0822, 1.766, 1.0863), 0.1263, 1.4478, 0.1, 0.9),
            new(3.8e9, P681Environment.RuralWooded, 30, new(2.1218, 1.4895, 2.4539, -1.5431, 1.8811, -0.7288, -14.1626, -0.1241, 0.9482, 1.3253), new(2.2051, 1.5741, 2.1289, -12.8884, 3.0097, 0.6635, -4.6034, -0.0634, 2.3898, 1.3253), 0.0849, 1.6324, 0.1, 0.9),
            new(3.8e9, P681Environment.RuralWooded, 45, new(3.1803, 1.3427, 6.7673, 0.0428, 1.6768, -0.9948, -14.4265, -0.1377, 1.0077, 2.0419), new(2.4017, 1.1315, 3.5668, -11.3173, 2.7467, 0.2929, -9.791, -0.0387, 2.6194, 2.0419), 0.1894, 2.1378, 0.1, 0.9),
            new(3.8e9, P681Environment.RuralWooded, 60, new(2.4961, 1.4379, 3.7229, -1.0828, 1.0022, -1.2973, -16.6791, -0.1187, 0.6254, 1.9038), new(2.2113, 1.1254, 1.9001, -12.3044, 2.3641, 0.5456, -6.466, -0.0443, 2.3029, 1.9038), 0.1624, 1.8417, 0.1, 0.9),
            new(3.8e9, P681Environment.RuralWooded, 70, new(2.8382, 1.3804, 6.8051, -0.8923, 0.9455, -1.3425, -17.5636, -0.121, 0.6444, 2.1466), new(2.147, 1.0038, 1.9195, -11.5722, 2.3437, 0.3459, -9.5399, -0.0275, 2.6238, 2.1466), 0.0593, 2.8854, 0.1, 0.9),
            new(3.8e9, P681Environment.Residential, 20, new(2.905, 1.7236, 10.7373, -1.4426, 1.2989, 0.4875, -13.5981, 0.1343, 1.8247, 1.2788), new(2.1969, 0.9865, 2.2901, -14.4036, 3.0396, 0.5813, -6.979, -0.0911, 2.1475, 1.2788), 0.2334, 0.7612, 0.1, 0.9),
            new(3.8e9, P681Environment.Residential, 30, new(2.7334, 1.6971, 10.2996, -0.9996, 1.0752, 0.3407, -14.8465, -0.0413, 1.2006, 1.7072), new(1.8403, 0.9268, 1.8073, -12.9855, 2.8149, 0.3553, -9.9284, 0.0501, 3.8667, 1.7072), 0.0443, 2.2591, 0.1, 0.9),
            new(3.8e9, P681Environment.Residential, 60, new(3.4044, 1.398, 10.4862, 0.464, 0.706, 0.371, -19.6032, 0.0332, 0.5053, 1.8017), new(2.5534, 1.7143, 4.7289, -2.3787, 0.8123, -2.3834, -24.6987, 0.0172, 0.7237, 1.8017), 3.1149, 3.5721, 0.1, 0.9),
            new(3.8e9, P681Environment.Residential, 70, new(2.9223, 1.0267, 7.3764, -0.1628, 0.5104, 0.159, -20.4767, 0.1137, 0.4579, 1.3531), new(2.5188, 1.3166, 7.2801, -2.3703, 1.5998, -1.0228, -22.4769, -0.0986, 0.2879, 1.3531), -0.0538, 5.1204, 0.1, 0.9),
            new(11.7e9, P681Environment.Rural, 34, new(1.7663, 1.935, 0.9, 0.05, 0.0, 0.0, -40.25, 0.0, 0.39, 0.5), new(-0.4722, 1.7232, 0.8, -16.0, 10.4, 0.87, -14.26, -0.21, 0.0, 0.5), 0.088, 1.21, 0.1, 0.9),
            new(11.7e9, P681Environment.Suburban, 34, new(1.0125, 1.6944, 1.5, -0.02, 0.0, 0.0, -38.17, 0.0, 0.39, 0.5), new(-0.8026, 1.288, 1.1, -5.4, 7.3, 0.69, -15.97, -0.21, 0.0, 0.5), 0.036, 0.8, 0.1, 0.6),
            new(20.0e9, P681Environment.Suburban, 30, new(1.66, 1.64, 0.01, -0.23, 0.49, 0.0, -30.99, 0.0, 0.49, 7.9), new(0.1, 1.7, 0.5, -8.93, 8.41, 0.48, -11.37, -0.45, 0.0, 7.9), 0.08, 1.67, 0.006, 0.921),
            new(20.0e9, P681Environment.Highway, 30, new(1.27, 1.86, 0.01, -0.16, 0.39, 0.0, -29.61, 0.0, 0.39, 31.7), new(-0.31, 1.35, 0.5, -5.92, 8.2, 0.34, -14.39, -0.41, 0.0, 31.7), 0.15, 1.28, 0.001, 0.861),
            new(20.0e9, P681Environment.Urban, 30, new(1.95, 1.82, 0.01, -0.21, 0.44, 0.0, -32.62, 0.0, 0.44, 5.67), new(0.05, 1.4, 1.0, -13.96, 8.93, 0.68, -10.06, -0.37, 0.0, 5.67), 0.1, 1.49, 0.03, 0.97),
            new(20.0e9, P681Environment.Railway, 30, new(1.37, 1.94, 0.01, -0.19, 0.47, 0.0, -26.07, 0.0, 0.47, 19.52), new(-0.02, 1.92, 0.5, -5.84, 7.47, 0.47, -12.78, -0.41, 0.0, 19.52), 0.12, 1.78, 0.0006, 0.88),
        ];
    }
}
