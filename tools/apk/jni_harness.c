/* A host for libsuperpack.so that speaks enough JNI to start it.
 *
 * The library is a JNI library: the only way in is JNI_OnLoad(JavaVM*, void*),
 * which calls RegisterNatives and hands over the real function pointers. There
 * is no VM here, so this file is the VM: a JavaVM whose GetEnv returns an
 * environment table, and an environment table whose interesting slots are
 * implemented over plain C objects (arrays, strings) and whose slots nobody
 * asked for return zero and say so.
 *
 * Why bother: RegisterNatives is the library telling us its own entry points
 * and their signatures, which is more than the DEX can say and cannot be wrong.
 *
 * It runs under bionic, on the NDK compiler, with the runtime from a system
 * image./linker64 --library-path SYS./harness.
 */
#include <jni.h>
#include <dlfcn.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <malloc.h>

/* ------------------------------------------------------------------ */
/* Objects the library will hold. None of these are Java objects; they are */
/* C structs that answer the few questions a JNI call asks of them. */

#define MAGIC_ARRAY 0x41525259
#define MAGIC_STRING 0x53545247
#define MAGIC_OBJARRAY 0x4f424a41
#define MAGIC_CLASS 0x434c4153
#define MAGIC_FIELD 0x464c4449
#define MAGIC_METHOD 0x4d455448

typedef struct {
    int magic;
    jsize len;
    void *data;
    void *elems; /* what Get*ArrayElements handed out, if anything */
    jsize elemsLen;
} FakeArray;

typedef struct {
    int magic;
    char *utf;
} FakeString;

typedef struct {
    int magic;
    jsize len;
    jclass cls;
    jobject *elems;
} FakeObjArray;

typedef struct {
    int magic;
    char name[256];
    char sig[256];
} FakeMember;

static int g_arrayCount, g_stringCount, g_objectCount;

/* ------------------------------------------------------------------ */
/* Unknown slots. Every pointer in the table starts as one of these, so a */
/* call nobody expected prints its own number instead of jumping to zero. */

#define UNKNOWN_SLOTS 260

#define UNKNOWN(N)                                                      \
    static jlong unknown_##N(void *a, ...)                               \
    {                                                                    \
        (void)a;                                                         \
        fprintf(stderr, "JNI slot %d called (not implemented)\n", N);    \
        return 0;                                                        \
    }

