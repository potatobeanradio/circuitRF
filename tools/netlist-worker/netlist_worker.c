/*
 * netlist_worker.c — play host to a compiled model library so it will describe its parts.
 *
 * See README.md for the design. The two rules that shape every line here:
 *
 *   1. NO A PRIORI KNOWLEDGE. Nothing about a kit is tabulated, derived by rule, or remembered
 *      between runs. Host module names, host symbol names and the prefix a library puts on them
 *      are all read out of that library at run time.
 *
 *   2. THEREFORE NOTHING HERE IS NAMED AFTER A KIT. What this file knows is ABI vocabulary — the
 *      role half of a host symbol, the part after any prefix. Matching on that suffix is what
 *      lets the prefix stay on the user's machine.
 *
 * ONE SOURCE, TWO PRODUCTS (mirrors tools/senior-worker; do not fork it):
 *
 *   -DCRF_DRIVER   the executable: scan, intercept, load, report.
 *   -DCRF_SHIM     a host module, for a library that binds its host STATICALLY. Compiled once per
 *                  module name that library asks for, each with its own generated .def.
 *
 * PLATFORM. Windows-only by nature — the libraries are Windows DLLs. On macOS and Linux it runs
 * under Wine in the container run.sh builds. That container is TEMPORARY; see README.md §"macOS and
 * Linux". Never tested on Windows.
 */

#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* ────────────────────────────────────────────────────────────────────────────
 * The ABI roles this worker understands.
 *
 * Each entry is the SUFFIX of a host symbol — the operation, with any leading prefix removed. Host
 * symbols are conventionally <Prefix><Role>; this binds on <Role> and derives the prefix. The role
 * words name operations, exactly as the entries in tools/senior-worker/crf-model-host.def do.
 *
 * `required` marks a role the worker cannot proceed without. A library missing one is reported by
 * name rather than loaded and allowed to half-work.
 * ──────────────────────────────────────────────────────────────────────────── */
typedef enum {
    ROLE_ATTACH_RECORD = 0,   /* library declares one part */
    ROLE_REMOVE_RECORD,       /* ... and withdraws it */
    ROLE_GET_RECORD,          /* library asks for a record back, by name — recursion happens here */
    ROLE_GET_COMMON,          /* the host's common-services object, incl. the assertion channel */
    ROLE_COUNT
} AbiRole;

/*
 * --list answers an unimplemented host entry with NULL, on purpose: that is what made the observed
 * ABI grow from ten symbols to seventeen (see README). --build cannot afford that policy — a
 * library that gets nothing back from its host stops building — so it answers host getters with a
 * generic stand-in instead. One flag, so the discovery command stays blind and the build command
 * does not.
 */
static int g_build_mode;

static const struct { const char *suffix; int required; } ROLE[ROLE_COUNT] = {
    [ROLE_ATTACH_RECORD] = { "AttachEleRecord", 1 },
    [ROLE_REMOVE_RECORD] = { "RemoveEleRecord", 0 },
    [ROLE_GET_RECORD]    = { "GetEleRecord",    1 },
    [ROLE_GET_COMMON]    = { "GetCommonObject", 0 },
};

#define MAX_MODULES 16
#define MAX_SYMBOLS 256
#define NAME_MAX    256

static int ends_with(const char *s, const char *suffix)
{
    size_t ls = strlen(s), lx = strlen(suffix);
    return ls >= lx && strcmp(s + (ls - lx), suffix) == 0;
}

/* Which role does this symbol name play, judged only by its tail? -1 for none. */
static int role_of_symbol(const char *name)
{
    for (int r = 0; r < ROLE_COUNT; r++)
        if (ends_with(name, ROLE[r].suffix)) return r;
    return -1;
}

/*
 * A second layer of the host ABI: entries whose name is PARAMETERISED by an interface.
 *
 *   <prefix>GetFactory_<Interface>       "give me something that makes an <Interface>"
 *   <prefix>AttachRecord_<Interface>     "here is a record implementing <Interface>"
 *
 * These matter more than their count suggests. The interface name is built at run time from a
 * class name, so it exists nowhere in the image and no static scan can find it — the only way to
 * learn which interfaces a library needs is to watch it ask. Every one it asks for and does not
 * get is a piece of host this worker has yet to supply, so the list IS the remaining work, read
 * off the library rather than guessed at.
 *
 * Returns the interface name, or NULL when the symbol is not of this shape.
 */
static const char *parameterised_interface(const char *name, char *buf, size_t n)
{
    static const char *const FORM[] = { "GetFactory_", "AttachRecord_" };

    for (size_t f = 0; f < sizeof FORM / sizeof FORM[0]; f++) {
        const char *at = strstr(name, FORM[f]);
        if (!at) continue;

        const char *iface = at + strlen(FORM[f]);
        if (!*iface) continue;

        /* A stdcall-decorated retry carries a trailing "@<bytes>" that is calling-convention
           bookkeeping, not part of the interface. Left on, the same interface is counted twice. */
        const char *cut = strrchr(iface, '@');
        size_t len = cut ? (size_t)(cut - iface) : strlen(iface);
        if (!len || len >= n) continue;

        memcpy(buf, iface, len);
        buf[len] = 0;
        return buf;
    }
    return NULL;
}

/* ────────────────────────────────────────────────────────────────────────────
 * The record store, and reading a record's identity.
 *
 * Shared by both products: the driver holds it when it intercepts the host calls itself, and the
 * shim holds it when a library binds statically and calls into a real module.
 * ──────────────────────────────────────────────────────────────────────────── */

#define MAX_RECORDS 1024

static void *g_record[MAX_RECORDS];
static long  g_records;

/* Names the library asked its host for, in order — the observation that makes this blind. */
static char  g_asked[MAX_SYMBOLS][NAME_MAX];
static int   g_asks;

/* Interfaces the library asked the host to supply — see parameterised_interface(). */
static char g_iface[MAX_SYMBOLS][NAME_MAX];
static int  g_ifaces;

static void note_interface(const char *name)
{
    if (g_ifaces >= MAX_SYMBOLS) return;
    for (int i = 0; i < g_ifaces; i++)
        if (strcmp(g_iface[i], name) == 0) return;
    snprintf(g_iface[g_ifaces++], NAME_MAX, "%s", name);
}

static void note_ask(const char *name)
{
    if (g_asks >= MAX_SYMBOLS) return;
    for (int i = 0; i < g_asks; i++)
        if (strcmp(g_asked[i], name) == 0) return;
    snprintf(g_asked[g_asks++], NAME_MAX, "%s", name);
}

/*
 * A record's concrete class, from the complete-object locator at vtable[-1].
 *
 * Pointer arithmetic against a fixed MSVC layout — nothing on the record is called. That matters:
 * vtable slot labels recovered by static analysis are indicative only, so anything that depends on
 * calling the right slot is a guess and a wrong guess faults. Identify first, call later.
 */
static const char *record_class(void *obj)
{
    if (!obj || IsBadReadPtr(obj, sizeof(void *))) return NULL;

    void **vt = *(void ***)obj;
    if (!vt || IsBadReadPtr(vt - 1, sizeof(void *))) return NULL;

    char *col = (char *)vt[-1];
    if (!col || IsBadReadPtr(col, 6 * sizeof(DWORD))) return NULL;

    /* Signature 1 is the 64-bit, image-relative layout; the locator's own RVA sits at +20, which is
       the only way back to the image base. Anything else is a build we do not run. */
    if (*(DWORD *)col != 1) return NULL;

    char *base = col - *(DWORD *)(col + 20);
    char *td   = base + *(DWORD *)(col + 12);
    if (IsBadReadPtr(td, 24)) return NULL;

    const char *decorated = td + 16;                 /* type_info::name(), still decorated */
    return IsBadStringPtrA(decorated, 512) ? NULL : decorated;
}

/*
 * Recover a readable class name from a decorated one.
 *
 *   ".?AVKSomething@@"                       -> "KSomething"
 *   ".?AV?$KOuter@VKInnerName@@@@"           -> "KInnerName"
 *
 * The second form is the one that matters in practice: a record is commonly a TEMPLATE over the
 * part's own class, so the part is the template ARGUMENT and the outer name is the same wrapper on
 * every record. Returning the wrapper would make every part read alike.
 *
 * Deliberately conservative: anything not matching either form is passed through untouched rather
 * than mangled further, because a half-undecorated name is worse to read than a decorated one.
 */
static const char *undecorate(const char *s, char *buf, size_t n)
{
    if (!s || strncmp(s, ".?AV", 4) != 0) return s;

    const char *start = s + 4;

    /* Template: skip "?$", skip the outer name to its '@', then take the first class argument. */
    if (start[0] == '?' && start[1] == '$') {
        const char *at = strchr(start + 2, '@');
        if (at && at[1] == 'V') start = at + 2;
        else return s;
    }

    const char *end = strstr(start, "@@");
    if (!end || end == start || (size_t)(end - start) >= n) return s;
    if (memchr(start, '@', (size_t)(end - start))) return s;    /* still nested — leave it alone */

    memcpy(buf, start, (size_t)(end - start));
    buf[end - start] = 0;
    return buf;
}

