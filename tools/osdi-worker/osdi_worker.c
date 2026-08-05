/*
 * osdi-worker — evaluates a compiled device model that speaks the OSDI ABI, out of process.
 *
 * A THIRD WORKER, not an extension of either existing one. `senior-worker` evaluates a proprietary
 * model ABI; `netlist-worker` asks a library to DESCRIBE a circuit. This one hosts a documented,
 * openly specified ABI and shares no vocabulary with either. Same relationship those two already
 * have to each other, and the same rule applies: do not fork one into another.
 *
 * WHAT IT SPEAKS. Upward, circuitRF's ordinary device-worker protocol — the framed pipe, the JSON
 * control plane, the raw little-endian doubles. Downward, the ABI declared in osdi.h. Neither side
 * knows about the other, which is the point.
 *
 * NOTHING HERE NAMES A KIT, A VENDOR OR A TOOL. Every device type, parameter name, node count and
 * node role is read out of the library's own descriptor at run time.
 *
 * osdi.h is third-party (MPL-2.0, notice intact in that file) and is kept UNMODIFIED. It is the ABI
 * contract: the struct layout must match the producing compiler's byte for byte, so a hand-copied
 * or "tidied" version is a silent corruption, not a style choice.
 */

#include <dlfcn.h>
#include <errno.h>
#include <math.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

#include "osdi.h"

#define MAX_FRAME_BYTES (512u * 1024u * 1024u)
#define MAX_INSTANCES   4096

/* ── frame I/O ────────────────────────────────────────────────────────────────
 *
 * [ uint32 jsonLen ][ uint32 binLen ][ jsonLen bytes UTF-8 ][ binLen bytes of doubles ]
 *
 * binLen is a BYTE count — it is what the reader must consume, and a count in elements would be
 * ambiguous the moment anything but a double is carried.
 *
 * A short read is NORMAL on a pipe. Treating one as end-of-stream yields frames that decode as
 * garbage only under load, which is the worst way to find out.
 */

/* A read interrupted by a signal is not a failure; it is retried. */
static int errno_is_intr(void) { return errno == EINTR; }

static int read_exact(void *dst, size_t n) {
    unsigned char *p = (unsigned char *)dst;
    while (n > 0) {
        ssize_t r = read(STDIN_FILENO, p, n);
        if (r == 0) return 0;               /* clean end of stream: circuitRF went away */
        if (r < 0) { if (errno_is_intr()) continue; return -1; }
        p += (size_t)r; n -= (size_t)r;
    }
    return 1;
}

static int write_exact(const void *src, size_t n) {
    const unsigned char *p = (const unsigned char *)src;
    while (n > 0) {
        ssize_t w = write(STDOUT_FILENO, p, n);
        if (w <= 0) { if (w < 0 && errno_is_intr()) continue; return -1; }
        p += (size_t)w; n -= (size_t)w;
    }
    return 1;
}

static int write_frame(const char *json, const double *payload, size_t payload_count) {
    uint32_t json_len = (uint32_t)strlen(json);
    uint32_t bin_len  = (uint32_t)(payload_count * sizeof(double));
    if (write_exact(&json_len, 4) != 1) return -1;
    if (write_exact(&bin_len, 4) != 1) return -1;
    if (json_len && write_exact(json, json_len) != 1) return -1;
    if (bin_len && write_exact(payload, bin_len) != 1) return -1;
    return 0;
}

/* ── minimal JSON reading ─────────────────────────────────────────────────────
 *
 * Deliberately small and deliberately not general. The control plane is written by one known
 * producer and carries only strings, numbers and one flat object, so a full parser would be more
 * code to be wrong in. Anything unexpected is reported rather than guessed at.
 */

static const char *skip_ws(const char *p, const char *end) {
    while (p < end && (*p == ' ' || *p == '\t' || *p == '\n' || *p == '\r')) p++;
    return p;
}