UNKNOWN(0) UNKNOWN(1) UNKNOWN(2) UNKNOWN(3) UNKNOWN(4) UNKNOWN(5)
UNKNOWN(6) UNKNOWN(7) UNKNOWN(8) UNKNOWN(9) UNKNOWN(10) UNKNOWN(11)
UNKNOWN(12) UNKNOWN(13) UNKNOWN(14) UNKNOWN(15) UNKNOWN(16) UNKNOWN(17)
UNKNOWN(18) UNKNOWN(19) UNKNOWN(20) UNKNOWN(21) UNKNOWN(22) UNKNOWN(23)
UNKNOWN(24) UNKNOWN(25) UNKNOWN(26) UNKNOWN(27) UNKNOWN(28) UNKNOWN(29)
UNKNOWN(30) UNKNOWN(31) UNKNOWN(32) UNKNOWN(33) UNKNOWN(34) UNKNOWN(35)
UNKNOWN(36) UNKNOWN(37) UNKNOWN(38) UNKNOWN(39) UNKNOWN(40) UNKNOWN(41)
UNKNOWN(42) UNKNOWN(43) UNKNOWN(44) UNKNOWN(45) UNKNOWN(46) UNKNOWN(47)
UNKNOWN(48) UNKNOWN(49) UNKNOWN(50) UNKNOWN(51) UNKNOWN(52) UNKNOWN(53)
UNKNOWN(54) UNKNOWN(55) UNKNOWN(56) UNKNOWN(57) UNKNOWN(58) UNKNOWN(59)
UNKNOWN(60) UNKNOWN(61) UNKNOWN(62) UNKNOWN(63) UNKNOWN(64) UNKNOWN(65)
UNKNOWN(66) UNKNOWN(67) UNKNOWN(68) UNKNOWN(69) UNKNOWN(70) UNKNOWN(71)
UNKNOWN(72) UNKNOWN(73) UNKNOWN(74) UNKNOWN(75) UNKNOWN(76) UNKNOWN(77)
UNKNOWN(78) UNKNOWN(79) UNKNOWN(80) UNKNOWN(81) UNKNOWN(82) UNKNOWN(83)
UNKNOWN(84) UNKNOWN(85) UNKNOWN(86) UNKNOWN(87) UNKNOWN(88) UNKNOWN(89)
UNKNOWN(90) UNKNOWN(91) UNKNOWN(92) UNKNOWN(93) UNKNOWN(94) UNKNOWN(95)
UNKNOWN(96) UNKNOWN(97) UNKNOWN(98) UNKNOWN(99) UNKNOWN(100) UNKNOWN(101)
UNKNOWN(102) UNKNOWN(103) UNKNOWN(104) UNKNOWN(105) UNKNOWN(106) UNKNOWN(107)
UNKNOWN(108) UNKNOWN(109) UNKNOWN(110) UNKNOWN(111) UNKNOWN(112) UNKNOWN(113)
UNKNOWN(114) UNKNOWN(115) UNKNOWN(116) UNKNOWN(117) UNKNOWN(118) UNKNOWN(119)
UNKNOWN(120) UNKNOWN(121) UNKNOWN(122) UNKNOWN(123) UNKNOWN(124) UNKNOWN(125)
UNKNOWN(126) UNKNOWN(127) UNKNOWN(128) UNKNOWN(129) UNKNOWN(130) UNKNOWN(131)
UNKNOWN(132) UNKNOWN(133) UNKNOWN(134) UNKNOWN(135) UNKNOWN(136) UNKNOWN(137)
UNKNOWN(138) UNKNOWN(139) UNKNOWN(140) UNKNOWN(141) UNKNOWN(142) UNKNOWN(143)
UNKNOWN(144) UNKNOWN(145) UNKNOWN(146) UNKNOWN(147) UNKNOWN(148) UNKNOWN(149)
UNKNOWN(150) UNKNOWN(151) UNKNOWN(152) UNKNOWN(153) UNKNOWN(154) UNKNOWN(155)
UNKNOWN(156) UNKNOWN(157) UNKNOWN(158) UNKNOWN(159) UNKNOWN(160) UNKNOWN(161)
UNKNOWN(162) UNKNOWN(163) UNKNOWN(164) UNKNOWN(165) UNKNOWN(166) UNKNOWN(167)
UNKNOWN(168) UNKNOWN(169) UNKNOWN(170) UNKNOWN(171) UNKNOWN(172) UNKNOWN(173)
UNKNOWN(174) UNKNOWN(175) UNKNOWN(176) UNKNOWN(177) UNKNOWN(178) UNKNOWN(179)
UNKNOWN(180) UNKNOWN(181) UNKNOWN(182) UNKNOWN(183) UNKNOWN(184) UNKNOWN(185)
UNKNOWN(186) UNKNOWN(187) UNKNOWN(188) UNKNOWN(189) UNKNOWN(190) UNKNOWN(191)
UNKNOWN(192) UNKNOWN(193) UNKNOWN(194) UNKNOWN(195) UNKNOWN(196) UNKNOWN(197)
UNKNOWN(198) UNKNOWN(199) UNKNOWN(200) UNKNOWN(201) UNKNOWN(202) UNKNOWN(203)
UNKNOWN(204) UNKNOWN(205) UNKNOWN(206) UNKNOWN(207) UNKNOWN(208) UNKNOWN(209)
UNKNOWN(210) UNKNOWN(211) UNKNOWN(212) UNKNOWN(213) UNKNOWN(214) UNKNOWN(215)
UNKNOWN(216) UNKNOWN(217) UNKNOWN(218) UNKNOWN(219) UNKNOWN(220) UNKNOWN(221)
UNKNOWN(222) UNKNOWN(223) UNKNOWN(224) UNKNOWN(225) UNKNOWN(226) UNKNOWN(227)
UNKNOWN(228) UNKNOWN(229) UNKNOWN(230) UNKNOWN(231) UNKNOWN(232) UNKNOWN(233)
UNKNOWN(234) UNKNOWN(235) UNKNOWN(236) UNKNOWN(237) UNKNOWN(238) UNKNOWN(239)
UNKNOWN(240) UNKNOWN(241) UNKNOWN(242) UNKNOWN(243) UNKNOWN(244) UNKNOWN(245)
UNKNOWN(246) UNKNOWN(247) UNKNOWN(248) UNKNOWN(249) UNKNOWN(250) UNKNOWN(251)
UNKNOWN(252) UNKNOWN(253) UNKNOWN(254) UNKNOWN(255) UNKNOWN(256) UNKNOWN(257)
UNKNOWN(258) UNKNOWN(259)