/* ────────────────────────────────────────────────────────────────────────────
 * STAND-INS: what a host has to BE before a library will build a part.
 *
 * Registering an inventory needs almost nothing from a host. Building a part needs three objects,
 * and a library will not get far without them:
 *
 *   - a SERVICES object, which is what a library reaches for when it wants to report its own
 *     errors. Denied one, it faults inside its own diagnostics — the failure lands nowhere near
 *     the cause.
 *   - an ELEMENT RECORD for each primitive it asks the host for. Which primitives a kit uses is
 *     not knowable ahead of time, so one is manufactured per name that arrives.
 *   - a MODEL per component, handed out by that record's factory slot.
 *
 * ONE MODEL OBJECT PER COMPONENT, not one shared by all of them. The library calls a sub-model with
 * identical arguments every time; the only thing separating one component's call from another's is
 * `this`. Share the object and every per-component question becomes unanswerable by construction.
 *
 * EVERY SLOT IS ITS OWN THUNK, so a call identifies itself by index instead of being one anonymous
 * "something was called", and a call to a slot past anything we expected lands on a thunk that
 * names itself rather than running off the end of the table.
 * ──────────────────────────────────────────────────────────────────────────── */

/* {AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE} -> the 16 bytes a COM comparison sees. */
static int guid_parse(const char *s, unsigned char out[16])
{
    unsigned v[11];
    if (!s) return 0;
    if (*s == '{') s++;
    if (sscanf(s, "%8x-%4x-%4x-%2x%2x-%2x%2x%2x%2x%2x%2x",
               &v[0], &v[1], &v[2], &v[3], &v[4],
               &v[5], &v[6], &v[7], &v[8], &v[9], &v[10]) != 11) return 0;

    out[0] = (unsigned char)(v[0]);        out[1] = (unsigned char)(v[0] >> 8);
    out[2] = (unsigned char)(v[0] >> 16);  out[3] = (unsigned char)(v[0] >> 24);
    out[4] = (unsigned char)(v[1]);        out[5] = (unsigned char)(v[1] >> 8);
    out[6] = (unsigned char)(v[2]);        out[7] = (unsigned char)(v[2] >> 8);
    for (int i = 0; i < 8; i++) out[8 + i] = (unsigned char)v[3 + i];
    return 1;
}

static void guid_text(const void *p, char *buf, size_t n)
{
    const unsigned char *g = (const unsigned char *)p;
    if (!p || IsBadReadPtr(p, 16)) { snprintf(buf, n, "?"); return; }
    snprintf(buf, n, "{%02X%02X%02X%02X-%02X%02X-%02X%02X-%02X%02X-%02X%02X%02X%02X%02X%02X}",
             g[3], g[2], g[1], g[0], g[5], g[4], g[7], g[6],
             g[8], g[9], g[10], g[11], g[12], g[13], g[14], g[15]);
}

/* IIDs the library asked any of our stand-ins for. The point of recording them: the identifier a
   library wants back from a factory is a 16-byte constant that exists nowhere a static scan can
   reach, so the only way to know it is to be asked. See --build's --iid. */
static char g_iid[MAX_SYMBOLS][40];
static int  g_iids;

static void note_iid(const void *p)
{
    char t[40];
    guid_text(p, t, sizeof t);
    if (t[0] == '?' || g_iids >= MAX_SYMBOLS) return;
    for (int i = 0; i < g_iids; i++) if (strcmp(g_iid[i], t) == 0) return;
    snprintf(g_iid[g_iids++], sizeof g_iid[0], "%s", t);
}

/* Wide enough that a call to any plausible slot is caught by a named thunk rather than read off the
   end of the table. The services object gets the wider one — a host interface is the deeper of the
   two in every library measured. */
#define SLOTS_64(X) \
    X(0)  X(1)  X(2)  X(3)  X(4)  X(5)  X(6)  X(7)  X(8)  X(9)  X(10) X(11) X(12) X(13) X(14) \
    X(15) X(16) X(17) X(18) X(19) X(20) X(21) X(22) X(23) X(24) X(25) X(26) X(27) X(28) X(29) \
    X(30) X(31) X(32) X(33) X(34) X(35) X(36) X(37) X(38) X(39) X(40) X(41) X(42) X(43) X(44) \
    X(45) X(46) X(47) X(48) X(49) X(50) X(51) X(52) X(53) X(54) X(55) X(56) X(57) X(58) X(59) \
    X(60) X(61) X(62) X(63)

#define SLOTS_128(X) SLOTS_64(X) \
    X(64) X(65) X(66) X(67) X(68) X(69) X(70) X(71) X(72) X(73) X(74) X(75) X(76) X(77) X(78) \
    X(79) X(80) X(81) X(82) X(83) X(84) X(85) X(86) X(87) X(88) X(89) X(90) X(91) X(92) X(93) \
    X(94) X(95) X(96) X(97) X(98) X(99) X(100) X(101) X(102) X(103) X(104) X(105) X(106) X(107) \
    X(108) X(109) X(110) X(111) X(112) X(113) X(114) X(115) X(116) X(117) X(118) X(119) X(120) \
    X(121) X(122) X(123) X(124) X(125) X(126) X(127)

/* ── the services object ───────────────────────────────────────────────────── */

/*
 * ANSWER QUERYINTERFACE POSITIVELY and hand the same object back. Saying "no" to the first question
 * ends the conversation there, and a stand-in that ends the conversation looks exactly like a
 * library that refused to build. COM identity rules are not what is being served here.
 */
static void *g_host_vptr;
static void *host_object(void) { return &g_host_vptr; }

static ULONG_PTR host_slot(int n, void *self, void *a, void *b, void *c)
{
    (void)c;
    if (n == 0) {                                        /* QueryInterface */
        note_iid(a);
        if (b && !IsBadWritePtr(b, sizeof(void *))) *(void **)b = self ? self : host_object();
        return 0;                                        /* S_OK */
    }
    if (n == 1) return 2;                                /* AddRef  */
    if (n == 2) return 1;                                /* Release */
    return 0;
}

#define DEF_HOST(n) static ULONG_PTR hs##n(void *s, void *a, void *b, void *c) \
                    { return host_slot(n, s, a, b, c); }
#define REF_HOST(n) (void *)hs##n,
SLOTS_128(DEF_HOST)
static void *g_host_vt[] = { SLOTS_128(REF_HOST) };

/* ── what the library asks OF a primitive, recorded as it arrives ──────────── */

/*
 * Slot calls on our records, in arrival order, filtered to the ones that carry information (0/1/2
 * are COM bookkeeping and would bury everything else).
 *
 * RAW FIRST, INTERPRETED SECOND. Which slot carries a component's wiring is a property of the ABI
 * and is not something this file should be certain about, so the calls are reported verbatim and
 * the netlist is derived from them separately. A wrong reading then shows up as a netlist that
 * disagrees with a call log sitting right next to it, instead of as a plausible netlist.
 */
#define MAX_CALLS 40000

typedef struct {
    int  prim;                  /* which primitive this call belongs to */
    int  obj;                   /* -1 = the shared record; >= 0 = that component's own model */
    int  slot;
    int  arg[3];                /* the low 32 bits of each — node indices are the expectation */
    int  argIsPtr[3];
} SlotCall;

static SlotCall g_call[MAX_CALLS];
static int      g_calls;

#define MAX_PRIMS 64

static char  g_prim_name[MAX_PRIMS][NAME_MAX];
static void *g_prim_obj[MAX_PRIMS][2];      /* [0] is the vtable; &g_prim_obj[i] IS the object */
static int   g_prim_asked[MAX_PRIMS];       /* how many times the library asked for this one */
static int   g_prims;

/* One model per component, so each is self-identifying. Which primitive each came from is kept
   alongside — without it a call on a model says "something was called" and nothing more, which is
   the whole reason for having a pool rather than one shared object. */
#define MAX_MODELS 8192
static void *g_model_pool[MAX_MODELS][2];
static int   g_model_prim[MAX_MODELS];
static int   g_models;

static int   g_terminals = 64;              /* what slot 6 answers; see --terminals */

static void *g_prim_vt[64];                 /* filled below, once the thunks exist */
static void *g_model_vt[64];

static void *prim_object(int i)
{
    return (i >= 0 && i < g_prims) ? (void *)g_prim_obj[i] : NULL;
}

static int prim_index(void *self)
{
    for (int i = 0; i < g_prims; i++)
        if (self == (void *)g_prim_obj[i]) return i;
    return -1;
}

/*
 * A record per NAME, never one shared by all of them: a record answers for its own terminal count
 * and the library wires every component of that kind against it, so one shared record can only give
 * one answer for parts that do not agree.
 */
static int synth_primitive(const char *name)
{
    for (int i = 0; i < g_prims; i++)
        if (_stricmp(g_prim_name[i], name) == 0) { g_prim_asked[i]++; return i; }

    if (g_prims >= MAX_PRIMS) return -1;

    int i = g_prims++;
    snprintf(g_prim_name[i], NAME_MAX, "%s", name);
    g_prim_obj[i][0] = g_prim_vt;
    g_prim_obj[i][1] = NULL;
    g_prim_asked[i]  = 1;
    fprintf(stderr, "[netlist-worker] supplying a primitive record for '%s'\n", name);
    return i;
}

static void *synth_model(int prim)
{
    if (g_models >= MAX_MODELS) return g_model_pool[MAX_MODELS - 1];   /* exhausted: share */
    g_model_pool[g_models][0] = g_model_vt;
    g_model_pool[g_models][1] = g_model_vt;
    g_model_prim[g_models]    = prim;
    return g_model_pool[g_models++];
}

static int model_index(void *self)
{
    for (int i = 0; i < g_models; i++)
        if (self == (void *)g_model_pool[i]) return i;
    return -1;
}

static long g_calls_dropped;

/*
 * An argument that is a POINTER is recorded only as a truncated address, which says nothing. That
 * is fine while the interesting arguments are small integers — and it stops being fine the moment a
 * component has more terminals than fit in a register, because then its wiring HAS to arrive by
 * reference and the log shows an address where the answer is.
 *
 * So --dump-args reads a little of what each pointer argument points at, as integers and as text.
 * Nothing is interpreted here; the bytes are reported and the reading happens outside.
 *
 * Bounded deliberately: the first few hundred calls carry the structure, and a part with 1,700
 * components would otherwise turn a diagnostic into megabytes. The cut-off is reported.
 */
