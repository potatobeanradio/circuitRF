/*
 * fake-osdi-model — a test-only shared library that speaks the OSDI ABI.
 *
 * WHY THIS EXISTS. The real producers of this ABI are Verilog-A models compiled by a GPL-licensed
 * compiler that is not installed here and must never be a build dependency. Without a stand-in, the
 * worker could only be exercised on a machine that already had a full model toolchain — which is the
 * same trap `tools/fake-model-lib` exists to avoid for the other worker's ABI, and it is solved the
 * same way: a tiny library that implements the ABI honestly and nothing else.
 *
 * IT IS NOT A MODEL. Every device here has a CLOSED-FORM answer written down in its comment, so a
 * test asserts against arithmetic rather than against another implementation. That is the whole
 * point — a second copy of our own code agreeing with itself proves nothing.
 *
 * NOT BUILT BY `dotnet build`. See build.sh.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include <stdlib.h>
#include <math.h>
#include <string.h>

#include "../osdi-worker/osdi.h"

/* ── device 1: "crf_rc" — a two-terminal parallel RC ───────────────────────────
 *
 *   v      = V(node0) - V(node1)
 *   g(T)   = g0 * (1 + tc * (T - tnom))        T in KELVIN, as OSDI supplies it
 *   I[0]   = +g(T)*v      I[1]   = -g(T)*v
 *   Q[0]   = +c*v         Q[1]   = -c*v
 *
 *   dI/dV  =  [ +g  -g ; -g  +g ]      dQ/dV = [ +c  -c ; -c  +c ]
 *
 * `tc` is what lets a test prove the TEMPERATURE actually arrived through setup_instance rather
 * than being silently defaulted — a temperature that never lands still produces finite, plausible
 * currents, so it has to be observable in the answer.
 */

typedef struct {
    double g0;    /* model param */
    double c;     /* model param */
    double tc;    /* model param, 1/K */
    double tnom;  /* model param, K   */
} RcModel;

typedef struct {
    double   mult;              /* instance param */

    /* Written by the HOST before eval. */
    uint32_t node_mapping[2];
    double  *jac_resist[4];
    double  *jac_react[4];
    bool     collapsed[1];      /* no collapsible pairs here; kept so the offset is real */

    /* Written by the MODEL during eval, read by the host at the declared byte offsets. */
    double   resid_resist[2];
    double   resid_react[2];

    /* Op-var: the temperature this instance was set up at. Observable, so a test can check it. */
    double   temp_k;

    double   g_eff;             /* cached g(T)*mult */
    double   c_eff;
} RcInst;

static void *rc_access(void *inst, void *model, uint32_t id, uint32_t flags) {
    (void)flags;
    RcModel *m = (RcModel *)model;
    RcInst  *i = (RcInst *)inst;
    /* Ids are the index into param_opvar[], which is what the worker uses. Nothing here depends on
     * model params and instance params being segregated in that array — the worker reads each
     * entry's own `flags` to learn which it is, so the ordering convention cannot bite us. */
    switch (id) {
        case 0: return m ? (void *)&m->g0   : NULL;
        case 1: return m ? (void *)&m->c    : NULL;
        case 2: return m ? (void *)&m->tc   : NULL;
        case 3: return m ? (void *)&m->tnom : NULL;
        case 4: return i ? (void *)&i->mult : NULL;
        case 5: return i ? (void *)&i->temp_k : NULL;   /* opvar, read-only in practice */
        default: return NULL;
    }
}

static void rc_setup_model(void *handle, void *model, OsdiSimParas *sim, OsdiInitInfo *res) {
    (void)handle; (void)sim;
    RcModel *m = (RcModel *)model;
    /* A host that sets no parameter must get the model's own defaults, so they are established
     * here rather than assumed to be zero-initialised into something useful. */
    if (m->tnom == 0.0) m->tnom = 300.0;
    res->flags = 0; res->num_errors = 0; res->errors = NULL;
}

static void rc_setup_instance(void *handle, void *inst, void *model, double temperature,
                              uint32_t num_terminals, OsdiSimParas *sim, OsdiInitInfo *res) {
    (void)handle; (void)num_terminals; (void)sim;
    RcModel *m = (RcModel *)model;
    RcInst  *i = (RcInst *)inst;

    if (i->mult == 0.0) i->mult = 1.0;

    i->temp_k = temperature;                       /* recorded so a test can see it arrived */
    double g  = m->g0 * (1.0 + m->tc * (temperature - m->tnom));
    i->g_eff  = g * i->mult;
    i->c_eff  = m->c * i->mult;

    res->flags = 0; res->num_errors = 0; res->errors = NULL;
}

static uint32_t rc_eval(void *handle, void *inst, const void *model, const OsdiSimInfo *info) {
    (void)handle; (void)model;
    RcInst *i = (RcInst *)inst;

    /* prev_solve is the HOST's solution vector; node_mapping says where this instance's nodes sit
     * in it. Going through the mapping rather than assuming 0,1 is the whole contract. */
    double v0 = info->prev_solve[i->node_mapping[0]];
    double v1 = info->prev_solve[i->node_mapping[1]];
    double v  = v0 - v1;

    i->resid_resist[0] =  i->g_eff * v;
    i->resid_resist[1] = -i->g_eff * v;
    i->resid_react[0]  =  i->c_eff * v;
    i->resid_react[1]  = -i->c_eff * v;
    return 0;
}

/* Jacobian entries, in the order declared below:  (0,0) (0,1) (1,0) (1,1) */
static void rc_load_jacobian_resist(void *inst, void *model) {
    (void)model;
    RcInst *i = (RcInst *)inst;
    double g = i->g_eff;
    if (i->jac_resist[0]) *i->jac_resist[0] +=  g;
    if (i->jac_resist[1]) *i->jac_resist[1] += -g;
    if (i->jac_resist[2]) *i->jac_resist[2] += -g;
    if (i->jac_resist[3]) *i->jac_resist[3] +=  g;
}