static void *unknown_table[UNKNOWN_SLOTS] = {
#define U(N) (void *)unknown_##N,
    U(0) U(1) U(2) U(3) U(4) U(5) U(6) U(7) U(8) U(9)
    U(10) U(11) U(12) U(13) U(14) U(15) U(16) U(17) U(18) U(19)
    U(20) U(21) U(22) U(23) U(24) U(25) U(26) U(27) U(28) U(29)
    U(30) U(31) U(32) U(33) U(34) U(35) U(36) U(37) U(38) U(39)
    U(40) U(41) U(42) U(43) U(44) U(45) U(46) U(47) U(48) U(49)
    U(50) U(51) U(52) U(53) U(54) U(55) U(56) U(57) U(58) U(59)
    U(60) U(61) U(62) U(63) U(64) U(65) U(66) U(67) U(68) U(69)
    U(70) U(71) U(72) U(73) U(74) U(75) U(76) U(77) U(78) U(79)
    U(80) U(81) U(82) U(83) U(84) U(85) U(86) U(87) U(88) U(89)
    U(90) U(91) U(92) U(93) U(94) U(95) U(96) U(97) U(98) U(99)
    U(100) U(101) U(102) U(103) U(104) U(105) U(106) U(107) U(108) U(109)
    U(110) U(111) U(112) U(113) U(114) U(115) U(116) U(117) U(118) U(119)
    U(120) U(121) U(122) U(123) U(124) U(125) U(126) U(127) U(128) U(129)
    U(130) U(131) U(132) U(133) U(134) U(135) U(136) U(137) U(138) U(139)
    U(140) U(141) U(142) U(143) U(144) U(145) U(146) U(147) U(148) U(149)
    U(150) U(151) U(152) U(153) U(154) U(155) U(156) U(157) U(158) U(159)
    U(160) U(161) U(162) U(163) U(164) U(165) U(166) U(167) U(168) U(169)
    U(170) U(171) U(172) U(173) U(174) U(175) U(176) U(177) U(178) U(179)
    U(180) U(181) U(182) U(183) U(184) U(185) U(186) U(187) U(188) U(189)
    U(190) U(191) U(192) U(193) U(194) U(195) U(196) U(197) U(198) U(199)
    U(200) U(201) U(202) U(203) U(204) U(205) U(206) U(207) U(208) U(209)
    U(210) U(211) U(212) U(213) U(214) U(215) U(216) U(217) U(218) U(219)
    U(220) U(221) U(222) U(223) U(224) U(225) U(226) U(227) U(228) U(229)
    U(230) U(231) U(232) U(233) U(234) U(235) U(236) U(237) U(238) U(239)
    U(240) U(241) U(242) U(243) U(244) U(245) U(246) U(247) U(248) U(249)
    U(250) U(251) U(252) U(253) U(254) U(255) U(256) U(257) U(258) U(259)
#undef U
};

/* ------------------------------------------------------------------ */
/* The environment. */

static struct JNINativeInterface gEnv;
static struct JNIInvokeInterface gVmTable;

/* A JNIEnv is already a pointer to the function table, so what the library is
 * handed is the address of a variable that holds that pointer. Passing the
 * table's own address makes the first call read the table's first word as a
 * pointer, which is how the first attempt died at 0x30. */
static JNIEnv gEnvValue;
static JavaVM gVmValue;

static jint h_get_version(JNIEnv *e)
{
    (void)e;
    return JNI_VERSION_1_6;
}

static jclass h_find_class(JNIEnv *e, const char *name)
{
    (void)e;
    FakeMember *cls = (FakeMember *)calloc(1, sizeof(FakeMember));
    cls->magic = MAGIC_CLASS;
    snprintf(cls->name, sizeof cls->name, "%s", name ? name : "?");
    fprintf(stderr, "[jni] FindClass %s\n", cls->name);

    return (jclass)cls;
}

static jclass h_get_object_class(JNIEnv *e, jobject o)
{
    (void)e; (void)o;
    return (jclass)0x1;
}

static jint h_register_natives(JNIEnv *e, jclass cls,
                              const JNINativeMethod *methods, jint n)
{
    (void)e; (void)cls;

    int i;
    fprintf(stderr, "[jni] RegisterNatives: %d method(s)\n", (int)n);
    for (i = 0; i < n; i++) {
        Dl_info info;
        const char *sym = "?";
        unsigned long off = 0;

        if (dladdr((void *)methods[i].fnPtr, &info)) {
            if (info.dli_sname) sym = info.dli_sname;
            if (info.dli_fbase)
                off = (unsigned long)((const char *)methods[i].fnPtr -
                                      (const char *)info.dli_fbase);
        }

        fprintf(stderr, "  %-28s %-46s %s+0x%lx\n",
                methods[i].name, methods[i].signature, sym, off);
    }

    return JNI_OK;
}

static jfieldID h_get_field_id(JNIEnv *e, jclass cls, const char *name,
                              const char *sig)
{
    (void)e; (void)cls;
    FakeMember *f = (FakeMember *)calloc(1, sizeof(FakeMember));
    f->magic = MAGIC_FIELD;
    snprintf(f->name, sizeof f->name, "%s", name ? name : "?");
    snprintf(f->sig, sizeof f->sig, "%s", sig ? sig : "?");
    fprintf(stderr, "[jni] GetFieldID %s : %s\n", f->name, f->sig);

    return (jfieldID)f;
}