#define PREVIEW_INTS 8
#define MAX_PREVIEWS 512

typedef struct {
    int         call;               /* which entry in g_call */
    int         arg;                /* 0..2 */
    const char *owner;              /* self | library | other — read this FIRST */
    int         ints[PREVIEW_INTS];
    int         readable;           /* how many of them read */
    char        text[64];           /* when it reads as a string instead */
} ArgPreview;

static ArgPreview g_preview[MAX_PREVIEWS];
static int        g_previews;
static long       g_previews_dropped;
static int        g_dump_args;

/* Defined with the record-request machinery further down; the same defensive read serves both. */
static int read_as_text(const void *p, char *out, size_t n);

/*
 * ── ATTRIBUTE EVERY POINTER BEFORE READING ANYTHING INTO IT ──────────────────
 *
 * On x64 the first four arguments are registers, so a callee ALWAYS "receives" four whether or not
 * four were passed. A slot that takes one argument still hands us three, and the extras are
 * whatever those registers happened to hold. They look exactly like pointers.
 *
 * This is not hypothetical. Reading them as arguments produced a confident, wrong conclusion within
 * minutes of the preview being added: an N-port's wiring call appeared to carry two pointers where a
 * two-terminal device carried two integers, which reads as "the big component gets its node list by
 * reference". The pointers were leftovers — one of them pointed at THIS FILE'S OWN string literals,
 * and their values changed from run to run.
 *
 * So every pointer is attributed to the image it falls in. `self` means it came from us and is
 * therefore noise by construction: our own addresses cannot be something the library passed.
 */
static ULONG_PTR g_self_base, g_self_size;      /* this executable */
static ULONG_PTR g_lib_base,  g_lib_size;       /* the model library under test */

static void image_range(HMODULE m, ULONG_PTR *base, ULONG_PTR *size)
{
    *base = *size = 0;
    if (!m) return;
    IMAGE_DOS_HEADER *dos = (IMAGE_DOS_HEADER *)m;
    if (IsBadReadPtr(dos, sizeof *dos) || dos->e_magic != IMAGE_DOS_SIGNATURE) return;
    IMAGE_NT_HEADERS64 *nt = (IMAGE_NT_HEADERS64 *)((char *)m + dos->e_lfanew);
    if (IsBadReadPtr(nt, sizeof *nt)) return;
    *base = (ULONG_PTR)m;
    *size = nt->OptionalHeader.SizeOfImage;
}

static const char *ptr_owner(const void *p)
{
    ULONG_PTR v = (ULONG_PTR)p;
    if (!v) return "null";
    if (g_self_base && v >= g_self_base && v < g_self_base + g_self_size) return "self";
    if (g_lib_base  && v >= g_lib_base  && v < g_lib_base  + g_lib_size)  return "library";
    return "other";
}

static void note_preview(int call, int arg, void *p)
{
    if (!g_dump_args || !p) return;
    if (g_previews >= MAX_PREVIEWS) { g_previews_dropped++; return; }

    ArgPreview *v = &g_preview[g_previews];
    memset(v, 0, sizeof *v);
    v->call  = call;
    v->arg   = arg;
    v->owner = ptr_owner(p);

    /* Ours by construction cannot be something the library passed. Recorded, not read: seeing the
       leftover is what makes it recognisable as one. */
    if (strcmp(v->owner, "self") == 0) { g_previews++; return; }

    /* Text first: a name read as a row of integers is unrecognisable, and a name is the other
       thing a pointer argument commonly is. */
    if (read_as_text(p, v->text, sizeof v->text)) { g_previews++; return; }

    for (int i = 0; i < PREVIEW_INTS; i++) {
        const int *q = (const int *)p + i;
        if (IsBadReadPtr(q, sizeof(int))) break;
        v->ints[i] = *q;
        v->readable = i + 1;
    }
    if (v->readable) g_previews++;
}

static void note_call_on(int prim, int obj, int slot, void *a, void *b, void *c)
{
    if (g_calls >= MAX_CALLS) { g_calls_dropped++; return; }
    int idx = g_calls;
    SlotCall *k = &g_call[g_calls++];
    void *v[3] = { a, b, c };
    k->prim = prim;
    k->obj  = obj;
    k->slot = slot;
    for (int i = 0; i < 3; i++) {
        k->arg[i]      = (int)(ULONG_PTR)v[i];
        k->argIsPtr[i] = v[i] && !IsBadReadPtr(v[i], 1);
        if (k->argIsPtr[i]) note_preview(idx, i, v[i]);
    }
}

/* Calls on the shared record. */
static void note_call(int prim, int slot, void *a, void *b, void *c)
{
    note_call_on(prim, -1, slot, a, b, c);
}

/*
 * The primitive's record.
 *
 *   slot 0/1/2   COM.
 *   slot 3       the factory. A non-NULL first argument is an aggregation request and must be
 *                refused — a library that gets a model back from THAT has been told something
 *                untrue about who owns it. The identifier it asks with is recorded, because it is
 *                the one thing --build needs and cannot read out of the image.
 *   slot 6       the terminal count, written through the first out-pointer.
 *   everything else is logged and returns 0.
 */
static ULONG_PTR prim_slot(int n, void *self, void *a, void *b, void *c)
{
    int me = prim_index(self);

    if (n == 0) {
        note_iid(a);
        if (b && !IsBadWritePtr(b, sizeof(void *))) *(void **)b = self;
        return 0;
    }
    if (n == 1) return 2;
    if (n == 2) return 1;

    if (n == 3) {
        note_iid(b);
        if (a) return 0x80040110UL;                      /* CLASS_E_NOAGGREGATION */
        void *inst = synth_model(me);
        if (c && !IsBadWritePtr(c, sizeof(void *))) *(void **)c = inst;
        note_call(me, n, a, b, c);
        return 0;
    }

    if (n == 6) {
        /* Not validated at construction, so a wrong answer surfaces somewhere else entirely. A
           generous count is the safe direction: too FEW terminals is what trips a library's own
           range check on the node it is about to wire. */
        if (a && !IsBadWritePtr(a, sizeof(int))) *(int *)a = g_terminals;
        note_call(me, n, a, b, c);
        return 0;
    }

    note_call(me, n, a, b, c);
    return 0;
}

/*
 * The per-component model.
 *
 * ITS CALLS ARE LOGGED, and that is the point of there being one object per component rather than
 * one shared by all of them: the library calls a sub-model with identical arguments every time, so
 * `this` is the only thing separating one component's call from another's. A shared object makes
 * every per-component question unanswerable, and an unlogged pool makes having the pool pointless —
 * which it was, until a device's second terminal turned out to be missing and this was the one
 * channel nothing was watching.
 */
static ULONG_PTR model_slot(int n, void *self, void *a, void *b, void *c)
{
    int me = model_index(self);

    if (n == 0) {
        note_iid(a);
        if (b && !IsBadWritePtr(b, sizeof(void *))) *(void **)b = self;
        return 0;
    }
    if (n == 1) return 2;
    if (n == 2) return 1;

    note_call_on(me >= 0 ? g_model_prim[me] : -1, me, n, a, b, c);
    return 0;
}

#define DEF_PRIM(n)  static ULONG_PTR pr##n(void *s, void *a, void *b, void *c) \
                     { return prim_slot(n, s, a, b, c); }
#define REF_PRIM(n)  (void *)pr##n,
#define DEF_MODEL(n) static ULONG_PTR md##n(void *s, void *a, void *b, void *c) \
                     { return model_slot(n, s, a, b, c); }
#define REF_MODEL(n) (void *)md##n,
SLOTS_64(DEF_PRIM)
SLOTS_64(DEF_MODEL)

static void stand_ins_init(void)
{
    static void *prim_vt[]  = { SLOTS_64(REF_PRIM) };
    static void *model_vt[] = { SLOTS_64(REF_MODEL) };

    memcpy(g_prim_vt,  prim_vt,  sizeof g_prim_vt);
    memcpy(g_model_vt, model_vt, sizeof g_model_vt);
    g_host_vptr = g_host_vt;
}

/* ── the host behaviour itself ─────────────────────────────────────────────── */

static int host_attach_record(void *rec)
{
    if (g_records < MAX_RECORDS) g_record[g_records++] = rec;
    return 1;
}

static int host_remove_record(void *rec)
{
    for (long i = 0; i < g_records; i++)
        if (g_record[i] == rec) { g_record[i] = NULL; break; }
    return 1;
}

/*
 * The library asks for a record back.
 *
 * PRECEDENCE IS LOAD-BEARING: the kept records FIRST, primitives only as a fallback. Reversed, a
 * library asking for one of its own sub-parts is served a primitive-shaped record, and the part
 * still builds and reports success — a wrong circuit, reported as correct. Composite parts are
 * assembled by recursion through this one callback, so the precedence decides whether a two-level
 * part is built or quietly flattened into nonsense.
 *
 * WHAT IT ASKS WITH IS OBSERVED, NOT ASSUMED. The argument convention is not documented and not
 * guessable, and a wrong guess here is the silent kind: a library served the wrong record still
 * builds and still reports success. So the request is read defensively, logged verbatim, and
 * reported — the evidence comes first, and a matching rule is only worth writing once the requests
 * can be seen.
 *
 * The match itself is likewise reported rather than assumed. A record's identity is known here only
 * as its RTTI class, and whether that class relates to the name a library asks by is exactly the
 * sort of thing that has to be checked rather than derived. Each rule tried is named in the output
 * so a wrong one is visible instead of merely producing a part.
 */

