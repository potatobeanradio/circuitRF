/*
 * senior_worker.c -- an out-of-process device worker for compiled model libraries.
 *
 * circuitRF loads no device library itself. A compiled model calls back into the process that
 * loaded it for services that process must export as C symbols, which a managed host cannot do;
 * and one process can hold exactly one build of one library. Both constraints dissolve once the
 * model lives in its own process, which is what this is.
 *
 * KIT-AGNOSTIC BY CONSTRUCTION. Nothing here names a vendor, a library, a family or an offset:
 *   - every boot_senior_* entry point is found by walking the library's own export table,
 *     so a library serving families this was never built against works unchanged;
 *   - each one's load_elements callback hands over its UserElemDef array, carrying name,
 *     numExtNodes, numPars and the params table;
 *   - numIntNodes and the analyze_* function pointers are read out of UserNonLinDef rather than
 *     from a per-family symbol offset, so reading the struct generalises for free;
 *   - on Windows, the name of the module a model imports its host callbacks FROM is read out of
 *     that model's own PE import table, selected by OUR OWN ABI symbols -- never from a
 *     remembered module name (see derive_host_module).
 *
 * The one thing that cannot be derived is which node a DEGENERATE node follows: probing finds
 * identically-zero Jacobian rows structurally and so knows WHICH nodes are degenerate, but not
 * what each replicates. That is definition data about a particular model, so it is supplied as
 * data at run time (see g_alias_*) rather than compiled in -- a family with none reports
 * slavedTo = null and the client refuses to solve, which is loud on purpose.
 *
 * ONE SOURCE FILE, THREE PRODUCTS -- do not fork it.
 *
 *   Linux                        one executable; this file compiles whole.
 *   Windows  crf-model-host.dll  -DCRF_HOST_DLL   the 15 callbacks + the protocol + crf_worker_main
 *   Windows  senior_worker.exe   -DCRF_HOST_STUB  derive the host-module name, stage the shim,
 *                                                 load it, call crf_worker_main
 *
 * Why the Windows split exists at all: a Linux model leaves its host callbacks UNDEFINED and the
 * loader resolves them against whatever loaded it (that is what -rdynamic is for). A Windows model
 * IMPORTS them BY NAME FROM A NAMED MODULE, and an executable's exports are never consulted for a
 * DLL's import-by-name -- so a module under that name must exist at load time. The callbacks are
 * not pure (they write g_I, g_Q, g_G, g_C, g_npins_cur, g_curv, g_delay, g_booting), so the
 * callbacks and the state they touch have to live in the SAME module; splitting them would need a
 * registration handshake and a forwarding thunk per callback. Hence: logic in the DLL, launcher in
 * the EXE. A forked worker would be two implementations of one wire protocol to keep in step,
 * which is the failure this repo already avoided once by making tools/DeviceWorkerExample
 * reference nothing.
 *
 * PROTOCOL. Framed messages, batched from the start: HB calls eval per harmonic sample per Newton
 * iteration, so a round trip per evaluation would dominate runtime.
 *
 *     [ uint32 jsonLen ][ uint32 binLen ][ jsonLen bytes of JSON ][ binLen bytes of raw doubles ]
 *
 * JSON carries the control plane and stays human-readable in a hex dump; bulk numeric payloads
 * ride as raw little-endian doubles so a large batch costs no parsing. Both directions use the
 * same framing.
 *
 * Commands:  describe | create | probe | eval | destroy | shutdown     (see handle_* below)
 *
 * usage: senior_worker <model-library> [alias-map.json]
 */

/* ================================================================= compile mode */
#ifdef _WIN32
#  if defined(CRF_HOST_DLL) && defined(CRF_HOST_STUB)
#    error "define exactly one of CRF_HOST_DLL / CRF_HOST_STUB, not both"
#  endif
#  if !defined(CRF_HOST_DLL) && !defined(CRF_HOST_STUB)
#    error "on Windows, define CRF_HOST_DLL (the shim) or CRF_HOST_STUB (the launcher)"
#  endif
#  ifdef CRF_HOST_DLL
#    define CRF_CORE 1
#  endif
#else
   /* One binary does both jobs: the model resolves its callbacks against this executable. */
#  define CRF_CORE 1
#  define _GNU_SOURCE
#endif

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>
#include <ctype.h>
#include <math.h>
#include <setjmp.h>

#ifdef _WIN32
#  define WIN32_LEAN_AND_MEAN
#  include <windows.h>
#  include <shellapi.h>          /* CommandLineToArgvW (links shell32) */
#  include <wchar.h>
#  include <io.h>
#  include <fcntl.h>
typedef int crf_ssize_t;
#  define crf_read(fd, buf, n)  _read((fd), (buf), (unsigned int)(n))
#  define crf_write(fd, buf, n) _write((fd), (buf), (unsigned int)(n))
#  ifndef STDIN_FILENO
#    define STDIN_FILENO 0
#  endif
#  ifndef STDOUT_FILENO
#    define STDOUT_FILENO 1
#  endif
typedef HMODULE crf_lib_t;
#else
#  include <dlfcn.h>
#  include <link.h>
#  include <unistd.h>
#  include <sys/personality.h>
#  include <signal.h>
typedef ssize_t crf_ssize_t;
#  define crf_read  read
#  define crf_write write
typedef void* crf_lib_t;
#endif

#define MAXPINS      32
#define MAX_PARAMS   128   /* LDMOS declares 60; 32 silently skipped its whole table */
#define MAX_FAMILIES 16
#define MAX_ALIAS    64   /* delayed-alias entries read from the optional map */
#define MAXNAME      128  /* longest family or entry-point name kept */
#define MAX_INST     64
#define MAX_DELAY    8
#define USERELEMDEF_SIZE 0x60

/* How a model advertises the families it serves. Read from the library, never compiled in. */
#define BOOT_PREFIX "boot_senior_"

/* ================================================================= the ABI this worker supplies
 * The 15 names a compiled model resolves against its host. This ONE list is the whole contract:
 * the Linux build satisfies it by exporting them from the executable's dynamic table (-rdynamic);
 * the Windows build satisfies it by exporting them from crf-model-host.dll, staged under whatever
 * module name the model itself asks for. The stub also matches against this list to work out WHICH
 * import descriptor is the host module -- never against a remembered module name. */
#define CRF_ABI_SYMBOLS                                                                   \
    "add_lin_n", "add_lin_y", "add_nl_gc", "add_nl_iq", "add_tr_capacitor", "add_tr_gc",  \
    "add_tr_iq", "add_tr_lossy_inductor", "add_tr_mutual_inductor", "add_tr_resistor",    \
    "get_delay_v", "load_elements", "send_error_to_scn", "send_info_to_scn"
/* The fifteenth is DeviceInstaller's constructor, whose name is mangled and therefore differs
 * per platform: _ZN15DeviceInstallerC1EPKcPFivEi (Itanium/ELF) vs ??0DeviceInstaller@@... (MSVC).
 * Matching on the fourteen unmangled ones identifies the descriptor unambiguously. */

static FILE* g_log;                 /* diagnostics go to stderr; stdout is protocol only */
#define LOGF(...) do { if (g_log) { fprintf(g_log, __VA_ARGS__); fflush(g_log); } } while (0)

#ifdef CRF_CORE

/* ================================================================= safe reads */
typedef struct { uintptr_t start, end; int readable; } MapRange;
static MapRange g_ranges[4096];
static int g_nranges = 0;

#ifdef _WIN32
/* VirtualQuery walks the whole address space, region by region -- the Windows counterpart of
 * /proc/self/maps. Called once per boot (from load_elements), so the walk cost never matters. */
static void refresh_maps(void) {
    g_nranges = 0;
    MEMORY_BASIC_INFORMATION mbi;
    unsigned char* addr = NULL;
    while (g_nranges < 4096 && VirtualQuery(addr, &mbi, sizeof(mbi)) == sizeof(mbi)) {
        DWORD prot = mbi.Protect;
        int readable =
            mbi.State == MEM_COMMIT &&
            !(prot & PAGE_GUARD) && !(prot & PAGE_NOACCESS) &&
            (prot & (PAGE_READONLY | PAGE_READWRITE | PAGE_WRITECOPY |
                     PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY)) != 0;

        g_ranges[g_nranges].start    = (uintptr_t)mbi.BaseAddress;
        g_ranges[g_nranges].end      = (uintptr_t)mbi.BaseAddress + (uintptr_t)mbi.RegionSize;
        g_ranges[g_nranges].readable = readable;
        g_nranges++;

        unsigned char* next = (unsigned char*)mbi.BaseAddress + mbi.RegionSize;
        if (next <= addr) break;               /* wrapped at the top of the address space */
        addr = next;
    }
}
#else
static void refresh_maps(void) {
    FILE* f = fopen("/proc/self/maps", "r");
    if (!f) { g_nranges = 0; return; }
    char line[512]; int n = 0;
    while (fgets(line, sizeof(line), f) && n < 4096) {
        unsigned long lo, hi; char perms[8];
        if (sscanf(line, "%lx-%lx %7s", &lo, &hi, perms) == 3) {
            g_ranges[n].start = lo; g_ranges[n].end = hi;
            g_ranges[n].readable = (perms[0] == 'r'); n++;
        }
    }
    fclose(f); g_nranges = n;
}
#endif