static jmethodID h_get_method_id(JNIEnv *e, jclass cls, const char *name,
                                const char *sig)
{
    (void)e; (void)cls;
    FakeMember *f = (FakeMember *)calloc(1, sizeof(FakeMember));
    f->magic = MAGIC_METHOD;
    snprintf(f->name, sizeof f->name, "%s", name ? name : "?");
    snprintf(f->sig, sizeof f->sig, "%s", sig ? sig : "?");
    fprintf(stderr, "[jni] GetMethodID %s : %s\n", f->name, f->sig);

    return (jmethodID)f;
}

static jstring h_new_string_utf(JNIEnv *e, const char *utf)
{
    (void)e;
    FakeString *s = (FakeString *)calloc(1, sizeof(FakeString));
    s->magic = MAGIC_STRING;
    s->utf = strdup(utf ? utf : "");
    g_stringCount++;

    return (jstring)s;
}

static const char *h_get_string_utf_chars(JNIEnv *e, jstring js, jboolean *copy)
{
    (void)e;
    FakeString *s = (FakeString *)js;
    if (copy) *copy = JNI_FALSE;
    if (!s || s->magic != MAGIC_STRING) return "";

    return s->utf;
}

static void h_release_string_utf_chars(JNIEnv *e, jstring js, const char *c)
{
    (void)e; (void)js; (void)c;
}

static jsize h_get_string_length(JNIEnv *e, jstring js)
{
    (void)e;
    FakeString *s = (FakeString *)js;
    if (!s || s->magic != MAGIC_STRING) return 0;

    return (jsize)strlen(s->utf);
}

static jsize h_get_string_utf_length(JNIEnv *e, jstring js)
{
    return h_get_string_length(e, js);
}

static jbyteArray h_new_byte_array(JNIEnv *e, jsize len)
{
    (void)e;
    FakeArray *a = (FakeArray *)calloc(1, sizeof(FakeArray));
    a->magic = MAGIC_ARRAY;
    a->len = len;
    a->data = calloc(1, len > 0 ? (size_t)len : 1);
    g_arrayCount++;

    return (jbyteArray)a;
}

static jsize h_get_array_length(JNIEnv *e, jarray a)
{
    (void)e;
    if (!a) return 0;

    return ((FakeArray *)a)->len;
}

static jbyte *h_get_byte_array_elements(JNIEnv *e, jbyteArray a, jboolean *copy)
{
    (void)e;
    FakeArray *f = (FakeArray *)a;
    if (copy) *copy = JNI_FALSE;
    if (!f) return NULL;
    f->elems = f->data;
    f->elemsLen = f->len;

    return f->data;
}

static void h_release_byte_array_elements(JNIEnv *e, jbyteArray a, jbyte *p,
                                          jint mode)
{
    (void)e; (void)a; (void)p; (void)mode;
}

static void h_get_byte_array_region(JNIEnv *e, jbyteArray a, jsize start,
                                    jsize len, jbyte *buf)
{
    (void)e;
    FakeArray *f = (FakeArray *)a;
    if (!f || start < 0 || len < 0 || start + len > f->len) return;
    memcpy(buf, (jbyte *)f->data + start, (size_t)len);
}

static void h_set_byte_array_region(JNIEnv *e, jbyteArray a, jsize start,
                                    jsize len, const jbyte *buf)
{
    (void)e;
    FakeArray *f = (FakeArray *)a;
    if (!f || start < 0 || len < 0 || start + len > f->len) return;
    memcpy((jbyte *)f->data + start, buf, (size_t)len);
}

static jobjectArray h_new_object_array(JNIEnv *e, jsize len, jclass cls,
                                      jobject init)
{
    (void)e; (void)cls; (void)init;
    FakeObjArray *a = (FakeObjArray *)calloc(1, sizeof(FakeObjArray));
    a->magic = MAGIC_OBJARRAY;
    a->len = len;
    a->elems = (jobject *)calloc(1, sizeof(jobject) * (len > 0 ? (size_t)len : 1));
    g_objectCount++;

    return (jobjectArray)a;
}

static jobject h_get_object_array_element(JNIEnv *e, jobjectArray a, jsize i)
{
    (void)e;
    FakeObjArray *f = (FakeObjArray *)a;
    if (!f || i < 0 || i >= f->len) return NULL;

    return f->elems[i];
}

static void h_set_object_array_element(JNIEnv *e, jobjectArray a, jsize i,
                                       jobject o)
{
    (void)e;
    FakeObjArray *f = (FakeObjArray *)a;
    if (!f || i < 0 || i >= f->len) return;
    f->elems[i] = o;
}