/* Advances past one complete JSON value. */
static const char *skip_value(const char *p, const char *end) {
    p = skip_ws(p, end);
    if (p >= end) return end;
    if (*p == '"') {
        p++;
        while (p < end && *p != '"') { if (*p == '\\' && p + 1 < end) p++; p++; }
        return p < end ? p + 1 : end;
    }
    if (*p == '{' || *p == '[') {
        char open = *p, close = (open == '{') ? '}' : ']';
        int depth = 0;
        while (p < end) {
            if (*p == '"') { p = skip_value(p, end); continue; }
            if (*p == open) depth++;
            else if (*p == close) { depth--; if (depth == 0) return p + 1; }
            p++;
        }
        return end;
    }
    while (p < end && *p != ',' && *p != '}' && *p != ']' &&
           *p != ' ' && *p != '\n' && *p != '\r' && *p != '\t') p++;
    return p;
}

/* Finds `key` among the members of the object starting at `obj`, returning its VALUE. */
static const char *json_member(const char *obj, const char *end, const char *key) {
    const char *p = skip_ws(obj, end);
    if (p >= end || *p != '{') return NULL;
    p++;
    size_t klen = strlen(key);
    while (p < end) {
        p = skip_ws(p, end);
        if (p >= end || *p == '}') return NULL;
        if (*p != '"') return NULL;
        const char *name = p + 1;
        const char *name_end = name;
        while (name_end < end && *name_end != '"') {
            if (*name_end == '\\' && name_end + 1 < end) name_end++;
            name_end++;
        }
        p = (name_end < end) ? name_end + 1 : end;
        p = skip_ws(p, end);
        if (p < end && *p == ':') p++;
        p = skip_ws(p, end);
        bool hit = ((size_t)(name_end - name) == klen) && (memcmp(name, key, klen) == 0);
        if (hit) return p;
        p = skip_value(p, end);
        p = skip_ws(p, end);
        if (p < end && *p == ',') p++;
    }
    return NULL;
}

static bool json_str(const char *v, const char *end, char *out, size_t out_size) {
    if (!v || v >= end || *v != '"') return false;
    v++;
    size_t o = 0;
    while (v < end && *v != '"') {
        char c = *v;
        if (c == '\\' && v + 1 < end) {
            v++;
            switch (*v) {
                case 'n': c = '\n'; break; case 't': c = '\t'; break;
                case 'r': c = '\r'; break; case 'b': c = '\b'; break;
                case 'f': c = '\f'; break; default: c = *v; break;
            }
        }
        if (o + 1 >= out_size) return false;
        out[o++] = c;
        v++;
    }
    out[o] = '\0';
    return true;
}

static bool json_num(const char *v, const char *end, double *out) {
    if (!v || v >= end) return false;
    char buf[64];
    size_t n = 0;
    const char *p = v;
    while (p < end && n + 1 < sizeof buf &&
           (*p == '-' || *p == '+' || *p == '.' || *p == 'e' || *p == 'E' ||
            (*p >= '0' && *p <= '9'))) buf[n++] = *p++;
    if (n == 0) return false;
    buf[n] = '\0';
    char *stop = NULL;
    double d = strtod(buf, &stop);
    if (stop == buf) return false;
    *out = d;
    return true;
}

/* ── JSON writing ─────────────────────────────────────────────────────────── */

typedef struct { char *buf; size_t len, cap; } Sb;

static void sb_reserve(Sb *s, size_t extra) {
    if (s->len + extra + 1 <= s->cap) return;
    size_t cap = s->cap ? s->cap : 256;
    while (cap < s->len + extra + 1) cap *= 2;
    s->buf = (char *)realloc(s->buf, cap);
    s->cap = cap;
}
static void sb_puts(Sb *s, const char *t) {
    size_t n = strlen(t); sb_reserve(s, n); memcpy(s->buf + s->len, t, n); s->len += n; s->buf[s->len] = 0;
}
static void sb_json_str(Sb *s, const char *t) {
    sb_puts(s, "\"");
    for (const char *p = t; p && *p; p++) {
        char e[8];
        if (*p == '"' || *p == '\\') { e[0] = '\\'; e[1] = *p; e[2] = 0; sb_puts(s, e); }
        else if ((unsigned char)*p < 0x20) { snprintf(e, sizeof e, "\\u%04x", *p); sb_puts(s, e); }
        else { e[0] = *p; e[1] = 0; sb_puts(s, e); }
    }
    sb_puts(s, "\"");
}
static void sb_int(Sb *s, long v) { char b[32]; snprintf(b, sizeof b, "%ld", v); sb_puts(s, b); }