static int readable(uintptr_t a, size_t len) {
    for (int i = 0; i < g_nranges; i++)
        if (a >= g_ranges[i].start && a + len <= g_ranges[i].end) return g_ranges[i].readable;
    return 0;
}
static int read_cstr(uintptr_t a, char* out, size_t sz) {
    if (!a || !readable(a, 1)) return 0;
    size_t n = 0;
    while (n + 1 < sz && n < 200) {
        if (!readable(a + n, 1)) break;
        char c = *(const char*)(a + n);
        if (c == '\0') break;
        if (!isprint((unsigned char)c) && c != '\t') return 0;
        out[n++] = c;
    }
    out[n] = '\0'; return n > 0;
}

/* ================================================================= family descriptors */
typedef struct {
    char      name[96];
    int       numExtNodes, numIntNodes, numPars;
    char      paramKeyword[MAX_PARAMS][64];
    int       paramDataType[MAX_PARAMS];
    uintptr_t elemAddr;                 /* &family_elements[i] -- goes into UserInstDef.userDef */
    uintptr_t devDef;
    void*     analyze_nl;               /* devDef word 2 */
    void*     analyze_lin;              /* devDef word 1 */
    int       valid;
} Family;

static Family g_fam[MAX_FAMILIES];
static int    g_nfam = 0;
static uintptr_t g_base = 0;
static const char* g_booting = NULL;    /* family being booted, for load_elements attribution */

/* ================================================================= capture sinks */
static int      g_npins_cur = 0;
static double   g_I[MAXPINS], g_Q[MAXPINS];
static double   g_G[MAXPINS][MAXPINS], g_C[MAXPINS][MAXPINS];
static const double* g_curv = NULL;
static const double* g_curdelay = NULL;   /* client-supplied delayed values, or NULL */
static int      g_ndelay_seen = 0;
static struct { int i, j; double tau; } g_delay[MAX_DELAY];
static int      g_delay_idx = 0;

static void capture_reset(void) {
    memset(g_I, 0, sizeof(g_I)); memset(g_Q, 0, sizeof(g_Q));
    memset(g_G, 0, sizeof(g_G)); memset(g_C, 0, sizeof(g_C));
    g_delay_idx = 0;
}

int add_nl_iq(void* inst, int i, double cur, double q) {
    (void)inst;
    if (i >= 0 && i < MAXPINS) { g_I[i] += cur; g_Q[i] += q; }
    return 1;
}
int add_nl_gc(void* inst, int i, int j, double g, double c) {
    (void)inst;
    if (i >= 0 && i < MAXPINS && j >= 0 && j < MAXPINS) { g_G[i][j] += g; g_C[i][j] += c; }
    return 1;
}
int add_lin_y(void* i_, int i, int j, double a, double b) { (void)i_;(void)i;(void)j;(void)a;(void)b; return 1; }
int add_lin_n(void* i_, int i, int j, double a, double b) { (void)i_;(void)i;(void)j;(void)a;(void)b; return 1; }
int add_tr_capacitor(void* i_, int i, int j, double c) { (void)i_;(void)i;(void)j;(void)c; return 1; }
int add_tr_gc(void* i_, int i, int j, double g, double c) { (void)i_;(void)i;(void)j;(void)g;(void)c; return 1; }
int add_tr_iq(void* i_, int i, double v, double q) { (void)i_;(void)i;(void)v;(void)q; return 1; }
int add_tr_lossy_inductor(void* i_, int i, int j, double l, double r) { (void)i_;(void)i;(void)j;(void)l;(void)r; return 1; }
int add_tr_mutual_inductor(void* i_, int i, int j, int k, int l, double m) { (void)i_;(void)i;(void)j;(void)k;(void)l;(void)m; return 1; }
int add_tr_resistor(void* i_, int i, int j, double r) { (void)i_;(void)i;(void)j;(void)r; return 1; }

/* get_delay_v -- signature established:
 *     get_delay_v(UserInstDef*, int iPin, int jPin, double *out, double tau)
 * The result returns through the OUT-POINTER, not the return register. A stub that does not write
 * *out silently drives the model with a controlling voltage of zero at every bias -- that bug cost
 * this project the entire M1 "no drain current" symptom, so it is implemented properly here and
 * every (i, j, tau) triple is recorded so the descriptor can report the delay structure.
 *
 * At DC delayed == instantaneous. In HB the caller supplies the per-harmonic delayed value
 * (v_delayed = V(i)-V(j) rotated by exp(-j*w*tau)) through the eval payload; when it does,
 * g_curdelay is non-NULL and is used verbatim. */
static int g_no_delay_write = 0;   /* SENIOR_NO_DELAY_WRITE=1 -- reproduces the old broken stub,
                                    * so the causal claim in §30.1 can be tested rather than asserted */
double get_delay_v(void* inst, int i, int j, double* out, double tau) {
    (void)inst;
    if (g_no_delay_write) {
        if (g_delay_idx < MAX_DELAY) { g_delay[g_delay_idx].i = i; g_delay[g_delay_idx].j = j;
                                       g_delay[g_delay_idx].tau = tau; }
        g_delay_idx++;
        return 0.0;                 /* deliberately does NOT write *out -- the original bug */
    }
    double dv = 0.0;
    if (g_curdelay && g_delay_idx < g_ndelay_seen) dv = g_curdelay[g_delay_idx];
    else if (g_curv && i >= 0 && i < g_npins_cur && j >= 0 && j < g_npins_cur)
        dv = g_curv[i] - g_curv[j];
    if (g_delay_idx < MAX_DELAY) {
        g_delay[g_delay_idx].i = i; g_delay[g_delay_idx].j = j; g_delay[g_delay_idx].tau = tau;
    }
    g_delay_idx++;
    if (out) *out = dv;
    return dv;
}

int send_error_to_scn(const char* fmt, ...) { if (fmt) LOGF("model-error: %s\n", fmt); return 0; }
int send_info_to_scn(const char* fmt, ...)  { (void)fmt; return 0; }

/* load_elements: fires once per boot_senior_*, handing us that family's UserElemDef array. */
int load_elements(void* array, int count) {
    refresh_maps();
    for (int k = 0; k < count && k < 4 && g_nfam < MAX_FAMILIES; k++) {
        uintptr_t elem = (uintptr_t)array + (size_t)k * USERELEMDEF_SIZE;
        if (!readable(elem, USERELEMDEF_SIZE)) continue;
        Family* f = &g_fam[g_nfam];
        memset(f, 0, sizeof(*f));
        f->elemAddr = elem;

        uintptr_t namep; memcpy(&namep, (const void*)elem, 8);
        if (!read_cstr(namep, f->name, sizeof(f->name)))
            snprintf(f->name, sizeof(f->name), "%s", g_booting ? g_booting : "?");

        uint64_t w1; memcpy(&w1, (const void*)(elem + 0x08), 8);
        f->numExtNodes = (int)(uint32_t)(w1 & 0xffffffffu);
        f->numPars     = (int)(uint32_t)(w1 >> 32);

        uintptr_t params; memcpy(&params, (const void*)(elem + 0x10), 8);
        memcpy(&f->devDef, (const void*)(elem + 0x38), 8);

        /* UserNonLinDef: word0 numIntNodes, word1 analyze_lin, word2 analyze_nl. Reading the
         * function pointers here is what makes this worker family-generic. */
        if (f->devDef && readable(f->devDef, 24)) {
            uint32_t n; memcpy(&n, (const void*)f->devDef, 4);
            f->numIntNodes = (int)n;
            memcpy(&f->analyze_lin, (const void*)(f->devDef + 0x08), 8);
            memcpy(&f->analyze_nl,  (const void*)(f->devDef + 0x10), 8);
        }

        if (f->numPars > MAX_PARAMS) {
            LOGF("WARNING: %s declares %d params, MAX_PARAMS=%d -- TRUNCATING\n",
                 f->name, f->numPars, MAX_PARAMS);
            f->numPars = MAX_PARAMS;
        }
        if (params && f->numPars > 0) {
            for (int i = 0; i < f->numPars; i++) {
                uintptr_t e = params + (size_t)i * 16;
                if (!readable(e, 16)) continue;
                uintptr_t kw; uint32_t dt;
                memcpy(&kw, (const void*)e, 8);
                memcpy(&dt, (const void*)(e + 8), 4);
                char kb[64];
                if (read_cstr(kw, kb, sizeof(kb))) {
                    strncpy(f->paramKeyword[i], kb, sizeof(f->paramKeyword[i]) - 1);
                    f->paramKeyword[i][sizeof(f->paramKeyword[i]) - 1] = '\0';
                }
                f->paramDataType[i] = (int)dt;
            }
        }
        /* A family is USABLE if we can address its pins; whether it is NONLINEAR is a separate
         * capability. Some can report numIntNodes=0 and a NULL analyze_nl -- they are linear-only
         * models driven through analyze_lin, and hiding them would misreport the library. */
        f->valid = (f->numExtNodes > 0 && f->numExtNodes < MAXPINS);
        LOGF("family[%d] %s ext=%d int=%d pars=%d analyze_nl=%p valid=%d\n",
             g_nfam, f->name, f->numExtNodes, f->numIntNodes, f->numPars, f->analyze_nl, f->valid);
        g_nfam++;
    }
    return 1;
}

