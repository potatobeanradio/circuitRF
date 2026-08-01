/*
 * fake_model.c -- a test-only library that mimics the compiled-model ABI circuitRF's device worker
 * drives, so that the worker can be exercised end to end with no vendor data anywhere.
 *
 * WHY IT EXISTS. The repository commits no vendor kit, so without this there is nothing for a CI
 * run to load: every test of the worker would be a test of our own reader agreeing with our own
 * writer. This library is the other side of the contract, written independently of it -- and on
 * Windows it is the ONLY way the shim mechanism is proven rather than hoped for, because it is the
 * thing that imports host callbacks from a named module and therefore needs one staged for it.
 *
 * IT REFERENCES NO PROJECT IN THIS REPOSITORY, deliberately -- the same treatment
 * tools/DeviceWorkerExample gets, and for the same reason: a second copy of our own code agreeing
 * with itself proves nothing. Everything below is written from the ABI as documented, not shared
 * with the worker's source.
 *
 * WHAT IT SERVES. One family, CRF_TEST_V1: a two-terminal linear conductance of 10 mS, with one
 * declared parameter. Deliberately simple, and deliberately SYMMETRIC in the Jacobian, so a probe
 * classifies both pins as conductively coupled and neither as degenerate -- a device that answers
 * every command correctly is what makes a failure anywhere else unambiguous.
 *
 *   Linux    fake_model.so    host callbacks left UNDEFINED, resolved against the worker executable
 *   Windows  fake_model.dll   host callbacks IMPORTED from crf_test_host.dll, a name of its own
 *
 * That Windows module name is this library's own business, exactly as a vendor's is: the worker
 * reads it out of this file's import table and stages its shim under it. Nothing anywhere in
 * circuitRF knows the name.
 */

#include <stdint.h>
#include <stddef.h>

#ifdef _WIN32
#  define HOST_IMPORT __declspec(dllimport)
#  define MODEL_EXPORT __declspec(dllexport)
#else
#  define HOST_IMPORT
#  define MODEL_EXPORT
#endif

/* ── the host services this model resolves against whoever loaded it ────────── */
HOST_IMPORT int    load_elements(void* array, int count);
HOST_IMPORT int    add_nl_iq(void* inst, int i, double current, double charge);
HOST_IMPORT int    add_nl_gc(void* inst, int i, int j, double g, double c);
HOST_IMPORT int    send_info_to_scn(const char* fmt, ...);
HOST_IMPORT int    send_error_to_scn(const char* fmt, ...);
HOST_IMPORT double get_delay_v(void* inst, int i, int j, double* out, double tau);

/* Referenced so the import descriptor carries the whole ABI rather than the handful this model
 * happens to call -- a kit's library imports all of them, and the worker's descriptor match
 * should be exercised against that shape. Never actually called. */
HOST_IMPORT int add_lin_n(void*, int, int, double, double);
HOST_IMPORT int add_lin_y(void*, int, int, double, double);
HOST_IMPORT int add_tr_capacitor(void*, int, int, double);
HOST_IMPORT int add_tr_gc(void*, int, int, double, double);
HOST_IMPORT int add_tr_iq(void*, int, double, double);
HOST_IMPORT int add_tr_lossy_inductor(void*, int, int, double, double);
HOST_IMPORT int add_tr_mutual_inductor(void*, int, int, int, int, double);
HOST_IMPORT int add_tr_resistor(void*, int, int, double);

static void* const g_never_called[] = {
    (void*)add_lin_n, (void*)add_lin_y, (void*)add_tr_capacitor, (void*)add_tr_gc,
    (void*)add_tr_iq, (void*)add_tr_lossy_inductor, (void*)add_tr_mutual_inductor,
    (void*)add_tr_resistor, (void*)send_error_to_scn, (void*)get_delay_v,
};

/* ── the structures the worker reads out of us ──────────────────────────────── */

/* A parameter's declaration: keyword, then its data type (0 = double). 16 bytes. */
typedef struct {
    const char* keyword;
    uint32_t    dataType;
    uint32_t    pad;
} UserParamDef;

/* Word 0 is the internal-node count; words 1 and 2 are the analyse entry points. The worker reads
 * the function pointers out of here rather than from a per-family symbol, which is what lets it
 * serve families it was never built against. */
typedef struct {
    uint32_t numIntNodes;
    uint32_t pad0;
    void*    analyze_lin;
    void*    analyze_nl;
} UserNonLinDef;

/* 0x60 bytes, with the three fields the worker actually reads at 0x00, 0x08/0x0C, 0x10 and 0x38. */
typedef struct {
    const char* name;                    /* 0x00 */
    uint32_t    numExtNodes;             /* 0x08 */
    uint32_t    numPars;                 /* 0x0C */
    UserParamDef* params;                /* 0x10 */
    char        reserved0[0x38 - 0x18];  /* 0x18 */
    UserNonLinDef* devDef;               /* 0x38 */
    char        reserved1[0x60 - 0x40];  /* 0x40 */
} UserElemDef;

/* ── the model ──────────────────────────────────────────────────────────────── */

#define TEST_CONDUCTANCE 0.01   /* 10 mS: a plain two-terminal resistor of 100 ohm */

static int analyze_nl(void* inst, double* v)
{
    double dv = v[0] - v[1];
    double i  = TEST_CONDUCTANCE * dv;

    /* Current is positive INTO the device at each terminal, which is the convention the worker and
     * circuitRF both already use — no flip anywhere in the chain. */
    add_nl_iq(inst, 0,  i, 0.0);
    add_nl_iq(inst, 1, -i, 0.0);

    /* A SYMMETRIC Jacobian on purpose: the worker's probe separates a conductive path from a
     * thermal one by reciprocity, not magnitude, so an asymmetric fixture would classify wrongly. */
    add_nl_gc(inst, 0, 0,  TEST_CONDUCTANCE, 0.0);
    add_nl_gc(inst, 0, 1, -TEST_CONDUCTANCE, 0.0);
    add_nl_gc(inst, 1, 0, -TEST_CONDUCTANCE, 0.0);
    add_nl_gc(inst, 1, 1,  TEST_CONDUCTANCE, 0.0);

    return 1;
}

static UserParamDef g_params[] = {
    { "W", 0, 0 },
};

static UserNonLinDef g_devDef = {
    /* numIntNodes */ 0,
    /* pad0        */ 0,
    /* analyze_lin */ NULL,
    /* analyze_nl  */ (void*)analyze_nl,
};

static UserElemDef g_elements[] = {
    {
        /* name        */ "CRF_TEST_V1",
        /* numExtNodes */ 2,
        /* numPars     */ 1,
        /* params      */ g_params,
        /* reserved0   */ { 0 },
        /* devDef      */ &g_devDef,
        /* reserved1   */ { 0 },
    },
};

/* The entry point the worker finds by walking THIS library's own export/symbol table. The suffix
 * after boot_senior_ is the family name; nothing but this file decides it. */
MODEL_EXPORT int boot_senior_CRF_TEST_V1(void)
{
    (void)g_never_called;
    send_info_to_scn("fake_model: registering CRF_TEST_V1");
    return load_elements(&g_elements[0], 1);
}
