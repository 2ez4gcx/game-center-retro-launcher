package com.khuongdoan.gamecenter.player

import android.annotation.SuppressLint
import android.content.Context
import android.graphics.Canvas
import android.graphics.Paint
import android.graphics.Path
import android.graphics.RectF
import android.os.SystemClock
import android.view.KeyEvent
import android.view.MotionEvent
import android.view.View
import kotlin.math.abs
import kotlin.math.hypot

/**
 * Nút cảm ứng phủ trên màn hình game (máy không cắm tay cầm).
 *
 * Bố cục: L2 / L1 / SELECT xếp dọc ở mép trái, R2 / R1 / START xếp dọc ở mép phải (nút chữ nhật bo góc);
 * phím hướng ở góc dưới trái, 4 nút bấm ở góc dưới phải; ba nút nhỏ ở giữa cạnh trên: menu (đổi đĩa, thoát),
 * ẩn / hiện nút cảm ứng, tốc độ game (×1, ×2, ×3). Hệ PlayStation ([psSymbols]) vẽ 4 nút bằng tam giác, vuông, tròn, chéo;
 * hệ khác vẽ chữ A, B, X, Y. Hệ máy có cần analog (PSP, PS2) thì thêm một cần analog ảo.
 *
 * Mỗi lần nút đổi trạng thái gọi [onKey] với mã phím Android (KEYCODE_BUTTON_*, KEYCODE_DPAD_*; nút menu là
 * KEYCODE_MENU, nút chạy nhanh là KEYCODE_MEDIA_FAST_FORWARD). Cần analog gọi [onAnalog] với x, y trong khoảng -1..1.
 *
 * Mũi tên và biểu tượng được vẽ bằng hình (không dùng ký tự): trên nhiều máy các ký tự đó bị vẽ thành biểu tượng
 * màu hoặc rất mờ.
 */
