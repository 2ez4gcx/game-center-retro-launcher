package com.khuongdoan.gamecenter.player

import android.app.Activity
import android.app.AlertDialog
import android.content.Intent
import android.content.res.Configuration
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.util.Log
import android.view.Gravity
import android.view.InputDevice
import android.view.KeyEvent
import android.view.MotionEvent
import android.view.View
import android.view.ViewGroup.LayoutParams.MATCH_PARENT
import android.widget.FrameLayout
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.OnBackPressedCallback
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import androidx.lifecycle.lifecycleScope
import com.swordfish.libretrodroid.GLRetroView
import com.swordfish.libretrodroid.GLRetroViewData
import com.swordfish.libretrodroid.Variable
import kotlinx.coroutines.launch
import java.io.File

/**
 * Màn hình chơi game: chạy một core libretro (file .so trong APK) bằng LibretroDroid.
 *
 * Game Center (phần .NET) quyết định mọi thứ về file: core nào, game nào, save nằm ở đâu, có nạp chỗ đang chơi
 * không. Activity này chỉ chạy game, ghi save trong game (.srm) định kỳ và chụp lại chỗ đang chơi khi thoát.
 * Thoát game phải bấm 2 lần (Back, hoặc START + SELECT trên tay cầm) như bản máy tính.
 */
class GameActivity : ComponentActivity() {

    companion object {
        private const val TAG = "GameCenterPlayer"

        const val EXTRA_CORE = "core"                 // đường dẫn file core (.so)
        const val EXTRA_GAME = "game"                 // đường dẫn file game
        const val EXTRA_SYSTEM_DIR = "systemDir"      // thư mục BIOS
        const val EXTRA_SAVES_DIR = "savesDir"        // thư mục core tự ghi file (thẻ nhớ PS2...)
        const val EXTRA_SRAM_FILE = "sramFile"        // save trong game: <tên game>.srm
        const val EXTRA_STATE_FILE = "stateFile"      // chỗ đang chơi: <tên game>.state.auto
        const val EXTRA_LOAD_STATE = "loadState"      // true: "Chơi tiếp" từ stateFile
        const val EXTRA_SAVE_STATE_ON_EXIT = "saveStateOnExit"
        const val EXTRA_VARIABLES = "variables"       // tùy chọn core, mỗi phần tử "khóa=giá trị"
        const val EXTRA_ENV = "env"                   // biến môi trường cho core, mỗi phần tử "TÊN=giá trị"
        const val EXTRA_ANALOG = "analog"             // true: hiện cần analog ảo (PS2, PSP)
        const val EXTRA_TEXT_EXIT_AGAIN = "textExitAgain"
        const val EXTRA_TITLE = "title"                       // tên game, làm tiêu đề menu
        const val EXTRA_TEXT_MENU_CONTINUE = "textMenuContinue"
        const val EXTRA_TEXT_MENU_DISC = "textMenuDisc"
        const val EXTRA_TEXT_MENU_EXIT = "textMenuExit"
        const val EXTRA_TEXT_DISC = "textDisc"                // "Đĩa {0}"

        /** Kết quả trả về khi core hoặc game không mở được; mã lỗi LibretroDroid nằm trong [EXTRA_ERROR]. */
        const val RESULT_ERROR = Activity.RESULT_FIRST_USER
        const val EXTRA_ERROR = "error"

        private const val SRAM_INTERVAL_MS = 10_000L
        private const val EXIT_CONFIRM_MS = 3_000L
    }

    private lateinit var retroView: GLRetroView
    private lateinit var touchPad: TouchPadView
    private val handler = Handler(Looper.getMainLooper())

    private var sramFile: File? = null
    private var stateFile: File? = null
    private var lastSram: ByteArray? = null
    private var gameStarted = false
    private var pendingLoadState = false
    private var exiting = false
    private var lastExitPress = 0L
    private var startDown = false
    private var selectDown = false
    private var thumbLDown = false
    private var thumbRDown = false
    private var menu: AlertDialog? = null