/* ── the loaded library ───────────────────────────────────────────────────── */

typedef struct {
    void            *handle;
    uint32_t         major, minor;
    uint32_t         num_descriptors;
    size_t           descriptor_size;   /* 0.4 exports this; 0.3 does not */
    unsigned char   *descriptors;       /* walked by descriptor_size, never by sizeof */
} Library;

typedef struct {
    bool                  live;
    const OsdiDescriptor *d;
    void                 *model;
    void                 *inst;
    uint32_t              n;
} Instance;

static Library  g_lib;
static Instance g_inst[MAX_INSTANCES];

static const OsdiDescriptor *descriptor_at(uint32_t i) {
    return (const OsdiDescriptor *)(g_lib.descriptors + (size_t)i * g_lib.descriptor_size);
}

static int load_library(const char *path) {
    g_lib.handle = dlopen(path, RTLD_NOW | RTLD_LOCAL);
    if (!g_lib.handle) { fprintf(stderr, "osdi-worker: dlopen failed: %s\n", dlerror()); return -1; }

    uint32_t *maj = (uint32_t *)dlsym(g_lib.handle, "OSDI_VERSION_MAJOR");
    uint32_t *min = (uint32_t *)dlsym(g_lib.handle, "OSDI_VERSION_MINOR");
    uint32_t *num = (uint32_t *)dlsym(g_lib.handle, "OSDI_NUM_DESCRIPTORS");
    void     *des = dlsym(g_lib.handle, "OSDI_DESCRIPTORS");
    if (!maj || !min || !num || !des) {
        fprintf(stderr, "osdi-worker: '%s' does not export the OSDI entry points "
                        "(OSDI_VERSION_MAJOR/MINOR, OSDI_NUM_DESCRIPTORS, OSDI_DESCRIPTORS).\n", path);
        return -1;
    }

    g_lib.major = *maj; g_lib.minor = *min; g_lib.num_descriptors = *num;
    g_lib.descriptors = (unsigned char *)des;

    /* 0.4 exports the descriptor size so the array can be walked WITHOUT depending on our own
     * sizeof(OsdiDescriptor) — which is exactly what lets a 0.4 library be driven by a header
     * built for 0.3, since 0.4 only appends. Without it we must use our own size, which is
     * correct only when the major/minor match what this header was generated for. */
    size_t *dsz = (size_t *)dlsym(g_lib.handle, "OSDI_DESCRIPTOR_SIZE");
    if (dsz) g_lib.descriptor_size = *dsz;
    else     g_lib.descriptor_size = sizeof(OsdiDescriptor);

    if (g_lib.major != OSDI_VERSION_MAJOR_CURR) {
        fprintf(stderr, "osdi-worker: '%s' declares OSDI %u.%u; this worker implements %u.x. "
                        "A major-version difference changes the descriptor layout, so it is refused "
                        "rather than read as though it matched.\n",
                path, g_lib.major, g_lib.minor,
                (unsigned)OSDI_VERSION_MAJOR_CURR);
        return -1;
    }
    return 0;
}

/* ── describe ─────────────────────────────────────────────────────────────── */

/* osdi.h spells these as (3 << 30) etc., which is a SIGNED int expression. Comparing them against
 * a uint32_t field is a sign-mismatch, and the header must not be edited — it is the ABI contract.
 * So the masks are re-expressed here, once, as unsigned. */
#define KIND_MASK  ((uint32_t)PARA_KIND_MASK)
#define KIND_INST  ((uint32_t)PARA_KIND_INST)
#define KIND_OPVAR ((uint32_t)PARA_KIND_OPVAR)