/* The fifteenth ABI entry: DeviceInstaller's constructor. The model's static initialisers call it
 * to register themselves; we only have to satisfy the link.
 *
 * The NAME is mangled and therefore platform-specific, but the SIGNATURE is not: on x86-64 `this`
 * arrives in the first integer argument slot under both the SysV and the Microsoft calling
 * conventions, so one (void*, const char*, void*, int) body serves both. On ELF the mangled name
 * is a valid C identifier and can simply be declared; on Windows it is not (`?`, `@`), so the
 * alias is made in crf-model-host.def instead. */
#ifdef _WIN32
void crf_device_installer_ctor(void* s, const char* n, void* f, int t) {
    (void)s; (void)n; (void)f; (void)t;
}
#else
void _ZN15DeviceInstallerC1EPKcPFivEi(void* s, const char* n, void* f, int t) {
    (void)s; (void)n; (void)f; (void)t;
}
#endif

/* ================================================================= delayed-alias map
 * Some nodes are not free unknowns -- they are delayed replicas of another node. Solving for them
 * yields a singular system and a device that never conducts. cmd_probe DETECTS degeneracy
 * structurally (identically-zero Jacobian rows) and needs no data at all; this supplies the missing
 * half -- WHICH node each degenerate one follows -- which structural probing cannot reveal and the
 * library does not state.
 *
 * So it is DATA, not code: an optional JSON file named on the command line, of the form
 *
 *     { "FAMILY_NAME": { "6": 5, "7": 4 } }
 *
 * keeping this file free of any particular model's definition data. A family with no entry reports
 * slavedTo = null for its degenerate nodes and the client must refuse to solve rather than silently
 * produce a dead device. That failure mode is loud on purpose. */
typedef struct { char family[MAXNAME]; int node, slavedTo; } AliasEntry;
static AliasEntry g_alias[MAX_ALIAS];
static int        g_nalias = 0;

static int alias_for(const char* family, int node) {
    for (int i = 0; i < g_nalias; i++)
        if (!strcmp(g_alias[i].family, family) && g_alias[i].node == node)
            return g_alias[i].slavedTo;
    return -1;
}

/* Minimal reader for the shape above; anything it cannot make sense of is skipped, because a
 * malformed map must degrade to "no aliases known" rather than to a wrong one.
 *
 * A KEY IS A FAMILY ONLY IF ITS VALUE IS AN OBJECT. That check is the whole difference between this
 * working and a map that loads the right NUMBER of entries under the wrong NAME: the shipped file
 * opens with a "_note" key whose value is an array of prose, and a reader that takes the first
 * quoted string it sees files every alias under "_note". Nothing about that is visible from the
 * outside -- the load line reports two entries, the lookup silently never matches, and the symptom
 * is the same grinding bias ramp the map exists to fix. */
static void load_alias_map(const char* path) {
    FILE* f = fopen(path, "rb");
    if (!f) { LOGF("alias map '%s' not readable; continuing with none\n", path); return; }

    char buf[1 << 16];
    size_t n = fread(buf, 1, sizeof(buf) - 1, f);
    fclose(f);
    buf[n] = '\0';

    const char* p = buf;
    while (g_nalias < MAX_ALIAS) {
        const char* q = strchr(p, '"');                       /* family name */
        if (!q) break;
        const char* e = strchr(q + 1, '"');
        if (!e) break;

        char family[MAXNAME];
        size_t len = (size_t)(e - q - 1);
        if (len >= sizeof(family)) len = sizeof(family) - 1;
        memcpy(family, q + 1, len);
        family[len] = '\0';

        /* Its value must be an object, immediately: "key" : { ... }. A key whose value is anything
         * else -- prose, an array, a number -- is not a family, and the scan simply moves past it
         * rather than adopting its name. */
        const char* v = e + 1;
        while (*v == ' ' || *v == '\t' || *v == '\r' || *v == '\n') v++;
        if (*v != ':') { p = e + 1; continue; }
        v++;
        while (*v == ' ' || *v == '\t' || *v == '\r' || *v == '\n') v++;
        if (*v != '{') { p = e + 1; continue; }

        const char* brace = v;
        const char* end = strchr(brace, '}');
        if (!end) break;

        /* "node": slavedTo pairs inside this family's object */
        const char* r = brace;
        while (g_nalias < MAX_ALIAS) {
            const char* ks = strchr(r, '"');
            if (!ks || ks > end) break;
            const char* ke = strchr(ks + 1, '"');
            if (!ke || ke > end) break;

            const char* colon = strchr(ke, ':');
            if (!colon || colon > end) break;

            int node = atoi(ks + 1), slaved = atoi(colon + 1);
            snprintf(g_alias[g_nalias].family, sizeof(g_alias[0].family), "%s", family);
            g_alias[g_nalias].node = node;
            g_alias[g_nalias].slavedTo = slaved;
            g_nalias++;

            r = colon + 1;
        }

        p = end + 1;
    }

    LOGF("alias map: %d entr%s from %s\n", g_nalias, g_nalias == 1 ? "y" : "ies", path);
}

/* ================================================================= instances */
typedef struct {
    int32_t dataType; int32_t pad;
    union { double d; int64_t i; void* p; char* s; } v;
} UserParamData;

typedef struct {
    int       used;
    Family*   fam;
    unsigned char* inst;
    UserParamData* pdata;
    char*     tag;
    int       npins;
    int       alias[MAXPINS];
    int       ndelay;
} Instance;

static Instance g_inst[MAX_INST];

typedef int (*AnalyzeFn)(void*, double*);

/* Crash containment around third-party model code. Same intent on both platforms: catch the
 * fault, abandon that one evaluation, keep serving. */
#ifdef _WIN32
static jmp_buf     g_segv;
static volatile LONG g_segv_armed = 0;
static LONG CALLBACK crf_vectored_handler(EXCEPTION_POINTERS* ep) {
    if (g_segv_armed && ep && ep->ExceptionRecord &&
        ep->ExceptionRecord->ExceptionCode == EXCEPTION_ACCESS_VIOLATION) {
        g_segv_armed = 0;
        longjmp(g_segv, 1);
    }
    return EXCEPTION_CONTINUE_SEARCH;
}
#else
static sigjmp_buf g_segv;
static void on_segv(int s) { (void)s; siglongjmp(g_segv, 1); }
#endif

/* One analyze_nl call. Aliased nodes are resolved into vbuf first, exactly as the model.s own DC entry point does. */
static int eval_one(Instance* in, const double* v, double* vout, const double* delayed) {
    static double vbuf[MAXPINS];
    memcpy(vbuf, v, sizeof(double) * (size_t)in->npins);
    for (int i = 0; i < in->npins; i++)
        if (in->alias[i] >= 0) vbuf[i] = vbuf[in->alias[i]];

    capture_reset();
    g_npins_cur = in->npins;
    g_curv = vbuf;
    g_curdelay = delayed;

    int ok = 0;
#ifdef _WIN32
    g_segv_armed = 1;
    if (setjmp(g_segv) == 0) {
        ok = ((AnalyzeFn)in->fam->analyze_nl)(in->inst, vbuf) != 0;
    } else {
        LOGF("eval: access violation caught\n");
        ok = 0;
    }
    g_segv_armed = 0;
#else
    struct sigaction sa, old; memset(&sa, 0, sizeof(sa));
    sa.sa_handler = on_segv; sigemptyset(&sa.sa_mask);
    sigaction(SIGSEGV, &sa, &old);

    if (sigsetjmp(g_segv, 1) == 0) {
        ok = ((AnalyzeFn)in->fam->analyze_nl)(in->inst, vbuf) != 0;
    } else {
        LOGF("eval: SIGSEGV caught\n");
        ok = 0;
    }
    sigaction(SIGSEGV, &old, NULL);
#endif

    g_curv = NULL; g_curdelay = NULL;
    if (g_ndelay_seen < g_delay_idx) g_ndelay_seen = g_delay_idx;

    for (int i = 0; i < in->npins; i++)
        if (!isfinite(g_I[i])) ok = 0;

    /* THE BIAS THE MODEL WAS ACTUALLY HANDED, when it would not take it.
     *
     * From the host a refused point says nothing about the voltages that caused it, so "the bias is
     * outside the model's range" and "the solver handed it nonsense" are indistinguishable -- and
     * they have completely different fixes. Printing the vector separates them at a glance: an
     * operating point a person recognises, or a number no circuit ever had.
     *
     * Capped, because a harmonic-balance batch is thousands of points and the first few say
     * everything the rest would. */
    static int refusals_logged = 0;
    if (!ok && refusals_logged < 4) {
        refusals_logged++;
        char b[640]; size_t o = 0;
        for (int i = 0; i < in->npins && o + 32 < sizeof(b); i++)
            o += (size_t)snprintf(b + o, sizeof(b) - o, "%s%.6g", i ? " " : "", vbuf[i]);
        LOGF("eval %s: refused, V[%d] = [%s]\n", in->fam->name, in->npins, b);
    }

    if (vout) memcpy(vout, vbuf, sizeof(double) * (size_t)in->npins);
    return ok;
}

/* ================================================================= tiny JSON */
typedef struct { const char* s; size_t n; } Slice;