static jint h_throw_new(JNIEnv *e, jclass cls, const char *msg)
{
    (void)e; (void)cls;
    fprintf(stderr, "[jni] ThrowNew: %s\n", msg ? msg : "");

    return 0;
}

static jboolean h_exception_check(JNIEnv *e)
{
    (void)e;

    return JNI_FALSE;
}

static jthrowable h_exception_occurred(JNIEnv *e)
{
    (void)e;

    return NULL;
}

static void h_exception_clear(JNIEnv *e)
{
    (void)e;
}

static jint h_get_java_vm(JNIEnv *e, JavaVM **vm);

/* ------------------------------------------------------------------ */
/* The VM */

static struct JNIInvokeInterface gVmTable;

static jint vm_destroy(JavaVM *vm)
{
    (void)vm;

    return JNI_OK;
}

static jint vm_attach(JavaVM *vm, JNIEnv **env, void *args)
{
    (void)vm; (void)args;
    if (env) *env = &gEnvValue;

    return JNI_OK;
}

static jint vm_detach(JavaVM *vm)
{
    (void)vm;

    return JNI_OK;
}

static jint vm_get_env(JavaVM *vm, void **env, jint version)
{
    (void)vm; (void)version;
    if (env) *env = (void *)&gEnvValue;

    return JNI_OK;
}

static jint h_get_java_vm(JNIEnv *e, JavaVM **vm)
{
    (void)e;
    if (vm) *vm = &gVmValue;

    return JNI_OK;
}

static void build_env(void)
{
    size_t slots = sizeof gEnv / sizeof(void *);
    void **table = (void **)&gEnv;
    size_t i;

    memset(&gEnv, 0, sizeof gEnv);

    /* Everything starts as a stub that names its own slot. */
    for (i = 0; i < slots && i < UNKNOWN_SLOTS; i++) table[i] = unknown_table[i];

    /*.and the slots that matter are replaced. */
    gEnv.GetVersion = h_get_version;
    gEnv.FindClass = h_find_class;
    gEnv.GetObjectClass = h_get_object_class;
    gEnv.RegisterNatives = h_register_natives;
    gEnv.GetFieldID = h_get_field_id;
    gEnv.GetMethodID = h_get_method_id;
    gEnv.NewStringUTF = h_new_string_utf;
    gEnv.GetStringUTFChars = h_get_string_utf_chars;
    gEnv.ReleaseStringUTFChars = h_release_string_utf_chars;
    gEnv.GetStringLength = h_get_string_length;
    gEnv.GetStringUTFLength = h_get_string_utf_length;
    gEnv.NewByteArray = h_new_byte_array;
    gEnv.GetArrayLength = h_get_array_length;
    gEnv.GetByteArrayElements = h_get_byte_array_elements;
    gEnv.ReleaseByteArrayElements = h_release_byte_array_elements;
    gEnv.GetByteArrayRegion = h_get_byte_array_region;
    gEnv.SetByteArrayRegion = h_set_byte_array_region;
    gEnv.NewObjectArray = h_new_object_array;
    gEnv.GetObjectArrayElement = h_get_object_array_element;
    gEnv.SetObjectArrayElement = h_set_object_array_element;
    gEnv.ThrowNew = h_throw_new;
    gEnv.ExceptionCheck = h_exception_check;
    gEnv.ExceptionOccurred = h_exception_occurred;
    gEnv.ExceptionClear = h_exception_clear;
    gEnv.GetJavaVM = h_get_java_vm;
}

/* ------------------------------------------------------------------ */
/* Captured entry points, so one can be called by name. */

typedef struct {
    char name[128];
    char sig[256];
    void *fn;
} Entry;

static Entry gEntries[32];
static int gEntryCount;

static Entry *find_entry(const char *name)
{
    int i;
    for (i = 0; i < gEntryCount; i++)
        if (strcmp(gEntries[i].name, name) == 0) return &gEntries[i];

    return NULL;
}

/* RegisterNatives is the only place the table comes from, so it is parsed
 * here too, after the first JNI_OnLoad printed it. */
static jint h_register_natives_capture(JNIEnv *e, jclass cls,
                                      const JNINativeMethod *methods, jint n)
{
    int i;
    h_register_natives(e, cls, methods, n);
    for (i = 0; i < n && gEntryCount < 32; i++) {
        snprintf(gEntries[gEntryCount].name, sizeof gEntries[0].name, "%s",
                 methods[i].name);
        snprintf(gEntries[gEntryCount].sig, sizeof gEntries[0].sig, "%s",
                 methods[i].signature);
        gEntries[gEntryCount].fn = (void *)methods[i].fnPtr;
        gEntryCount++;
    }

    return JNI_OK;
}

/* ------------------------------------------------------------------ */
/* Files and arrays */