static const char *param_kind_word(uint32_t flags) {
    switch (flags & PARA_TY_MASK) {
        case PARA_TY_INT: return "int";
        case PARA_TY_STR: return "string";
        default:          return "double";
    }
}

static void emit_describe(Sb *s) {
    sb_puts(s, "{\"types\":[");
    for (uint32_t t = 0; t < g_lib.num_descriptors; t++) {
        const OsdiDescriptor *d = descriptor_at(t);
        if (t) sb_puts(s, ",");
        sb_puts(s, "{\"typeId\":");      sb_json_str(s, d->name);
        sb_puts(s, ",\"displayName\":");  sb_json_str(s, d->name);
        sb_puts(s, ",\"externalPinCount\":");  sb_int(s, (long)d->num_terminals);
        sb_puts(s, ",\"internalNodeCount\":"); sb_int(s, (long)(d->num_nodes - d->num_terminals));
        sb_puts(s, ",\"nonlinear\":true,\"linear\":false");

        /* Parameters. Op-vars are NOT offered: they are outputs, and presenting one as settable
         * would put a writable box in the editor for a value the model computes. */
        sb_puts(s, ",\"params\":[");
        bool first = true;
        for (uint32_t i = 0; i < d->num_params; i++) {
            const OsdiParamOpvar *p = &d->param_opvar[i];
            if ((p->flags & KIND_MASK) == KIND_OPVAR) continue;
            if (!p->name || !p->name[0]) continue;
            if (!first) sb_puts(s, ",");
            first = false;
            sb_puts(s, "{\"name\":"); sb_json_str(s, p->name[0]);
            sb_puts(s, ",\"kind\":");  sb_json_str(s, param_kind_word(p->flags));
            sb_puts(s, "}");
        }
        sb_puts(s, "]");

        /* Nodes. Terminals first, then internal — the order the descriptor itself declares. */
        sb_puts(s, ",\"nodes\":[");
        for (uint32_t i = 0; i < d->num_nodes; i++) {
            if (i) sb_puts(s, ",");
            sb_puts(s, "{\"index\":"); sb_int(s, (long)i);
            sb_puts(s, ",\"external\":"); sb_puts(s, i < d->num_terminals ? "true" : "false");
            sb_puts(s, ",\"label\":"); sb_json_str(s, d->nodes[i].name ? d->nodes[i].name : "");
            sb_puts(s, "}");
        }
        sb_puts(s, "]}");
    }
    sb_puts(s, "]}");
}


/* ── simulator parameters ─────────────────────────────────────────────────── */

/* The values a model asks the HOST for at set-up time: `$simparam("gmin", ...)` and friends.
 *
 * THE ARRAYS MUST BE NON-NULL AND NULL-TERMINATED, and that is the whole point of this block. A
 * model resolves such a request by SCANNING `names` for a match, so a null pointer there is not
 * "no parameters" — it is a null dereference inside the model, during setup_instance, before any
 * of circuitRF's code runs again. It presents as the worker dying with no output at all.
 *
 * This cost a real compiled model to find: the test fixture never asks for a simulator parameter,
 * so nothing here could catch it, and every synthetic test passed. A model that asks for a name
 * absent from this list falls back to its own default, which is why a short honest list is safe
 * and a null one is not. */
static char  *g_sim_names[] = {
    "gmin", "imax", "imelt", "scale", "shrink", "tnom",
    "simulatorVersion", "sourceScaleFactor", "iteration", NULL,
};
static double g_sim_vals[] = {
    1e-12,  1.0,    1.0,     1.0,     1.0,      27.0,
    1.0,               1.0,                 1.0,
};
static char *g_sim_str_names[] = { NULL };
static char *g_sim_str_vals[]  = { NULL };

static OsdiSimParas sim_paras(void) {
    OsdiSimParas s;
    s.names     = g_sim_names;
    s.vals      = g_sim_vals;
    s.names_str = g_sim_str_names;
    s.vals_str  = g_sim_str_vals;
    return s;
}