static const char* json_skip(const char* p, const char* e) {
    while (p < e && (*p == ' ' || *p == '\t' || *p == '\n' || *p == '\r')) p++;
    return p;
}
/* Find "key" at the top level of a JSON object and return a pointer just past its colon. */
static const char* json_find(const char* js, size_t len, const char* key) {
    const char* e = js + len;
    int depth = 0, instr = 0;
    size_t klen = strlen(key);
    for (const char* p = js; p < e; p++) {
        if (instr) {
            if (*p == '\\') { p++; continue; }
            if (*p == '"') instr = 0;
            continue;
        }
        if (*p == '"') {
            if (depth == 1 && (size_t)(e - p) > klen + 1 &&
                !strncmp(p + 1, key, klen) && p[1 + klen] == '"') {
                const char* q = json_skip(p + 2 + klen, e);
                if (q < e && *q == ':') return json_skip(q + 1, e);
            }
            instr = 1; continue;
        }
        if (*p == '{' || *p == '[') depth++;
        else if (*p == '}' || *p == ']') depth--;
    }
    return NULL;
}
static int json_str(const char* js, size_t len, const char* key, char* out, size_t osz) {
    const char* p = json_find(js, len, key);
    if (!p || *p != '"') return 0;
    p++; size_t n = 0;
    while (*p && *p != '"' && n + 1 < osz) {
        if (*p == '\\' && p[1]) p++;
        out[n++] = *p++;
    }
    out[n] = '\0';
    return 1;
}
static int json_num(const char* js, size_t len, const char* key, double* out) {
    const char* p = json_find(js, len, key);
    if (!p) return 0;
    if (*p == 't') { *out = 1; return 1; }
    if (*p == 'f') { *out = 0; return 1; }
    char* end = NULL;
    double d = strtod(p, &end);
    if (end == p) return 0;
    *out = d; return 1;
}

/* ================================================================= framing */
static int read_exact(void* buf, size_t n) {
    unsigned char* p = buf; size_t got = 0;
    while (got < n) {
        crf_ssize_t r = crf_read(STDIN_FILENO, p + got, n - got);
        if (r <= 0) return 0;
        got += (size_t)r;
    }
    return 1;
}
static void write_exact(const void* buf, size_t n) {
    const unsigned char* p = buf; size_t put = 0;
    while (put < n) {
        crf_ssize_t w = crf_write(STDOUT_FILENO, p + put, n - put);
        if (w <= 0) return;
        put += (size_t)w;
    }
}
static void send_frame(const char* json, const void* bin, uint32_t binLen) {
    uint32_t jl = (uint32_t)strlen(json);
    uint32_t hdr[2] = { jl, binLen };
    write_exact(hdr, sizeof(hdr));
    write_exact(json, jl);
    if (binLen) write_exact(bin, binLen);
}
static void send_err(const char* msg) {
    char buf[512];
    snprintf(buf, sizeof(buf), "{\"ok\":false,\"error\":\"%s\"}", msg);
    send_frame(buf, NULL, 0);
}

/* ================================================================= commands */
static const char* kind_name(int dataType) {
    /* Observed live: 0 = real, 3 = string (the File path). Others reported numerically so an
     * unknown kind surfaces rather than being silently coerced. */
    switch (dataType) {
        case 0: return "double";
        case 1: return "int";
        case 3: return "filePath";
        default: return "unknown";
    }
}

static void cmd_describe(void) {
    char out[16384]; size_t o = 0;
    o += (size_t)snprintf(out + o, sizeof(out) - o, "{\"ok\":true,\"protocol\":1,\"types\":[");
    int first = 1;
    for (int i = 0; i < g_nfam; i++) {
        Family* f = &g_fam[i];
        if (!f->valid) continue;
        o += (size_t)snprintf(out + o, sizeof(out) - o,
            "%s{\"typeId\":\"%s\",\"displayName\":\"%s\",\"externalPinCount\":%d,"
            "\"internalNodeCount\":%d,\"nonlinear\":%s,\"linear\":%s,\"params\":[",
            first ? "" : ",", f->name, f->name, f->numExtNodes, f->numIntNodes,
            f->analyze_nl ? "true" : "false", f->analyze_lin ? "true" : "false");
        first = 0;
        for (int p = 0; p < f->numPars; p++)
            o += (size_t)snprintf(out + o, sizeof(out) - o,
                "%s{\"name\":\"%s\",\"kind\":\"%s\"}",
                p ? "," : "", f->paramKeyword[p], kind_name(f->paramDataType[p]));
        o += (size_t)snprintf(out + o, sizeof(out) - o, "],\"nodes\":[");
        for (int n = 0; n < f->numExtNodes + f->numIntNodes; n++) {
            int a = alias_for(f->name, n);
            char slaved[16];
            if (a >= 0) snprintf(slaved, sizeof(slaved), "%d", a);
            else        snprintf(slaved, sizeof(slaved), "null");
            o += (size_t)snprintf(out + o, sizeof(out) - o,
                "%s{\"index\":%d,\"external\":%s,\"slavedTo\":%s}",
                n ? "," : "", n, n < f->numExtNodes ? "true" : "false", slaved);
        }
        o += (size_t)snprintf(out + o, sizeof(out) - o, "]}");
    }
    o += (size_t)snprintf(out + o, sizeof(out) - o, "]}");
    send_frame(out, NULL, 0);
}

static void cmd_create(const char* js, size_t jl) {
    char typeId[96];
    if (!json_str(js, jl, "typeId", typeId, sizeof(typeId))) { send_err("missing typeId"); return; }
    Family* f = NULL;
    for (int i = 0; i < g_nfam; i++)
        if (g_fam[i].valid && !strcmp(g_fam[i].name, typeId)) { f = &g_fam[i]; break; }
    if (!f) { send_err("unknown typeId"); return; }
    if (!f->analyze_nl) { send_err("family has no nonlinear analyze entry point"); return; }

    int h = -1;
    for (int i = 0; i < MAX_INST; i++) if (!g_inst[i].used) { h = i; break; }
    if (h < 0) { send_err("no free handles"); return; }
    Instance* in = &g_inst[h];
    memset(in, 0, sizeof(*in));

    /* Params come from the request's "params" object, matched BY KEYWORD against the family's own
     * declared table -- the worker never assumes a name, a count or an order. */
    const char* pobj = json_find(js, jl, "params");
    size_t plen = pobj ? (size_t)((js + jl) - pobj) : 0;

    in->pdata = calloc((size_t)(f->numPars > 0 ? f->numPars : 1), sizeof(UserParamData));
    for (int p = 0; p < f->numPars; p++) {
        in->pdata[p].dataType = f->paramDataType[p];
        if (!pobj) continue;
        if (f->paramDataType[p] == 3) {
            char sval[1024];
            if (json_str(pobj, plen, f->paramKeyword[p], sval, sizeof(sval)))
                in->pdata[p].v.s = strdup(sval);
        } else {
            double d;
            if (json_num(pobj, plen, f->paramKeyword[p], &d)) in->pdata[p].v.d = d;
        }
    }

    /* WHAT THE MODEL WAS ACTUALLY GIVEN.
     *
     * A compiled model's entire configuration can be a data file -- one family here declares four
     * parameters and one of them is the path to its .mdl -- and a path that is correct on the host
     * but absent HERE, inside the VM, fails as a refused operating point with nothing anywhere
     * mentioning a file. Establishing that took several rounds; stating it costs one line per
     * create, and the answer comes from the process that would actually open it.
     *
     * EVERY parameter is reported, not only the file ones. The worker matches the request against
     * the family's OWN declared keywords, so a name the host spells differently is not an error
     * anywhere -- it simply never matches, the model keeps its default, and the only symptom is a
     * device behaving as though a parameter it was given had not been given. Saying "supplied" or
     * "NOT SUPPLIED" per declared keyword is what makes that visible instead of invisible. */
    for (int p = 0; p < f->numPars; p++) {
        if (f->paramDataType[p] == 3) {
            const char* v = in->pdata[p].v.s;
            if (!v) { LOGF("create %s: %s (file) NOT SUPPLIED\n", f->name, f->paramKeyword[p]); continue; }
            FILE* probe = fopen(v, "rb");
            LOGF("create %s: %s=%s (%s)\n", f->name, f->paramKeyword[p], v,
                 probe ? "readable" : "NOT READABLE HERE");
            if (probe) fclose(probe);
        } else {
            double d;
            int supplied = pobj && json_num(pobj, plen, f->paramKeyword[p], &d);
            LOGF("create %s: %s=%.6g (%s)\n", f->name, f->paramKeyword[p], in->pdata[p].v.d,
                 supplied ? "supplied" : "NOT SUPPLIED, model default stands");
        }
    }

    char tagbuf[64];
    snprintf(tagbuf, sizeof(tagbuf), "h%d", h);
    in->tag = strdup(tagbuf);
    in->inst = calloc(1, 0x30);
    memcpy(in->inst + 0x00, &in->tag, sizeof(in->tag));
    memcpy(in->inst + 0x08, &f->elemAddr, sizeof(f->elemAddr));
    memcpy(in->inst + 0x10, &in->pdata, sizeof(in->pdata));
    /* +0x18/+0x20 NULL, +0x28 seniorData NULL -> pre_analysis self-inits (§29.2). */

    in->fam = f;
    in->npins = f->numExtNodes + f->numIntNodes;
    /* "alias":false on create genuinely disables the mapping INSIDE eval_one -- not just in the
     * caller's solver. Without this the switch is cosmetic and any A/B done with it is invalid. */
    double dal = 1.0; int use_alias = !(json_num(js, jl, "alias", &dal) && dal == 0.0);
    for (int i = 0; i < MAXPINS; i++) in->alias[i] = -1;
    if (use_alias)
        for (int i = 0; i < in->npins; i++) in->alias[i] = alias_for(f->name, i);
    in->used = 1;

    /* One throwaway eval so the delay structure is known before the client asks for it. */
    double v0[MAXPINS]; memset(v0, 0, sizeof(v0));
    g_ndelay_seen = 0;
    int ok = eval_one(in, v0, NULL, NULL);
    in->ndelay = g_delay_idx;

    /* A model that cannot be evaluated even at zero bias is not a biasing problem, and saying so
     * here separates "the operating point is outside its range" from "this instance was never
     * usable" -- which otherwise look identical from the host. */
    if (!ok) LOGF("create %s: probe eval at zero bias FAILED\n", f->name);

    char out[2048]; size_t o = 0;
    o += (size_t)snprintf(out + o, sizeof(out) - o,
        "{\"ok\":true,\"handle\":%d,\"pinCount\":%d,\"externalPinCount\":%d,"
        "\"internalNodeCount\":%d,\"probeEval\":%s,\"delayPairs\":[",
        h, in->npins, f->numExtNodes, f->numIntNodes, ok ? "true" : "false");
    for (int d = 0; d < in->ndelay && d < MAX_DELAY; d++)
        o += (size_t)snprintf(out + o, sizeof(out) - o, "%s{\"i\":%d,\"j\":%d,\"tau\":%.17g}",
                              d ? "," : "", g_delay[d].i, g_delay[d].j, g_delay[d].tau);
    o += (size_t)snprintf(out + o, sizeof(out) - o, "],\"alias\":[");
    for (int i = 0; i < in->npins; i++)
        o += (size_t)snprintf(out + o, sizeof(out) - o, "%s%d", i ? "," : "", in->alias[i]);
    o += (size_t)snprintf(out + o, sizeof(out) - o, "]}");
    send_frame(out, NULL, 0);
}