static void rc_load_jacobian_react(void *inst, void *model, double alpha) {
    (void)model;
    RcInst *i = (RcInst *)inst;
    double c = i->c_eff * alpha;
    if (i->jac_react[0]) *i->jac_react[0] +=  c;
    if (i->jac_react[1]) *i->jac_react[1] += -c;
    if (i->jac_react[2]) *i->jac_react[2] += -c;
    if (i->jac_react[3]) *i->jac_react[3] +=  c;
}

static void rc_load_residual_resist(void *inst, void *model, double *dst) {
    (void)model;
    RcInst *i = (RcInst *)inst;
    dst[i->node_mapping[0]] += i->resid_resist[0];
    dst[i->node_mapping[1]] += i->resid_resist[1];
}

static void rc_load_residual_react(void *inst, void *model, double *dst) {
    (void)model;
    RcInst *i = (RcInst *)inst;
    dst[i->node_mapping[0]] += i->resid_react[0];
    dst[i->node_mapping[1]] += i->resid_react[1];
}

/* ── descriptor tables ───────────────────────────────────────────────────────── */

static char *rc_name_g0[]   = { (char *)"g0"   };
static char *rc_name_c[]    = { (char *)"c"    };
static char *rc_name_tc[]   = { (char *)"tc"   };
static char *rc_name_tnom[] = { (char *)"tnom" };
static char *rc_name_mult[] = { (char *)"mult" };
static char *rc_name_temp[] = { (char *)"temp_k" };

static OsdiParamOpvar rc_params[] = {
    { rc_name_g0,   0, (char *)"conductance at tnom", (char *)"S",  PARA_TY_REAL | PARA_KIND_MODEL, 1 },
    { rc_name_c,    0, (char *)"capacitance",         (char *)"F",  PARA_TY_REAL | PARA_KIND_MODEL, 1 },
    { rc_name_tc,   0, (char *)"tempco of g",         (char *)"1/K",PARA_TY_REAL | PARA_KIND_MODEL, 1 },
    { rc_name_tnom, 0, (char *)"nominal temperature", (char *)"K",  PARA_TY_REAL | PARA_KIND_MODEL, 1 },
    { rc_name_mult, 0, (char *)"multiplier",          (char *)"",   PARA_TY_REAL | PARA_KIND_INST,  1 },
    { rc_name_temp, 0, (char *)"temperature seen",    (char *)"K",  PARA_TY_REAL | PARA_KIND_OPVAR, 1 },
};

static OsdiNode rc_nodes[] = {
    { (char *)"A", (char *)"V", (char *)"A",
      (uint32_t)offsetof(RcInst, resid_resist[0]),
      (uint32_t)offsetof(RcInst, resid_react[0]),
      UINT32_MAX, UINT32_MAX, false },
    { (char *)"B", (char *)"V", (char *)"A",
      (uint32_t)offsetof(RcInst, resid_resist[1]),
      (uint32_t)offsetof(RcInst, resid_react[1]),
      UINT32_MAX, UINT32_MAX, false },
};

static OsdiJacobianEntry rc_jacobian[] = {
    { { 0, 0 }, (uint32_t)offsetof(RcInst, jac_react[0]), JACOBIAN_ENTRY_RESIST | JACOBIAN_ENTRY_REACT },
    { { 0, 1 }, (uint32_t)offsetof(RcInst, jac_react[1]), JACOBIAN_ENTRY_RESIST | JACOBIAN_ENTRY_REACT },
    { { 1, 0 }, (uint32_t)offsetof(RcInst, jac_react[2]), JACOBIAN_ENTRY_RESIST | JACOBIAN_ENTRY_REACT },
    { { 1, 1 }, (uint32_t)offsetof(RcInst, jac_react[3]), JACOBIAN_ENTRY_RESIST | JACOBIAN_ENTRY_REACT },
};

/* ── device 2: "crf_collapse" — the node-collapsing case ───────────────────────
 *
 * Nodes:  0 = A (terminal)   1 = B (terminal)   2 = T (internal)   3 = AI (internal)
 *
 * A real compact model degenerates nodes it no longer needs, and it SAYS SO rather than leaving the
 * host to notice: a zero series resistance makes the internal node behind that resistance identical
 * to the terminal in front of it, and switching self-heating off deletes the thermal node outright.
 * Both are declared as collapsible PAIRS and marked collapsed during setup_instance. This device
 * exists to exercise exactly that, and both flavours of it — collapse onto another node, and
 * collapse onto GROUND — because a collapsed node the host leaves as a free unknown is an all-zero
 * row and column, which is a solve that does not converge with nothing anywhere saying why.
 *
 *   gs = (rs > 0) ? 1/rs : 0
 *
 *   I[0] = gs*(v0 - v3)
 *   I[1] = -g*(v3 - v1)
 *   I[2] = sh ? gth*v2 : 0
 *   I[3] = gs*(v3 - v0) + g*(v3 - v1)
 *
 * With rs == 0 the series branch disappears entirely (gs = 0) and node 3 carries the whole
 * conduction on its own — which, once the host has given node 3 node 0's index, is a conductance g
 * straight from A to B. That is the arithmetic the test asserts, and it is what makes the collapse
 * observable in the ANSWER rather than only in the report.
 *
 * Charges: none. Every react residual offset is UINT32_MAX and the react load functions are NULL,
 * which is the "this node has no reactive part" path the ABI specifies and which the RC device
 * above cannot exercise.
 */

typedef struct {
    double g;     /* model param, S     */
    double rs;    /* model param, Ohm   */
    double gth;   /* model param, W/K   */
} ColModel;

typedef struct {
    int32_t  sh;                /* instance param: self-heating on */

    uint32_t node_mapping[4];
    double  *jac_resist[8];
    bool     collapsed[2];      /* [0] = AI onto A, [1] = T onto ground */

    double   resid_resist[4];

    double   gs_eff;
    double   g_eff;
    double   gth_eff;
} ColInst;

static void *col_access(void *inst, void *model, uint32_t id, uint32_t flags) {
    (void)flags;
    ColModel *m = (ColModel *)model;
    ColInst  *i = (ColInst *)inst;
    switch (id) {
        case 0: return m ? (void *)&m->g   : NULL;
        case 1: return m ? (void *)&m->rs  : NULL;
        case 2: return m ? (void *)&m->gth : NULL;
        case 3: return i ? (void *)&i->sh  : NULL;
        default: return NULL;
    }
}

