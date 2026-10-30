package dev.baton.android.notify

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class OneTimeCodesTest {
    @Test
    fun findsCodesInCommonWordings() {
        assertEquals("482913", OneTimeCodes.find("Your verification code is 482913"))
        assertEquals("482913", OneTimeCodes.find("G-482913 is your Google verification code."))
        assertEquals("482913", OneTimeCodes.find("Ihr Bestätigungscode lautet 482 913"))
        assertEquals("7311", OneTimeCodes.find("Your PIN: 7311"))
        assertEquals("55102938", OneTimeCodes.find("Ihre TAN für die Überweisung: 55102938"))
    }

    @Test
    fun ignoresNumbersThatAreNotCodes() {
        assertNull(OneTimeCodes.find("Meeting moved to 14:30, room 4012"))
        assertNull(OneTimeCodes.find("Your order 123456 has shipped"))
        assertNull(OneTimeCodes.find("Login from Berlin in 2026"))
        assertNull(OneTimeCodes.find("Your code arrives soon"))
    }
}