/* Structural probe: which nodes are real unknowns and which are degenerate, measured rather than
 * declared. A node whose numeric Jacobian ROW is identically zero carries no current under any
 * perturbation -- it is not a free unknown, and solving for it gives a singular system (§30.3).
 * Also classifies external pins: a pin with no conductive coupling to any other node is thermal
 * (§30.2 -- pin 3 has only the dissipated-power source attached, nothing conductive). */
static void cmd_probe(const char* js, size_t jl, const double* bin, uint32_t binLen) {
    double dh; if (!json_num(js, jl, "handle", &dh)) { send_err("missing handle"); return; }
    int h = (int)dh;
    if (h < 0 || h >= MAX_INST || !g_inst[h].used) { send_err("bad handle"); return; }
    Instance* in = &g_inst[h];
    int n = in->npins;

    double v[MAXPINS]; memset(v, 0, sizeof(v));
    if (binLen >= sizeof(double) * (size_t)n) memcpy(v, bin, sizeof(double) * (size_t)n);

    double I0[MAXPINS], num[MAXPINS][MAXPINS];
    if (!eval_one(in, v, NULL, NULL)) { send_err("probe base eval failed"); return; }
    memcpy(I0, g_I, sizeof(I0));
    double Gan[MAXPINS][MAXPINS]; memcpy(Gan, g_G, sizeof(Gan));

    const double hstep = 1e-4;
    for (int j = 0; j < n; j++) {
        if (in->alias[j] >= 0) { for (int i = 0; i < n; i++) num[i][j] = 0.0; continue; }
        double save = v[j]; v[j] = save + hstep;
        if (!eval_one(in, v, NULL, NULL)) { v[j] = save; for (int i = 0; i < n; i++) num[i][j] = 0.0; continue; }
        for (int i = 0; i < n; i++) num[i][j] = (g_I[i] - I0[i]) / hstep;
        v[j] = save;
    }

    char out[8192]; size_t o = 0;
    o += (size_t)snprintf(out + o, sizeof(out) - o, "{\"ok\":true,\"nodes\":[");
    for (int i = 0; i < n; i++) {
        int degenerate = 1, coupled = 0;
        for (int j = 0; j < n; j++) if (fabs(num[i][j]) > 1e-9) degenerate = 0;
        /* Symmetry is the discriminator, NOT magnitude. A conductive path shows a
         * reciprocal pair -- dI(i)/dV(j) matching dI(j)/dV(i) -- while a thermal
         * coupling is strongly one-way, so comparing the two entries separates them
         * where comparing either one against a threshold cannot. */
        for (int j = 0; j < n; j++) {
            if (i == j) continue;
            double a = Gan[i][j], b = Gan[j][i];
            if (fabs(a) > 1e-6 && fabs(a - b) <= 1e-3 * fabs(a)) coupled = 1;
        }
        o += (size_t)snprintf(out + o, sizeof(out) - o,
            "%s{\"index\":%d,\"external\":%s,\"degenerate\":%s,\"conductivelyCoupled\":%s,"
            "\"slavedTo\":%d,\"quantityKind\":\"%s\"}",
            i ? "," : "", i, i < in->fam->numExtNodes ? "true" : "false",
            degenerate ? "true" : "false", coupled ? "true" : "false", in->alias[i],
            (i < in->fam->numExtNodes && !coupled) ? "thermal" : "electrical");
    }
    o += (size_t)snprintf(out + o, sizeof(out) - o, "]}");

    /* HOW THE NODES WERE CLASSIFIED, once per instance.
     *
     * These roles are measured and then decide how the host stamps the device — which node is a
     * free unknown, which is thermal, which follows another. A misreading is invisible from the
     * host: the device stamps cleanly, every number is finite, and the only symptom is a solve that
     * will not converge. Cheap to print, and it is the one part of the contract that is inferred
     * rather than declared. */
    {
        char roles[600]; size_t r = 0;
        for (int i = 0; i < n && r + 32 < sizeof(roles); i++) {
            int degenerate = 1, coupled = 0;
            for (int j = 0; j < n; j++) if (fabs(num[i][j]) > 1e-9) degenerate = 0;
            for (int j = 0; j < n; j++) {
                if (i == j) continue;
                double a = Gan[i][j], b = Gan[j][i];
                if (fabs(a) > 1e-6 && fabs(a - b) <= 1e-3 * fabs(a)) coupled = 1;
            }
            // Row zero says nothing responds to this node's own current. Whether anything responds
            // to its VOLTAGE is the column, and the two answers call for opposite handling: a node
            // isolated in both directions can be collapsed away safely, while one whose voltage
            // still matters but which nothing drives is genuinely underdetermined.
            int columnZero = 1;
            for (int j = 0; j < n; j++) if (fabs(num[j][i]) > 1e-9) columnZero = 0;

            r += (size_t)snprintf(roles + r, sizeof(roles) - r, "%s%d:%s%s%s",
                                  i ? " " : "", i,
                                  i < in->fam->numExtNodes ? "ext" : "int",
                                  (i < in->fam->numExtNodes && !coupled) ? "-thermal" : "",
                                  degenerate ? (columnZero ? "-ISOLATED" : "-UNDRIVEN") : "");
        }
        LOGF("probe %s: %s\n", in->fam->name, roles);
    }

    /* IS THE MODEL'S OWN JACOBIAN THE ONE ITS CURRENTS IMPLY?
     *
     * Both matrices are already in hand here: Gan is what the model reported analytically, num is
     * finite differences of its own currents. Newton cannot converge on a Jacobian that does not
     * match the residual it is meant to linearise — the solve stalls partway up the bias ramp and
     * grinds, which reads as "the circuit will not converge" and blames the circuit.
     *
     * The TRANSPOSED comparison is the one worth making rather than assuming: dI(i)/dV(j) read in
     * the wrong index order is the classic way an N-port derivative block goes wrong, it is nearly
     * invisible on a symmetric device, and a FET is not symmetric. If the transposed error is the
     * small one, the matrix is being read the wrong way round. */
    {
        double scale = 0, direct = 0, transposed = 0;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) {
                if (fabs(num[i][j]) > scale) scale = fabs(num[i][j]);
                double d = fabs(Gan[i][j] - num[i][j]);
                double t = fabs(Gan[j][i] - num[i][j]);
                if (d > direct)     direct = d;
                if (t > transposed) transposed = t;
            }
        LOGF("probe %s: jacobian vs finite-diff: direct %.3g, transposed %.3g (scale %.3g)\n",
             in->fam->name, direct, transposed, scale);
    }

    send_frame(out, NULL, 0);
}

/* BATCHED eval. in:  count * npins doubles  (+ optionally count * ndelay doubles)
 *              out:  per vector -- I[npins], Q[npins], G[npins*npins], C[npins*npins],
 *                    preceded by a count-long status array (1.0 ok / 0.0 failed). */