static void col_setup_model(void *handle, void *model, OsdiSimParas *sim, OsdiInitInfo *res) {
    (void)handle; (void)sim;
    ColModel *m = (ColModel *)model;
    if (m->g   == 0.0) m->g   = 0.005;
    if (m->gth == 0.0) m->gth = 0.1;
    res->flags = 0; res->num_errors = 0; res->errors = NULL;
}

static void col_setup_instance(void *handle, void *inst, void *model, double temperature,
                               uint32_t num_terminals, OsdiSimParas *sim, OsdiInitInfo *res) {
    (void)handle; (void)temperature; (void)num_terminals; (void)sim;
    ColModel *m = (ColModel *)model;
    ColInst  *i = (ColInst *)inst;

    /* This is the whole point of the device: the decision is taken HERE, from the parameters this
     * instance was given, and written where the host reads it. Which nodes collapse is per-instance
     * and cannot be answered at describe time. */
    i->collapsed[0] = (m->rs <= 0.0);
    i->collapsed[1] = (i->sh == 0);

    i->gs_eff  = i->collapsed[0] ? 0.0 : 1.0 / m->rs;
    i->g_eff   = m->g;
    i->gth_eff = i->collapsed[1] ? 0.0 : m->gth;

    res->flags = 0; res->num_errors = 0; res->errors = NULL;
}

static uint32_t col_eval(void *handle, void *inst, const void *model, const OsdiSimInfo *info) {
    (void)handle; (void)model;
    ColInst *i = (ColInst *)inst;

    double v0 = info->prev_solve[i->node_mapping[0]];
    double v1 = info->prev_solve[i->node_mapping[1]];
    double v2 = info->prev_solve[i->node_mapping[2]];
    double v3 = info->prev_solve[i->node_mapping[3]];

    double is = i->gs_eff * (v0 - v3);   /* A -> AI through the series branch */
    double ig = i->g_eff  * (v3 - v1);   /* AI -> B through the conductance   */

    i->resid_resist[0] =  is;
    i->resid_resist[1] = -ig;
    i->resid_resist[2] =  i->gth_eff * v2;
    i->resid_resist[3] = -is + ig;
    return 0;
}

/* Entries, in the order declared below:
 * (0,0) (0,3) (3,0) (3,3) (3,1) (1,3) (1,1) (2,2) */
static void col_load_jacobian_resist(void *inst, void *model) {
    (void)model;
    ColInst *i = (ColInst *)inst;
    double gs = i->gs_eff, g = i->g_eff;
    if (i->jac_resist[0]) *i->jac_resist[0] +=  gs;
    if (i->jac_resist[1]) *i->jac_resist[1] += -gs;
    if (i->jac_resist[2]) *i->jac_resist[2] += -gs;
    if (i->jac_resist[3]) *i->jac_resist[3] +=  gs + g;
    if (i->jac_resist[4]) *i->jac_resist[4] += -g;
    if (i->jac_resist[5]) *i->jac_resist[5] += -g;
    if (i->jac_resist[6]) *i->jac_resist[6] +=  g;
    if (i->jac_resist[7]) *i->jac_resist[7] +=  i->gth_eff;
}

static void col_load_residual_resist(void *inst, void *model, double *dst) {
    (void)model;
    ColInst *i = (ColInst *)inst;
    for (uint32_t k = 0; k < 4; k++) dst[i->node_mapping[k]] += i->resid_resist[k];
}

static char *col_name_g[]   = { (char *)"g"   };
static char *col_name_rs[]  = { (char *)"rs"  };
static char *col_name_gth[] = { (char *)"gth" };
static char *col_name_sh[]  = { (char *)"sh"  };

static OsdiParamOpvar col_params[] = {
    { col_name_g,   0, (char *)"conductance",        (char *)"S",   PARA_TY_REAL | PARA_KIND_MODEL, 1 },
    { col_name_rs,  0, (char *)"series resistance",  (char *)"Ohm", PARA_TY_REAL | PARA_KIND_MODEL, 1 },
    { col_name_gth, 0, (char *)"thermal conductance",(char *)"W/K", PARA_TY_REAL | PARA_KIND_MODEL, 1 },
    { col_name_sh,  0, (char *)"self-heating on",    (char *)"",    PARA_TY_INT  | PARA_KIND_INST,  1 },
};

/* Every react residual offset is UINT32_MAX: this device stores no charge, and saying so is the
 * ABI's own way of expressing it. */
static OsdiNode col_nodes[] = {
    { (char *)"A",  (char *)"V", (char *)"A",
      (uint32_t)offsetof(ColInst, resid_resist[0]), UINT32_MAX, UINT32_MAX, UINT32_MAX, false },
    { (char *)"B",  (char *)"V", (char *)"A",
      (uint32_t)offsetof(ColInst, resid_resist[1]), UINT32_MAX, UINT32_MAX, UINT32_MAX, false },
    { (char *)"T",  (char *)"V", (char *)"A",
      (uint32_t)offsetof(ColInst, resid_resist[2]), UINT32_MAX, UINT32_MAX, UINT32_MAX, false },
    { (char *)"AI", (char *)"V", (char *)"A",
      (uint32_t)offsetof(ColInst, resid_resist[3]), UINT32_MAX, UINT32_MAX, UINT32_MAX, false },
};

static OsdiJacobianEntry col_jacobian[] = {
    { { 0, 0 }, UINT32_MAX, JACOBIAN_ENTRY_RESIST },
    { { 0, 3 }, UINT32_MAX, JACOBIAN_ENTRY_RESIST },
    { { 3, 0 }, UINT32_MAX, JACOBIAN_ENTRY_RESIST },
    { { 3, 3 }, UINT32_MAX, JACOBIAN_ENTRY_RESIST },
    { { 3, 1 }, UINT32_MAX, JACOBIAN_ENTRY_RESIST },
    { { 1, 3 }, UINT32_MAX, JACOBIAN_ENTRY_RESIST },
    { { 1, 1 }, UINT32_MAX, JACOBIAN_ENTRY_RESIST },
    { { 2, 2 }, UINT32_MAX, JACOBIAN_ENTRY_RESIST },
};