static jbyteArray read_file(const char *path, int *lenOut)
{
    FILE *f = fopen(path, "rb");
    long size;
    jbyteArray a;

    if (!f) {
        fprintf(stderr, "cannot open %s\n", path);
        return NULL;
    }
    fseek(f, 0, SEEK_END);
    size = ftell(f);
    fseek(f, 0, SEEK_SET);

    a = h_new_byte_array((JNIEnv *)&gEnvValue, (jsize)size);
    if (fread(((FakeArray *)a)->data, 1, (size_t)size, f) != (size_t)size)
        fprintf(stderr, "short read on %s\n", path);
    fclose(f);

    if (lenOut) *lenOut = (int)size;

    return a;
}

static void dump_returned_array(jarray result)
{
    FakeObjArray *a = (FakeObjArray *)result;
    int i;

    if (!a) {
        printf("returned null\n");
        return;
    }
    if (a->magic != MAGIC_OBJARRAY) {
        printf("returned something that is not a String[] (magic %x)\n", a->magic);
        return;
    }

    printf("returned %d item(s)\n", (int)a->len);
    for (i = 0; i < a->len && i < 16; i++) {
        FakeString *s = (FakeString *)a->elems[i];
        printf("  [%d] %s\n", i,
               (s && s->magic == MAGIC_STRING) ? s->utf : "(not a string)");
    }
}

/* ------------------------------------------------------------------ */
/* Raw blobs, for the calls that take a pointer instead of a byte array. */

static void *read_blob(const char *path, long *lenOut)
{
    FILE *f = fopen(path, "rb");
    long size;
    void *buf;

    if (!f) {
        fprintf(stderr, "cannot open %s\n", path);
        return NULL;
    }
    fseek(f, 0, SEEK_END);
    size = ftell(f);
    fseek(f, 0, SEEK_SET);

    buf = malloc(size > 0 ? (size_t)size : 1);
    if (fread(buf, 1, (size_t)size, f) != (size_t)size)
        fprintf(stderr, "short read on %s\n", path);
    fclose(f);

    if (lenOut) *lenOut = size;

    return buf;
}

static void dump_head(const char *label, const unsigned char *p, long len)
{
    long i;

    printf("%s: %ld byte(s)\n  hex:", label, len);
    for (i = 0; i < len && i < 24; i++) printf(" %02x", p[i]);
    printf("\n  text:");
    for (i = 0; i < len && i < 96; i++)
        putchar((p[i] >= 32 && p[i] < 127) ? p[i] : '.');
    printf("\n");
}

/* WhatsAppOpenboxArchive.openNative(int, long, long): a container over a
 * buffer that can say how many files it holds and where each one starts. */
static void run_obi(Entry *open, Entry *count, Entry *fptr, Entry *fsize,
                    void *buf, long len, int mode)
{
    jlong h;
    jint n;
    int i;

    if (!open || !count || !fptr || !fsize) {
        printf("obi: not every entry point was registered\n");
        return;
    }

    h = ((jlong (*)(JNIEnv *, jclass, jint, jlong, jlong))open->fn)(
        (JNIEnv *)&gEnvValue, (jclass)0x1, mode, (jlong)(long)buf, (jlong)len);
    printf("openNative(mode=%d, ptr=%p, len=%ld) -> %p\n", mode, buf, len,
           (void *)(long)h);
    if (!h) return;

    n = ((jint (*)(JNIEnv *, jclass, jlong))count->fn)(
        (JNIEnv *)&gEnvValue, (jclass)0x1, h);
    printf("getFileCountNative -> %d\n", (int)n);

    for (i = 0; i < n && i < 48; i++) {
        jlong ptr = ((jlong (*)(JNIEnv *, jclass, jlong, jint))fptr->fn)(
            (JNIEnv *)&gEnvValue, (jclass)0x1, h, i);
        jint size = ((jint (*)(JNIEnv *, jclass, jlong, jint))fsize->fn)(
            (JNIEnv *)&gEnvValue, (jclass)0x1, h, i);
        long offset = ptr ? (long)((const unsigned char *)(long)ptr -
                                   (const unsigned char *)buf) : -1;
        char label[64];

        printf("  file %d: ptr=%p size=%d offset=%ld\n", i, (void *)(long)ptr,
               (int)size, offset);
        if (ptr && size > 0) {
            snprintf(label, sizeof label, "  file %d", i);
            dump_head(label, (const unsigned char *)(long)ptr, size);
        }
    }
}

