package com.khuongdoan.gamecenter.player

import android.annotation.SuppressLint
import android.content.Context
import android.graphics.Canvas
import android.graphics.Paint
import android.os.SystemClock
import android.view.KeyEvent
import android.view.MotionEvent
import android.view.View
import kotlin.math.abs
import kotlin.math.hypot

/**
 * Nút cảm ứng phủ trên màn hình game (máy không cắm tay cầm): phím hướng bên trái, 4 nút bên phải,
 * L/R ở hai góc trên, SELECT/START ở giữa cạnh dưới. Nút to, chữ lớn, bấm nhiều nút cùng lúc được.
 * Mỗi lần nút đổi trạng thái gọi [onKey] với mã phím Android (KEYCODE_BUTTON_*, KEYCODE_DPAD_*).
 */
@SuppressLint("ViewConstructor")
class TouchPadView(context: Context, private val onKey: (keyCode: Int, down: Boolean) -> Unit) : View(context) {

    private class Button(val keyCode: Int, val label: String, var x: Float = 0f, var y: Float = 0f, var r: Float = 0f)

    private val buttons = listOf(
        Button(KeyEvent.KEYCODE_BUTTON_A, "A"),
        Button(KeyEvent.KEYCODE_BUTTON_B, "B"),
        Button(KeyEvent.KEYCODE_BUTTON_X, "X"),
        Button(KeyEvent.KEYCODE_BUTTON_Y, "Y"),
        Button(KeyEvent.KEYCODE_BUTTON_L1, "L1"),
        Button(KeyEvent.KEYCODE_BUTTON_L2, "L2"),
        Button(KeyEvent.KEYCODE_BUTTON_R1, "R1"),
        Button(KeyEvent.KEYCODE_BUTTON_R2, "R2"),
        Button(KeyEvent.KEYCODE_BUTTON_SELECT, "SELECT"),
        Button(KeyEvent.KEYCODE_BUTTON_START, "START"),
    )
    private val dpadKeys = intArrayOf(
        KeyEvent.KEYCODE_DPAD_UP, KeyEvent.KEYCODE_DPAD_DOWN, KeyEvent.KEYCODE_DPAD_LEFT, KeyEvent.KEYCODE_DPAD_RIGHT,
    )
    private var dpadX = 0f
    private var dpadY = 0f
    private var dpadR = 0f

    private var pressed = emptySet<Int>()
    private val downAt = HashMap<Int, Long>()