/* ── create ───────────────────────────────────────────────────────────────── */

/* Finds a parameter by name and returns its index, or -1. Matching walks every alias the model
 * declares, because a model routinely offers more than one spelling of the same parameter. */
static long find_param(const OsdiDescriptor *d, const char *name, uint32_t *flags_out) {
    for (uint32_t i = 0; i < d->num_params; i++) {
        const OsdiParamOpvar *p = &d->param_opvar[i];
        uint32_t aliases = p->num_alias + 1u;
        for (uint32_t a = 0; a < aliases; a++) {
            if (p->name[a] && strcmp(p->name[a], name) == 0) {
                if (flags_out) *flags_out = p->flags;
                return (long)i;
            }
        }
    }
    return -1;
}

static void report_error(const char *msg) {
    Sb s = {0};
    sb_puts(&s, "{\"error\":"); sb_json_str(&s, msg); sb_puts(&s, "}");
    write_frame(s.buf, NULL, 0);
    free(s.buf);
}

static int cmd_create(const char *js, const char *js_end) {
    char type[256];
    if (!json_str(json_member(js, js_end, "typeId"), js_end, type, sizeof type)) {
        report_error("create: no typeId"); return 0;
    }

    const OsdiDescriptor *d = NULL;
    for (uint32_t t = 0; t < g_lib.num_descriptors; t++)
        if (strcmp(descriptor_at(t)->name, type) == 0) { d = descriptor_at(t); break; }
    if (!d) { report_error("create: no such device type in this library"); return 0; }

    int slot = -1;
    for (int i = 0; i < MAX_INSTANCES; i++) if (!g_inst[i].live) { slot = i; break; }
    if (slot < 0) { report_error("create: too many live instances"); return 0; }

    void *model = calloc(1, d->model_size);
    void *inst  = calloc(1, d->instance_size);
    if (!model || !inst) { free(model); free(inst); report_error("create: out of memory"); return 0; }

    /* Parameters are set BEFORE setup, because setup is what turns them into whatever the model
     * actually evaluates with. Setting one afterwards would be accepted and ignored. */
    const char *params = json_member(js, js_end, "params");
    double temperature = 300.0;   /* kelvin; overridden below if the host stated one */

    if (params && *skip_ws(params, js_end) == '{') {
        const char *p = skip_ws(params, js_end) + 1;
        while (p < js_end) {
            p = skip_ws(p, js_end);
            if (p >= js_end || *p == '}') break;
            if (*p != '"') break;
            char name[256];
            const char *name_val = p;
            if (!json_str(name_val, js_end, name, sizeof name)) break;
            p = skip_value(p, js_end);
            p = skip_ws(p, js_end);
            if (p < js_end && *p == ':') p++;
            p = skip_ws(p, js_end);
            const char *value = p;
            p = skip_value(p, js_end);

            uint32_t flags = 0;
            long idx = find_param(d, name, &flags);
            if (idx < 0) {
                /* An unknown name is refused, not ignored. A model matches by keyword and drops
                 * what it does not know, so a typo would otherwise present as a device quietly
                 * running on a default — which converges and is wrong. */
                Sb e = {0};
                sb_puts(&e, "create: '"); sb_puts(&e, name);
                sb_puts(&e, "' is not a parameter of this device type");
                report_error(e.buf); free(e.buf);
                free(model); free(inst);
                return 0;
            }

            bool is_inst = (flags & KIND_MASK) == KIND_INST;
            void *dst = d->access(is_inst ? inst : NULL, is_inst ? NULL : model,
                                  (uint32_t)idx,
                                  ACCESS_FLAG_SET | (is_inst ? ACCESS_FLAG_INSTANCE : 0u));
            if (!dst) { free(model); free(inst); report_error("create: parameter not settable"); return 0; }

            if ((flags & PARA_TY_MASK) == PARA_TY_STR) {
                char sv[1024];
                if (json_str(value, js_end, sv, sizeof sv)) *(char **)dst = strdup(sv);
            } else {
                double dv = 0.0;
                if (json_num(value, js_end, &dv)) {
                    if ((flags & PARA_TY_MASK) == PARA_TY_INT) *(int32_t *)dst = (int32_t)dv;
                    else                                       *(double  *)dst = dv;
                }
            }

            p = skip_ws(p, js_end);
            if (p < js_end && *p == ',') p++;
        }
    }

    /* circuitRF states the device temperature in KELVIN through a reserved name, because OSDI's
     * setup_instance takes one as a required argument and there is nowhere else to put it. It is
     * NOT forwarded as a model parameter — a model that happens to declare a parameter of the same
     * name would then get it twice, with the two meanings silently competing. */
    const char *tk = json_member(js, js_end, "temperatureK");
    if (tk) { double t; if (json_num(tk, js_end, &t) && t > 0.0) temperature = t; }

    OsdiSimParas sim = sim_paras();
    OsdiInitInfo info = { 0, 0, NULL };

    d->setup_model(NULL, model, &sim, &info);
    if (info.num_errors) { free(model); free(inst); report_error("create: model setup reported errors"); return 0; }

    info.num_errors = 0;
    d->setup_instance(NULL, inst, model, temperature, d->num_terminals, &sim, &info);
    if (info.num_errors) { free(model); free(inst); report_error("create: instance setup reported errors"); return 0; }

    g_inst[slot].live  = true;
    g_inst[slot].d     = d;
    g_inst[slot].model = model;
    g_inst[slot].inst  = inst;
    g_inst[slot].n     = d->num_nodes;

    /* Node mapping is the identity: this worker evaluates ONE device against its own local
     * solution vector, so node k of the device is row k of that vector. */
    uint32_t *map = (uint32_t *)((char *)inst + d->node_mapping_offset);
    for (uint32_t i = 0; i < d->num_nodes; i++) map[i] = i;

    Sb s = {0};
    sb_puts(&s, "{\"handle\":"); sb_int(&s, slot);
    sb_puts(&s, ",\"pinCount\":"); sb_int(&s, (long)d->num_nodes);

    /* Node COLLAPSING is declared by the model and read back after setup — this is what circuitRF
     * calls a slaved node. It is reported here rather than measured by probing, because the model
     * states it outright: which node each collapsed one FOLLOWS is the part a structural probe
     * cannot recover, and getting it wrong is a solve that will not converge with no error. */
    sb_puts(&s, ",\"collapsed\":[");
    if (d->num_collapsible && d->collapsible) {
        const bool *collapsed = (const bool *)((char *)inst + d->collapsed_offset);
        bool first = true;
        for (uint32_t k = 0; k < d->num_collapsible; k++) {
            if (!collapsed[k]) continue;
            if (!first) sb_puts(&s, ",");
            first = false;
            sb_puts(&s, "{\"node\":"); sb_int(&s, (long)d->collapsible[k].node_1);
            sb_puts(&s, ",\"to\":");
            /* node_2 == UINT32_MAX means collapsed to ground rather than to another node. */
            if (d->collapsible[k].node_2 == UINT32_MAX) sb_puts(&s, "-1");
            else sb_int(&s, (long)d->collapsible[k].node_2);
            sb_puts(&s, "}");
        }
    }
    sb_puts(&s, "]}");
    write_frame(s.buf, NULL, 0);
    free(s.buf);
    return 0;
}