typedef struct {
    char asked[NAME_MAX];       /* what the library asked for, if it could be read at all */
    char matchedBy[32];         /* which rule answered it — "" when nothing did */
    long recordIndex;           /* -1 when unanswered; -2 for a primitive synthesised on demand */
    int  prim;                  /* index into the synthesised primitives, or -1 */
} RecordRequest;

/* Large enough for the biggest part measured anywhere (~1,700 components). Past it the requests
   stop being RECORDED but must keep being ANSWERED — a lookup that returns NULL because a log is
   full fails the build for a reason that has nothing to do with the library. The overflow is
   counted and reported; a cap that is silent reads as "that was all of them". */
#define MAX_REQUESTS 8192
static RecordRequest g_request[MAX_REQUESTS];
static int           g_requests;
static long          g_requests_dropped;

/*
 * Read a pointer as text without trusting it. The argument may be narrow, wide, or not a string at
 * all; every branch is guarded because a fault here would end the run and lose everything already
 * observed.
 */
static int read_as_text(const void *p, char *out, size_t n)
{
    out[0] = 0;
    if (!p || IsBadReadPtr(p, 2)) return 0;

    /* Wide first: a UTF-16 name has a zero in its second byte, which narrow text never does. */
    const wchar_t *w = (const wchar_t *)p;
    if (!IsBadStringPtrW(w, 256) && w[0] && !((const unsigned char *)p)[1]) {
        int got = WideCharToMultiByte(CP_UTF8, 0, w, -1, out, (int)n, NULL, NULL);
        if (got > 1) return 1;
    }

    /*
     * Narrow: require the WHOLE run to be printable ASCII, not just the first byte. A looser test
     * calls arbitrary bytes "a name", and the bytes that matter most here — a node list — start
     * with small integers that pass a first-byte check and then read as garbage. Falling through to
     * the integer preview is the more useful answer for anything that is not really text.
     */
    const char *s = (const char *)p;
    if (IsBadStringPtrA(s, 256)) return 0;
    for (size_t i = 0; i < n - 1; i++) {
        if (IsBadReadPtr(s + i, 1)) return 0;
        if (s[i] == 0) { out[i] = 0; return i > 0; }
        if (s[i] < 0x20 || (unsigned char)s[i] > 0x7E) return 0;
        out[i] = s[i];
    }
    out[n - 1] = 0;
    return 1;
}

/*
 * Does this record answer to that name? Every rule tried is named, so the output shows WHICH
 * relationship held rather than only that something matched.
 */
static long match_record(const char *wanted, const char **rule_out)
{
    char plain[NAME_MAX];

    for (long i = 0; i < g_records; i++) {
        const char *cls = record_class(g_record[i]);
        if (!cls) continue;
        const char *name = undecorate(cls, plain, sizeof plain);

        if (_stricmp(name, wanted) == 0)                    { *rule_out = "exact";        return i; }
        if (name[0] == 'K' && _stricmp(name + 1, wanted) == 0) { *rule_out = "class-minus-K"; return i; }
    }
    *rule_out = NULL;
    return -1;
}

/*
 * ── THE ANSWER GOES IN AN OUT-PARAMETER, NOT THE RETURN VALUE ────────────────
 *
 * This was written the other way round first, and both sides of the test agreed with each other
 * while being wrong: the double checked the return value because the worker returned it there. The
 * ABI's own shape is
 *
 *     void GetEleRecord(const wchar_t *name, Record **out);   // *out is the answer
 *
 * and a library given the record only as a return value reports "unable to locate the component"
 * while a non-NULL return makes the call look like it succeeded. That is the silent failure this
 * whole file is arranged to avoid, so BOTH are written now: `*out` because that is the contract,
 * and the return value because it costs nothing and a library reading it either way is served.
 */
static void *host_get_record(void *a, void *b, void *c, void *d)
{
    (void)c; (void)d;

    /* When the log is full the request is still SERVED — only unrecorded. */
    static RecordRequest overflow;
    RecordRequest *req;
    if (g_requests < MAX_REQUESTS) {
        req = &g_request[g_requests++];
    } else {
        req = &overflow;
        g_requests_dropped++;
    }
    req->recordIndex = -1;
    req->matchedBy[0] = 0;
    req->prim = -1;

    void *answer = NULL;

    if (read_as_text(a, req->asked, sizeof req->asked)) {
        /* PRECEDENCE IS LOAD-BEARING: the kept records FIRST, a synthesised primitive only as a
           fallback. Reversed, a library asking for one of its own sub-parts is served a
           primitive-shaped record and the part still builds and reports success. */
        const char *rule = NULL;
        long idx = match_record(req->asked, &rule);
        if (idx >= 0) {
            snprintf(req->matchedBy, sizeof req->matchedBy, "%s", rule);
            req->recordIndex = idx;
            answer = g_record[idx];
        } else if (g_build_mode) {
            /* Not one of the library's own — so it is something the SIMULATOR is expected to
               supply. Which primitives a kit uses is not knowable ahead of time and is not
               tabulated here: one is manufactured for whatever name arrives, and the name is
               reported. */
            req->prim = synth_primitive(req->asked);
            if (req->prim >= 0) {
                snprintf(req->matchedBy, sizeof req->matchedBy, "primitive");
                req->recordIndex = -2;
                answer = prim_object(req->prim);
            }
        }
    }

    if (b && !IsBadWritePtr(b, sizeof(void *))) *(void **)b = answer;
    return answer;
}

static void *host_stub_ptr(void *a, void *b, void *c, void *d)
{
    (void)a; (void)b; (void)c; (void)d;
    return NULL;
}

/* A host getter that hands out the generic services object — but only while building. During
   --list every unrecognised entry still answers NULL, which is what makes the ABI observable. */
static void *host_service(void *a, void *b, void *c, void *d)
{
    (void)a; (void)b; (void)c; (void)d;
    return g_build_mode ? host_object() : NULL;
}

/* Entries that acknowledge rather than return anything. A library checks these. */
static ULONG_PTR host_ack(void *a, void *b, void *c, void *d)
{
    (void)a; (void)b; (void)c; (void)d;
    return 1;
}

/* Pick the implementation for a symbol from its role, with a do-nothing getter as the default.
   Almost every host entry can be inert while a library is only REGISTERING; building is what needs
   the services object, and ROLE_GET_COMMON is where a library's assertion channel comes from. */
static void *impl_for_role(int role)
{
    switch (role) {
        case ROLE_ATTACH_RECORD: return (void *)host_attach_record;
        case ROLE_REMOVE_RECORD: return (void *)host_remove_record;
        case ROLE_GET_RECORD:    return (void *)host_get_record;
        case ROLE_GET_COMMON:    return (void *)host_service;
        default:                 return (void *)host_stub_ptr;
    }
}

/*
 * A symbol with no role still has to be answered while building, and the only thing available to
 * answer it FROM is the verb its name starts with. That is the same kind of knowledge as the role
 * suffixes — it names an operation, not a kit — and it is why nothing here has to know what any
 * particular library calls its host.
 *
 * A library that gets NULL retries under the stdcall-decorated spelling, so the decoration comes off
 * before the verb is read; otherwise the retry classifies differently from the first attempt.
 */
static const char *strip_decoration(const char *name, char *buf, size_t n)
{
    if (name[0] != '_') return name;

    const char *at = strrchr(name, '@');
    if (!at || at == name + 1) return name;

    size_t len = (size_t)(at - name) - 1;
    if (!len || len >= n) return name;
    memcpy(buf, name + 1, len);
    buf[len] = 0;
    return buf;
}

static int starts_with(const char *s, const char *p)
{
    return strncmp(s, p, strlen(p)) == 0;
}

static void *impl_for_symbol(const char *name)
{
    int role = role_of_symbol(name);
    if (role >= 0) return impl_for_role(role);
    if (!g_build_mode) return NULL;          /* --list stays blind deliberately */

    char undec[NAME_MAX], ifbuf[NAME_MAX];
    const char *s = strip_decoration(name, undec, sizeof undec);

    /* A factory parameterised by an interface we do not implement. NULL is the honest answer and
       is what the observation in --list is FOR: each one asked for and not supplied is a piece of
       host still missing, and handing back a callable would hide that. */
    if (parameterised_interface(s, ifbuf, sizeof ifbuf)) return NULL;

    /* Some other flavour of record getter. There is nothing to give it, and a services object is
       not a record — answering with one would be served straight into a vtable call. */
    if (ends_with(s, "Record")) return NULL;

    if (starts_with(s, "Attach") || starts_with(s, "Remove") ||
        strstr(s, "Register"))
        return (void *)host_ack;

    if (strstr(s, "Get")) return (void *)host_service;

    return NULL;
}

/* ────────────────────────────────────────────────────────────────────────────
 * CRF_SHIM — a host module, for a library that binds its host statically.
 * The .def generated by --gen-shims maps that library's own symbol names onto these.
 * ──────────────────────────────────────────────────────────────────────────── */
#ifdef CRF_SHIM

__declspec(dllexport) int   crf_attach_record(void *rec) { return host_attach_record(rec); }
__declspec(dllexport) int   crf_remove_record(void *rec) { return host_remove_record(rec); }

__declspec(dllexport) void *crf_get_record(void *a, void *b, void *c, void *d)
{ return host_get_record(a, b, c, d); }

__declspec(dllexport) void *crf_stub_ptr(void *a, void *b, void *c, void *d)
{ return host_stub_ptr(a, b, c, d); }

__declspec(dllexport) long  crf_record_count(void) { return g_records; }

