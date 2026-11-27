/*
 * Một số core libretro bản Android cần con trỏ JavaVM để gắn luồng của nó vào máy ảo Java (Play!, PS2: luồng
 * giả lập gọi Framework::CJavaVM::AttachCurrentThread). Bình thường core nhận JavaVM qua JNI_OnLoad, nhưng frontend
 * libretro nạp core bằng dlopen nên JNI_OnLoad (nếu có) không được gọi, JavaVM rỗng và core sập ngay khi chạy.
 *
 * Hàm này mở core trước (frontend mở lại sau đó sẽ nhận đúng bản đang nạp), tìm hàm đặt JavaVM mà core xuất ra
 * và gọi nó. Core không có hàm đó thì không làm gì. Không đóng handle: core phải ở lại trong bộ nhớ.
 */
#include <jni.h>
#include <dlfcn.h>
#include <android/log.h>

#define TAG "GameCenterPlayer"

/* Framework::CJavaVM::SetJavaVM(JavaVM*) của Play! (tên đã mangle theo Itanium C++ ABI) */
static const char *const SET_JAVA_VM_SYMBOLS[] = {
    "_ZN9Framework7CJavaVM9SetJavaVMEP7_JavaVM",
};

JNIEXPORT jboolean JNICALL
Java_com_khuongdoan_gamecenter_player_CoreHooks_giveJavaVm(JNIEnv *env, jclass clazz, jstring corePath)
{
    (void)clazz;
    JavaVM *vm = NULL;
    if ((*env)->GetJavaVM(env, &vm) != JNI_OK || vm == NULL) return JNI_FALSE;

    const char *path = (*env)->GetStringUTFChars(env, corePath, NULL);
    if (path == NULL) return JNI_FALSE;
    void *core = dlopen(path, RTLD_NOW | RTLD_LOCAL);
    if (core == NULL)
        __android_log_print(ANDROID_LOG_WARN, TAG, "dlopen %s: %s", path, dlerror());
    (*env)->ReleaseStringUTFChars(env, corePath, path);
    if (core == NULL) return JNI_FALSE;

    for (size_t i = 0; i < sizeof(SET_JAVA_VM_SYMBOLS) / sizeof(SET_JAVA_VM_SYMBOLS[0]); i++) {
        void (*set_java_vm)(JavaVM *) = (void (*)(JavaVM *))dlsym(core, SET_JAVA_VM_SYMBOLS[i]);
        if (set_java_vm != NULL) {
            set_java_vm(vm);
            __android_log_print(ANDROID_LOG_INFO, TAG, "Da trao JavaVM cho core (%s)", SET_JAVA_VM_SYMBOLS[i]);
            return JNI_TRUE;
        }
    }
    dlclose(core);
    return JNI_FALSE;
}