/* ── eval ─────────────────────────────────────────────────────────────────── */

static int cmd_eval(const char *js, const char *js_end, const double *in, size_t in_count) {
    double hv = -1, cv = 0;
    json_num(json_member(js, js_end, "handle"), js_end, &hv);
    json_num(json_member(js, js_end, "count"),  js_end, &cv);
    int    h     = (int)hv;
    size_t count = (size_t)cv;

    if (h < 0 || h >= MAX_INSTANCES || !g_inst[h].live) { report_error("eval: no such instance"); return 0; }
    Instance *ins = &g_inst[h];
    const OsdiDescriptor *d = ins->d;
    uint32_t n = ins->n;

    if (in_count != count * n) { report_error("eval: voltage payload does not match count x nodes"); return 0; }

    size_t per_point = 2u * n + 2u * (size_t)n * n;
    size_t out_count = count + count * per_point;
    double *out = (double *)calloc(out_count ? out_count : 1, sizeof(double));
    double *sol = (double *)calloc(n ? n : 1, sizeof(double));
    double *jr  = (double *)calloc(d->num_jacobian_entries ? d->num_jacobian_entries : 1, sizeof(double));
    double *jx  = (double *)calloc(d->num_jacobian_entries ? d->num_jacobian_entries : 1, sizeof(double));
    if (!out || !sol || !jr || !jx) { free(out); free(sol); free(jr); free(jx);
                                      report_error("eval: out of memory"); return 0; }

    /* Install our scratch as the matrix the model writes through. The model accumulates (+=) into
     * these, because in a real host several instances share one matrix entry — so each is zeroed
     * per point rather than assumed to be overwritten. */
    double **pr = (double **)((char *)ins->inst + d->jacobian_ptr_resist_offset);
    for (uint32_t e = 0; e < d->num_jacobian_entries; e++) {
        pr[e] = &jr[e];
        uint32_t off = d->jacobian_entries[e].react_ptr_off;
        if (off != UINT32_MAX) *(double **)((char *)ins->inst + off) = &jx[e];
    }

    for (size_t k = 0; k < count; k++) {
        for (uint32_t i = 0; i < n; i++) sol[i] = in[k * n + i];

        OsdiSimParas sim = sim_paras();
        OsdiSimInfo info;
        memset(&info, 0, sizeof info);
        info.paras      = sim;
        info.abstime    = 0.0;
        info.prev_solve = sol;
        info.prev_state = NULL;
        info.next_state = NULL;
        /* Limiting is deliberately NOT enabled. It is a Newton-damping device whose meaning is tied
         * to a previous iterate, and it does not carry over to a frequency-domain solve. */
        info.flags = CALC_RESIST_RESIDUAL | CALC_REACT_RESIDUAL |
                     CALC_RESIST_JACOBIAN | CALC_REACT_JACOBIAN |
                     CALC_OP | ANALYSIS_DC | ANALYSIS_STATIC;

        memset(jr, 0, d->num_jacobian_entries * sizeof(double));
        memset(jx, 0, d->num_jacobian_entries * sizeof(double));

        uint32_t rc = d->eval(NULL, ins->inst, ins->model, &info);

        double *point = out + count + k * per_point;
        double *I = point;
        double *Q = I + n;
        double *G = Q + n;
        double *C = G + (size_t)n * n;

        if (rc & EVAL_RET_FLAG_FATAL) { out[k] = 0.0; continue; }

        /* Residuals are read at the byte offsets the descriptor declares, straight out of the
         * instance. This is the raw i and q — deliberately NOT load_spice_rhs_*, which returns a
         * LINEARIZED right-hand side in SPICE's own convention. That convention is wrong for
         * harmonic balance, which wants the quantities themselves. */
        bool finite = true;
        for (uint32_t i = 0; i < n; i++) {
            uint32_t ro = d->nodes[i].resist_residual_off;
            uint32_t xo = d->nodes[i].react_residual_off;
            I[i] = (ro != UINT32_MAX) ? *(double *)((char *)ins->inst + ro) : 0.0;
            Q[i] = (xo != UINT32_MAX) ? *(double *)((char *)ins->inst + xo) : 0.0;
            if (!isfinite(I[i]) || !isfinite(Q[i])) finite = false;
        }

        if (d->load_jacobian_resist) d->load_jacobian_resist(ins->inst, ins->model);
        /* alpha = 1 gives the raw dQ/dV. A transient host passes an integration coefficient here;
         * circuitRF wants the capacitance itself and applies its own weighting. */
        if (d->load_jacobian_react)  d->load_jacobian_react(ins->inst, ins->model, 1.0);

        for (uint32_t e = 0; e < d->num_jacobian_entries; e++) {
            uint32_t r = d->jacobian_entries[e].nodes.node_1;
            uint32_t c = d->jacobian_entries[e].nodes.node_2;
            if (r >= n || c >= n) continue;
            G[(size_t)r * n + c] += jr[e];
            C[(size_t)r * n + c] += jx[e];
            if (!isfinite(jr[e]) || !isfinite(jx[e])) finite = false;
        }

        out[k] = finite ? 1.0 : 0.0;
    }

    Sb s = {0};
    sb_puts(&s, "{\"count\":"); sb_int(&s, (long)count);
    sb_puts(&s, ",\"pinCount\":"); sb_int(&s, (long)n);
    sb_puts(&s, "}");
    write_frame(s.buf, out, out_count);
    free(s.buf); free(out); free(sol); free(jr); free(jx);
    return 0;
}