__declspec(dllexport) const char *crf_record_class(long i)
{ return (i >= 0 && i < g_records) ? record_class(g_record[i]) : NULL; }

/* Exported, and that is what keeps it from being "defined but not used": these are all reachable
   only from the driver, and one source compiled two ways has to be warning-free both times. */
__declspec(dllexport) void crf_keep_referenced(void)
{
    (void)role_of_symbol; (void)impl_for_role; (void)undecorate;
    (void)impl_for_symbol; (void)stand_ins_init; (void)guid_parse; (void)host_ack;
    (void)note_ask; (void)note_interface; (void)image_range;
}

#endif /* CRF_SHIM */

/* ────────────────────────────────────────────────────────────────────────────
 * CRF_DRIVER
 * ──────────────────────────────────────────────────────────────────────────── */
#ifdef CRF_DRIVER

/* ── reading the image as a FILE, to learn how it reaches its host ────────────
 *
 * Mapped as data, never loaded: a scan must not run a line of the library's code. LoadLibrary would
 * run DLL_PROCESS_ATTACH, which is exactly what --list does deliberately and --scan must not do by
 * accident.
 * ──────────────────────────────────────────────────────────────────────────── */

typedef struct {
    char  *base;
    size_t size;
    IMAGE_NT_HEADERS64 *nt;
} PeImage;

static void pe_close(PeImage *p)
{
    if (p->base) UnmapViewOfFile(p->base);
    memset(p, 0, sizeof *p);
}

static int pe_open(const char *path, PeImage *out)
{
    memset(out, 0, sizeof *out);

    HANDLE f = CreateFileA(path, GENERIC_READ, FILE_SHARE_READ, NULL,
                           OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (f == INVALID_HANDLE_VALUE) return 0;

    LARGE_INTEGER sz;
    if (!GetFileSizeEx(f, &sz) || sz.QuadPart < (LONGLONG)sizeof(IMAGE_DOS_HEADER)) {
        CloseHandle(f); return 0;
    }

    HANDLE m = CreateFileMappingA(f, NULL, PAGE_READONLY, 0, 0, NULL);
    CloseHandle(f);
    if (!m) return 0;

    char *base = (char *)MapViewOfFile(m, FILE_MAP_READ, 0, 0, 0);
    CloseHandle(m);
    if (!base) return 0;

    IMAGE_DOS_HEADER *dos = (IMAGE_DOS_HEADER *)base;
    if (dos->e_magic != IMAGE_DOS_SIGNATURE) { UnmapViewOfFile(base); return 0; }

    IMAGE_NT_HEADERS64 *nt = (IMAGE_NT_HEADERS64 *)(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE) { UnmapViewOfFile(base); return 0; }

    out->base = base; out->size = (size_t)sz.QuadPart; out->nt = nt;
    return 1;
}

/*
 * RVA -> file offset. A mapped-as-data image keeps the on-disk section layout, so every RVA has to
 * be walked back through the section table. Reading it as though it were loaded silently reads the
 * wrong bytes for any section whose file and virtual addresses differ, which is most of them.
 */
static const char *pe_at(const PeImage *p, DWORD rva)
{
    IMAGE_SECTION_HEADER *s = IMAGE_FIRST_SECTION(p->nt);
    for (int i = 0; i < p->nt->FileHeader.NumberOfSections; i++, s++) {
        DWORD va = s->VirtualAddress;
        DWORD vs = s->Misc.VirtualSize ? s->Misc.VirtualSize : s->SizeOfRawData;
        if (rva >= va && rva < va + vs) {
            size_t off = s->PointerToRawData + (rva - va);
            return off < p->size ? p->base + off : NULL;
        }
    }
    return NULL;
}

typedef struct {
    char module[NAME_MAX];
    char symbol[MAX_SYMBOLS][NAME_MAX];
    int  symbols;
} HostModule;

typedef struct {
    HostModule mod[MAX_MODULES];
    int        modules;
    char       prefix[NAME_MAX];      /* whatever precedes the role words — DERIVED, not stored */
    int        prefix_known;
    char       role_symbol[ROLE_COUNT][NAME_MAX];
    int        role_module[ROLE_COUNT];
} HostAbi;

/*
 * Bind each role to an imported symbol by suffix, taking whatever precedes the FIRST bound suffix
 * as the prefix. A later role whose prefix disagrees is reported rather than accepted: two prefixes
 * in one import table means the suffix match found something that is not the host ABI, and
 * continuing would wire a stub to a function that does something else.
 */
static void abi_bind_roles(HostAbi *abi)
{
    for (int r = 0; r < ROLE_COUNT; r++) abi->role_module[r] = -1;

    for (int r = 0; r < ROLE_COUNT; r++)
        for (int m = 0; m < abi->modules && abi->role_module[r] < 0; m++)
            for (int s = 0; s < abi->mod[m].symbols; s++) {
                const char *name = abi->mod[m].symbol[s];
                if (!ends_with(name, ROLE[r].suffix)) continue;

                size_t plen = strlen(name) - strlen(ROLE[r].suffix);
                if (plen >= NAME_MAX) continue;

                char pfx[NAME_MAX];
                memcpy(pfx, name, plen);
                pfx[plen] = 0;

                if (!abi->prefix_known) { strcpy(abi->prefix, pfx); abi->prefix_known = 1; }
                else if (strcmp(abi->prefix, pfx) != 0) {
                    fprintf(stderr, "[netlist-worker] role '%s' matched a symbol whose prefix "
                                    "disagrees with the one already derived; ignoring it.\n",
                            ROLE[r].suffix);
                    continue;
                }

                strcpy(abi->role_symbol[r], name);
                abi->role_module[r] = m;
                break;
            }
}

static int abi_scan(const char *library, HostAbi *abi)
{
    memset(abi, 0, sizeof *abi);

    PeImage pe;
    if (!pe_open(library, &pe)) {
        fprintf(stderr, "[netlist-worker] cannot open '%s' as a PE image\n", library);
        return 0;
    }

    IMAGE_DATA_DIRECTORY dd = pe.nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    if (!dd.VirtualAddress) {
        fprintf(stderr, "[netlist-worker] '%s' imports nothing\n", library);
        pe_close(&pe);
        return 0;
    }

    const IMAGE_IMPORT_DESCRIPTOR *imp =
        (const IMAGE_IMPORT_DESCRIPTOR *)pe_at(&pe, dd.VirtualAddress);

    for (; imp && imp->Name && abi->modules < MAX_MODULES; imp++) {
        const char *modname = pe_at(&pe, imp->Name);
        if (!modname) continue;

        /* The import NAME table keeps the names even after binding; the address table may not. */
        DWORD trva = imp->OriginalFirstThunk ? imp->OriginalFirstThunk : imp->FirstThunk;
        const ULONGLONG *thunk = (const ULONGLONG *)pe_at(&pe, trva);
        if (!thunk) continue;

        HostModule *hm = &abi->mod[abi->modules];
        snprintf(hm->module, NAME_MAX, "%s", modname);
        hm->symbols = 0;

        for (int i = 0; thunk[i] && hm->symbols < MAX_SYMBOLS; i++) {
            if (thunk[i] & IMAGE_ORDINAL_FLAG64) continue;      /* by ordinal — no name to read */
            const IMAGE_IMPORT_BY_NAME *ibn =
                (const IMAGE_IMPORT_BY_NAME *)pe_at(&pe, (DWORD)thunk[i]);
            if (!ibn) continue;
            snprintf(hm->symbol[hm->symbols++], NAME_MAX, "%s", (const char *)ibn->Name);
        }
        abi->modules++;
    }

    pe_close(&pe);
    abi_bind_roles(abi);
    return 1;
}

/*
 * How does this library reach its host?
 *
 *   STATIC   the host symbols are in the import table, bound to a named module at load time.
 *            Answer it by supplying a module of that name exporting those symbols.
 *   DYNAMIC  resolved at run time through LoadLibrary + GetProcAddress. The names are NOT in the
 *            import table and cannot be read ahead of time. Answer it by intercepting those two
 *            calls and replying to whatever is asked for.
 *
 * A library that resolves dynamically still has to import the resolver, so LoadLibrary +
 * GetProcAddress present with no bound role is that case, not a broken scan.
 *
 * This has to be MEASURED, never assumed: the two need different designs, and reading the import
 * table of a dynamic library returns silence rather than an error.
 */
typedef enum { RESOLVE_UNKNOWN = 0, RESOLVE_STATIC, RESOLVE_DYNAMIC } ResolveKind;

static ResolveKind abi_resolve_kind(const HostAbi *abi)
{
    for (int r = 0; r < ROLE_COUNT; r++)
        if (abi->role_module[r] >= 0) return RESOLVE_STATIC;

    int loader = 0, getproc = 0;
    for (int m = 0; m < abi->modules; m++)
        for (int s = 0; s < abi->mod[m].symbols; s++) {
            const char *n = abi->mod[m].symbol[s];
            if (strncmp(n, "LoadLibrary", 11) == 0)    loader  = 1;
            else if (strcmp(n, "GetProcAddress") == 0) getproc = 1;
        }
    return (loader && getproc) ? RESOLVE_DYNAMIC : RESOLVE_UNKNOWN;
}

static const char *resolve_name(ResolveKind k)
{
    return k == RESOLVE_STATIC ? "static" : k == RESOLVE_DYNAMIC ? "dynamic" : "unknown";
}

static int module_is_ours(const HostAbi *abi, int m)
{
    for (int r = 0; r < ROLE_COUNT; r++)
        if (abi->role_module[r] == m) return 1;
    return 0;
}

/* ────────────────────────────────────────────────────────────────────────────
 * Interception, for a library that resolves its host at run time.
 *
 * THE ORDERING PROBLEM, AND WHY THE HOOK GOES IN KERNEL32 RATHER THAN IN THE LIBRARY.
 * Registration happens inside DLL_PROCESS_ATTACH, so the hooks must already be live when the
 * library is loaded — but its import thunks do not exist to patch until it is loaded. Patching the
 * library's own IAT therefore cannot work: by the time there is something to patch, the calls that
 * mattered have been made. Patching the loader's entry points first, then loading the library
 * normally, has no such ordering — every call it makes, including from its static initialisers,
 * arrives here.
 *
 * NO TRAMPOLINE IS NEEDED, which is what keeps this small. A detour normally has to relocate the
 * bytes it overwrites so the original can still be called, and that needs an instruction-length
 * decoder. Nothing here calls the originals: LoadLibraryA/W is re-implemented over
 * LoadLibraryExA/W (which is what they call internally, and which is not hooked), and
 * GetProcAddress is re-implemented by walking the export directory. The overwritten bytes are
 * never needed again.
 * ──────────────────────────────────────────────────────────────────────────── */

static HMODULE g_host_handle;    /* the handle handed out for a module we answer for */

/* Resolve an export from a LOADED image. Used in place of GetProcAddress once that is hooked —
   calling the hooked one from inside the hook would recurse. */
static void *export_lookup(HMODULE mod, const char *want)
{
    if (!mod || !want) return NULL;

    char *base = (char *)mod;
    IMAGE_DOS_HEADER *dos = (IMAGE_DOS_HEADER *)base;
    if (dos->e_magic != IMAGE_DOS_SIGNATURE) return NULL;

    IMAGE_NT_HEADERS64 *nt = (IMAGE_NT_HEADERS64 *)(base + dos->e_lfanew);
    IMAGE_DATA_DIRECTORY dd = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_EXPORT];
    if (!dd.VirtualAddress) return NULL;

    IMAGE_EXPORT_DIRECTORY *ex = (IMAGE_EXPORT_DIRECTORY *)(base + dd.VirtualAddress);
    DWORD *names = (DWORD *)(base + ex->AddressOfNames);
    WORD  *ords  = (WORD  *)(base + ex->AddressOfNameOrdinals);
    DWORD *funcs = (DWORD *)(base + ex->AddressOfFunctions);

    for (DWORD i = 0; i < ex->NumberOfNames; i++)
        if (strcmp(base + names[i], want) == 0)
            return base + funcs[ords[i]];
    return NULL;
}