    private val fill = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = 0x40FFFFFF }
    private val fillDown = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = 0x90FFFFFF.toInt() }
    private val stroke = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = 0x90FFFFFF.toInt(); style = Paint.Style.STROKE }
    private val text = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = 0xE0FFFFFF.toInt(); textAlign = Paint.Align.CENTER; isFakeBoldText = true }

    override fun onSizeChanged(w: Int, h: Int, oldw: Int, oldh: Int) {
        val u = minOf(w, h) * 0.11f
        val portrait = h > w
        stroke.strokeWidth = u * 0.06f
        // Màn hình dọc: hình game nằm sát cạnh trên, mọi nút dồn xuống nửa dưới. Màn hình ngang: nút hai bên, L/R ở góc trên.
        val clusterY = if (portrait) h - u * 3.6f else h - u * 2.7f
        dpadX = u * 2.3f; dpadY = clusterY; dpadR = u * 1.9f

        val fx = w - u * 2.3f
        val d = u * 1.25f
        fun place(key: Int, x: Float, y: Float, r: Float) = buttons.first { it.keyCode == key }.let { it.x = x; it.y = y; it.r = r }
        // Bố trí như tay cầm SNES / PlayStation: A phải, B dưới, X trên, Y trái
        place(KeyEvent.KEYCODE_BUTTON_A, fx + d, clusterY, u * 0.68f)
        place(KeyEvent.KEYCODE_BUTTON_B, fx, clusterY + d, u * 0.68f)
        place(KeyEvent.KEYCODE_BUTTON_X, fx, clusterY - d, u * 0.68f)
        place(KeyEvent.KEYCODE_BUTTON_Y, fx - d, clusterY, u * 0.68f)
        val shoulderY = if (portrait) clusterY - u * 2.9f else u * 1.0f
        place(KeyEvent.KEYCODE_BUTTON_L1, u * 1.2f, shoulderY, u * 0.6f)
        place(KeyEvent.KEYCODE_BUTTON_L2, u * 2.9f, shoulderY, u * 0.6f)
        place(KeyEvent.KEYCODE_BUTTON_R1, w - u * 1.2f, shoulderY, u * 0.6f)
        place(KeyEvent.KEYCODE_BUTTON_R2, w - u * 2.9f, shoulderY, u * 0.6f)
        val midY = h - u * 0.85f
        place(KeyEvent.KEYCODE_BUTTON_SELECT, w / 2f - u * 0.8f, midY, u * 0.62f)
        place(KeyEvent.KEYCODE_BUTTON_START, w / 2f + u * 0.8f, midY, u * 0.62f)
    }

    override fun onDraw(canvas: Canvas) {
        // Phím hướng: một vòng tròn lớn, 4 mũi tên
        canvas.drawCircle(dpadX, dpadY, dpadR, fill)
        canvas.drawCircle(dpadX, dpadY, dpadR, stroke)
        text.textSize = dpadR * 0.42f
        val arrows = arrayOf("▲" to (0f to -1f), "▼" to (0f to 1f), "◀" to (-1f to 0f), "▶" to (1f to 0f))
        for ((i, a) in arrows.withIndex()) {
            val cx = dpadX + a.second.first * dpadR * 0.62f
            val cy = dpadY + a.second.second * dpadR * 0.62f
            if (dpadKeys[i] in pressed) canvas.drawCircle(cx, cy, dpadR * 0.34f, fillDown)
            canvas.drawText(a.first, cx, cy - (text.ascent() + text.descent()) / 2, text)
        }
        for (b in buttons) {
            canvas.drawCircle(b.x, b.y, b.r, if (b.keyCode in pressed) fillDown else fill)
            canvas.drawCircle(b.x, b.y, b.r, stroke)
            text.textSize = if (b.label.length > 2) b.r * 0.42f else b.r * 0.85f
            canvas.drawText(b.label, b.x, b.y - (text.ascent() + text.descent()) / 2, text)
        }
    }

    @SuppressLint("ClickableViewAccessibility")
    override fun onTouchEvent(event: MotionEvent): Boolean {
        val now = HashSet<Int>()
        val ended = event.actionMasked == MotionEvent.ACTION_UP || event.actionMasked == MotionEvent.ACTION_CANCEL
        if (!ended) {
            for (i in 0 until event.pointerCount) {
                // Ngón vừa nhấc lên vẫn còn trong danh sách của sự kiện POINTER_UP
                if (event.actionMasked == MotionEvent.ACTION_POINTER_UP && i == event.actionIndex) continue
                hit(event.getX(i), event.getY(i), now)
            }
        }
        if (now != pressed) {
            val time = SystemClock.uptimeMillis()
            for (k in pressed) if (k !in now) release(k, time)
            for (k in now) if (k !in pressed) { downAt[k] = time; onKey(k, true) }
            pressed = now
            invalidate()
        }
        // Chạm ra ngoài các nút vẫn nhận sự kiện, để chạm lỡ không lọt xuống game
        return true
    }

    /**
     * Nhả nút. Core chỉ đọc nút mỗi khung hình một lần (khoảng 16 ms), nên một cú chạm thật nhanh có thể bị bỏ lỡ:
     * giữ nút tối thiểu [MIN_PRESS_MS] rồi mới nhả.
     */
    private fun release(key: Int, time: Long) {
        val held = time - (downAt[key] ?: 0L)
        if (held >= MIN_PRESS_MS) onKey(key, false)
        else postDelayed({ if (key !in pressed) onKey(key, false) }, MIN_PRESS_MS - held)
    }

    private fun hit(x: Float, y: Float, out: MutableSet<Int>) {
        val dx = x - dpadX
        val dy = y - dpadY
        val dist = hypot(dx, dy)
        // Vùng phím hướng rộng hơn hình vẽ một chút để ngón trượt ra ngoài không mất hướng; chéo thì bấm 2 hướng
        if (dist < dpadR * 1.35f) {
            if (dist > dpadR * 0.2f) {
                val diagonal = dist * 0.38f
                if (dy < -diagonal) out += KeyEvent.KEYCODE_DPAD_UP
                if (dy > diagonal) out += KeyEvent.KEYCODE_DPAD_DOWN
                if (dx < -diagonal) out += KeyEvent.KEYCODE_DPAD_LEFT
                if (dx > diagonal) out += KeyEvent.KEYCODE_DPAD_RIGHT
            }
            return
        }
        for (b in buttons) {
            if (abs(x - b.x) < b.r * 1.15f && abs(y - b.y) < b.r * 1.15f) out += b.keyCode
        }
    }

    private companion object {
        const val MIN_PRESS_MS = 50L
    }
}