/* ── command loop ─────────────────────────────────────────────────────────── */

int main(int argc, char **argv) {
    if (argc < 2) {
        fprintf(stderr, "usage: %s <library.osdi>\n", argv[0]);
        return 2;
    }
    if (load_library(argv[1]) != 0) return 3;

    for (;;) {
        uint32_t json_len = 0, bin_len = 0;
        int r = read_exact(&json_len, 4);
        if (r == 0) break;                       /* circuitRF closed the pipe: ordinary shutdown */
        if (r < 0) return 4;
        if (read_exact(&bin_len, 4) != 1) return 4;
        if (json_len > MAX_FRAME_BYTES || bin_len > MAX_FRAME_BYTES) {
            /* A length past any plausible frame means the stream is out of step, not that a huge
             * result is coming. Believing it allocates gigabytes on a corrupt number. */
            fprintf(stderr, "osdi-worker: implausible frame length; stream is desynchronised\n");
            return 5;
        }

        char *js = (char *)malloc(json_len + 1u);
        if (!js) return 6;
        if (json_len && read_exact(js, json_len) != 1) { free(js); return 4; }
        js[json_len] = '\0';

        double *payload = NULL;
        size_t  payload_count = bin_len / sizeof(double);
        if (bin_len) {
            payload = (double *)malloc(bin_len);
            if (!payload) { free(js); return 6; }
            if (read_exact(payload, bin_len) != 1) { free(js); free(payload); return 4; }
        }

        const char *js_end = js + json_len;
        char cmd[64] = {0};
        json_str(json_member(js, js_end, "cmd"), js_end, cmd, sizeof cmd);

        if (strcmp(cmd, "describe") == 0) {
            Sb s = {0}; emit_describe(&s); write_frame(s.buf, NULL, 0); free(s.buf);
        } else if (strcmp(cmd, "create") == 0) {
            cmd_create(js, js_end);
        } else if (strcmp(cmd, "eval") == 0) {
            cmd_eval(js, js_end, payload, payload_count);
        } else if (strcmp(cmd, "destroy") == 0) {
            double hv = -1; json_num(json_member(js, js_end, "handle"), js_end, &hv);
            int h = (int)hv;
            if (h >= 0 && h < MAX_INSTANCES && g_inst[h].live) {
                free(g_inst[h].model); free(g_inst[h].inst);
                memset(&g_inst[h], 0, sizeof g_inst[h]);
            }
            write_frame("{}", NULL, 0);
        } else if (strcmp(cmd, "shutdown") == 0) {
            write_frame("{}", NULL, 0);
            free(js); free(payload);
            break;
        } else if (strcmp(cmd, "probe") == 0) {
            /* Nothing to measure: this ABI DECLARES its collapsed nodes, and `create` already
             * reported them. Answering with no nodes leaves the declared descriptor standing,
             * which is exactly what the host does with a worker that cannot probe. */
            write_frame("{}", NULL, 0);
        } else {
            report_error("unknown command");
        }

        free(js);
        free(payload);
        fflush(stdout);
    }
    return 0;
}