/* Overwrite an entry point with an absolute jump: jmp qword ptr [rip+0]; <8-byte target>. */
static int patch_jump(void *at, void *to)
{
    unsigned char code[14] = { 0xFF, 0x25, 0, 0, 0, 0 };
    memcpy(code + 6, &to, sizeof to);

    DWORD old;
    if (!VirtualProtect(at, sizeof code, PAGE_EXECUTE_READWRITE, &old)) return 0;
    memcpy(at, code, sizeof code);
    VirtualProtect(at, sizeof code, old, &old);
    FlushInstructionCache(GetCurrentProcess(), at, sizeof code);
    return 1;
}

/*
 * A module the loader cannot find is one we answer for. The handle is only ever a token: every
 * symbol lookup against it comes back through the GetProcAddress hook, so it need not export
 * anything. Our own image is used because it IS a valid loaded module, so anything else the
 * library does with the handle keeps working.
 */
static HMODULE answer_for_module(void)
{
    if (!g_host_handle) g_host_handle = GetModuleHandleA(NULL);
    return g_host_handle;
}

static HMODULE WINAPI my_LoadLibraryA(LPCSTR name)
{
    HMODULE real = LoadLibraryExA(name, NULL, 0);
    if (real) return real;

    fprintf(stderr, "[netlist-worker] answering for host module '%s'\n", name ? name : "(null)");
    return answer_for_module();
}

static HMODULE WINAPI my_LoadLibraryW(LPCWSTR name)
{
    HMODULE real = LoadLibraryExW(name, NULL, 0);
    if (real) return real;

    fprintf(stderr, "[netlist-worker] answering for a host module requested as wide text\n");
    return answer_for_module();
}

static FARPROC WINAPI my_GetProcAddress(HMODULE mod, LPCSTR name)
{
    /* An import by ordinal carries no name to match a role against, and there is nothing useful to
       answer with; let it fail rather than hand back something arbitrary. */
    if ((ULONG_PTR)name <= 0xFFFF) return NULL;

    if (g_host_handle && mod == g_host_handle) {
        note_ask(name);

        char ifbuf[NAME_MAX];
        const char *iface = parameterised_interface(name, ifbuf, sizeof ifbuf);
        if (iface) note_interface(iface);

        /* An unimplemented entry answers NULL rather than a do-nothing function: a library handed
           a callable believes the host supplies that interface and finds out otherwise much later,
           somewhere unrelated. NULL is what a host without it returns, and libraries handle it.
           impl_for_symbol keeps that policy for --list and relaxes it only for --build, which
           cannot get a part built out of a host that answers nothing. */
        return (FARPROC)impl_for_symbol(name);
    }
    return (FARPROC)export_lookup(mod, name);
}

/* Hooks go in before the library is loaded, and are never removed: the process exists to run one
   library and then exit. */
static int install_hooks(void)
{
    HMODULE k32 = GetModuleHandleA("kernel32.dll");
    if (!k32) { fprintf(stderr, "[netlist-worker] no kernel32\n"); return 0; }

    void *la = export_lookup(k32, "LoadLibraryA");
    void *lw = export_lookup(k32, "LoadLibraryW");
    void *gp = export_lookup(k32, "GetProcAddress");
    if (!la || !lw || !gp) {
        fprintf(stderr, "[netlist-worker] could not locate the loader entry points to intercept\n");
        return 0;
    }

    if (!patch_jump(la, (void *)my_LoadLibraryA) ||
        !patch_jump(lw, (void *)my_LoadLibraryW) ||
        !patch_jump(gp, (void *)my_GetProcAddress)) {
        fprintf(stderr, "[netlist-worker] could not write the interception jumps\n");
        return 0;
    }
    return 1;
}

/* ── output ────────────────────────────────────────────────────────────────── */

static void json_escape(const char *s, char *out, size_t n)
{
    /* Anything outside printable ASCII is escaped, not copied. These strings come out of another
       process's memory, so a byte that is not valid UTF-8 is entirely possible — and one of them
       makes the whole document unparseable, losing every finding in it. */
    size_t o = 0;
    for (; s && *s && o + 8 < n; s++) {
        unsigned char ch = (unsigned char)*s;
        if (ch == '"' || ch == '\\') { out[o++] = '\\'; out[o++] = ch; }
        else if (ch < 0x20 || ch > 0x7E) o += (size_t)snprintf(out + o, n - o, "\\u%04x", ch);
        else out[o++] = (char)ch;
    }
    out[o] = 0;
}

static void cmd_scan(const HostAbi *abi)
{
    char esc[NAME_MAX * 2];

    printf("{\n  \"hostModules\": [\n");
    for (int m = 0; m < abi->modules; m++) {
        json_escape(abi->mod[m].module, esc, sizeof esc);
        printf("    { \"name\": \"%s\", \"supplyThis\": %s, \"symbols\": [",
               esc, module_is_ours(abi, m) ? "true" : "false");
        for (int s = 0; s < abi->mod[m].symbols; s++) {
            json_escape(abi->mod[m].symbol[s], esc, sizeof esc);
            printf("%s\"%s\"", s ? ", " : "", esc);
        }
        printf("] }%s\n", m + 1 < abi->modules ? "," : "");
    }
    printf("  ],\n");

    /* Reported so a user can see what was derived. It is not stored anywhere. */
    json_escape(abi->prefix_known ? abi->prefix : "", esc, sizeof esc);
    printf("  \"derivedPrefix\": \"%s\",\n  \"roles\": {\n", esc);

    int missing = 0;
    for (int r = 0; r < ROLE_COUNT; r++) {
        if (abi->role_module[r] < 0) {
            printf("    \"%s\": null%s\n", ROLE[r].suffix, r + 1 < ROLE_COUNT ? "," : "");
            if (ROLE[r].required) missing++;
        } else {
            json_escape(abi->role_symbol[r], esc, sizeof esc);
            printf("    \"%s\": { \"symbol\": \"%s\", \"module\": \"%s\" }%s\n",
                   ROLE[r].suffix, esc, abi->mod[abi->role_module[r]].module,
                   r + 1 < ROLE_COUNT ? "," : "");
        }
    }

    ResolveKind kind = abi_resolve_kind(abi);
    printf("  },\n  \"hostResolution\": \"%s\",\n  \"usable\": %s\n}\n",
           resolve_name(kind),
           (kind == RESOLVE_DYNAMIC || (kind == RESOLVE_STATIC && !missing)) ? "true" : "false");

    if (kind == RESOLVE_DYNAMIC)
        fprintf(stderr, "[netlist-worker] host resolved at run time; --list intercepts it.\n");
    else if (missing)
        fprintf(stderr, "[netlist-worker] %d required ABI role(s) did not bind, and no run-time "
                        "resolver is imported either. This library does not present a host "
                        "interface this worker recognises.\n", missing);
}

