/* The asset API, over plain files.
 *
 * `decompress` takes an AssetManager and an asset name, so the archive is
 * supposed to sit inside the APK. Here it sits on disk, and this file makes
 * the bionic asset calls answer from ASSET_DIR instead: what the library opens
 * as an asset is the file of that name in that directory.
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <fcntl.h>
#include <unistd.h>
#include <dirent.h>
#include <sys/stat.h>
#include <sys/types.h>

#define AASSET_MODE_BUFFER 3

typedef struct {
    unsigned char *data;
    size_t len;
    size_t pos;
    int fd;
} FakeAsset;

typedef struct {
    int unused;
} FakeManager;

typedef struct {
    char **names;
    int count;
    int index;
} FakeAssetDir;

static FakeManager gManager;

static char *join(const char *name)
{
    const char *dir = getenv("ASSET_DIR");
    char *p;

    if (!dir) dir = ".";
    p = (char *)malloc(strlen(dir) + strlen(name) + 2);
    sprintf(p, "%s/%s", dir, name);

    return p;
}

void *AAssetManager_fromJava(void *env, void *obj)
{
    (void)env; (void)obj;

    return &gManager;
}

void *AAssetManager_open(void *mgr, const char *filename, int mode)
{
    (void)mgr;
    char *path = join(filename ? filename : "");
    FakeAsset *a = (FakeAsset *)calloc(1, sizeof(FakeAsset));
    struct stat st;
    FILE *f;

    (void)mode;
    a->fd = open(path, O_RDONLY);
    if (a->fd < 0) {
        fprintf(stderr, "[asset] %s: not found\n", path);
        free(path);
        free(a);
        return NULL;
    }
    if (fstat(a->fd, &st) == 0) a->len = (size_t)st.st_size;

    /* Read it whole: an asset here is a few hundred kilobytes. */
    a->data = (unsigned char *)malloc(a->len ? a->len : 1);
    f = fdopen(dup(a->fd), "rb");
    if (f) {
        if (fread(a->data, 1, a->len, f) != a->len)
            fprintf(stderr, "[asset] short read on %s\n", path);
        fclose(f);
    }
    fprintf(stderr, "[asset] opened %s (%zu bytes)\n", path, a->len);
    free(path);

    return a;
}

int AAsset_read(void *asset, void *buf, size_t count)
{
    FakeAsset *a = (FakeAsset *)asset;
    size_t left = a->len - a->pos;
    size_t n = count < left ? count : left;

    memcpy(buf, a->data + a->pos, n);
    a->pos += n;

    return (int)n;
}

long AAsset_seek(void *asset, long offset, int whence)
{
    FakeAsset *a = (FakeAsset *)asset;
    long base = whence == SEEK_SET ? 0 : (whence == SEEK_CUR ? (long)a->pos
                                                            : (long)a->len);
    long pos = base + offset;

    if (pos < 0 || (size_t)pos > a->len) return -1;
    a->pos = (size_t)pos;

    return pos;
}

long long AAsset_seek64(void *asset, long long offset, int whence)
{
    return (long long)AAsset_seek(asset, (long)offset, whence);
}

long AAsset_getLength(void *asset)
{
    return (long)((FakeAsset *)asset)->len;
}

long long AAsset_getLength64(void *asset)
{
    return (long long)((FakeAsset *)asset)->len;
}

long AAsset_getRemainingLength(void *asset)
{
    FakeAsset *a = (FakeAsset *)asset;

    return (long)(a->len - a->pos);
}

long long AAsset_getRemainingLength64(void *asset)
{
    FakeAsset *a = (FakeAsset *)asset;

    return (long long)(a->len - a->pos);
}

void *AAsset_getBuffer(void *asset)
{
    return ((FakeAsset *)asset)->data;
}

int AAsset_isAllocated(void *asset)
{
    (void)asset;

    return 0;
}

int AAsset_openFileDescriptor(void *asset, long *outStart, long *outLength)
{
    FakeAsset *a = (FakeAsset *)asset;

    if (outStart) *outStart = 0;
    if (outLength) *outLength = (long)a->len;

    return dup(a->fd);
}

int AAsset_openFileDescriptor64(void *asset, long long *outStart,
                                long long *outLength)
{
    FakeAsset *a = (FakeAsset *)asset;

    if (outStart) *outStart = 0;
    if (outLength) *outLength = (long long)a->len;

    return dup(a->fd);
}

void AAsset_close(void *asset)
{
    FakeAsset *a = (FakeAsset *)asset;

    if (!a) return;
    close(a->fd);
    free(a->data);
    free(a);
}

/* Directories, for the reference directory the library walks. */
void *AAssetManager_openDir(void *mgr, const char *dirname)
{
    (void)mgr;
    char *path = join(dirname ? dirname : "");
    DIR *d = opendir(path);
    FakeAssetDir *dir = (FakeAssetDir *)calloc(1, sizeof(FakeAssetDir));
    struct dirent *e;
    int cap = 16;

    free(path);
    if (!d) {
        free(dir);
        return NULL;
    }

    dir->names = (char **)calloc((size_t)cap, sizeof(char *));
    while ((e = readdir(d)) != NULL) {
        if (strcmp(e->d_name, ".") == 0 || strcmp(e->d_name, "..") == 0) continue;
        if (dir->count == cap) {
            cap *= 2;
            dir->names = (char **)realloc(dir->names, (size_t)cap * sizeof(char *));
        }
        dir->names[dir->count] = strdup(e->d_name);
        dir->count++;
    }
    closedir(d);

    return dir;
}

const char *AAssetDir_getNextFileName(void *assetDir)
{
    FakeAssetDir *d = (FakeAssetDir *)assetDir;

    if (!d || d->index >= d->count) return NULL;

    return d->names[d->index++];
}

void AAssetDir_rewind(void *assetDir)
{
    FakeAssetDir *d = (FakeAssetDir *)assetDir;

    if (d) d->index = 0;
}

void AAssetDir_close(void *assetDir)
{
    FakeAssetDir *d = (FakeAssetDir *)assetDir;
    int i;

    if (!d) return;
    for (i = 0; i < d->count; i++) free(d->names[i]);
    free(d->names);
    free(d);
}