static void cmd_eval(const char* js, size_t jl, const double* bin, uint32_t binLen) {
    double dh, dc;
    if (!json_num(js, jl, "handle", &dh) || !json_num(js, jl, "count", &dc)) {
        send_err("missing handle/count"); return;
    }
    int h = (int)dh, count = (int)dc;
    if (h < 0 || h >= MAX_INST || !g_inst[h].used) { send_err("bad handle"); return; }
    Instance* in = &g_inst[h];
    int n = in->npins;
    if (count <= 0) { send_err("count must be positive"); return; }

    double ddel = 0; int hasDelay = json_num(js, jl, "delayed", &ddel) && ddel != 0.0;
    size_t perVecIn = (size_t)n + (hasDelay ? (size_t)in->ndelay : 0);
    size_t needIn = perVecIn * (size_t)count * sizeof(double);
    if (binLen < needIn) { send_err("short eval payload"); return; }

    size_t perVecOut = (size_t)(2 * n + 2 * n * n);
    size_t outLen = ((size_t)count + perVecOut * (size_t)count) * sizeof(double);
    double* out = malloc(outLen);
    if (!out) { send_err("oom"); return; }

    double* status = out;
    double* body = out + count;
    for (int k = 0; k < count; k++) {
        const double* v = bin + (size_t)k * perVecIn;
        const double* dly = hasDelay ? v + n : NULL;
        int ok = eval_one(in, v, NULL, dly);
        status[k] = ok ? 1.0 : 0.0;
        double* w = body + (size_t)k * perVecOut;
        memcpy(w, g_I, sizeof(double) * (size_t)n);            w += n;
        memcpy(w, g_Q, sizeof(double) * (size_t)n);            w += n;
        for (int i = 0; i < n; i++) { memcpy(w, g_G[i], sizeof(double) * (size_t)n); w += n; }
        for (int i = 0; i < n; i++) { memcpy(w, g_C[i], sizeof(double) * (size_t)n); w += n; }
    }

    char hdr[256];
    snprintf(hdr, sizeof(hdr),
        "{\"ok\":true,\"count\":%d,\"pinCount\":%d,"
        "\"layout\":\"status[count],then per vector I[n],Q[n],G[n*n],C[n*n]\"}", count, n);
    send_frame(hdr, out, (uint32_t)outLen);
    free(out);
}

static void cmd_destroy(const char* js, size_t jl) {
    double dh; if (!json_num(js, jl, "handle", &dh)) { send_err("missing handle"); return; }
    int h = (int)dh;
    if (h < 0 || h >= MAX_INST || !g_inst[h].used) { send_err("bad handle"); return; }
    Instance* in = &g_inst[h];
    /* seniorData (+0x28) was calloc'd by pre_analysis inside the model; the ABI exposes no
     * teardown entry point for it, so it is deliberately leaked rather than freed blind. Bounded
     * and documented: MAX_INST instances per worker lifetime. */
    free(in->inst); free(in->pdata); free(in->tag);
    memset(in, 0, sizeof(*in));
    send_frame("{\"ok\":true}", NULL, 0);
}

/* ================================================================= exported-symbol discovery
 * Which families a library serves is a property OF THE LIBRARY, so it is read from the library --
 * not from a list of names compiled in here, which would have to be edited for every library that
 * ever ships and would silently serve nothing for one it had not heard of.
 *
 * Same rule, same output, two container formats. */

#ifdef _WIN32
/* PE export directory. The loaded image is already RVA-addressable from its own base, so the
 * walk is a straight pointer chase; the only subtlety is FORWARDER exports, whose "address" RVA
 * points back INSIDE the export directory (at a "OtherDll.symbol" string) rather than at code.
 * Calling one of those as a function would jump into a string. */
static int find_boot_symbols(crf_lib_t lib, char out[][MAXNAME], int max) {
    if (!lib) return 0;
    unsigned char* base = (unsigned char*)lib;

    IMAGE_DOS_HEADER* dos = (IMAGE_DOS_HEADER*)base;
    if (dos->e_magic != IMAGE_DOS_SIGNATURE) return 0;
    IMAGE_NT_HEADERS* nt = (IMAGE_NT_HEADERS*)(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE) return 0;

    IMAGE_DATA_DIRECTORY dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_EXPORT];
    if (!dir.VirtualAddress || !dir.Size) return 0;

    IMAGE_EXPORT_DIRECTORY* ex = (IMAGE_EXPORT_DIRECTORY*)(base + dir.VirtualAddress);
    DWORD* nameRvas = (DWORD*)(base + ex->AddressOfNames);
    DWORD* funcRvas = (DWORD*)(base + ex->AddressOfFunctions);
    WORD*  ordinals = (WORD*)(base + ex->AddressOfNameOrdinals);

    int n = 0;
    for (DWORD i = 0; i < ex->NumberOfNames && n < max; i++) {
        const char* name = (const char*)(base + nameRvas[i]);
        if (strncmp(name, BOOT_PREFIX, sizeof(BOOT_PREFIX) - 1) != 0) continue;
        if (name[sizeof(BOOT_PREFIX) - 1] == '\0') continue;

        DWORD rva = funcRvas[ordinals[i]];
        if (rva >= dir.VirtualAddress && rva < dir.VirtualAddress + dir.Size) continue; /* forwarder */

        int seen = 0;
        for (int k = 0; k < n; k++) if (!strcmp(out[k], name)) { seen = 1; break; }
        if (seen) continue;

        snprintf(out[n], MAXNAME, "%s", name);
        n++;
    }
    return n;
}
static void* crf_sym(crf_lib_t lib, const char* name) {
    return (void*)(uintptr_t)GetProcAddress(lib, name);
}
#else
/* ELF dynamic symbol table, reached through the DYNAMIC section. The symbol count is not stored
 * directly, so it comes from whichever hash table is present: DT_HASH states nchain outright;
 * DT_GNU_HASH (the only one many libraries now carry) requires walking the bucket chains to find
 * the highest index actually used. */
static int find_boot_symbols(crf_lib_t lib, char out[][MAXNAME], int max) {
    struct link_map* lm = NULL;
    if (!lib || dlinfo(lib, RTLD_DI_LINKMAP, &lm) != 0 || !lm || !lm->l_ld) return 0;

    const ElfW(Sym)* symtab = NULL;
    const char*      strtab = NULL;
    const ElfW(Word)* hash  = NULL;
    const ElfW(Word)* gnu   = NULL;

    for (const ElfW(Dyn)* d = lm->l_ld; d->d_tag != DT_NULL; d++) {
        switch (d->d_tag) {
            case DT_SYMTAB:   symtab = (const ElfW(Sym)*)d->d_un.d_ptr;  break;
            case DT_STRTAB:   strtab = (const char*)d->d_un.d_ptr;       break;
            case DT_HASH:     hash   = (const ElfW(Word)*)d->d_un.d_ptr; break;
            case DT_GNU_HASH: gnu    = (const ElfW(Word)*)d->d_un.d_ptr; break;
            default: break;
        }
    }
    if (!symtab || !strtab) return 0;

    /* How many symbols there are. */
    size_t count = 0;
    if (hash) {
        count = hash[1];                                  /* nchain */
    } else if (gnu) {
        ElfW(Word) nbuckets = gnu[0], symoffset = gnu[1], bloomsize = gnu[2];
        const ElfW(Addr)* bloom = (const ElfW(Addr)*)&gnu[4];
        const ElfW(Word)* buckets = (const ElfW(Word)*)&bloom[bloomsize];
        const ElfW(Word)* chain   = &buckets[nbuckets];

        ElfW(Word) last = 0;
        for (ElfW(Word) i = 0; i < nbuckets; i++)
            if (buckets[i] > last) last = buckets[i];

        if (last < symoffset) return 0;
        while (!(chain[last - symoffset] & 1)) last++;     /* to the end of that bucket's chain */
        count = last + 1;
    } else {
        return 0;
    }

    int n = 0;
    for (size_t i = 0; i < count && n < max; i++) {
        const ElfW(Sym)* sym = &symtab[i];
        if (sym->st_shndx == SHN_UNDEF) continue;          /* imported, not served here */
        if (ELF64_ST_TYPE(sym->st_info) != STT_FUNC) continue;

        const char* name = strtab + sym->st_name;
        if (strncmp(name, BOOT_PREFIX, sizeof(BOOT_PREFIX) - 1) != 0) continue;
        if (name[sizeof(BOOT_PREFIX) - 1] == '\0') continue;

        int seen = 0;                                      /* a symbol can appear more than once */
        for (int k = 0; k < n; k++) if (!strcmp(out[k], name)) { seen = 1; break; }
        if (seen) continue;

        snprintf(out[n], MAXNAME, "%s", name);
        n++;
    }
    return n;
}
static void* crf_sym(crf_lib_t lib, const char* name) { return dlsym(lib, name); }
#endif

/* ================================================================= the worker itself
 * On Linux this is called straight from main. On Windows it lives in crf-model-host.dll and is
 * called by the launcher stub AFTER that stub has staged and loaded this very module under the
 * name the model asks for -- so by the time the model is loaded here, its import already resolves. */