/* node_2 == UINT32_MAX is the ABI's spelling of "collapsed to GROUND" rather than to another node
 * of this device — a distinct case, and the one the host cannot express as "follows node 0". */
static OsdiNodePair col_collapsible[] = {
    { 3, 0          },   /* AI onto A, when rs == 0 */
    { 2, UINT32_MAX },   /* T to ground, when sh == 0 */
};

/* ── device 3: "crf_fet" — a NONLINEAR three-terminal FET with charge ──────────
 *
 * WHY A THIRD DEVICE. The two above are both LINEAR, which is fine for everything they were written
 * for and useless for measuring what harmonic balance costs: a linear device's Newton loop converges
 * in one or two iterations, so it evaluates a fraction of the operating points a real transistor
 * does, and any per-evaluation cost measured against it is understated in exactly the ratio that
 * matters. This device generates harmonics, stores charge, and has three terminals — the shape a
 * PA's DUT actually has.
 *
 * IT IS STILL NOT A MODEL. The closed form is written here and a test asserts against the
 * arithmetic, exactly as for the other two.
 *
 * Nodes:  0 = G   1 = D   2 = S    (three terminals, no internal nodes)
 *
 *   vgs = v0 - v2      vds = v1 - v2      vgd = v0 - v1
 *
 *   vov  = ½( (vgs - vth) + sqrt((vgs - vth)² + delta²) )      smooth positive part
 *   sat  = x / sqrt(1 + x²)        with x = alpha·vds          smooth triode → saturation
 *   id   = beta · vov² · (1 + lambda·vds) · sat
 *   ig   = ggs · vgs
 *
 *   I[G] = ig      I[D] = id      I[S] = −(ig + id)
 *   Q[G] = cgs·vgs + cgd·vgd      Q[D] = −cgd·vgd      Q[S] = −cgs·vgs
 *
 * Derivatives, in the same coordinates:
 *
 *   dvov/dvgs = ½( 1 + (vgs − vth)/sqrt((vgs − vth)² + delta²) )
 *   gm   = ∂id/∂vgs = 2·beta·vov·(dvov/dvgs)·(1 + lambda·vds)·sat
 *   gds  = ∂id/∂vds = beta·vov²·[ lambda·sat + (1 + lambda·vds)·alpha·(1 + x²)^(−3/2) ]
 *
 * `sat` is written with a square root rather than tanh on purpose: the smoothing only has to be
 * C-infinity and odd, and this form keeps the library free of any libm call a platform might not
 * inline. Nothing physical rests on the choice.
 */

typedef struct {
    double beta;    /* model param, A/V^2 */
    double vth;     /* model param, V     */
    double lambda;  /* model param, 1/V   */
    double alpha;   /* model param, 1/V   */
    double delta;   /* model param, V — the smoothing width at pinch-off */
    double cgs;     /* model param, F     */
    double cgd;     /* model param, F     */
    double ggs;     /* model param, S — a small real gate conduction */
} FetModel;

typedef struct {
    double   mult;              /* instance param */

    uint32_t node_mapping[3];
    double  *jac_resist[9];
    double  *jac_react[9];
    bool     collapsed[1];      /* no collapsible pairs; kept so the offset is real */

    double   resid_resist[3];
    double   resid_react[3];

    /* Cached per-eval derivatives, handed to the Jacobian loaders. */
    double   d_gm, d_gds, d_ggs;
    double   d_cgs, d_cgd;
} FetInst;

static void *fet_access(void *inst, void *model, uint32_t id, uint32_t flags) {
    (void)flags;
    FetModel *m = (FetModel *)model;
    FetInst  *i = (FetInst *)inst;
    switch (id) {
        case 0: return m ? (void *)&m->beta   : NULL;
        case 1: return m ? (void *)&m->vth    : NULL;
        case 2: return m ? (void *)&m->lambda : NULL;
        case 3: return m ? (void *)&m->alpha  : NULL;
        case 4: return m ? (void *)&m->delta  : NULL;
        case 5: return m ? (void *)&m->cgs    : NULL;
        case 6: return m ? (void *)&m->cgd    : NULL;
        case 7: return m ? (void *)&m->ggs    : NULL;
        case 8: return i ? (void *)&i->mult   : NULL;
        default: return NULL;
    }
}

static void fet_setup_model(void *handle, void *model, OsdiSimParas *sim, OsdiInitInfo *res) {
    (void)handle; (void)sim;
    FetModel *m = (FetModel *)model;
    /* A host that sets nothing must get a working transistor, not a dead one. */
    if (m->beta   == 0.0) m->beta   = 0.06;
    if (m->vth    == 0.0) m->vth    = -2.5;
    if (m->lambda == 0.0) m->lambda = 0.02;
    if (m->alpha  == 0.0) m->alpha  = 1.5;
    if (m->delta  == 0.0) m->delta  = 0.2;
    if (m->cgs    == 0.0) m->cgs    = 2.0e-12;
    if (m->cgd    == 0.0) m->cgd    = 0.2e-12;
    if (m->ggs    == 0.0) m->ggs    = 1.0e-6;
    res->flags = 0; res->num_errors = 0; res->errors = NULL;
}

static void fet_setup_instance(void *handle, void *inst, void *model, double temperature,
                               uint32_t num_terminals, OsdiSimParas *sim, OsdiInitInfo *res) {
    (void)handle; (void)model; (void)temperature; (void)num_terminals; (void)sim;
    FetInst *i = (FetInst *)inst;
    if (i->mult == 0.0) i->mult = 1.0;
    res->flags = 0; res->num_errors = 0; res->errors = NULL;
}

