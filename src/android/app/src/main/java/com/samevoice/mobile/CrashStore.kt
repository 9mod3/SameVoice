package com.samevoice.mobile

import android.content.Context
import android.os.Build
import android.util.Log
import java.io.File
import java.io.PrintWriter
import java.io.StringWriter
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * Last-resort crash log. The app can crash before the UI is interactive, so the
 * next launch reads this file and shows a recovery screen instead of silently
 * flashing closed again.
 */
object CrashStore {
    private const val FILE_NAME = "last-crash.txt"
    private const val TAG = "SameVoiceCrash"

    fun save(context: Context, thread: Thread, throwable: Throwable) {
        try {
            val writer = StringWriter()
            PrintWriter(writer).use { throwable.printStackTrace(it) }
            val stack = writer.toString().take(48_000)
            val header = buildString {
                append("thread=")
                append(thread.name)
                append('\n')
                append("time=")
                append(SimpleDateFormat("yyyy-MM-dd HH:mm:ss", Locale.US).format(Date()))
                append('\n')
                append("device=")
                append(Build.MANUFACTURER).append(' ').append(Build.MODEL)
                append(" / Android ").append(Build.VERSION.RELEASE)
                append(" (API ").append(Build.VERSION.SDK_INT).append(")\n")
                append("app=").append(BuildConfig.VERSION_NAME).append('\n')
                append('\n')
            }
            File(context.filesDir, FILE_NAME).writeText(header + stack, Charsets.UTF_8)
            Log.e(TAG, "Fatal exception on ${thread.name}", throwable)
        } catch (ignored: Throwable) {
            Log.e(TAG, "Could not persist crash log", ignored)
        }
    }

    fun read(context: Context): String? = try {
        val file = File(context.filesDir, FILE_NAME)
        if (file.isFile) file.readText(Charsets.UTF_8) else null
    } catch (_: Throwable) {
        null
    }

    fun isMainThreadCrash(log: String): Boolean =
        log.lineSequence().any { it.trim() == "thread=main" }

    fun clear(context: Context) {
        try { File(context.filesDir, FILE_NAME).delete() } catch (_: Throwable) {}
    }
}