#ifdef _WIN32
__declspec(dllexport)
#endif
int crf_worker_main(int argc, char** argv) {
    g_log = stderr;

#ifdef _WIN32
    /* R-win-7. Windows stdio defaults to TEXT mode and would translate 0x0A to 0x0D 0x0A inside
     * the raw-doubles payload -- corrupting numerics in a way that reads as a model producing wrong
     * answers rather than as a transport fault. A describe round trip would never show it; only
     * real doubles would. Set before the first frame crosses, in both directions. */
    _setmode(_fileno(stdout), _O_BINARY);
    _setmode(_fileno(stdin),  _O_BINARY);
    AddVectoredExceptionHandler(1, crf_vectored_handler);
#endif

    if (argc < 2) {
        fprintf(stderr, "usage: %s <model-library> [alias-map.json]\n",
                argc > 0 ? argv[0] : "senior_worker");
        return 2;
    }
    g_no_delay_write = getenv("SENIOR_NO_DELAY_WRITE") != NULL;
    if (g_no_delay_write) LOGF("get_delay_v: OUT-POINTER WRITE DISABLED (bug repro)\n");

    crf_lib_t lib;
#ifdef _WIN32
    {
        int wlen = MultiByteToWideChar(CP_UTF8, 0, argv[1], -1, NULL, 0);
        wchar_t* wpath = wlen > 0 ? (wchar_t*)calloc((size_t)wlen, sizeof(wchar_t)) : NULL;
        if (!wpath) { fprintf(stderr, "model path could not be converted\n"); return 1; }
        MultiByteToWideChar(CP_UTF8, 0, argv[1], -1, wpath, wlen);
        lib = LoadLibraryExW(wpath, NULL, LOAD_WITH_ALTERED_SEARCH_PATH);
        if (!lib) {
            fprintf(stderr, "LoadLibrary failed for %s (error %lu)\n",
                    argv[1], (unsigned long)GetLastError());
            free(wpath);
            return 1;
        }
        free(wpath);
        g_base = (uintptr_t)lib;          /* on Windows the HMODULE IS the load base */
    }
#else
    personality(ADDR_NO_RANDOMIZE);
    lib = dlopen(argv[1], RTLD_NOW | RTLD_LOCAL);
    if (!lib) { fprintf(stderr, "dlopen failed: %s\n", dlerror()); return 1; }
    {
        struct link_map* lm = NULL;
        if (dlinfo(lib, RTLD_DI_LINKMAP, &lm) == 0 && lm) g_base = (uintptr_t)lm->l_addr;
    }
#endif

    /* Every family the library ADVERTISES, booted in turn. Each boot calls load_elements. */
    char boots[MAX_FAMILIES][MAXNAME];
    int  nboots = find_boot_symbols(lib, boots, MAX_FAMILIES);
    if (nboots == 0)
        LOGF("no '%s*' entry points found in %s -- is this a model library?\n", BOOT_PREFIX, argv[1]);

    for (int i = 0; i < nboots; i++) {
        int (*fn)(void) = (int (*)(void))(uintptr_t)crf_sym(lib, boots[i]);
        if (!fn) { LOGF("symbol lookup for %s failed\n", boots[i]); continue; }
        g_booting = boots[i];
        fn();
        g_booting = NULL;
    }
    if (argc > 2) load_alias_map(argv[2]);

    LOGF("worker ready: %d families, base=0x%llx\n", g_nfam, (unsigned long long)g_base);

    for (;;) {
        uint32_t hdr[2];
        if (!read_exact(hdr, sizeof(hdr))) break;
        uint32_t jl = hdr[0], bl = hdr[1];
        if (jl > (1u << 20) || bl > (1u << 28)) { send_err("frame too large"); break; }
        char* js = malloc(jl + 1);
        unsigned char* bin = bl ? malloc(bl) : NULL;
        if (!js || (bl && !bin)) { send_err("oom"); free(js); free(bin); break; }
        if (!read_exact(js, jl) || (bl && !read_exact(bin, bl))) { free(js); free(bin); break; }
        js[jl] = '\0';

        char cmd[32];
        if (!json_str(js, jl, "cmd", cmd, sizeof(cmd))) send_err("missing cmd");
        else if (!strcmp(cmd, "describe")) cmd_describe();
        else if (!strcmp(cmd, "create"))   cmd_create(js, jl);
        else if (!strcmp(cmd, "probe"))    cmd_probe(js, jl, (const double*)bin, bl);
        else if (!strcmp(cmd, "eval"))     cmd_eval(js, jl, (const double*)bin, bl);
        else if (!strcmp(cmd, "destroy"))  cmd_destroy(js, jl);
        else if (!strcmp(cmd, "shutdown")) { send_frame("{\"ok\":true}", NULL, 0); free(js); free(bin); break; }
        else send_err("unknown cmd");

        free(js); free(bin);
    }
    return 0;
}

#ifndef _WIN32
int main(int argc, char** argv) { return crf_worker_main(argc, argv); }
#endif

#endif /* CRF_CORE */

/* ===================================================================================== *
 *  WINDOWS LAUNCHER STUB  (-DCRF_HOST_STUB)                                             *
 *                                                                                        *
 *  Its whole job is to make the model's import-by-name resolvable before the model is    *
 *  loaded, then hand over.                                                               *
 *                                                                                        *
 *    1. read the model library's own PE import table and find the module our ABI symbols  *
 *       come from                          (R-win-2 / R-win-2a -- never a remembered name) *
 *    2. copy the shipped crf-model-host.dll into a per-user cache under THAT name          *
 *                                          (R-win-3 -- never into the repo, install or kit) *
 *    3. LoadLibraryW the staged copy, then call into it                                    *
 *                                          (R-win-4 -- explicit load, never a search path) *
 * ===================================================================================== */
#if defined(_WIN32) && defined(CRF_HOST_STUB)

static const char* const g_abi_symbols[] = { CRF_ABI_SYMBOLS };
#define CRF_ABI_COUNT ((int)(sizeof(g_abi_symbols) / sizeof(g_abi_symbols[0])))

/* -------- a bounded view over the model file's bytes -------- */
typedef struct { const unsigned char* p; size_t n; } Blob;

static int blob_u16(Blob b, size_t off, uint16_t* out) {
    if (off + 2 > b.n) return 0;
    memcpy(out, b.p + off, 2); return 1;
}
static int blob_u32(Blob b, size_t off, uint32_t* out) {
    if (off + 4 > b.n) return 0;
    memcpy(out, b.p + off, 4); return 1;
}
static int blob_u64(Blob b, size_t off, uint64_t* out) {
    if (off + 8 > b.n) return 0;
    memcpy(out, b.p + off, 8); return 1;
}
/* A NUL-terminated ASCII string that must lie entirely inside the blob. */
static int blob_str(Blob b, size_t off, char* out, size_t osz) {
    size_t i = 0;
    while (off + i < b.n && i + 1 < osz) {
        char c = (char)b.p[off + i];
        out[i] = c;
        if (c == '\0') return 1;
        i++;
    }
    return 0;
}

/* PE section table -> file offset for a given RVA. A file on disk is NOT RVA-addressable the way a
 * loaded image is, so every RVA has to go through this. */
typedef struct { uint32_t va, vsize, raw, rawsize; } Section;

typedef struct {
    Section  sec[96];
    int      nsec;
    int      pe32plus;
    uint32_t importRva, importSize;
} PeInfo;

static int pe_parse(Blob b, PeInfo* info) {
    memset(info, 0, sizeof(*info));

    uint16_t mz;
    if (!blob_u16(b, 0, &mz) || mz != 0x5A4D) return 0;              /* "MZ" */
    uint32_t peOff;
    if (!blob_u32(b, 0x3C, &peOff)) return 0;
    uint32_t sig;
    if (!blob_u32(b, peOff, &sig) || sig != 0x00004550) return 0;    /* "PE\0\0" */

    size_t coff = (size_t)peOff + 4;
    uint16_t nsec, optSize;
    if (!blob_u16(b, coff + 2, &nsec)) return 0;
    if (!blob_u16(b, coff + 16, &optSize)) return 0;

    size_t opt = coff + 20;
    uint16_t magic;
    if (!blob_u16(b, opt, &magic)) return 0;
    if (magic == 0x20B)      info->pe32plus = 1;
    else if (magic == 0x10B) info->pe32plus = 0;
    else                     return 0;

    /* DataDirectory[1] is the import table. Its offset inside the optional header differs by
     * format only because PE32+ widens four of the preceding fields. */
    size_t dd = opt + (info->pe32plus ? 112 : 96);
    if (!blob_u32(b, dd + 8, &info->importRva)) return 0;
    if (!blob_u32(b, dd + 12, &info->importSize)) return 0;

    if (nsec > 96) nsec = 96;
    size_t sh = opt + optSize;
    for (int i = 0; i < nsec; i++) {
        size_t s = sh + (size_t)i * 40;
        uint32_t vsize, va, rawsize, raw;
        if (!blob_u32(b, s + 8, &vsize))   return 0;
        if (!blob_u32(b, s + 12, &va))     return 0;
        if (!blob_u32(b, s + 16, &rawsize))return 0;
        if (!blob_u32(b, s + 20, &raw))    return 0;
        info->sec[info->nsec].va      = va;
        info->sec[info->nsec].vsize   = vsize ? vsize : rawsize;
        info->sec[info->nsec].raw     = raw;
        info->sec[info->nsec].rawsize = rawsize;
        info->nsec++;
    }
    return info->nsec > 0;
}

static int pe_off(const PeInfo* info, uint32_t rva, size_t* out) {
    for (int i = 0; i < info->nsec; i++) {
        const Section* s = &info->sec[i];
        if (rva >= s->va && rva < s->va + s->vsize) {
            uint32_t delta = rva - s->va;
            if (delta >= s->rawsize) return 0;        /* lives only in the virtual tail */
            *out = (size_t)s->raw + delta;
            return 1;
        }
    }
    return 0;
}

/* R-win-2 / R-win-2a. Walk the import descriptors and return the module name of the one that
 * imports OUR OWN ABI symbols. Deliberately NOT "the descriptor whose name looks familiar":
 * matching a remembered module name would put kit knowledge back into this file one string at a
 * time, and would silently serve nothing for a kit that names its host module differently. */
