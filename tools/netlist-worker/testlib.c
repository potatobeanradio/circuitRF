/*
 * testlib.c — a model library that behaves like the real thing, for driving netlist_worker with no
 * kit present.
 *
 * `tools/fake-model-lib` does this job for tools/senior-worker. This is the same idea for the other
 * ABI, and it exists for the same reason: without it nothing here runs in CI, and every change to
 * the worker has to be validated against a library that not every machine has.
 *
 * WHAT IT REPRODUCES, and each of these is a property the worker has to cope with:
 *
 *   - NO EXPORTS. Nothing can be called into by name. Registration happens during
 *     DLL_PROCESS_ATTACH, which is what makes "load it" the whole registration experiment.
 *   - THE HOST IS RESOLVED AT RUN TIME, through LoadLibrary + GetProcAddress, so the host module
 *     and symbol names appear nowhere in the import table.
 *   - THE HOST SYMBOLS CARRY A PREFIX. The worker must derive it rather than know it.
 *   - RECORDS ARE IDENTIFIED BY RTTI, through the complete-object locator at vtable[-1], so the
 *     worker's identity path is exercised rather than a shortcut.
 *   - A RECORD IS A FACTORY. Its slot 3 builds the part, and building is where the library asks
 *     the host for the primitives it does not implement itself.
 *   - A PART CAN BE MADE OF ANOTHER PART, so the host's lookup has to resolve back into the
 *     library's own records and recurse.
 *   - THE ANSWER TO A LOOKUP ARRIVES IN AN OUT-PARAMETER. This is deliberate and it is the reason
 *     this file changed: it used to read the answer from the RETURN value, which is not what the
 *     ABI does. The worker returned it there too, so both sides agreed with each other and were
 *     wrong together — a double that shares its subject's assumption tests nothing. It now reads
 *     ONLY `*out`, and says so when `*out` is empty.
 *
 * Build: x86_64-w64-mingw32-gcc -shared -o crf_testlib.dll testlib.c
 */

#include <windows.h>
#include <stdio.h>

/*
 * The names this test library uses for its host, and the identifier its factories answer to. They
 * are arbitrary and local to this file — the point of the exercise is that the worker discovers
 * them rather than being told, so they are deliberately NOT the names of anything real.
 */
#define TEST_HOST_MODULE "crf_testhost.dll"
#define TEST_PREFIX      "Tl_"

/* What a caller must ask a record's factory for. netlist_worker has to be TOLD this (--iid) or
   shown it; it cannot read it out of the image, which is the point. */
static const GUID TEST_MODEL_IID =
    { 0x1a2b3c4d, 0x5e6f, 0x4708, { 0x91, 0xa2, 0xb3, 0xc4, 0xd5, 0xe6, 0xf7, 0x08 } };

typedef int   (*attach_fn)(void *rec);
typedef void  (*getrec_fn)(const wchar_t *name, void **out);

typedef ULONG_PTR (*slot_fn)(void *self, void *a, void *b, void *c);

/* ── a minimal but genuine MSVC RTTI chain ────────────────────────────────────
 *
 * Laid out exactly as the compiler would: vtable[-1] points at a complete-object locator whose
 * fields are IMAGE-RELATIVE, with the locator's own RVA at offset 20 so a reader can recover the
 * image base from it. The worker walks precisely this; a shortcut here would test nothing.
 * The RVAs cannot be static initialisers — they depend on where the image lands — so they are
 * filled in on attach.
 */
typedef struct {
    void *pVFTable;
    void *spare;
    char  name[64];          /* decorated: ".?AV<class>@@" */
} TypeDescriptor;

typedef struct {
    DWORD signature;         /* 1 = image-relative (the 64-bit layout) */
    DWORD offset;
    DWORD cdOffset;
    DWORD pTypeDescriptor;   /* RVA */
    DWORD pClassDescriptor;  /* RVA */
    DWORD pSelf;             /* RVA of this locator — the image-base anchor */
} CompleteObjectLocator;

#define RECORD_COUNT 3
#define REC_SLOTS    16

typedef struct {
    void **vptr;             /* points at slot 1 of vtable[] below */
    int    tag;
} Record;

static struct {
    TypeDescriptor        td;
    CompleteObjectLocator col;
    void                 *vtable[1 + REC_SLOTS];   /* [0] = &col, [1..] = the object's own slots */
    Record                rec;
} g_rec[RECORD_COUNT];