static int cmd_gen_shims(const HostAbi *abi, const char *outdir)
{
    if (abi_resolve_kind(abi) != RESOLVE_STATIC) {
        fprintf(stderr, "[netlist-worker] --gen-shims serves a library that binds its host "
                        "statically. This one does not (run --scan). There are no names in the "
                        "import table to generate from.\n");
        return 0;
    }

    int written = 0;
    for (int m = 0; m < abi->modules; m++) {
        if (!module_is_ours(abi, m)) continue;

        char path[MAX_PATH];
        snprintf(path, sizeof path, "%s\\%s.def", outdir, abi->mod[m].module);

        FILE *f = fopen(path, "w");
        if (!f) { fprintf(stderr, "[netlist-worker] cannot write %s\n", path); return 0; }

        fprintf(f, "; GENERATED by netlist_worker --gen-shims from the model library's own import\n"
                   "; table. Not checked in anywhere: the names below belong to the kit on this\n"
                   "; machine. See tools/netlist-worker/README.md.\n\nEXPORTS\n");

        for (int s = 0; s < abi->mod[m].symbols; s++) {
            const char *name = abi->mod[m].symbol[s];

            int role = -1;
            for (int r = 0; r < ROLE_COUNT; r++)
                if (abi->role_module[r] == m && strcmp(abi->role_symbol[r], name) == 0) role = r;

            const char *impl = role == ROLE_ATTACH_RECORD ? "crf_attach_record"
                             : role == ROLE_REMOVE_RECORD ? "crf_remove_record"
                             : role == ROLE_GET_RECORD    ? "crf_get_record"
                             :                              "crf_stub_ptr";
            fprintf(f, "    %s = %s\n", name, impl);
        }
        fprintf(f, "    crf_record_count\n    crf_record_class\n");
        fclose(f);

        printf("%s\n", path);
        written++;
    }

    if (!written)
        fprintf(stderr, "[netlist-worker] no module carried a bound role — nothing to generate.\n");
    return written > 0;
}

/*
 * Load the library with the host in place and report what it registered.
 *
 * Loading IS the experiment: the library's export directory is empty, so registration during
 * DLL_PROCESS_ATTACH is the only reachable code. A zero count is therefore a real refusal, not a
 * call that was never made.
 */
static int cmd_list(const char *library, const HostAbi *abi)
{
    if (abi_resolve_kind(abi) == RESOLVE_STATIC) {
        fprintf(stderr, "[netlist-worker] this library binds its host statically; --list "
                        "intercepts a run-time resolver and would see nothing. Build the modules "
                        "--gen-shims describes and put them where the loader will find them.\n");
        return 0;
    }

    if (!install_hooks()) return 0;

    if (!LoadLibraryExA(library, NULL, 0)) {
        fprintf(stderr, "[netlist-worker] load failed (%lu). The library's own dependencies have "
                        "to be reachable; a missing one is not named here.\n", GetLastError());
        return 0;
    }

    char esc[NAME_MAX * 2], plain[NAME_MAX];

    printf("{\n  \"hostSymbolsAsked\": [");
    for (int i = 0; i < g_asks; i++) {
        json_escape(g_asked[i], esc, sizeof esc);
        printf("%s\"%s\"", i ? ", " : "", esc);
    }
    printf("],\n  \"interfacesRequested\": [");
    for (int i = 0; i < g_ifaces; i++) {
        json_escape(g_iface[i], esc, sizeof esc);
        printf("%s\"%s\"", i ? ", " : "", esc);
    }
    printf("],\n  \"recordsRequested\": [");
    for (int i = 0; i < g_requests; i++) {
        json_escape(g_request[i].asked, esc, sizeof esc);
        printf("%s\n    { \"asked\": %s%s%s, \"matchedBy\": %s%s%s, \"recordIndex\": %ld }",
               i ? "," : "",
               g_request[i].asked[0] ? "\"" : "", g_request[i].asked[0] ? esc : "null",
               g_request[i].asked[0] ? "\"" : "",
               g_request[i].matchedBy[0] ? "\"" : "",
               g_request[i].matchedBy[0] ? g_request[i].matchedBy : "null",
               g_request[i].matchedBy[0] ? "\"" : "",
               g_request[i].recordIndex);
    }
    printf("%s],\n  \"recordCount\": %ld,\n  \"records\": [\n",
           g_requests ? "\n  " : "", g_records);

    for (long i = 0; i < g_records; i++) {
        const char *cls = record_class(g_record[i]);
        if (cls) {
            json_escape(undecorate(cls, plain, sizeof plain), esc, sizeof esc);
            printf("    { \"index\": %ld, \"class\": \"%s\" }%s\n",
                   i, esc, i + 1 < g_records ? "," : "");
        } else {
            /* A record whose locator does not read is reported as such rather than skipped: a
               short list and a list with holes mean different things. */
            printf("    { \"index\": %ld, \"class\": null }%s\n",
                   i, i + 1 < g_records ? "," : "");
        }
    }
    printf("  ]\n}\n");

    if (!g_records)
        fprintf(stderr, "[netlist-worker] the library registered nothing. If it asked for no host "
                        "symbols either, it never found a host at all.\n");
    return g_records > 0;
}

/* ────────────────────────────────────────────────────────────────────────────
 * --build: ask the library to build one part, and record what it asks for.
 * ──────────────────────────────────────────────────────────────────────────── */

/*
 * The identifier a library's factory slot wants. It is a 16-byte constant that a library compares
 * and nothing more — it is ABI vocabulary in the same sense as the role suffixes, not a fact about
 * any kit — but unlike a role suffix it cannot be read out of the image, because it only appears as
 * an argument at run time.
 *
 * So it is settled by OBSERVATION, in this order:
 *   1. --iid, when the caller already knows it.
 *   2. the identifier the library asked one of OUR OWN records for. Any build that gets far enough
 *      to request a primitive reveals it, and it is reported either way.
 * and when neither is available the run says so, with the identifiers it did see, rather than
 * guessing. A wrong identifier here cannot produce a wrong netlist — a factory that does not
 * recognise it hands back nothing at all — which is exactly why it is safe to have to ask.
 */
typedef ULONG_PTR (*create_fn)(void *self, void *outer, const void *riid, void **ppv);

#define FACTORY_SLOT 3

static long find_part(const char *part, const char **rule_out)
{
    long idx = match_record(part, rule_out);
    return idx;
}

static void print_call_log(void)
{
    printf("  \"recordCalls\": [");
    for (int i = 0; i < g_calls; i++) {
        char esc[NAME_MAX * 2];
        json_escape(g_call[i].prim >= 0 ? g_prim_name[g_call[i].prim] : "?", esc, sizeof esc);
        printf("%s\n    { \"primitive\": \"%s\", \"on\": ", i ? "," : "", esc);
        if (g_call[i].obj < 0) printf("\"record\"");
        else                   printf("%d", g_call[i].obj);   /* that component's own model */
        printf(", \"slot\": %d, \"args\": [%d, %d, %d], \"argIsPointer\": [%s, %s, %s] }",
               g_call[i].slot,
               g_call[i].arg[0], g_call[i].arg[1], g_call[i].arg[2],
               g_call[i].argIsPtr[0] ? "true" : "false",
               g_call[i].argIsPtr[1] ? "true" : "false",
               g_call[i].argIsPtr[2] ? "true" : "false");
    }
    printf("%s],\n", g_calls ? "\n  " : "");

    if (!g_dump_args) return;

    printf("  \"argPreviews\": [");
    for (int i = 0; i < g_previews; i++) {
        ArgPreview *v = &g_preview[i];
        char esc[160];
        printf("%s\n    { \"call\": %d, \"arg\": %d, \"owner\": \"%s\", ",
               i ? "," : "", v->call, v->arg, v->owner);
        if (strcmp(v->owner, "self") == 0) {
            printf("\"note\": \"our own address — a leftover register, not an argument\" }");
        } else if (v->text[0]) {
            json_escape(v->text, esc, sizeof esc);
            printf("\"text\": \"%s\" }", esc);
        } else {
            printf("\"ints\": [");
            for (int j = 0; j < v->readable; j++) printf("%s%d", j ? ", " : "", v->ints[j]);
            printf("] }");
        }
    }
    printf("%s],\n  \"argPreviewsNotRecorded\": %ld,\n",
           g_previews ? "\n  " : "", g_previews_dropped);
}

/*
 * The netlist, DERIVED from the call log and clearly labelled as a derivation.
 *
 * A component's wiring arrives as a slot call carrying small integers and no pointers. Which slot
 * that is is not assumed: the log is scanned for the slot whose calls fit that shape, and the slot
 * chosen is reported next to the result. If the wrong slot is picked, the netlist and the log
 * disagree in the same output — which is the point of printing both.
 *
 * PER PRIMITIVE, NOT ONCE FOR THE WHOLE PART. This began as one slot for everything and the first
 * real library refuted it in one run: a slot carrying two node indices for one primitive carried
 * two POINTERS for another, at the same index, in the same part. A single global choice let the
 * second disqualify the first and the netlist came out empty. One primitive's ABI says nothing
 * about another's.
 *
 * A primitive with no slot of that shape gets none, and is reported that way. Its wiring is
 * somewhere this does not yet read, and an empty answer is the honest one — inventing nodes for it
 * would produce exactly the kind of plausible, wrong netlist this whole tool exists to avoid.
 */
static int wiring_slot_for(int prim)
{
    int best = -1, best_n = 0;

    for (int slot = 0; slot < 64; slot++) {
        if (slot == FACTORY_SLOT || slot == 6) continue;   /* the factory, and the terminal count */

        int n = 0, fits = 1;
        for (int i = 0; i < g_calls && fits; i++) {
            /* Record calls only. A component's wiring is stated to the thing that represents the
               component KIND, not to its instance — and mixing the two would let a per-instance
               call masquerade as wiring. */
            if (g_call[i].obj >= 0) continue;
            if (g_call[i].prim != prim || g_call[i].slot != slot) continue;
            /* Node indices are small integers. A slot whose arguments are addresses is carrying
               objects, not wiring — and ONE such call disqualifies the slot for this primitive,
               because a slot that means two things cannot be read as either. */
            if (g_call[i].argIsPtr[1] || g_call[i].argIsPtr[2]) fits = 0;
            else n++;
        }
        if (fits && n > best_n) { best_n = n; best = slot; }
    }
    return best;
}