    private val sramTick = object : Runnable {
        override fun run() {
            saveSram()
            handler.postDelayed(this, SRAM_INTERVAL_MS)
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        val core = intent.getStringExtra(EXTRA_CORE)
        val game = intent.getStringExtra(EXTRA_GAME)
        if (core == null || game == null) {
            finishWithError(GLRetroView.ERROR_GENERIC)
            return
        }
        sramFile = intent.getStringExtra(EXTRA_SRAM_FILE)?.let(::File)
        stateFile = intent.getStringExtra(EXTRA_STATE_FILE)?.let(::File)
        pendingLoadState = intent.getBooleanExtra(EXTRA_LOAD_STATE, false) && stateFile?.isFile == true

        // Một số core đọc đường dẫn từ biến môi trường thay vì hỏi frontend (Play! ghi dữ liệu vào $EXTERNAL_STORAGE,
        // mặc định là gốc bộ nhớ chung, nơi app không được ghi). Chỉ đổi trong tiến trình của app.
        for (pair in intent.getStringArrayExtra(EXTRA_ENV).orEmpty()) {
            val parts = pair.split('=', limit = 2)
            if (parts.size == 2) android.system.Os.setenv(parts[0], parts[1], true)
        }

        // Core cần xử lý riêng (Play!) chạy qua lớp bọc trong libgcplayer.so; core khác nạp thẳng
        val useProxy = try { CoreHooks.prepare(core) } catch (e: Throwable) {
            Log.w(TAG, "Không chạy được CoreHooks", e)
            false
        }

        val data = GLRetroViewData(this).apply {
            coreFilePath = if (useProxy) CoreHooks.proxyPath(applicationInfo.nativeLibraryDir) else core
            gameFilePath = game
            intent.getStringExtra(EXTRA_SYSTEM_DIR)?.let { systemDirectory = it }
            intent.getStringExtra(EXTRA_SAVES_DIR)?.let { savesDirectory = it }
            variables = intent.getStringArrayExtra(EXTRA_VARIABLES).orEmpty()
                .mapNotNull { it.split('=', limit = 2).takeIf { p -> p.size == 2 } }
                .map { Variable(it[0], it[1]) }
                .toTypedArray()
            saveRAMState = sramFile?.takeIf { it.isFile }?.readBytes()
            rumbleEventsEnabled = false
        }
        lastSram = data.saveRAMState

        retroView = GLRetroView(this, data).apply {
            isFocusable = true
            isFocusableInTouchMode = true
        }
        lifecycle.addObserver(retroView)

        touchPad = TouchPadView(
            this,
            analog = intent.getBooleanExtra(EXTRA_ANALOG, false),
            onKey = { keyCode, down ->
                if (keyCode == KeyEvent.KEYCODE_MENU) {
                    if (down) showMenu()
                } else {
                    retroView.sendKeyEvent(if (down) KeyEvent.ACTION_DOWN else KeyEvent.ACTION_UP, keyCode)
                }
            },
            onAnalog = { x, y -> retroView.sendMotionEvent(GLRetroView.MOTION_SOURCE_ANALOG_LEFT, x, y) },
        )
        // Đã cắm tay cầm thì không cần nút cảm ứng
        touchPad.visibility = if (hasGamepad()) View.GONE else View.VISIBLE

        setContentView(FrameLayout(this).apply {
            setBackgroundColor(0xFF000000.toInt())
            addView(retroView, FrameLayout.LayoutParams(MATCH_PARENT, MATCH_PARENT))
            addView(touchPad, FrameLayout.LayoutParams(MATCH_PARENT, MATCH_PARENT))
        })
        retroView.requestFocus()
        hideSystemBars()
        applyOrientation(resources.configuration)

        lifecycleScope.launch {
            retroView.getGLRetroEvents().collect { event ->
                if (event is GLRetroView.GLRetroEvents.FrameRendered && !gameStarted) onGameStarted()
            }
        }
        lifecycleScope.launch {
            retroView.getGLRetroErrors().collect { code ->
                Log.e(TAG, "LibretroDroid báo lỗi $code")
                finishWithError(code)
            }
        }

        onBackPressedDispatcher.addCallback(this, object : OnBackPressedCallback(true) {
            override fun handleOnBackPressed() = requestExit()
        })
    }

    /** Khung hình đầu tiên đã vẽ: core và game đã sẵn sàng, giờ mới nạp được chỗ đang chơi. */
    private fun onGameStarted() {
        gameStarted = true
        if (pendingLoadState) {
            pendingLoadState = false
            try {
                val ok = retroView.unserializeState(stateFile!!.readBytes())
                Log.i(TAG, "Nạp chỗ đang chơi: $ok")
            } catch (e: Exception) {
                Log.e(TAG, "Không nạp được chỗ đang chơi", e)
            }
        }
        handler.postDelayed(sramTick, SRAM_INTERVAL_MS)
    }

    // ---------------------------------------------------------------- Save

    /** Ghi save trong game (SRAM, thẻ nhớ PS1) nếu có thay đổi. Ghi ra file tạm rồi đổi tên để không hỏng file khi bị tắt ngang. */
    private fun saveSram() {
        val file = sramFile ?: return
        if (!gameStarted) return
        try {
            val bytes = retroView.serializeSRAM()
            if (bytes.isEmpty() || bytes.contentEquals(lastSram)) return
            writeAtomic(file, bytes)
            lastSram = bytes
        } catch (e: Exception) {
            Log.e(TAG, "Không ghi được save ${file.name}", e)
        }
    }

    private fun saveState() {
        val file = stateFile ?: return
        if (!gameStarted) return
        try {
            val bytes = retroView.serializeState()
            if (bytes.isNotEmpty()) writeAtomic(file, bytes)
        } catch (e: Exception) {
            Log.e(TAG, "Không chụp được chỗ đang chơi", e)
        }
    }

    private fun writeAtomic(file: File, bytes: ByteArray) {
        file.parentFile?.mkdirs()
        val tmp = File(file.path + ".tmp")
        tmp.writeBytes(bytes)
        if (!tmp.renameTo(file)) {
            file.delete()
            if (!tmp.renameTo(file)) throw java.io.IOException("Không đổi tên được ${tmp.name}")
        }
    }

    // ---------------------------------------------------------------- Menu trong game

    /**
     * Menu khi đang chơi: chơi tiếp, đổi đĩa (game nhiều đĩa), thoát game. Mở bằng nút menu (ba gạch) trên màn hình,
     * hoặc bấm lún cùng lúc 2 cần analog (L3 + R3) / nút giữa của tay cầm.
     */
    private fun showMenu() {
        if (exiting || !gameStarted || menu?.isShowing == true) return
        val disks = try { retroView.getAvailableDisks() } catch (e: Exception) { 0 }
        val labels = ArrayList<String>()
        val actions = ArrayList<() -> Unit>()
        labels += text(EXTRA_TEXT_MENU_CONTINUE, "Chơi tiếp"); actions += {}
        if (disks > 1) {
            labels += text(EXTRA_TEXT_MENU_DISC, "Đổi đĩa"); actions += { showDiscMenu(disks) }
        }
        labels += text(EXTRA_TEXT_MENU_EXIT, "Thoát game"); actions += { exitGame() }
        menu = AlertDialog.Builder(this, android.R.style.Theme_DeviceDefault_Dialog_Alert)
            .setTitle(intent.getStringExtra(EXTRA_TITLE))
            .setItems(labels.toTypedArray()) { _, which -> actions[which]() }
            .setOnDismissListener { hideSystemBars(); retroView.requestFocus() }
            .show()
    }

    private fun showDiscMenu(disks: Int) {
        val current = try { retroView.getCurrentDisk() } catch (e: Exception) { -1 }
        val pattern = text(EXTRA_TEXT_DISC, "Đĩa {0}")
        val labels = Array(disks) { i -> pattern.replace("{0}", (i + 1).toString()) + if (i == current) "  ✓" else "" }
        menu = AlertDialog.Builder(this, android.R.style.Theme_DeviceDefault_Dialog_Alert)
            .setTitle(text(EXTRA_TEXT_MENU_DISC, "Đổi đĩa"))
            .setItems(labels) { _, which ->
                if (which != current) {
                    try {
                        retroView.changeDisk(which)
                        Log.i(TAG, "Đổi sang đĩa ${which + 1}/$disks")
                    } catch (e: Exception) {
                        Log.e(TAG, "Không đổi được đĩa", e)
                    }
                }
            }
            .setOnDismissListener { hideSystemBars(); retroView.requestFocus() }
            .show()
    }

    private fun text(extra: String, fallback: String) = intent.getStringExtra(extra) ?: fallback

    // ---------------------------------------------------------------- Thoát game

    /** Lần đầu chỉ hiện "Nhấn lại để thoát...", lần thứ hai trong 3 giây mới thoát. */
    private fun requestExit() {
        val now = SystemClock.elapsedRealtime()
        if (now - lastExitPress > EXIT_CONFIRM_MS) {
            lastExitPress = now
            val text = intent.getStringExtra(EXTRA_TEXT_EXIT_AGAIN) ?: "Nhấn lại để thoát..."
            Toast.makeText(this, text, Toast.LENGTH_SHORT).show()
            return
        }
        exitGame()
    }

    private fun exitGame() {
        if (exiting) return
        exiting = true
        handler.removeCallbacks(sramTick)
        saveSram()
        if (intent.getBooleanExtra(EXTRA_SAVE_STATE_ON_EXIT, false)) saveState()
        setResult(RESULT_OK)
        finish()
    }

    private fun finishWithError(code: Int) {
        if (exiting) return
        exiting = true
        handler.removeCallbacks(sramTick)
        setResult(RESULT_ERROR, Intent().putExtra(EXTRA_ERROR, code))
        finish()
    }

    override fun onPause() {
        // App bị đưa xuống nền (có cuộc gọi, bấm Home): ghi save ngay, Android có thể tắt app bất cứ lúc nào
        if (!exiting) saveSram()
        super.onPause()
    }

    override fun onDestroy() {
        menu?.dismiss()
        handler.removeCallbacks(sramTick)
        super.onDestroy()
    }

    // ---------------------------------------------------------------- Tay cầm

    override fun dispatchKeyEvent(event: KeyEvent): Boolean {
        if (isGamepadEvent(event.source)) touchPad.visibility = View.GONE
        val down = event.action == KeyEvent.ACTION_DOWN
        when (event.keyCode) {
            KeyEvent.KEYCODE_BUTTON_START -> startDown = down
            KeyEvent.KEYCODE_BUTTON_SELECT -> selectDown = down
            KeyEvent.KEYCODE_BUTTON_THUMBL -> thumbLDown = down
            KeyEvent.KEYCODE_BUTTON_THUMBR -> thumbRDown = down
        }
        // Mở menu: L3 + R3 cùng lúc, hoặc nút giữa của tay cầm
        val thumbs = event.keyCode == KeyEvent.KEYCODE_BUTTON_THUMBL || event.keyCode == KeyEvent.KEYCODE_BUTTON_THUMBR
        if (down && event.repeatCount == 0 && (event.keyCode == KeyEvent.KEYCODE_BUTTON_MODE || thumbLDown && thumbRDown && thumbs)) {
            showMenu()
            return true
        }
        if (startDown && selectDown && event.action == KeyEvent.ACTION_DOWN && event.repeatCount == 0
            && (event.keyCode == KeyEvent.KEYCODE_BUTTON_START || event.keyCode == KeyEvent.KEYCODE_BUTTON_SELECT)
        ) {
            requestExit()
            return true
        }
        return super.dispatchKeyEvent(event)
    }

    override fun dispatchGenericMotionEvent(event: MotionEvent): Boolean {
        if (isGamepadEvent(event.source)) touchPad.visibility = View.GONE
        return super.dispatchGenericMotionEvent(event)
    }

    private fun isGamepadEvent(source: Int) =
        source and InputDevice.SOURCE_GAMEPAD == InputDevice.SOURCE_GAMEPAD ||
            source and InputDevice.SOURCE_JOYSTICK == InputDevice.SOURCE_JOYSTICK

    private fun hasGamepad() = InputDevice.getDeviceIds().any { id ->
        val device = InputDevice.getDevice(id)
        device != null && !device.isVirtual && isGamepadEvent(device.sources)
    }

    // ---------------------------------------------------------------- Hướng màn hình

    /**
     * Android 16 trở lên bỏ qua yêu cầu khóa ngang trên máy tính bảng, nên màn hình dọc vẫn phải dùng được:
     * hình game dồn lên cạnh trên, nút cảm ứng ở nửa dưới (TouchPadView tự xếp lại theo kích thước).
     */
    private fun applyOrientation(config: Configuration) {
        val portrait = config.orientation == Configuration.ORIENTATION_PORTRAIT
        // Dọc: vùng vẽ game là một ô vuông sát cạnh trên (rộng bằng màn hình); ngang: cả màn hình
        retroView.layoutParams = FrameLayout.LayoutParams(
            MATCH_PARENT,
            if (portrait) resources.displayMetrics.widthPixels else MATCH_PARENT,
            Gravity.TOP,
        )
    }

    override fun onConfigurationChanged(newConfig: Configuration) {
        super.onConfigurationChanged(newConfig)
        if (::retroView.isInitialized) applyOrientation(newConfig)
    }

    // ---------------------------------------------------------------- Toàn màn hình

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) hideSystemBars()
    }

    private fun hideSystemBars() {
        WindowCompat.setDecorFitsSystemWindows(window, false)
        WindowInsetsControllerCompat(window, window.decorView).apply {
            hide(WindowInsetsCompat.Type.systemBars())
            systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
        }
    }
}
