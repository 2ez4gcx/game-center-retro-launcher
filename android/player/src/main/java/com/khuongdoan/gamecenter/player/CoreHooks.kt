package com.khuongdoan.gamecenter.player

/** Việc phải làm với riêng một số core trước khi LibretroDroid nạp chúng (xem src/main/cpp/core_proxy.c). */
internal object CoreHooks {
    private const val LIBRARY = "gcplayer"

    init {
        System.loadLibrary(LIBRARY)
    }

    /**
     * Chuẩn bị core. Trả về true nếu core phải chạy qua lớp bọc (Play!, PS2: cần JavaVM và cần cách ly trạng thái
     * OpenGL với LibretroDroid); khi đó cho LibretroDroid nạp [proxyPath] thay cho file core.
     */
    @JvmStatic
    external fun prepare(corePath: String): Boolean

    /** Đường dẫn libgcplayer.so, bản thân nó là một core libretro chuyển tiếp sang core thật. */
    fun proxyPath(nativeLibraryDir: String) = "$nativeLibraryDir/${System.mapLibraryName(LIBRARY)}"
}