static uint32_t fet_eval(void *handle, void *inst, const void *model, const OsdiSimInfo *info) {
    (void)handle;
    const FetModel *m = (const FetModel *)model;
    FetInst  *i = (FetInst *)inst;

    double v0 = info->prev_solve[i->node_mapping[0]];
    double v1 = info->prev_solve[i->node_mapping[1]];
    double v2 = info->prev_solve[i->node_mapping[2]];

    double vgs = v0 - v2, vds = v1 - v2, vgd = v0 - v1;
    double mu  = i->mult;

    double u    = vgs - m->vth;
    double root = sqrt(u * u + m->delta * m->delta);
    double vov  = 0.5 * (u + root);
    double dvov = 0.5 * (1.0 + u / root);

    double x    = m->alpha * vds;
    double s    = 1.0 / sqrt(1.0 + x * x);
    double sat  = x * s;
    double dsat = m->alpha * s * s * s;          /* d(sat)/d(vds) = alpha·(1+x²)^(−3/2) */

    double chan = 1.0 + m->lambda * vds;

    double id  = m->beta * vov * vov * chan * sat;
    double gm  = 2.0 * m->beta * vov * dvov * chan * sat;
    double gds = m->beta * vov * vov * (m->lambda * sat + chan * dsat);
    double ig  = m->ggs * vgs;

    i->resid_resist[0] =  mu * ig;
    i->resid_resist[1] =  mu * id;
    i->resid_resist[2] = -mu * (ig + id);

    i->resid_react[0] =  mu * (m->cgs * vgs + m->cgd * vgd);
    i->resid_react[1] = -mu * (m->cgd * vgd);
    i->resid_react[2] = -mu * (m->cgs * vgs);

    i->d_gm  = mu * gm;
    i->d_gds = mu * gds;
    i->d_ggs = mu * m->ggs;
    i->d_cgs = mu * m->cgs;
    i->d_cgd = mu * m->cgd;
    return 0;
}

/* Entries, in the declared order: (0,0) (0,1) (0,2) (1,0) (1,1) (1,2) (2,0) (2,1) (2,2) */
static void fet_load_jacobian_resist(void *inst, void *model) {
    (void)model;
    FetInst *i = (FetInst *)inst;
    double gm = i->d_gm, gds = i->d_gds, gg = i->d_ggs;
    double j[9] = {
         gg,        0.0,   -gg,
         gm,        gds,   -(gm + gds),
        -(gg + gm), -gds,   gg + gm + gds,
    };
    for (int k = 0; k < 9; k++) if (i->jac_resist[k]) *i->jac_resist[k] += j[k];
}

static void fet_load_jacobian_react(void *inst, void *model, double alpha) {
    (void)model;
    FetInst *i = (FetInst *)inst;
    double cgs = i->d_cgs * alpha, cgd = i->d_cgd * alpha;
    double j[9] = {
        cgs + cgd, -cgd, -cgs,
        -cgd,       cgd,  0.0,
        -cgs,       0.0,  cgs,
    };
    for (int k = 0; k < 9; k++) if (i->jac_react[k]) *i->jac_react[k] += j[k];
}

static void fet_load_residual_resist(void *inst, void *model, double *dst) {
    (void)model;
    FetInst *i = (FetInst *)inst;
    for (uint32_t k = 0; k < 3; k++) dst[i->node_mapping[k]] += i->resid_resist[k];
}

static void fet_load_residual_react(void *inst, void *model, double *dst) {
    (void)model;
    FetInst *i = (FetInst *)inst;
    for (uint32_t k = 0; k < 3; k++) dst[i->node_mapping[k]] += i->resid_react[k];
}

static char *fet_name_beta[]   = { (char *)"beta"   };
static char *fet_name_vth[]    = { (char *)"vth"    };
static char *fet_name_lambda[] = { (char *)"lambda" };
static char *fet_name_alpha[]  = { (char *)"alpha"  };
static char *fet_name_delta[]  = { (char *)"delta"  };
static char *fet_name_cgs[]    = { (char *)"cgs"    };
static char *fet_name_cgd[]    = { (char *)"cgd"    };
static char *fet_name_ggs[]    = { (char *)"ggs"    };
static char *fet_name_mult[]   = { (char *)"mult"   };

static OsdiParamOpvar fet_params[] = {
    { fet_name_beta,   0, (char *)"transconductance parameter", (char *)"A/V^2", PARA_TY_REAL | PARA_KIND_MODEL, 1 },
    { fet_name_vth,    0, (char *)"threshold voltage",          (char *)"V",     PARA_TY_REAL | PARA_KIND_MODEL, 1 },
    { fet_name_lambda, 0, (char *)"output slope",               (char *)"1/V",   PARA_TY_REAL | PARA_KIND_MODEL, 1 },
    { fet_name_alpha,  0, (char *)"saturation knee",            (char *)"1/V",   PARA_TY_REAL | PARA_KIND_MODEL, 1 },
    { fet_name_delta,  0, (char *)"pinch-off smoothing width",  (char *)"V",     PARA_TY_REAL | PARA_KIND_MODEL, 1 },
    { fet_name_cgs,    0, (char *)"gate-source capacitance",    (char *)"F",     PARA_TY_REAL | PARA_KIND_MODEL, 1 },
    { fet_name_cgd,    0, (char *)"gate-drain capacitance",     (char *)"F",     PARA_TY_REAL | PARA_KIND_MODEL, 1 },
    { fet_name_ggs,    0, (char *)"gate conductance",           (char *)"S",     PARA_TY_REAL | PARA_KIND_MODEL, 1 },
    { fet_name_mult,   0, (char *)"multiplier",                 (char *)"",      PARA_TY_REAL | PARA_KIND_INST,  1 },
};

static OsdiNode fet_nodes[] = {
    { (char *)"G", (char *)"V", (char *)"A",
      (uint32_t)offsetof(FetInst, resid_resist[0]),
      (uint32_t)offsetof(FetInst, resid_react[0]),
      UINT32_MAX, UINT32_MAX, false },
    { (char *)"D", (char *)"V", (char *)"A",
      (uint32_t)offsetof(FetInst, resid_resist[1]),
      (uint32_t)offsetof(FetInst, resid_react[1]),
      UINT32_MAX, UINT32_MAX, false },
    { (char *)"S", (char *)"V", (char *)"A",
      (uint32_t)offsetof(FetInst, resid_resist[2]),
      (uint32_t)offsetof(FetInst, resid_react[2]),
      UINT32_MAX, UINT32_MAX, false },
};