static int derive_host_module(Blob b, char* out, size_t osz) {
    PeInfo info;
    if (!pe_parse(b, &info)) return 0;
    if (!info.importRva) return 0;

    size_t descOff;
    if (!pe_off(&info, info.importRva, &descOff)) return 0;

    for (int d = 0; d < 4096; d++) {
        size_t e = descOff + (size_t)d * 20;
        uint32_t origThunk, nameRva, firstThunk;
        if (!blob_u32(b, e + 0,  &origThunk))  return 0;
        if (!blob_u32(b, e + 12, &nameRva))    return 0;
        if (!blob_u32(b, e + 16, &firstThunk)) return 0;
        if (!origThunk && !nameRva && !firstThunk) break;      /* terminating null descriptor */

        uint32_t thunkRva = origThunk ? origThunk : firstThunk;
        if (!thunkRva || !nameRva) continue;

        size_t thunkOff;
        if (!pe_off(&info, thunkRva, &thunkOff)) continue;

        int matched = 0;
        for (int t = 0; t < 65536 && !matched; t++) {
            uint64_t entry;
            if (info.pe32plus) {
                if (!blob_u64(b, thunkOff + (size_t)t * 8, &entry)) break;
                if (!entry) break;
                if (entry & 0x8000000000000000ULL) continue;    /* imported by ordinal */
            } else {
                uint32_t e32;
                if (!blob_u32(b, thunkOff + (size_t)t * 4, &e32)) break;
                if (!e32) break;
                if (e32 & 0x80000000u) continue;
                entry = e32;
            }
            size_t hintOff;
            if (!pe_off(&info, (uint32_t)entry, &hintOff)) continue;

            char sym[256];
            if (!blob_str(b, hintOff + 2, sym, sizeof(sym))) continue;   /* +2 skips the hint */
            for (int a = 0; a < CRF_ABI_COUNT; a++)
                if (!strcmp(sym, g_abi_symbols[a])) { matched = 1; break; }
        }
        if (!matched) continue;

        size_t nameOff;
        if (!pe_off(&info, nameRva, &nameOff)) continue;
        if (!blob_str(b, nameOff, out, osz)) continue;
        return out[0] != '\0';
    }
    return 0;
}

/* -------- staging (R-win-3) --------
 * The file that ends up bearing the library's own module name is created ON THE USER'S MACHINE,
 * FROM THEIR OWN KIT -- it is never built, committed or distributed by us. A kit is read-only and
 * an install may sit under Program Files, so neither is a legal place to write it; a per-user
 * cache is. */
static uint64_t fnv1a(const char* s) {
    uint64_t h = 1469598103934665603ULL;
    for (; *s; s++) { h ^= (unsigned char)*s; h *= 1099511628211ULL; }
    return h;
}

static int widen(const char* s, wchar_t* out, int outCount) {
    return MultiByteToWideChar(CP_UTF8, 0, s, -1, out, outCount) > 0;
}

static void ensure_dir_chain(wchar_t* path) {
    for (wchar_t* p = path + 3; *p; p++) {          /* +3 skips "C:\" */
        if (*p != L'\\') continue;
        *p = L'\0';
        CreateDirectoryW(path, NULL);
        *p = L'\\';
    }
    CreateDirectoryW(path, NULL);
}

static int file_newer(const wchar_t* a, const wchar_t* b) {
    WIN32_FILE_ATTRIBUTE_DATA fa, fb;
    if (!GetFileAttributesExW(a, GetFileExInfoStandard, &fa)) return 0;
    if (!GetFileAttributesExW(b, GetFileExInfoStandard, &fb)) return 1;   /* b missing */
    return CompareFileTime(&fa.ftLastWriteTime, &fb.ftLastWriteTime) > 0;
}

/* %LOCALAPPDATA%\circuitRF\hostshim\<hash-of-derived-name>\<derived-name> */
static int stage_shim(const wchar_t* shippedDll, const char* derivedName,
                      wchar_t* outPath, int outCount) {
    wchar_t local[MAX_PATH];
    if (!GetEnvironmentVariableW(L"LOCALAPPDATA", local, MAX_PATH)) return 0;

    wchar_t wname[MAX_PATH];
    if (!widen(derivedName, wname, MAX_PATH)) return 0;

    wchar_t dir[MAX_PATH * 2];
    _snwprintf(dir, MAX_PATH * 2, L"%s\\circuitRF\\hostshim\\%016llx",
               local, (unsigned long long)fnv1a(derivedName));
    dir[MAX_PATH * 2 - 1] = L'\0';
    ensure_dir_chain(dir);

    _snwprintf(outPath, outCount, L"%s\\%s", dir, wname);
    outPath[outCount - 1] = L'\0';

    /* Refresh when the shipped DLL is newer than the staged copy (or the copy is missing).
     * A copy that is currently loaded by another worker cannot be overwritten -- that is fine:
     * it is the same content, and the existing file is what we load. */
    if (file_newer(shippedDll, outPath))
        CopyFileW(shippedDll, outPath, FALSE);

    return GetFileAttributesW(outPath) != INVALID_FILE_ATTRIBUTES;
}

/* -------- entry point -------- */
int main(void) {
    g_log = stderr;

    int wargc = 0;
    LPWSTR* wargv = CommandLineToArgvW(GetCommandLineW(), &wargc);
    if (!wargv || wargc < 2) {
        fprintf(stderr, "usage: senior_worker <model-library> [alias-map.json]\n");
        return 2;
    }

    /* 1. read the model library's bytes and derive the host module name it imports from. */
    HANDLE h = CreateFileW(wargv[1], GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                           NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE) {
        fprintf(stderr, "model library could not be opened (error %lu)\n",
                (unsigned long)GetLastError());
        return 1;
    }
    LARGE_INTEGER sz;
    if (!GetFileSizeEx(h, &sz) || sz.QuadPart <= 0 || sz.QuadPart > (LONGLONG)(512u << 20)) {
        fprintf(stderr, "model library has an implausible size\n");
        CloseHandle(h);
        return 1;
    }
    unsigned char* bytes = (unsigned char*)malloc((size_t)sz.QuadPart);
    if (!bytes) { CloseHandle(h); fprintf(stderr, "out of memory\n"); return 1; }
    DWORD got = 0, total = 0;
    while (total < (DWORD)sz.QuadPart &&
           ReadFile(h, bytes + total, (DWORD)sz.QuadPart - total, &got, NULL) && got)
        total += got;
    CloseHandle(h);

    Blob blob = { bytes, (size_t)total };
    char derived[MAX_PATH];
    if (!derive_host_module(blob, derived, sizeof(derived))) {
        /* A clear report, never a fallback guess: this library is not one this worker can drive. */
        fprintf(stderr,
                "This library imports none of the host callbacks this worker supplies, so it is\n"
                "not a model this worker can drive. Nothing was loaded.\n");
        free(bytes);
        return 1;
    }
    free(bytes);
    LOGF("host module derived from the model's own import table: %s\n", derived);

    /* 2. stage the shipped shim under that name, beside this executable. */
    wchar_t exePath[MAX_PATH];
    if (!GetModuleFileNameW(NULL, exePath, MAX_PATH)) { fprintf(stderr, "no module path\n"); return 1; }
    wchar_t* slash = wcsrchr(exePath, L'\\');
    if (slash) *(slash + 1) = L'\0';
    wchar_t shipped[MAX_PATH * 2];
    _snwprintf(shipped, MAX_PATH * 2, L"%scrf-model-host.dll", exePath);
    shipped[MAX_PATH * 2 - 1] = L'\0';

    wchar_t staged[MAX_PATH * 2];
    if (!stage_shim(shipped, derived, staged, MAX_PATH * 2)) {
        fprintf(stderr, "the host shim could not be staged for '%s'\n", derived);
        return 1;
    }

    /* 3. Load the staged shim EXPLICITLY, before the model. Windows resolves an import by first
     *    checking whether a module with that base name is ALREADY LOADED, so the model's import
     *    binds to this one with no SetDllDirectory, no AddDllDirectory and no PATH edit. The
     *    search-path approaches all work by accident of ordering and fail when something else on
     *    the machine gets there first. */
    HMODULE shim = LoadLibraryExW(staged, NULL, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (!shim) {
        fprintf(stderr, "the staged host shim failed to load (error %lu)\n",
                (unsigned long)GetLastError());
        return 1;
    }

    typedef int (*WorkerMainFn)(int, char**);
    WorkerMainFn worker = (WorkerMainFn)(uintptr_t)GetProcAddress(shim, "crf_worker_main");
    if (!worker) { fprintf(stderr, "crf_worker_main missing from the host shim\n"); return 1; }

    /* Hand the original arguments over as UTF-8; the shim converts back to wide for LoadLibraryW,
     * so a non-ASCII path round-trips losslessly rather than going through the ANSI codepage. */
    char** argv8 = (char**)calloc((size_t)wargc + 1, sizeof(char*));
    if (!argv8) { fprintf(stderr, "out of memory\n"); return 1; }
    for (int i = 0; i < wargc; i++) {
        int need = WideCharToMultiByte(CP_UTF8, 0, wargv[i], -1, NULL, 0, NULL, NULL);
        argv8[i] = (char*)calloc((size_t)(need > 0 ? need : 1), 1);
        if (need > 0) WideCharToMultiByte(CP_UTF8, 0, wargv[i], -1, argv8[i], need, NULL, NULL);
    }

    return worker(wargc, argv8);
}

#endif /* _WIN32 && CRF_HOST_STUB */