/* WhatsAppObiInputStream: opened from bytes, then read out in chunks. */
static void read_all(const char *label, Entry *read, jlong h, long limit)
{
    const int chunk = 65536;
    long total = 0;
    int rounds = 0;

    while (total < limit && rounds < 64) {
        jbyteArray a = h_new_byte_array((JNIEnv *)&gEnvValue, chunk);
        jint got = ((jint (*)(JNIEnv *, jclass, jlong, jbyteArray, jint, jint))read->fn)(
            (JNIEnv *)&gEnvValue, (jclass)0x1, h, a, 0, chunk);

        printf("%s: read %d byte(s)\n", label, (int)got);
        if (got <= 0) break;
        if (rounds == 0) dump_head(label, (unsigned char *)((FakeArray *)a)->data, got);
        total += got;
        rounds++;
    }
    printf("%s: %ld byte(s) in total\n", label, total);
}

static void run_from_bytes(Entry *open, Entry *read, void *buf, long len, int mode,
                           int archive, int swapped)
{
    jlong h;

    if (!open || !read) {
        printf("bytes: not every entry point was registered\n");
        return;
    }

    if (archive) {
        /* The first run crashed at the address 0x2fa0e, which is 195086: the
         * length. So the two longs are (size, pointer), not (pointer, size),
         * and the crash was the library reading the number as an address. */
        if (swapped) {
            h = ((jlong (*)(JNIEnv *, jclass, jlong, jlong, jint))open->fn)(
                (JNIEnv *)&gEnvValue, (jclass)0x1, (jlong)len, (jlong)(long)buf, mode);
            printf("openObArchiveBytesNative(len=%ld, ptr=%p, mode=%d) -> %p\n", len,
                   buf, mode, (void *)(long)h);
        } else {
            h = ((jlong (*)(JNIEnv *, jclass, jlong, jlong, jint))open->fn)(
                (JNIEnv *)&gEnvValue, (jclass)0x1, (jlong)(long)buf, (jlong)len, mode);
            printf("openObArchiveBytesNative(ptr=%p, len=%ld, mode=%d) -> %p\n", buf,
                   len, mode, (void *)(long)h);
        }
    } else {
        h = ((jlong (*)(JNIEnv *, jclass, jlong, jint))open->fn)(
            (JNIEnv *)&gEnvValue, (jclass)0x1, (jlong)(long)buf, (jint)len);
        printf("openRawBytesNative(ptr=%p, len=%ld) -> %p\n", buf, len,
               (void *)(long)h);
    }
    if (!h) return;

    read_all(archive ? "ob" : "raw", read, h, len + 4 * 1024 * 1024);
}