static OsdiJacobianEntry fet_jacobian[] = {
    { { 0, 0 }, (uint32_t)offsetof(FetInst, jac_react[0]), JACOBIAN_ENTRY_RESIST | JACOBIAN_ENTRY_REACT },
    { { 0, 1 }, (uint32_t)offsetof(FetInst, jac_react[1]), JACOBIAN_ENTRY_RESIST | JACOBIAN_ENTRY_REACT },
    { { 0, 2 }, (uint32_t)offsetof(FetInst, jac_react[2]), JACOBIAN_ENTRY_RESIST | JACOBIAN_ENTRY_REACT },
    { { 1, 0 }, (uint32_t)offsetof(FetInst, jac_react[3]), JACOBIAN_ENTRY_RESIST | JACOBIAN_ENTRY_REACT },
    { { 1, 1 }, (uint32_t)offsetof(FetInst, jac_react[4]), JACOBIAN_ENTRY_RESIST | JACOBIAN_ENTRY_REACT },
    { { 1, 2 }, (uint32_t)offsetof(FetInst, jac_react[5]), JACOBIAN_ENTRY_RESIST | JACOBIAN_ENTRY_REACT },
    { { 2, 0 }, (uint32_t)offsetof(FetInst, jac_react[6]), JACOBIAN_ENTRY_RESIST | JACOBIAN_ENTRY_REACT },
    { { 2, 1 }, (uint32_t)offsetof(FetInst, jac_react[7]), JACOBIAN_ENTRY_RESIST | JACOBIAN_ENTRY_REACT },
    { { 2, 2 }, (uint32_t)offsetof(FetInst, jac_react[8]), JACOBIAN_ENTRY_RESIST | JACOBIAN_ENTRY_REACT },
};

/* ── device 4: "crf_therm" — a THERMAL node, and a connected-terminal branch ───
 *
 * WHY A FOURTH DEVICE. The three above are all electrical, so every node they declare carries volts
 * against amps and nothing in this library could ever exercise the two facts a real electrothermal
 * compact model turns on:
 *
 *   1. A node's DISCIPLINE, which the ABI states only through the units of its potential and its
 *      residual — kelvin against watts for a temperature, volts against amps for a voltage. That
 *      string is the whole of what a host has to classify a thermal pin from.
 *   2. `$port_connected`, which a model reads to decide whether the host has actually wired a
 *      terminal, and which reaches it as the terminal count passed to setup_instance.
 *
 * IT IS STILL NOT A MODEL. Closed form, as for the other three:
 *
 *   Nodes:  0 = A (terminal, V/A)   1 = B (terminal, V/A)   2 = T (terminal, K/W)
 *
 *     vab   = v0 - v1
 *     p     = g * vab^2                       the power this device dissipates
 *     live  = sh != 0 AND T is connected      whether the thermal path exists at all
 *
 *     I[A] = +g*vab      I[B] = -g*vab
 *     I[T] = live ? (v2/rth - p) : 0
 *
 *     dI/dV = [ +g   -g    0        ]
 *             [ -g   +g    0        ]
 *             [ -2*g*vab  +2*g*vab  1/rth ]      (the last row all zero when not live)
 *
 * THE THREE-WAY BRANCH IS COPIED FROM THE MODEL SHAPE THAT MOTIVATED IT, and the middle case is the
 * one that matters:
 *
 *   - T NOT connected            -> the model grounds its own thermal node, declared as a collapse.
 *   - T connected, sh == 0       -> the model writes NO EQUATION for T. It has been told the host
 *                                   wired that terminal, so holding it is the host's job.
 *   - T connected, sh != 0       -> the ordinary electrothermal path.
 *
 * A host that always claims every terminal is connected can never reach the FIRST case, so a design
 * that switched self-heating off and drew no thermal pin lands in the second: an all-zero row that
 * nothing holds, which is a solve that does not converge with nothing anywhere saying why. That is
 * exactly what this device exists to make visible.
 */

typedef struct {
    double g;     /* model param, S    */
    double rth;   /* model param, K/W  */
} ThModel;

typedef struct {
    int32_t  sh;                /* instance param: self-heating on */

    uint32_t node_mapping[3];
    double  *jac_resist[7];
    bool     collapsed[1];      /* [0] = T to ground, when the terminal is not connected */

    double   resid_resist[3];

    double   g_eff;
    double   gth_eff;           /* 1/rth when the thermal path is live, 0 otherwise */
    double   dissipates;        /* 1 when the thermal path is live, 0 otherwise */
    double   vab;               /* held from eval, for the Jacobian's cross terms */
} ThInst;

static void *th_access(void *inst, void *model, uint32_t id, uint32_t flags) {
    (void)flags;
    ThModel *m = (ThModel *)model;
    ThInst  *i = (ThInst *)inst;
    switch (id) {
        case 0: return m ? (void *)&m->g   : NULL;
        case 1: return m ? (void *)&m->rth : NULL;
        case 2: return i ? (void *)&i->sh  : NULL;
        default: return NULL;
    }
}

static void th_setup_model(void *handle, void *model, OsdiSimParas *sim, OsdiInitInfo *res) {
    (void)handle; (void)sim;
    ThModel *m = (ThModel *)model;
    if (m->g   == 0.0) m->g   = 0.01;
    if (m->rth == 0.0) m->rth = 50.0;
    res->flags = 0; res->num_errors = 0; res->errors = NULL;
}

static void th_setup_instance(void *handle, void *inst, void *model, double temperature,
                              uint32_t num_terminals, OsdiSimParas *sim, OsdiInitInfo *res) {
    (void)handle; (void)temperature; (void)sim;
    ThModel *m = (ThModel *)model;
    ThInst  *i = (ThInst *)inst;

    /* `$port_connected(T)`, in the form the ABI actually delivers it: the count of terminals the
     * INSTANCE connects, which is not the count the type declares. Reading it is the whole point of
     * this device — every other device in this library ignores the argument. */
    bool t_connected = num_terminals >= 3;

    i->collapsed[0] = !t_connected;
    i->g_eff        = m->g;

    bool live       = t_connected && i->sh != 0;
    i->gth_eff      = live ? 1.0 / m->rth : 0.0;
    i->dissipates   = live ? 1.0 : 0.0;

    res->flags = 0; res->num_errors = 0; res->errors = NULL;
}