static void print_netlist(void)
{
    int slot[MAX_PRIMS];
    for (int p = 0; p < g_prims; p++) slot[p] = wiring_slot_for(p);

    char esc[NAME_MAX * 2];
    printf("  \"wiringSlot\": {");
    for (int p = 0; p < g_prims; p++) {
        json_escape(g_prim_name[p], esc, sizeof esc);
        if (slot[p] >= 0) printf("%s \"%s\": %d", p ? "," : "", esc, slot[p]);
        else              printf("%s \"%s\": null", p ? "," : "", esc);
    }
    printf("%s},\n  \"netlist\": [", g_prims ? " " : "");

    int emitted = 0;
    for (int i = 0; i < g_calls; i++) {
        int p = g_call[i].prim;
        if (g_call[i].obj >= 0) continue;
        if (p < 0 || slot[p] < 0 || g_call[i].slot != slot[p]) continue;
        json_escape(g_prim_name[p], esc, sizeof esc);
        printf("%s\n    { \"component\": %d, \"primitive\": \"%s\", \"nodes\": [%d, %d] }",
               emitted ? "," : "", emitted, esc, g_call[i].arg[1], g_call[i].arg[2]);
        emitted++;
    }
    printf("%s],\n", emitted ? "\n  " : "");

    for (int p = 0; p < g_prims; p++)
        if (slot[p] < 0)
            fprintf(stderr, "[netlist-worker] no slot on '%s' carried wiring-shaped arguments, so "
                            "its %d instance(s) are NOT in the netlist. recordCalls has everything "
                            "it was asked.\n", g_prim_name[p], g_prim_asked[p]);
}

static int cmd_build(const char *library, const HostAbi *abi, const char *part, const char *iidtext)
{
    if (abi_resolve_kind(abi) == RESOLVE_STATIC) {
        fprintf(stderr, "[netlist-worker] this library binds its host statically; --build "
                        "intercepts a run-time resolver and would see nothing.\n");
        return 0;
    }

    g_build_mode = 1;
    stand_ins_init();

    if (!install_hooks()) return 0;

    image_range(GetModuleHandleA(NULL), &g_self_base, &g_self_size);

    HMODULE lib = LoadLibraryExA(library, NULL, 0);
    if (!lib) {
        fprintf(stderr, "[netlist-worker] load failed (%lu).\n", GetLastError());
        return 0;
    }
    image_range(lib, &g_lib_base, &g_lib_size);
    if (!g_records) {
        fprintf(stderr, "[netlist-worker] the library registered nothing, so there is no part to "
                        "build. Run --list first.\n");
        return 0;
    }

    const char *rule = NULL;
    long idx = find_part(part, &rule);
    if (idx < 0) {
        fprintf(stderr, "[netlist-worker] no registered record answers to '%s'. --list prints the "
                        "%ld names this library has.\n", part, g_records);
        return 0;
    }
    fprintf(stderr, "[netlist-worker] '%s' is record %ld (matched by %s)\n", part, idx, rule);

    unsigned char iid[16];
    if (!iidtext || !guid_parse(iidtext, iid)) {
        if (iidtext)
            fprintf(stderr, "[netlist-worker] --iid '%s' is not a GUID\n", iidtext);
        else
            fprintf(stderr, "[netlist-worker] no --iid given: the factory needs an interface "
                            "identifier and there is nothing to read one from before the first "
                            "build. Any identifier the library asks a record for is reported under "
                            "iidsAsked — run --list, then pass one back with --iid.\n");
        return 0;
    }

    void **vt = *(void ***)g_record[idx];
    if (!vt) { fprintf(stderr, "[netlist-worker] record %ld has no vtable\n", idx); return 0; }

    /*
     * The first argument is an aggregation outer and MUST be NULL. It reads like a context object
     * and is not one: a non-NULL value is refused outright, which looks identical to a part that
     * cannot be built.
     */
    void *model = NULL;
    ULONG_PTR hr = ((create_fn)vt[FACTORY_SLOT])(g_record[idx], NULL, iid, &model);

    fprintf(stderr, "[netlist-worker] factory returned 0x%08llx, model %p\n",
            (unsigned long long)hr, model);

    char esc[NAME_MAX * 2], plain[NAME_MAX];
    const char *cls = model ? record_class(model) : NULL;

    json_escape(part, esc, sizeof esc);
    printf("{\n  \"part\": \"%s\",\n  \"recordIndex\": %ld,\n", esc, idx);
    printf("  \"factorySlot\": %d,\n  \"hr\": \"0x%08llx\",\n",
           FACTORY_SLOT, (unsigned long long)hr);

    if (cls) {
        json_escape(undecorate(cls, plain, sizeof plain), esc, sizeof esc);
        printf("  \"modelClass\": \"%s\",\n", esc);
    } else {
        printf("  \"modelClass\": null,\n");
    }

    printf("  \"iidsAsked\": [");
    for (int i = 0; i < g_iids; i++) printf("%s\"%s\"", i ? ", " : "", g_iid[i]);
    printf("],\n  \"componentsRequested\": [");
    for (int i = 0; i < g_requests; i++) {
        json_escape(g_request[i].asked, esc, sizeof esc);
        printf("%s\n    { \"asked\": \"%s\", \"matchedBy\": %s%s%s, \"recordIndex\": %ld }",
               i ? "," : "", esc,
               g_request[i].matchedBy[0] ? "\"" : "",
               g_request[i].matchedBy[0] ? g_request[i].matchedBy : "null",
               g_request[i].matchedBy[0] ? "\"" : "",
               g_request[i].recordIndex);
    }
    printf("%s],\n  \"primitives\": [", g_requests ? "\n  " : "");
    for (int i = 0; i < g_prims; i++) {
        json_escape(g_prim_name[i], esc, sizeof esc);
        printf("%s\n    { \"name\": \"%s\", \"instances\": %d, \"terminalsAnswered\": %d }",
               i ? "," : "", esc, g_prim_asked[i], g_terminals);
    }
    printf("%s],\n", g_prims ? "\n  " : "");

    print_call_log();
    print_netlist();

    printf("  \"componentCount\": %d,\n"
           "  \"componentsNotRecorded\": %ld,\n  \"recordCallsNotRecorded\": %ld\n}\n",
           g_requests, g_requests_dropped, g_calls_dropped);

    if (g_requests_dropped || g_calls_dropped)
        fprintf(stderr, "[netlist-worker] this part outgrew the logs: %ld component request(s) and "
                        "%ld record call(s) were served but not recorded. The netlist above is "
                        "INCOMPLETE.\n", g_requests_dropped, g_calls_dropped);

    if (hr != 0 || !model)
        fprintf(stderr, "[netlist-worker] the factory did not hand back a model. An identifier it "
                        "does not recognise is refused outright — check iidsAsked.\n");
    return hr == 0 && model != NULL;
}

static void usage(void)
{
    fprintf(stderr,
        "netlist_worker — ask a compiled model library what its parts are\n\n"
        "  netlist_worker --scan       <model-library>\n"
        "  netlist_worker --gen-shims  <model-library> <out-dir>   (static libraries)\n"
        "  netlist_worker --list       <model-library>             (dynamic libraries)\n"
        "  netlist_worker --build      <model-library> <part> [--iid <guid>] [--terminals <n>]\n"
        "                              [--dump-args]\n\n"
        "--dump-args reads a little of what each POINTER argument points at, as integers and as\n"
        "text. A component with more terminals than fit in a register must receive its wiring by\n"
        "reference, so this is where that wiring would be.\n\n"
        "--build needs the interface identifier a record's factory answers to. It cannot be read\n"
        "out of the image; --list reports every identifier the library asks for, and one of those\n"
        "is it. A wrong one is refused outright rather than silently building the wrong thing.\n\n"
        "Never tested on Windows. On macOS/Linux run it through run.sh.\n");
}

int main(int argc, char **argv)
{
    if (argc < 3) { usage(); return 2; }

    setvbuf(stdout, NULL, _IONBF, 0);
    setvbuf(stderr, NULL, _IONBF, 0);

    HostAbi abi;
    if (!abi_scan(argv[2], &abi)) return 1;

    if (strcmp(argv[1], "--scan") == 0) { cmd_scan(&abi); return 0; }

    if (strcmp(argv[1], "--gen-shims") == 0) {
        if (argc < 4) { usage(); return 2; }
        return cmd_gen_shims(&abi, argv[3]) ? 0 : 1;
    }

    if (strcmp(argv[1], "--list") == 0)
        return cmd_list(argv[2], &abi) ? 0 : 1;

    if (strcmp(argv[1], "--build") == 0) {
        if (argc < 4) { usage(); return 2; }

        const char *iid = NULL;
        for (int i = 4; i + 1 < argc; i++) {
            if (strcmp(argv[i], "--iid") == 0)            iid = argv[i + 1];
            else if (strcmp(argv[i], "--terminals") == 0) g_terminals = atoi(argv[i + 1]);
        }
        for (int i = 4; i < argc; i++)
            if (strcmp(argv[i], "--dump-args") == 0) g_dump_args = 1;
        return cmd_build(argv[2], &abi, argv[3], iid) ? 0 : 1;
    }

    usage();
    return 2;
}

#endif /* CRF_DRIVER */
