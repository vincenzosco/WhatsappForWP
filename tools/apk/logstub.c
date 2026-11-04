/* A liblog that prints instead of logging.
 *
 * The library explains every failure through __android_log_print, which on a
 * phone goes to logd. There is no logd here, so the message would vanish and
 * the only symptom would be a null return. This stub is put ahead of the real
 * liblog on the library path and writes the same text to stderr.
 */
#include <stdio.h>
#include <stdarg.h>
#include <stdlib.h>

static void emit(int prio, const char *tag, const char *fmt, va_list ap)
{
    fprintf(stderr, "[liblog prio=%d tag=%s] ", prio, tag ? tag : "");
    vfprintf(stderr, fmt, ap);
    fputc('\n', stderr);
    fflush(stderr);
}

int __android_log_print(int prio, const char *tag, const char *fmt, ...)
{
    va_list ap;
    va_start(ap, fmt);
    emit(prio, tag, fmt, ap);
    va_end(ap);

    return 0;
}

int __android_log_vprint(int prio, const char *tag, const char *fmt, va_list ap)
{
    emit(prio, tag, fmt, ap);

    return 0;
}

int __android_log_write(int prio, const char *tag, const char *text)
{
    fprintf(stderr, "[liblog prio=%d tag=%s] %s\n", prio, tag ? tag : "",
            text ? text : "");
    fflush(stderr);

    return 0;
}

int __android_log_buf_write(int buf, int prio, const char *tag, const char *text)
{
    (void)buf;

    return __android_log_write(prio, tag, text);
}

int __android_log_buf_print(int buf, int prio, const char *tag, const char *fmt, ...)
{
    va_list ap;
    (void)buf;
    va_start(ap, fmt);
    emit(prio, tag, fmt, ap);
    va_end(ap);

    return 0;
}

void __android_log_assert(const char *cond, const char *tag, const char *fmt, ...)
{
    va_list ap;

    fprintf(stderr, "[liblog assert] %s: ", cond ? cond : "");
    va_start(ap, fmt);
    vfprintf(stderr, fmt, ap);
    va_end(ap);
    fputc('\n', stderr);
    fflush(stderr);
    abort();
}