static uint32_t th_eval(void *handle, void *inst, const void *model, const OsdiSimInfo *info) {
    (void)handle; (void)model;
    ThInst *i = (ThInst *)inst;

    double v0 = info->prev_solve[i->node_mapping[0]];
    double v1 = info->prev_solve[i->node_mapping[1]];
    double v2 = info->prev_solve[i->node_mapping[2]];

    double vab = v0 - v1;
    i->vab = vab;

    double ig = i->g_eff * vab;
    double p  = i->dissipates * i->g_eff * vab * vab;

    i->resid_resist[0] =  ig;
    i->resid_resist[1] = -ig;
    i->resid_resist[2] =  i->gth_eff * v2 - p;
    return 0;
}

/* Entries, in the order declared below:
 * (0,0) (0,1) (1,0) (1,1) (2,2) (2,0) (2,1) */
static void th_load_jacobian_resist(void *inst, void *model) {
    (void)model;
    ThInst *i = (ThInst *)inst;
    double g = i->g_eff, dp = 2.0 * i->dissipates * i->g_eff * i->vab;
    if (i->jac_resist[0]) *i->jac_resist[0] +=  g;
    if (i->jac_resist[1]) *i->jac_resist[1] += -g;
    if (i->jac_resist[2]) *i->jac_resist[2] += -g;
    if (i->jac_resist[3]) *i->jac_resist[3] +=  g;
    if (i->jac_resist[4]) *i->jac_resist[4] +=  i->gth_eff;
    if (i->jac_resist[5]) *i->jac_resist[5] += -dp;
    if (i->jac_resist[6]) *i->jac_resist[6] +=  dp;
}

static void th_load_residual_resist(void *inst, void *model, double *dst) {
    (void)model;
    ThInst *i = (ThInst *)inst;
    for (uint32_t k = 0; k < 3; k++) dst[i->node_mapping[k]] += i->resid_resist[k];
}

static char *th_name_g[]   = { (char *)"g"   };
static char *th_name_rth[] = { (char *)"rth" };
static char *th_name_sh[]  = { (char *)"sh"  };

static OsdiParamOpvar th_params[] = {
    { th_name_g,   0, (char *)"conductance",         (char *)"S",   PARA_TY_REAL | PARA_KIND_MODEL, 1 },
    { th_name_rth, 0, (char *)"thermal resistance",  (char *)"K/W", PARA_TY_REAL | PARA_KIND_MODEL, 1 },
    { th_name_sh,  0, (char *)"self-heating on",     (char *)"",    PARA_TY_INT  | PARA_KIND_INST,  1 },
};

/* THE UNITS ARE THE POINT of this table. "K" against "W" is how Verilog-AMS's `thermal` discipline
 * reaches a host through this ABI, and it is the ONLY thing distinguishing node 2 from the two above
 * it — same struct, same offsets, same everything else. A host that does not read them classifies a
 * temperature as a voltage and every thermal-aware path it has goes quietly unused. */
static OsdiNode th_nodes[] = {
    { (char *)"A", (char *)"V", (char *)"A",
      (uint32_t)offsetof(ThInst, resid_resist[0]), UINT32_MAX, UINT32_MAX, UINT32_MAX, false },
    { (char *)"B", (char *)"V", (char *)"A",
      (uint32_t)offsetof(ThInst, resid_resist[1]), UINT32_MAX, UINT32_MAX, UINT32_MAX, false },
    { (char *)"T", (char *)"K", (char *)"W",
      (uint32_t)offsetof(ThInst, resid_resist[2]), UINT32_MAX, UINT32_MAX, UINT32_MAX, false },
};

static OsdiJacobianEntry th_jacobian[] = {
    { { 0, 0 }, UINT32_MAX, JACOBIAN_ENTRY_RESIST },
    { { 0, 1 }, UINT32_MAX, JACOBIAN_ENTRY_RESIST },
    { { 1, 0 }, UINT32_MAX, JACOBIAN_ENTRY_RESIST },
    { { 1, 1 }, UINT32_MAX, JACOBIAN_ENTRY_RESIST },
    { { 2, 2 }, UINT32_MAX, JACOBIAN_ENTRY_RESIST },
    { { 2, 0 }, UINT32_MAX, JACOBIAN_ENTRY_RESIST },
    { { 2, 1 }, UINT32_MAX, JACOBIAN_ENTRY_RESIST },
};

/* An external TERMINAL collapsed to ground, which the other collapsing device does not exercise: its
 * grounded node is internal. A model grounding one of its own terminals is what `$port_connected`
 * being false is FOR — the host did not wire it, so the model holds it. */
static OsdiNodePair th_collapsible[] = {
    { 2, UINT32_MAX },   /* T to ground, when the terminal is not connected */
};

/* ── the exports the ABI is discovered through ───────────────────────────────── */

uint32_t OSDI_VERSION_MAJOR = OSDI_VERSION_MAJOR_CURR;
uint32_t OSDI_VERSION_MINOR = OSDI_VERSION_MINOR_CURR;
uint32_t OSDI_NUM_DESCRIPTORS = 4;
uint32_t OSDI_DESCRIPTOR_SIZE = sizeof(OsdiDescriptor);

