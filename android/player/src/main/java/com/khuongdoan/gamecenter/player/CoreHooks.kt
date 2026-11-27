package com.khuongdoan.gamecenter.player

/** Việc phải làm với riêng một số core trước khi LibretroDroid nạp chúng (xem src/main/cpp/javavm_hook.c). */
internal object CoreHooks {
    init {
        System.loadLibrary("gcplayer")
    }

    /** Trao JavaVM cho core cần nó (Play!). Trả về false nếu core không cần hoặc không mở được. */
    @JvmStatic
    external fun giveJavaVm(corePath: String): Boolean
}