int main(int argc, char **argv)
{
    const char *lib = NULL, *fn = NULL, *spo = NULL, *payload = NULL;
    const char *out = NULL, *ref = NULL;
    const char *scenario = NULL, *bufPath = NULL;
    int mode = 0;
    void *handle;
    jint (*on_load)(JavaVM *, void *);
    int i;

    for (i = 1; i + 1 < argc; i += 2) {
        if (strcmp(argv[i], "--lib") == 0) lib = argv[i + 1];
        else if (strcmp(argv[i], "--fn") == 0) fn = argv[i + 1];
        else if (strcmp(argv[i], "--spo") == 0) spo = argv[i + 1];
        else if (strcmp(argv[i], "--payload") == 0) payload = argv[i + 1];
        else if (strcmp(argv[i], "--out") == 0) out = argv[i + 1];
        else if (strcmp(argv[i], "--ref") == 0) ref = argv[i + 1];
        else if (strcmp(argv[i], "--scenario") == 0) scenario = argv[i + 1];
        else if (strcmp(argv[i], "--buf") == 0) bufPath = argv[i + 1];
        else if (strcmp(argv[i], "--mode") == 0) mode = atoi(argv[i + 1]);
    }

    if (!lib) {
        fprintf(stderr, "usage: harness --lib PATH [--fn NAME] [--spo PATH] "
                        "[--payload PATH]\n");
        return 2;
    }

    build_env();
    gEnvValue = (JNIEnv)&gEnv;

    gVmTable.reserved0 = NULL;
    gVmTable.reserved1 = NULL;
    gVmTable.reserved2 = NULL;
    gVmTable.DestroyJavaVM = vm_destroy;
    gVmTable.AttachCurrentThread = vm_attach;
    gVmTable.DetachCurrentThread = vm_detach;
    gVmTable.GetEnv = vm_get_env;
    gVmTable.AttachCurrentThreadAsDaemon = vm_attach;
    gVmValue = (JavaVM)&gVmTable;

    /* Capture as well as print: the table is the point of the run. */
    gEnv.RegisterNatives = h_register_natives_capture;

    handle = dlopen(lib, RTLD_NOW);
    if (!handle) {
        fprintf(stderr, "dlopen failed: %s\n", dlerror());
        return 1;
    }
    printf("loaded %s\n", lib);

    on_load = (jint (*)(JavaVM *, void *))dlsym(handle, "JNI_OnLoad");
    if (!on_load) {
        fprintf(stderr, "no JNI_OnLoad: %s\n", dlerror());
        return 1;
    }

    printf("JNI_OnLoad -> %d\n", (int)on_load((JavaVM *)&gVmValue, NULL));
    printf("captured %d native method(s)\n", gEntryCount);

    if (scenario && bufPath) {
        long len = 0;
        void *blob = read_blob(bufPath, &len);

        if (!blob) return 1;
        printf("blob %s: %ld byte(s) at %p\n", bufPath, len, blob);

        if (strcmp(scenario, "obi") == 0) {
            run_obi(find_entry("openNative"), find_entry("getFileCountNative"),
                    find_entry("getFilePtrNative"), find_entry("getFileSizeNative"),
                    blob, len, mode);
        } else if (strcmp(scenario, "obib") == 0) {
            run_from_bytes(find_entry("openObArchiveBytesNative"),
                           find_entry("readNative"), blob, len, mode, 1, 0);
        } else if (strcmp(scenario, "obib2") == 0) {
            run_from_bytes(find_entry("openObArchiveBytesNative"),
                           find_entry("readNative"), blob, len, mode, 1, 1);
        } else if (strcmp(scenario, "raw") == 0) {
            run_from_bytes(find_entry("openRawBytesNative"),
                           find_entry("readNative"), blob, len, mode, 0, 0);
        } else {
            printf("unknown scenario %s\n", scenario);
        }

        return 0;
    }

    if (fn) {
        Entry *e = find_entry(fn);
        if (!e) {
            fprintf(stderr, "no captured method named %s\n", fn);
            return 1;
        }
        printf("calling %s %s\n", e->name, e->sig);

        if (strcmp(fn, "testDecompressorLibraryUsable") == 0) {
            jboolean ok = ((jboolean (*)(JNIEnv *, jclass))e->fn)(
                (JNIEnv *)&gEnvValue, (jclass)0x1);
            printf("%s -> %d\n", fn, (int)ok);
        } else if (strcmp(e->sig, "()I") == 0) {
            jint v = ((jint (*)(JNIEnv *, jclass))e->fn)((JNIEnv *)&gEnvValue,
                                                        (jclass)0x1);
            printf("%s -> %d\n", fn, (int)v);
        } else if (strcmp(fn, "decompress") == 0) {
            /* (AssetManager, assetName, outDir, refDir): the manager is the
             * stub's business, and it opens the name inside ASSET_DIR. */
            jstring name = h_new_string_utf((JNIEnv *)&gEnvValue, spo ? spo : "");
            jstring o = h_new_string_utf((JNIEnv *)&gEnvValue, out ? out : "");
            jstring rf = h_new_string_utf((JNIEnv *)&gEnvValue, ref ? ref : "");
            jarray r;

            printf("  asset = %s\n  out   = %s\n  ref   = %s\n",
                   spo ? spo : "", out ? out : "", ref ? ref : "");
            r = ((jarray (*)(JNIEnv *, jclass, jobject, jstring, jstring,
                             jstring))e->fn)((JNIEnv *)&gEnvValue, (jclass)0x1,
                                            NULL, name, o, rf);
            dump_returned_array(r);
        } else if (strstr(e->sig, "Ljava/lang/String;")) {
            /* These take file paths, not bytes: the archive in, a directory for
             * the output, and the two extra paths the Java side fills in. */
            const char *paths[4];
            jstring str[4];
            jarray r;
            int k;

            paths[0] = spo;
            paths[1] = out;
            paths[2] = ref;
            paths[3] = ref;
            for (k = 0; k < 4; k++) {
                str[k] = h_new_string_utf((JNIEnv *)&gEnvValue,
                                          paths[k] ? paths[k] : "");
                printf("  arg %d = %s\n", k, paths[k] ? paths[k] : "");
            }

            r = ((jarray (*)(JNIEnv *, jclass, jstring, jstring, jstring,
                             jstring))e->fn)((JNIEnv *)&gEnvValue, (jclass)0x1,
                                            str[0], str[1], str[2], str[3]);
            dump_returned_array(r);
        } else if (spo && strstr(e->sig, "[B")) {
            jbyteArray a = read_file(spo, NULL);
            jbyteArray p = payload ? read_file(payload, NULL) : NULL;
            jarray r = ((jarray (*)(JNIEnv *, jclass, jbyteArray, jbyteArray,
                                    jbyteArray, jobject))e->fn)(
                (JNIEnv *)&gEnvValue, (jclass)0x1, a, p, NULL, NULL);
            dump_returned_array(r);
        } else {
            printf("no argument recipe for %s\n", fn);
        }
    }

    printf("done, %d arrays, %d strings, %d object arrays\n",
           g_arrayCount, g_stringCount, g_objectCount);

    return 0;
}