OsdiDescriptor OSDI_DESCRIPTORS[] = {
    {
        .name = (char *)"crf_rc",

        .num_nodes     = 2,
        .num_terminals = 2,
        .nodes         = rc_nodes,

        .num_jacobian_entries = 4,
        .jacobian_entries     = rc_jacobian,

        .num_collapsible  = 0,
        .collapsible      = NULL,
        .collapsed_offset = (uint32_t)offsetof(RcInst, collapsed),

        .noise_sources  = NULL,
        .num_noise_src  = 0,

        .num_params          = 5,   /* g0, c, tc, tnom, mult */
        .num_instance_params = 1,   /* mult */
        .num_opvars          = 1,   /* temp_k */
        .param_opvar         = rc_params,

        .node_mapping_offset        = (uint32_t)offsetof(RcInst, node_mapping),
        .jacobian_ptr_resist_offset = (uint32_t)offsetof(RcInst, jac_resist),

        .num_states    = 0,
        .state_idx_off = 0,

        .bound_step_offset = 0,

        .instance_size = sizeof(RcInst),
        .model_size    = sizeof(RcModel),

        .access = rc_access,

        .setup_model    = rc_setup_model,
        .setup_instance = rc_setup_instance,

        .eval = rc_eval,

        .load_noise            = NULL,
        .load_residual_resist  = rc_load_residual_resist,
        .load_residual_react   = rc_load_residual_react,
        .load_limit_rhs_resist = NULL,
        .load_limit_rhs_react  = NULL,
        .load_spice_rhs_dc     = NULL,
        .load_spice_rhs_tran   = NULL,
        .load_jacobian_resist  = rc_load_jacobian_resist,
        .load_jacobian_react   = rc_load_jacobian_react,
        .load_jacobian_tran    = NULL,
    },
    {
        .name = (char *)"crf_collapse",

        .num_nodes     = 4,
        .num_terminals = 2,
        .nodes         = col_nodes,

        .num_jacobian_entries = 8,
        .jacobian_entries     = col_jacobian,

        .num_collapsible  = 2,
        .collapsible      = col_collapsible,
        .collapsed_offset = (uint32_t)offsetof(ColInst, collapsed),

        .noise_sources  = NULL,
        .num_noise_src  = 0,

        .num_params          = 4,   /* g, rs, gth, sh */
        .num_instance_params = 1,   /* sh */
        .num_opvars          = 0,
        .param_opvar         = col_params,

        .node_mapping_offset        = (uint32_t)offsetof(ColInst, node_mapping),
        .jacobian_ptr_resist_offset = (uint32_t)offsetof(ColInst, jac_resist),

        .num_states    = 0,
        .state_idx_off = 0,

        .bound_step_offset = 0,

        .instance_size = sizeof(ColInst),
        .model_size    = sizeof(ColModel),

        .access = col_access,

        .setup_model    = col_setup_model,
        .setup_instance = col_setup_instance,

        .eval = col_eval,

        .load_noise            = NULL,
        .load_residual_resist  = col_load_residual_resist,
        .load_residual_react   = NULL,
        .load_limit_rhs_resist = NULL,
        .load_limit_rhs_react  = NULL,
        .load_spice_rhs_dc     = NULL,
        .load_spice_rhs_tran   = NULL,
        .load_jacobian_resist  = col_load_jacobian_resist,
        .load_jacobian_react   = NULL,
        .load_jacobian_tran    = NULL,
    },
    {
        .name = (char *)"crf_fet",

        .num_nodes     = 3,
        .num_terminals = 3,
        .nodes         = fet_nodes,

        .num_jacobian_entries = 9,
        .jacobian_entries     = fet_jacobian,

        .num_collapsible  = 0,
        .collapsible      = NULL,
        .collapsed_offset = (uint32_t)offsetof(FetInst, collapsed),

        .noise_sources  = NULL,
        .num_noise_src  = 0,

        .num_params          = 9,   /* beta, vth, lambda, alpha, delta, cgs, cgd, ggs, mult */
        .num_instance_params = 1,   /* mult */
        .num_opvars          = 0,
        .param_opvar         = fet_params,

        .node_mapping_offset        = (uint32_t)offsetof(FetInst, node_mapping),
        .jacobian_ptr_resist_offset = (uint32_t)offsetof(FetInst, jac_resist),

        .num_states    = 0,
        .state_idx_off = 0,

        .bound_step_offset = 0,

        .instance_size = sizeof(FetInst),
        .model_size    = sizeof(FetModel),

        .access = fet_access,

        .setup_model    = fet_setup_model,
        .setup_instance = fet_setup_instance,

        .eval = fet_eval,

        .load_noise            = NULL,
        .load_residual_resist  = fet_load_residual_resist,
        .load_residual_react   = fet_load_residual_react,
        .load_limit_rhs_resist = NULL,
        .load_limit_rhs_react  = NULL,
        .load_spice_rhs_dc     = NULL,
        .load_spice_rhs_tran   = NULL,
        .load_jacobian_resist  = fet_load_jacobian_resist,
        .load_jacobian_react   = fet_load_jacobian_react,
        .load_jacobian_tran    = NULL,
    },
    {
        .name = (char *)"crf_therm",

        .num_nodes     = 3,
        .num_terminals = 3,
        .nodes         = th_nodes,

        .num_jacobian_entries = 7,
        .jacobian_entries     = th_jacobian,

        .num_collapsible  = 1,
        .collapsible      = th_collapsible,
        .collapsed_offset = (uint32_t)offsetof(ThInst, collapsed),

        .noise_sources  = NULL,
        .num_noise_src  = 0,

        .num_params          = 3,   /* g, rth, sh */
        .num_instance_params = 1,   /* sh */
        .num_opvars          = 0,
        .param_opvar         = th_params,

        .node_mapping_offset        = (uint32_t)offsetof(ThInst, node_mapping),
        .jacobian_ptr_resist_offset = (uint32_t)offsetof(ThInst, jac_resist),

        .num_states    = 0,
        .state_idx_off = 0,

        .bound_step_offset = 0,

        .instance_size = sizeof(ThInst),
        .model_size    = sizeof(ThModel),

        .access = th_access,

        .setup_model    = th_setup_model,
        .setup_instance = th_setup_instance,

        .eval = th_eval,

        .load_noise            = NULL,
        .load_residual_resist  = th_load_residual_resist,
        .load_residual_react   = NULL,
        .load_limit_rhs_resist = NULL,
        .load_limit_rhs_react  = NULL,
        .load_spice_rhs_dc     = NULL,
        .load_spice_rhs_tran   = NULL,
        .load_jacobian_resist  = th_load_jacobian_resist,
        .load_jacobian_react   = NULL,
        .load_jacobian_tran    = NULL,
    },
};
