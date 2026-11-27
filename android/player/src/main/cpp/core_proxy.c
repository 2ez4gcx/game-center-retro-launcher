/*
 * Core "bọc": libgcplayer.so tự nó cũng là một core libretro, mọi hàm retro_* chuyển tiếp sang core thật.
 *
 * Dùng cho core vẽ bằng OpenGL mà cần ngữ cảnh GL riêng (Play!, PS2). Core đó gọi
 * RETRO_ENVIRONMENT_SET_HW_SHARED_CONTEXT để frontend cấp cho nó một ngữ cảnh tách biệt (RetroArch làm vậy), nên nó
 * không dọn trạng thái GL sau khi vẽ. LibretroDroid không hỗ trợ lệnh đó: core và frontend dùng chung một ngữ cảnh.
 * Khi core vẽ xong còn để lại vertex array, buffer, scissor, blend... của nó; LibretroDroid vẽ khung hình ra màn hình
 * bằng mảng đỉnh phía client (glVertexAttribPointer với con trỏ thật) nên bị GL hiểu là offset trong buffer của core
 * và không vẽ được gì: game chạy nhưng màn hình đen.
 *
 * Lớp bọc chen vào đúng một chỗ: hàm video_refresh. Trước khi giao khung hình cho frontend, nó cất trạng thái GL của
 * core, đưa GL về trạng thái mặc định mà LibretroDroid cần, gọi frontend, rồi trả lại trạng thái cho core.
 */
#include <jni.h>
#include <dlfcn.h>
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include <string.h>
#include <GLES3/gl3.h>
#include <android/log.h>

#define TAG "GameCenterPlayer"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, TAG, __VA_ARGS__)
#define LOGW(...) __android_log_print(ANDROID_LOG_WARN, TAG, __VA_ARGS__)

/* Khung hình nằm sẵn trong framebuffer của frontend (libretro.h: RETRO_HW_FRAME_BUFFER_VALID) */
#define HW_FRAME_BUFFER_VALID ((void *)-1)

typedef void (*video_refresh_t)(const void *data, unsigned width, unsigned height, size_t pitch);

/* Framework::CJavaVM::SetJavaVM(JavaVM*) của Play! (tên đã mangle theo Itanium C++ ABI) */
static const char SET_JAVA_VM[] = "_ZN9Framework7CJavaVM9SetJavaVMEP7_JavaVM";

static void *g_core;
static video_refresh_t g_frontend_video;
static unsigned g_frames;

/* ------------------------------------------------------------------ Chuẩn bị core (gọi từ Kotlin) */

/*
 * Mở core trước khi LibretroDroid nạp. Core cần JavaVM (Play!: luồng giả lập gọi
 * Framework::CJavaVM::AttachCurrentThread; frontend libretro nạp core bằng dlopen nên JNI_OnLoad không chạy, JavaVM
 * rỗng và core sập) thì trao JavaVM và trả về true: GameActivity sẽ cho LibretroDroid nạp lớp bọc này thay cho core.
 * Core khác trả về false và được nạp thẳng như cũ.
 */
JNIEXPORT jboolean JNICALL
Java_com_khuongdoan_gamecenter_player_CoreHooks_prepare(JNIEnv *env, jclass clazz, jstring corePath)
{
    (void)clazz;
    JavaVM *vm = NULL;
    if ((*env)->GetJavaVM(env, &vm) != JNI_OK || vm == NULL) return JNI_FALSE;

    const char *path = (*env)->GetStringUTFChars(env, corePath, NULL);
    if (path == NULL) return JNI_FALSE;
    void *core = dlopen(path, RTLD_NOW | RTLD_LOCAL);
    if (core == NULL) LOGW("dlopen %s: %s", path, dlerror());
    (*env)->ReleaseStringUTFChars(env, corePath, path);
    if (core == NULL) return JNI_FALSE;

    void (*set_java_vm)(JavaVM *) = (void (*)(JavaVM *))dlsym(core, SET_JAVA_VM);
    if (set_java_vm == NULL) {
        dlclose(core);
        return JNI_FALSE;
    }
    set_java_vm(vm);
    /* Không đóng handle: core phải ở lại trong bộ nhớ suốt đời tiến trình */
    g_core = core;
    g_frontend_video = NULL;
    g_frames = 0;
    LOGI("Da trao JavaVM cho core, chay qua lop boc GL");
    return JNI_TRUE;
}

/* ------------------------------------------------------------------ Cách ly trạng thái GL */

static const GLenum CAPS[] = { GL_SCISSOR_TEST, GL_BLEND, GL_CULL_FACE, GL_STENCIL_TEST, GL_DEPTH_TEST, GL_DITHER };
#define CAP_COUNT (sizeof(CAPS) / sizeof(CAPS[0]))