static const char *const CLASS_NAME[RECORD_COUNT] = {
    ".?AVKTestPartAlpha@@",
    ".?AVKTestPartBeta@@",
    ".?AVKTestPartGamma@@",
};

/*
 * What each part is made of, and how it is wired. This is the thing a worker is supposed to
 * discover by asking, so it lives HERE, in the library, and nowhere in netlist_worker.c.
 *
 * `TestPartGamma` is made of two `TestPartAlpha`, which is the case that matters most: it forces
 * the host's lookup to resolve back into the library's own records and the build to recurse.
 */
typedef struct { const wchar_t *name; int a, b; } Wire;

static const Wire ALPHA[] = { { L"TLDIODE", 1, -1 }, { L"TLDIODE", 2, -1 } };
static const Wire BETA[]  = { { L"TLNPORT", 0, -1 },
                              { L"TLDIODE", 4, 5 }, { L"TLDIODE", 6, 7 } };
static const Wire GAMMA[] = { { L"TLNPORT", 0, -1 },
                              { L"TestPartAlpha", 3, -1 }, { L"TestPartAlpha", 4, -1 } };

static const struct { const Wire *w; int n; } RECIPE[RECORD_COUNT] = {
    { ALPHA, (int)(sizeof ALPHA / sizeof ALPHA[0]) },
    { BETA,  (int)(sizeof BETA  / sizeof BETA[0])  },
    { GAMMA, (int)(sizeof GAMMA / sizeof GAMMA[0]) },
};

/* The host, resolved once at load. */
static getrec_fn g_getrec;

/* The model a factory hands back. One shared object is fine here: nothing calls into it. */
static void *g_model_vt[REC_SLOTS];
static void *g_model[2];

static ULONG_PTR model_slot(void *self, void *a, void *b, void *c)
{
    (void)self; (void)a; (void)b; (void)c;
    return 0;
}

/* ── the factory: this is where a part gets built ─────────────────────────────
 *
 * Every component is resolved BY NAME through the host, then driven: its terminal count is read,
 * a model is created from it, and its wiring is handed to it. A worker that supplies the host side
 * of this sees the whole part without the library ever writing a netlist down.
 */
static ULONG_PTR build_part(int which, void **ppv)
{
    const Wire *w = RECIPE[which].w;

    for (int i = 0; i < RECIPE[which].n; i++) {
        void *comp = NULL;

        /* THE OUT-PARAMETER IS THE ANSWER. The return value is ignored here on purpose — reading
           it is what hid a real defect in the worker for as long as both sides did it. */
        g_getrec(w[i].name, &comp);

        if (!comp) {
            fprintf(stderr, "[testlib] host could not supply L\"%ls\" — *out was not written\n",
                    w[i].name);
            fflush(stderr);
            return 0x80004005UL;                     /* E_FAIL: the part cannot be built */
        }

        void **vt = *(void ***)comp;

        int terminals = 0;
        ((slot_fn)vt[6])(comp, &terminals, NULL, NULL);

        void *sub = NULL;
        ULONG_PTR hr = ((slot_fn)vt[3])(comp, NULL, (void *)&TEST_MODEL_IID, &sub);
        if (hr || !sub) {
            fprintf(stderr, "[testlib] L\"%ls\" would not create (hr=0x%llx)\n",
                    w[i].name, (unsigned long long)hr);
            fflush(stderr);
            return 0x80004005UL;
        }

        /* The wiring, stated by the library, one call per component. */
        ((slot_fn)vt[7])(comp, NULL, (void *)(INT_PTR)w[i].a, (void *)(INT_PTR)w[i].b);
    }

    g_model[0] = g_model_vt;
    g_model[1] = g_model_vt;
    if (ppv) *ppv = g_model;
    return 0;
}

static int guid_is_model(const void *p)
{
    return p && memcmp(p, &TEST_MODEL_IID, sizeof TEST_MODEL_IID) == 0;
}

static ULONG_PTR rec_slot(int which, int slot, void *self, void *a, void *b, void *c)
{
    switch (slot) {
    case 0:                                          /* QueryInterface */
        if (b) *(void **)b = self;
        return 0;
    case 1: return 2;
    case 2: return 1;
    case 3:                                          /* the factory */
        if (a) return 0x80040110UL;                  /* CLASS_E_NOAGGREGATION */
        if (!guid_is_model(b)) {
            fprintf(stderr, "[testlib] factory asked for an interface it does not implement\n");
            fflush(stderr);
            return 0x80004002UL;                     /* E_NOINTERFACE */
        }
        return build_part(which, (void **)c);
    case 6:                                          /* terminal count */
        if (a) *(int *)a = 2;
        return 0;
    default:
        return 0;
    }
}