@SuppressLint("ViewConstructor")
class TouchPadView(
    context: Context,
    private val analog: Boolean,
    private val psSymbols: Boolean,
    private val onKey: (keyCode: Int, down: Boolean) -> Unit,
    private val onAnalog: (x: Float, y: Float) -> Unit,
) : View(context) {

    /** Nút tròn khi [round], còn lại là chữ nhật bo góc nửa rộng [hw], nửa cao [hh]. */
    private class Button(
        val keyCode: Int,
        val label: String,
        val round: Boolean,
        val top: Boolean = false,
        var x: Float = 0f,
        var y: Float = 0f,
        var hw: Float = 0f,
        var hh: Float = 0f,
    )

    private val buttons = listOf(
        Button(KeyEvent.KEYCODE_BUTTON_A, "A", round = true),
        Button(KeyEvent.KEYCODE_BUTTON_B, "B", round = true),
        Button(KeyEvent.KEYCODE_BUTTON_X, "X", round = true),
        Button(KeyEvent.KEYCODE_BUTTON_Y, "Y", round = true),
        Button(KeyEvent.KEYCODE_BUTTON_L1, "L1", round = false),
        Button(KeyEvent.KEYCODE_BUTTON_L2, "L2", round = false),
        Button(KeyEvent.KEYCODE_BUTTON_R1, "R1", round = false),
        Button(KeyEvent.KEYCODE_BUTTON_R2, "R2", round = false),
        Button(KeyEvent.KEYCODE_BUTTON_SELECT, "SELECT", round = false),
        Button(KeyEvent.KEYCODE_BUTTON_START, "START", round = false),
        // Ba nút dưới không phải nút của máy game
        Button(KeyEvent.KEYCODE_MENU, "", round = false, top = true),
        Button(KEY_HIDE, "", round = false, top = true),
        Button(KeyEvent.KEYCODE_MEDIA_FAST_FORWARD, "", round = false, top = true),
    )

    /** Thứ tự: lên, xuống, trái, phải (hướng của mũi tên là dx, dy). */
    private val dpad = arrayOf(
        Triple(KeyEvent.KEYCODE_DPAD_UP, 0f, -1f),
        Triple(KeyEvent.KEYCODE_DPAD_DOWN, 0f, 1f),
        Triple(KeyEvent.KEYCODE_DPAD_LEFT, -1f, 0f),
        Triple(KeyEvent.KEYCODE_DPAD_RIGHT, 1f, 0f),
    )
    private var dpadX = 0f
    private var dpadY = 0f
    private var dpadR = 0f

    private var stickX = 0f
    private var stickY = 0f
    private var stickR = 0f
    private var stickValueX = 0f
    private var stickValueY = 0f

    private var pressed = emptySet<Int>()
    private val downAt = HashMap<Int, Long>()

    /** Đang ẩn nút cảm ứng (chỉ còn ba nút nhỏ ở cạnh trên). */
    private var hidden = false

    /** Tốc độ game hiện tại (1, 2 hoặc 3): nút tốc độ ghi "×N" và sáng lên khi lớn hơn 1. GameActivity đặt sau khi đổi. */
    var speed = 1
        set(value) { field = value; invalidate() }

    // Nút mờ khoảng 30% để không che hình game: nền tối và viền trắng đều 30%; chữ và ký hiệu đậm hơn một chút
    // (55%) để còn đọc được trên cảnh sáng. Nút đang bấm tô màu nhấn của app.
    private val fill = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = 0x4D101418 }
    private val fillDown = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = 0x992E8BFF.toInt() }
    private val stroke = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = 0x4DFFFFFF; style = Paint.Style.STROKE }
    private val shape = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = 0x8CFFFFFF.toInt(); style = Paint.Style.FILL }
    private val line = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = 0x8CFFFFFF.toInt(); style = Paint.Style.STROKE; strokeCap = Paint.Cap.ROUND; strokeJoin = Paint.Join.ROUND
    }
    private val knob = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = 0x66FFFFFF }
    private val text = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = 0x8CFFFFFF.toInt(); textAlign = Paint.Align.CENTER; isFakeBoldText = true }
    private val path = Path()
    private val rect = RectF()

    override fun onSizeChanged(w: Int, h: Int, oldw: Int, oldh: Int) {
        val portrait = h > w
        var u = minOf(w, h) * 0.11f
        // Màn hình dọc: hình game là ô vuông sát cạnh trên, mọi nút phải nằm gọn trong phần còn lại bên dưới.
        // Máy tính bảng dọc có phần dưới thấp: thu nhỏ nút cho vừa (không nhỏ hơn 2/3 cỡ thường).
        if (portrait) u = u.coerceAtMost((h - w) / 8.9f).coerceAtLeast(minOf(w, h) * 0.073f)
        stroke.strokeWidth = u * 0.035f

        fun place(key: Int, x: Float, y: Float, hw: Float, hh: Float = hw) =
            buttons.first { it.keyCode == key }.let { it.x = x; it.y = y; it.hw = hw; it.hh = hh }

        // Phím hướng và 4 nút bấm ở hai góc dưới
        val clusterY = h - u * 2.3f
        dpadX = u * 2.3f; dpadY = clusterY; dpadR = u * 1.85f
        val fx = w - u * 2.3f
        val d = u * 1.2f
        val fr = u * 0.68f
        // Bố trí như tay cầm SNES / PlayStation: A (tròn) phải, B (chéo) dưới, X (tam giác) trên, Y (vuông) trái
        place(KeyEvent.KEYCODE_BUTTON_A, fx + d, clusterY, fr)
        place(KeyEvent.KEYCODE_BUTTON_B, fx, clusterY + d, fr)
        place(KeyEvent.KEYCODE_BUTTON_X, fx, clusterY - d, fr)
        place(KeyEvent.KEYCODE_BUTTON_Y, fx - d, clusterY, fr)

        // Ba nút chữ nhật xếp dọc mỗi bên. Ngang: từ cạnh trên xuống. Dọc: ngay phía trên phím hướng / 4 nút bấm.
        val hw = u * 0.92f
        val hh = u * 0.43f
        val step = u * 1.12f
        val first = if (portrait) clusterY - u * 2.6f - step * 2 else u * 1.1f
        val lx = u * 0.25f + hw
        val rx = w - u * 0.25f - hw
        place(KeyEvent.KEYCODE_BUTTON_L2, lx, first, hw, hh)
        place(KeyEvent.KEYCODE_BUTTON_L1, lx, first + step, hw, hh)
        place(KeyEvent.KEYCODE_BUTTON_SELECT, lx, first + step * 2, hw, hh)
        place(KeyEvent.KEYCODE_BUTTON_R2, rx, first, hw, hh)
        place(KeyEvent.KEYCODE_BUTTON_R1, rx, first + step, hw, hh)
        place(KeyEvent.KEYCODE_BUTTON_START, rx, first + step * 2, hw, hh)

        // Ba nút nhỏ ở giữa: ngang thì sát cạnh trên, dọc thì ngay dưới hình game
        val topY = if (portrait) first - u * 1.0f else u * 0.42f
        val thw = u * 0.62f
        val thh = u * 0.32f
        val gap = u * 1.4f
        place(KeyEvent.KEYCODE_MENU, w / 2f - gap, topY, thw, thh)
        place(KEY_HIDE, w / 2f, topY, thw, thh)
        place(KeyEvent.KEYCODE_MEDIA_FAST_FORWARD, w / 2f + gap, topY, thw, thh)

        // Cần analog: ngang thì nằm cạnh phím hướng, dọc thì nằm giữa hai cột nút chữ nhật
        stickR = u * 1.05f
        if (portrait) { stickX = w / 2f; stickY = first + step } else { stickX = u * 5.6f; stickY = h - u * 1.6f }
    }

    override fun onDraw(canvas: Canvas) {
        if (!hidden) {
            drawDpad(canvas)
            if (analog) {
                canvas.drawCircle(stickX, stickY, stickR, fill)
                canvas.drawCircle(stickX, stickY, stickR, stroke)
                canvas.drawCircle(stickX + stickValueX * stickR * 0.55f, stickY + stickValueY * stickR * 0.55f, stickR * 0.45f, knob)
            }
        }
        for (b in buttons) {
            if (hidden && !b.top) continue
            val lit = b.keyCode in pressed || b.keyCode == KeyEvent.KEYCODE_MEDIA_FAST_FORWARD && speed > 1
            val paint = if (lit) fillDown else fill
            if (b.round) {
                canvas.drawCircle(b.x, b.y, b.hw, paint)
                canvas.drawCircle(b.x, b.y, b.hw, stroke)
            } else {
                rect.set(b.x - b.hw, b.y - b.hh, b.x + b.hw, b.y + b.hh)
                val corner = b.hh * 0.45f
                canvas.drawRoundRect(rect, corner, corner, paint)
                canvas.drawRoundRect(rect, corner, corner, stroke)
            }
            when {
                b.keyCode == KeyEvent.KEYCODE_MENU -> drawMenuIcon(canvas, b)
                b.keyCode == KEY_HIDE -> drawTriangle(canvas, b.x, b.y, 0f, if (hidden) -1f else 1f, b.hh * 0.42f)
                b.keyCode == KeyEvent.KEYCODE_MEDIA_FAST_FORWARD -> drawFastForwardIcon(canvas, b)
                b.round && psSymbols -> drawPsSymbol(canvas, b)
                else -> {
                    text.textSize = if (b.round) b.hw * 0.85f else if (b.label.length > 2) b.hh * 0.72f else b.hh * 0.95f
                    canvas.drawText(b.label, b.x, b.y - (text.ascent() + text.descent()) / 2, text)
                }
            }
        }
    }

    /** Phím hướng: một vòng viền lớn, bên trong là 4 nút tròn có mũi tên. */
    private fun drawDpad(canvas: Canvas) {
        canvas.drawCircle(dpadX, dpadY, dpadR, stroke)
        for ((key, dx, dy) in dpad) {
            val cx = dpadX + dx * dpadR * 0.56f
            val cy = dpadY + dy * dpadR * 0.56f
            canvas.drawCircle(cx, cy, dpadR * 0.33f, if (key in pressed) fillDown else fill)
            drawTriangle(canvas, cx, cy, dx, dy, dpadR * 0.12f)
        }
    }

    /** Tam giác đặc tâm (cx, cy): đỉnh chỉ theo hướng (dx, dy), đáy vuông góc với hướng đó. */
    private fun drawTriangle(canvas: Canvas, cx: Float, cy: Float, dx: Float, dy: Float, a: Float) {
        path.rewind()
        path.moveTo(cx + dx * a, cy + dy * a)
        path.lineTo(cx - dx * a - dy * a, cy - dy * a + dx * a)
        path.lineTo(cx - dx * a + dy * a, cy - dy * a - dx * a)
        path.close()
        canvas.drawPath(path, shape)
    }

    /** Ba gạch ngang. */
    private fun drawMenuIcon(canvas: Canvas, b: Button) {
        val half = b.hh * 0.6f
        val thick = b.hh * 0.16f
        for (i in -1..1) {
            val y = b.y + i * b.hh * 0.42f
            canvas.drawRect(b.x - half, y - thick / 2, b.x + half, y + thick / 2, shape)
        }
    }

    /** Hai tam giác chỉ sang phải và tốc độ hiện tại "×N". */
    private fun drawFastForwardIcon(canvas: Canvas, b: Button) {
        val a = b.hh * 0.36f
        drawTriangle(canvas, b.x - b.hw * 0.52f, b.y, 1f, 0f, a)
        drawTriangle(canvas, b.x - b.hw * 0.52f + a * 1.7f, b.y, 1f, 0f, a)
        text.textSize = b.hh * 0.95f
        canvas.drawText("×$speed", b.x + b.hw * 0.45f, b.y - (text.ascent() + text.descent()) / 2, text)
    }

    /** Ký hiệu nút PlayStation theo vị trí: X (trên) tam giác, Y (trái) vuông, A (phải) tròn, B (dưới) chéo. */
    private fun drawPsSymbol(canvas: Canvas, b: Button) {
        val s = b.hw * 0.42f
        line.strokeWidth = b.hw * 0.11f
        when (b.keyCode) {
            KeyEvent.KEYCODE_BUTTON_X -> {
                path.rewind()
                path.moveTo(b.x, b.y - s * 1.05f)
                path.lineTo(b.x + s * 1.15f, b.y + s * 0.85f)
                path.lineTo(b.x - s * 1.15f, b.y + s * 0.85f)
                path.close()
                canvas.drawPath(path, line)
            }
            KeyEvent.KEYCODE_BUTTON_Y -> canvas.drawRect(b.x - s * 0.9f, b.y - s * 0.9f, b.x + s * 0.9f, b.y + s * 0.9f, line)
            KeyEvent.KEYCODE_BUTTON_A -> canvas.drawCircle(b.x, b.y, s, line)
            else -> {
                canvas.drawLine(b.x - s * 0.9f, b.y - s * 0.9f, b.x + s * 0.9f, b.y + s * 0.9f, line)
                canvas.drawLine(b.x - s * 0.9f, b.y + s * 0.9f, b.x + s * 0.9f, b.y - s * 0.9f, line)
            }
        }
    }

    @SuppressLint("ClickableViewAccessibility")
    override fun onTouchEvent(event: MotionEvent): Boolean {
        val now = HashSet<Int>()
        var ax = 0f
        var ay = 0f
        val ended = event.actionMasked == MotionEvent.ACTION_UP || event.actionMasked == MotionEvent.ACTION_CANCEL
        if (!ended) {
            for (i in 0 until event.pointerCount) {
                // Ngón vừa nhấc lên vẫn còn trong danh sách của sự kiện POINTER_UP
                if (event.actionMasked == MotionEvent.ACTION_POINTER_UP && i == event.actionIndex) continue
                val x = event.getX(i)
                val y = event.getY(i)
                // Ngón đặt trong vùng cần analog (rộng hơn hình vẽ để kéo ra ngoài không mất) điều khiển cần, không bấm nút
                if (analog && !hidden && hypot(x - stickX, y - stickY) < stickR * 1.7f) {
                    ax = ((x - stickX) / stickR).coerceIn(-1f, 1f)
                    ay = ((y - stickY) / stickR).coerceIn(-1f, 1f)
                } else {
                    hit(x, y, now)
                }
            }
        }
        var changed = false
        if (ax != stickValueX || ay != stickValueY) {
            stickValueX = ax; stickValueY = ay
            onAnalog(ax, ay)
            changed = true
        }
        if (now != pressed) {
            val time = SystemClock.uptimeMillis()
            for (k in pressed) if (k !in now) release(k, time)
            for (k in now) if (k !in pressed) press(k, time)
            pressed = now
            changed = true
        }
        if (changed) invalidate()
        // Chạm ra ngoài các nút vẫn nhận sự kiện, để chạm lỡ không lọt xuống game
        return true
    }

    private fun press(key: Int, time: Long) {
        if (key == KEY_HIDE) {
            // Ẩn nút: nhả hết nút game đang giữ để không bị kẹt
            hidden = !hidden
            return
        }
        downAt[key] = time
        onKey(key, true)
    }

    /**
     * Nhả nút. Core chỉ đọc nút mỗi khung hình một lần (khoảng 16 ms), nên một cú chạm thật nhanh có thể bị bỏ lỡ:
     * giữ nút tối thiểu [MIN_PRESS_MS] rồi mới nhả.
     */
    private fun release(key: Int, time: Long) {
        if (key == KEY_HIDE) return
        val held = time - (downAt[key] ?: 0L)
        if (held >= MIN_PRESS_MS) onKey(key, false)
        else postDelayed({ if (key !in pressed) onKey(key, false) }, MIN_PRESS_MS - held)
    }

    private fun hit(x: Float, y: Float, out: MutableSet<Int>) {
        val dx = x - dpadX
        val dy = y - dpadY
        val dist = hypot(dx, dy)
        // Vùng phím hướng rộng hơn hình vẽ một chút để ngón trượt ra ngoài không mất hướng; chéo thì bấm 2 hướng
        if (!hidden && dist < dpadR * 1.2f) {
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
            if (hidden && !b.top) continue
            // Vùng bấm rộng hơn hình vẽ: nút tròn thêm 15%, nút chữ nhật thêm một chút mỗi phía
            val mx = if (b.round) b.hw * 1.15f else b.hw + b.hh * 0.25f
            val my = if (b.round) b.hh * 1.15f else b.hh * 1.25f
            if (abs(x - b.x) < mx && abs(y - b.y) < my) out += b.keyCode
        }
    }

    private companion object {
        const val MIN_PRESS_MS = 50L

        /** Mã riêng của nút ẩn / hiện nút cảm ứng (xử lý ngay trong view, không gửi ra ngoài). */
        const val KEY_HIDE = -1
    }
}
