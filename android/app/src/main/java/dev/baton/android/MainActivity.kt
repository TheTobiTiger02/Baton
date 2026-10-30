package dev.baton.android

import android.content.Intent
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import dev.baton.android.link.LinkService
import dev.baton.android.link.TrustStore
import dev.baton.android.protocol.PairingLink
import dev.baton.android.ui.BatonApp
import dev.baton.android.ui.BatonTheme

class MainActivity : ComponentActivity() {
    private lateinit var trust: TrustStore

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        trust = TrustStore(this)
        handle(intent)
        if (trust.isPaired) LinkService.start(this)
        setContent { BatonTheme { BatonApp(trust) } }
    }

    /** In front with focus, the one moment Android lets Baton read a copy made on this phone. */
    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) dev.baton.android.stream.ClipboardSync.onAppForeground(this)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        handle(intent)
    }

    /** A `baton://pair` link, from the in-app scanner, the camera app, or a shared text. */
    private fun handle(intent: Intent?) {
        val raw = intent?.dataString ?: return
        val link = PairingLink.parse(raw) ?: return
        LinkService.pair(this, link, raw)
    }
}