static void proxy_video_refresh(const void *data, unsigned width, unsigned height, size_t pitch)
{
    if (g_frontend_video == NULL) return;
    if (data != HW_FRAME_BUFFER_VALID) {
        g_frontend_video(data, width, height, pitch);
        return;
    }

    GLint vao, array_buffer, program, draw_fbo, read_fbo, active_texture, viewport[4];
    GLboolean color_mask[4], caps[CAP_COUNT];
    glGetIntegerv(GL_VERTEX_ARRAY_BINDING, &vao);
    glGetIntegerv(GL_ARRAY_BUFFER_BINDING, &array_buffer);
    glGetIntegerv(GL_CURRENT_PROGRAM, &program);
    glGetIntegerv(GL_DRAW_FRAMEBUFFER_BINDING, &draw_fbo);
    glGetIntegerv(GL_READ_FRAMEBUFFER_BINDING, &read_fbo);
    glGetIntegerv(GL_ACTIVE_TEXTURE, &active_texture);
    glGetIntegerv(GL_VIEWPORT, viewport);
    glGetBooleanv(GL_COLOR_WRITEMASK, color_mask);
    for (size_t i = 0; i < CAP_COUNT; i++) caps[i] = glIsEnabled(CAPS[i]);

    if (g_frames < 3) {
        LOGI("Khung hinh %u: %ux%u, vao=%d buffer=%d program=%d fbo=%d glError=0x%x",
             g_frames, width, height, vao, array_buffer, program, draw_fbo, glGetError());
    }
    g_frames++;

    glBindVertexArray(0);
    glBindBuffer(GL_ARRAY_BUFFER, 0);
    glUseProgram(0);
    for (size_t i = 0; i < CAP_COUNT; i++) glDisable(CAPS[i]);
    glColorMask(GL_TRUE, GL_TRUE, GL_TRUE, GL_TRUE);
    glActiveTexture(GL_TEXTURE0);

    g_frontend_video(data, width, height, pitch);

    for (size_t i = 0; i < CAP_COUNT; i++) {
        if (caps[i]) glEnable(CAPS[i]); else glDisable(CAPS[i]);
    }
    glColorMask(color_mask[0], color_mask[1], color_mask[2], color_mask[3]);
    glViewport(viewport[0], viewport[1], viewport[2], viewport[3]);
    glBindFramebuffer(GL_DRAW_FRAMEBUFFER, (GLuint)draw_fbo);
    glBindFramebuffer(GL_READ_FRAMEBUFFER, (GLuint)read_fbo);
    glUseProgram((GLuint)program);
    glBindVertexArray((GLuint)vao);
    glBindBuffer(GL_ARRAY_BUFFER, (GLuint)array_buffer);
    glActiveTexture((GLenum)active_texture);
}

/* ------------------------------------------------------------------ Các hàm libretro chuyển tiếp sang core thật */

static void *sym(const char *name)
{
    void *p = g_core != NULL ? dlsym(g_core, name) : NULL;
    if (p == NULL) LOGW("Lop boc: khong thay %s trong core", name);
    return p;
}

#define EXPORT __attribute__((visibility("default")))

#define FORWARD_VOID(name, params, args) \
    EXPORT void name params { void (*f) params = (void (*) params)sym(#name); if (f) f args; }
#define FORWARD(ret, name, params, args, fallback) \
    EXPORT ret name params { ret (*f) params = (ret (*) params)sym(#name); return f ? f args : (fallback); }

struct retro_system_info;
struct retro_system_av_info;
struct retro_game_info;

EXPORT void retro_set_video_refresh(video_refresh_t cb)
{
    g_frontend_video = cb;
    void (*f)(video_refresh_t) = (void (*)(video_refresh_t))sym("retro_set_video_refresh");
    if (f) f(proxy_video_refresh);
}

FORWARD_VOID(retro_set_environment, (void *cb), (cb))
FORWARD_VOID(retro_set_audio_sample, (void *cb), (cb))
FORWARD_VOID(retro_set_audio_sample_batch, (void *cb), (cb))
FORWARD_VOID(retro_set_input_poll, (void *cb), (cb))
FORWARD_VOID(retro_set_input_state, (void *cb), (cb))
FORWARD_VOID(retro_init, (void), ())
FORWARD_VOID(retro_deinit, (void), ())
FORWARD(unsigned, retro_api_version, (void), (), 1)
FORWARD_VOID(retro_get_system_info, (struct retro_system_info *info), (info))
FORWARD_VOID(retro_get_system_av_info, (struct retro_system_av_info *info), (info))
FORWARD_VOID(retro_set_controller_port_device, (unsigned port, unsigned device), (port, device))
FORWARD_VOID(retro_reset, (void), ())
FORWARD_VOID(retro_run, (void), ())
FORWARD(size_t, retro_serialize_size, (void), (), 0)
FORWARD(bool, retro_serialize, (void *data, size_t size), (data, size), false)
FORWARD(bool, retro_unserialize, (const void *data, size_t size), (data, size), false)
FORWARD_VOID(retro_cheat_reset, (void), ())
FORWARD_VOID(retro_cheat_set, (unsigned index, bool enabled, const char *code), (index, enabled, code))
FORWARD(bool, retro_load_game, (const struct retro_game_info *game), (game), false)
FORWARD(bool, retro_load_game_special, (unsigned type, const struct retro_game_info *info, size_t num), (type, info, num), false)
FORWARD_VOID(retro_unload_game, (void), ())
FORWARD(unsigned, retro_get_region, (void), (), 0)
FORWARD(void *, retro_get_memory_data, (unsigned id), (id), NULL)
FORWARD(size_t, retro_get_memory_size, (unsigned id), (id), 0)
