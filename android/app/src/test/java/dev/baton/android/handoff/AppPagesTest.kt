package dev.baton.android.handoff

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class AppPagesTest {
    @Test
    fun mapsSearchAndRoutes() {
        assertEquals("https://www.google.com/maps/search/?api=1&query=Brandenburg%20Gate%2C%20Pariser%20Platz",
            MapsLinks.search("Brandenburg Gate, Pariser Platz"))
        assertEquals("https://www.google.com/maps/dir/?api=1&origin=Alexanderplatz&destination=Brandenburg%20Gate&travelmode=transit",
            MapsLinks.directions("Alexanderplatz", "Brandenburg Gate", "Transit"))
        // From where the phone is: the PC starts from where it is.
        assertEquals("https://www.google.com/maps/dir/?api=1&destination=Caf%C3%A9&travelmode=walking",
            MapsLinks.directions("Your location", "Café", "Walking"))
        assertEquals("https://www.google.com/maps/dir/?api=1&destination=Home", MapsLinks.directions(null, "Home", "Flying"))
    }

    @Test
    fun chatNumbers() {
        assertEquals("4915123456789", ChatLinks.numberIn("+49 151 23456789"))
        assertEquals("4915123456789", ChatLinks.numberIn("0049 (151) 234-567-89"))
        assertNull(ChatLinks.numberIn("Anna"))
        assertNull(ChatLinks.numberIn("0151 23456789"))
        assertNull(ChatLinks.numberIn("+49 151 Anna"))
        assertEquals("https://wa.me/4915123456789", ChatLinks.whatsApp("4915123456789", " "))
        assertEquals("https://wa.me/4915123456789?text=bis%20gleich%21", ChatLinks.whatsApp("4915123456789", "bis gleich!"))
    }
}
