package com.remotedesk.agent;

import static org.junit.Assert.assertTrue;
import java.lang.reflect.Modifier;
import org.junit.Test;

public final class AndroidPasswordStoreTest {
    @Test public void firstUseKeyCreationIsSerializedAcrossCredentialKinds() throws Exception {
        int modifiers = AndroidPasswordStore.class.getDeclaredMethod("getOrCreateKey").getModifiers();
        assertTrue(Modifier.isStatic(modifiers));
        assertTrue(Modifier.isSynchronized(modifiers));
    }
}
