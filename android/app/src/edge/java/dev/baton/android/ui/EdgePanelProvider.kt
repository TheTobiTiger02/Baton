package dev.baton.android.ui

import android.app.PendingIntent
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.widget.RemoteViews
import com.samsung.android.sdk.look.cocktailbar.SlookCocktailManager
import com.samsung.android.sdk.look.cocktailbar.SlookCocktailProvider
import dev.baton.android.R
import dev.baton.android.handoff.HandoffEngine
import dev.baton.android.link.Link

/**
 * Baton's panel on Samsung's Edge screen: one tap sends what's on this phone to the PC, another
 * continues the PC's activity here. Built only with Samsung's Look SDK in android/app/libs, and
 * enabled only then (R.bool.edge_panel); [BatonNotifications.updateLink] refreshes it.
 */
class EdgePanelProvider : SlookCocktailProvider() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action == BatonNotifications.ACTION_EDGE_REFRESH) {
            val manager = runCatching { SlookCocktailManager.getInstance(context) }.getOrNull() ?: return
            onUpdate(context, manager, manager.getCocktailIds(ComponentName(context, EdgePanelProvider::class.java)))
            return
        }
        super.onReceive(context, intent)
    }

    override fun onUpdate(context: Context, manager: SlookCocktailManager, ids: IntArray?) {
        if (ids == null || ids.isEmpty()) return
        val views = RemoteViews(context.packageName, R.layout.edge_panel)
        val ready = Link.isReady
        val pc = Link.peers.value.firstOrNull { it.kind == "pc" }
        views.setTextViewText(R.id.edge_status, if (ready) pc?.name ?: "Your PC" else "Not connected to your PC")
        views.setTextViewText(R.id.edge_send_title, HandoffEngine.local.value.firstOrNull()?.title.orEmpty())
        views.setTextViewText(R.id.edge_pull_title, pc?.activities?.firstOrNull()?.title.orEmpty())
        views.setBoolean(R.id.edge_send, "setEnabled", ready)
        views.setBoolean(R.id.edge_pull, "setEnabled", ready)
        views.setOnClickPendingIntent(R.id.edge_send,
            PendingIntent.getBroadcast(context, 30, HandoffActionReceiver.send(context), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT))
        views.setOnClickPendingIntent(R.id.edge_pull,
            PendingIntent.getBroadcast(context, 31, HandoffActionReceiver.pullFromPc(context), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT))
        ids.forEach { manager.updateCocktail(it, views) }
    }
}