/* One thunk per (record, slot), so a record knows which part it is and a slot knows its index. */
#define REC_THUNK(r, s) \
    static ULONG_PTR p##r##_##s(void *self, void *a, void *b, void *c) \
    { return rec_slot(r, s, self, a, b, c); }

#define REC_THUNKS(r) REC_THUNK(r,0) REC_THUNK(r,1) REC_THUNK(r,2) REC_THUNK(r,3) REC_THUNK(r,4) \
    REC_THUNK(r,5) REC_THUNK(r,6) REC_THUNK(r,7) REC_THUNK(r,8) REC_THUNK(r,9) REC_THUNK(r,10) \
    REC_THUNK(r,11) REC_THUNK(r,12) REC_THUNK(r,13) REC_THUNK(r,14) REC_THUNK(r,15)

REC_THUNKS(0)
REC_THUNKS(1)
REC_THUNKS(2)

#define REC_VT(r) { (void *)p##r##_0,  (void *)p##r##_1,  (void *)p##r##_2,  (void *)p##r##_3,  \
                    (void *)p##r##_4,  (void *)p##r##_5,  (void *)p##r##_6,  (void *)p##r##_7,  \
                    (void *)p##r##_8,  (void *)p##r##_9,  (void *)p##r##_10, (void *)p##r##_11, \
                    (void *)p##r##_12, (void *)p##r##_13, (void *)p##r##_14, (void *)p##r##_15 }

static void *const REC_SLOT_TABLE[RECORD_COUNT][REC_SLOTS] = { REC_VT(0), REC_VT(1), REC_VT(2) };

static void build_records(HMODULE self)
{
    char *base = (char *)self;

    for (int i = 0; i < RECORD_COUNT; i++) {
        lstrcpynA(g_rec[i].td.name, CLASS_NAME[i], (int)sizeof g_rec[i].td.name);

        g_rec[i].col.signature       = 1;
        g_rec[i].col.pTypeDescriptor = (DWORD)((char *)&g_rec[i].td  - base);
        g_rec[i].col.pSelf           = (DWORD)((char *)&g_rec[i].col - base);

        g_rec[i].vtable[0] = &g_rec[i].col;         /* the [-1] slot */
        for (int s = 0; s < REC_SLOTS; s++)
            g_rec[i].vtable[1 + s] = REC_SLOT_TABLE[i][s];

        g_rec[i].rec.vptr = &g_rec[i].vtable[1];    /* so vptr[-1] is the locator */
        g_rec[i].rec.tag  = i;
    }

    for (int s = 0; s < REC_SLOTS; s++) g_model_vt[s] = (void *)model_slot;
}

/*
 * Registration happens here. A library with no exports has no other reachable code, so this is both
 * how the real one behaves and why loading it is the whole registration experiment. Building is a
 * separate act, driven later through a record's factory slot.
 */
static void announce(HMODULE self)
{
    build_records(self);

    HMODULE host = LoadLibraryA(TEST_HOST_MODULE);
    if (!host) {
        /* Worth saying out loud: a silent return here looks identical to a worker that hooked
           nothing, and telling the two apart afterwards is expensive. */
        fprintf(stderr, "[testlib] no host module answered '%s' — nothing to register with\n",
                TEST_HOST_MODULE);
        fflush(stderr);
        return;
    }

    attach_fn attach = (attach_fn)(void *)GetProcAddress(host, TEST_PREFIX "AttachEleRecord");
    g_getrec = (getrec_fn)(void *)GetProcAddress(host, TEST_PREFIX "GetEleRecord");

    /* Getters the worker should answer with its generic services object while building, and with
       nothing at all while it is only listing. */
    (void)GetProcAddress(host, TEST_PREFIX "GetCommonObject");
    (void)GetProcAddress(host, TEST_PREFIX "GetUserIO");

    if (!attach) {
        fprintf(stderr, "[testlib] host answered but has no attach entry\n");
        fflush(stderr);
        return;
    }

    for (int i = 0; i < RECORD_COUNT; i++)
        attach(&g_rec[i].rec);

    if (!g_getrec) {
        fprintf(stderr, "[testlib] host has no lookup entry; nothing can be built\n");
        fflush(stderr);
    }
}

BOOL WINAPI DllMain(HINSTANCE h, DWORD reason, LPVOID reserved)
{
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(h);
        announce(h);
    }
    return TRUE;
}
