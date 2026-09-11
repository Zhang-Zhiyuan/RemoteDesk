package com.remotedesk.agent;

import java.io.IOException;
import java.security.GeneralSecurityException;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicReference;
import org.junit.Test;
import static org.junit.Assert.*;

public class AndroidRelayStorageTest {
    @Test public void missingConfigurationDoesNotAccessKeystore() throws Exception {
        for (String missing : new String[] { null, "" })
            assertEquals("", AndroidPasswordStore.loadRelayValue(missing, value -> { throw new AssertionError(); }));
    }

    @Test public void existingConfigurationIsReadWithoutChangingIt() throws Exception {
        assertEquals("{old-settings}", AndroidPasswordStore.loadRelayValue("ciphertext", value -> {
            assertEquals("ciphertext", value);
            return "{old-settings}";
        }));
    }

    @Test public void unreadableConfigurationIsNotFirstUse() {
        for (String result : new String[] { null, "", "  " })
            assertThrows(IOException.class, () -> AndroidPasswordStore.loadRelayValue("damaged", value -> result));
        assertThrows(GeneralSecurityException.class, () -> AndroidPasswordStore.loadRelayValue("old-key", value -> {
            throw new GeneralSecurityException();
        }));
    }

    @Test public void saveCommitsOnlyEncryptedValue() throws Exception {
        AtomicReference<String> stored = new AtomicReference<>("old");
        AndroidPasswordStore.saveRelayValue("new-config", "old", value -> {
            assertEquals("new-config", value);
            return "encrypted-new-config";
        }, value -> { stored.set(value); return true; });
        assertEquals("encrypted-new-config", stored.get());
    }

    @Test public void failedPersistenceIsNotReportedAsSaved() {
        AtomicReference<String> stored = new AtomicReference<>("old-encrypted");
        assertThrows(IOException.class, () -> AndroidPasswordStore.saveRelayValue("config", stored.get(), value -> "encrypted", value -> {
            stored.set(value); return false;
        }));
        assertEquals("old-encrypted", stored.get());
    }

    @Test public void encryptionFailureNeverWritesOverOldValue() {
        AtomicInteger writes = new AtomicInteger();
        assertThrows(GeneralSecurityException.class, () -> AndroidPasswordStore.saveRelayValue("config", "old", value -> {
            throw new GeneralSecurityException();
        }, value -> { writes.incrementAndGet(); return true; }));
        assertEquals(0, writes.get());
    }

    @Test public void clearingConfigurationAlsoRequiresDurableCommit() throws Exception {
        AndroidPasswordStore.saveRelayValue(null, "old", value -> { throw new AssertionError(); }, value -> {
            assertNull(value); return true;
        });
        assertThrows(IOException.class, () -> AndroidPasswordStore.saveRelayValue("", "old", value -> "unused", value -> false));
    }

    @Test public void emptyEncryptionCannotClearExistingIdentity() {
        for (String result : new String[] { null, "" })
            assertThrows(IOException.class, () -> AndroidPasswordStore.saveRelayValue("config", "old", value -> result,
                value -> { throw new AssertionError(); }));
    }
}
