package com.remotedesk.agent;

import java.util.List;
import org.junit.Test;
import static org.junit.Assert.*;

public final class AndroidInputLifecycleRegressionTest {
    @Test public void androidComposerWhitespaceIsTextNotAGlobalShortcut() {
        for (String platform : new String[]{"Android", "android", "aNdRoId"}) {
            List<AndroidViewerInputQueue.Command> commands = AndroidViewerKeyboard.text("A\r\n\t中😀\rZ", 7, 9, platform);
            int[] points = "A\n\t中😀\nZ".codePoints().toArray();
            assertEquals(points.length, commands.size());
            for (int i = 0; i < points.length; i++) {
                assertEquals(RemoteDeskProtocol.INPUT_TEXT, commands.get(i).kind);
                assertEquals(points[i], commands.get(i).data);
                assertEquals(7, commands.get(i).mouseRouteGeneration);
                assertEquals(9, commands.get(i).inputCapabilityGeneration);
            }
        }
    }

    @Test public void nonAndroidAndUnknownPlatformsKeepExistingWhitespaceKeyContract() {
        for (String platform : new String[]{null, "", "Windows", "Linux", "Android-like", " Android "}) {
            List<AndroidViewerInputQueue.Command> commands = AndroidViewerKeyboard.text("\n\t", 7, 9, platform);
            assertEquals(4, commands.size());
            for (int i = 0; i < commands.size(); i++)
                assertEquals(i % 2 == 0 ? RemoteDeskProtocol.INPUT_KEY_DOWN : RemoteDeskProtocol.INPUT_KEY_UP,
                    commands.get(i).kind);
            assertEquals(13, commands.get(0).data);
            assertEquals(9, commands.get(2).data);
        }
        assertEquals(4, AndroidViewerKeyboard.text("\n\t", 7, 9).size());
    }

    @Test public void androidTextCommitDoesNotChangeExplicitToolbarTabOrQueueBounds() {
        List<AndroidViewerInputQueue.Command> tab = AndroidViewerKeyboard.shortcut(7, 9, 9);
        assertEquals(2, tab.size());
        assertEquals(RemoteDeskProtocol.INPUT_KEY_DOWN, tab.get(0).kind);
        assertEquals(RemoteDeskProtocol.INPUT_KEY_UP, tab.get(1).kind);
        assertEquals(9, tab.get(0).data);
        AndroidViewerInputQueue queue = new AndroidViewerInputQueue(64);
        assertTrue(queue.offerKeyboardBatch(AndroidViewerKeyboard.text("\t".repeat(128), 7, 9, "Android")));
        assertEquals(128, queue.size());
        assertFalse(queue.offerKeyboardBatch(AndroidViewerKeyboard.text("中", 7, 9, "Android")));
        assertTrue(AndroidViewerKeyboard.text("\t".repeat(129), 7, 9, "Android").isEmpty());
    }

    @Test public void composerNewlineAndToolbarEnterReachAndroidTextInsertion() {
        List<AndroidViewerInputQueue.Command> commands = AndroidViewerKeyboard.text("\r\n", 7, 9);
        assertEquals(2, commands.size());
        assertEquals(RemoteDeskProtocol.INPUT_KEY_DOWN, commands.get(0).kind);
        assertEquals(0x0d, commands.get(0).data);
        assertEquals('\n', AndroidInputInjector.textCodePointForKey(commands.get(0).data));
        assertEquals(RemoteDeskProtocol.INPUT_KEY_UP, commands.get(1).kind);
        assertEquals('\n', AndroidInputInjector.textCodePointForKey(
            AndroidViewerKeyboard.shortcut(7, 9, 0x0d).get(0).data));
    }

    @Test public void modifiedEnterIsNotReducedToABareNewline() {
        for (int modifier : new int[]{0x10,0xa0,0xa1,0x11,0xa2,0xa3,0x12,0xa4,0xa5,0x5b,0x5c}) {
            AndroidClipboardShortcutState keys = new AndroidClipboardShortcutState();
            keys.key(modifier, true);
            assertFalse(keys.permitsBareKey(0x0d));
            keys.key(modifier, false);
            assertTrue(keys.permitsBareKey(0x0d));
        }
        for (int key : new int[]{8,9,27,36,122,123,0x41})
            assertEquals(-1, AndroidInputInjector.textCodePointForKey(key));
    }

    @Test public void wireTextRejectsIsolatedSurrogatesAndInvalidScalars() {
        for (int point : new int[]{-1,0xd800,0xdbff,0xdc00,0xdfff,0x110000,Integer.MAX_VALUE})
            assertFalse("invalid Unicode scalar " + point, AndroidInputInjector.isSupportedTextCodePoint(point));
    }

    @Test public void wireTextKeepsChineseEmojiAndSupportedWhitespace() {
        for (int point : new int[]{'a','A','中',0x1f600,0x10ffff,'\n','\t'})
            assertTrue(AndroidInputInjector.isSupportedTextCodePoint(point));
        for (int point : new int[]{0,1,8,13,27,127})
            assertFalse(AndroidInputInjector.isSupportedTextCodePoint(point));
        List<AndroidViewerInputQueue.Command> commands = AndroidViewerKeyboard.text("😀", 1, 2);
        assertEquals(1, commands.size());
        assertEquals(0x1f600, commands.get(0).data);
        assertTrue(AndroidInputInjector.isSupportedTextCodePoint(commands.get(0).data));
    }

    @Test public void screenOffDiscardedKeyUpCannotLeaveModifierHeldAfterWake() {
        assertBlockedTransitionClearsOnlyCurrentOwner(true, false);
    }

    @Test public void automaticUnlockDiscardedKeyUpCannotLeaveModifierHeldAfterUnlock() {
        assertBlockedTransitionClearsOnlyCurrentOwner(false, true);
        assertBlockedTransitionClearsOnlyCurrentOwner(true, true);
    }

    private static void assertBlockedTransitionClearsOnlyCurrentOwner(boolean screenOff, boolean enteringPin) {
        AndroidHostSessionState state = new AndroidHostSessionState();
        AndroidHostSessionState other = new AndroidHostSessionState();
        state.gestureState.clipboardKeys.key(0x11,0x1d,3,true);
        state.gestureState.clipboardKeys.key(0x10,0x36,1,true);
        other.gestureState.clipboardKeys.key(0x12,true);
        assertTrue(state.blocksInputForScreenState(screenOff, enteringPin));
        assertTrue(state.gestureState.clipboardKeys.permitsBareKey(0x0d));
        assertEquals(0, state.gestureState.clipboardKeys.key(0x43,true));
        assertFalse(other.gestureState.clipboardKeys.permitsBareKey(0x0d));
        assertTrue(state.running.get());
        assertTrue(other.running.get());
        assertFalse(state.blocksInputForScreenState(false, false));
        state.gestureState.clipboardKeys.key(0x11,true);
        assertEquals(0x43, state.gestureState.clipboardKeys.key(0x43,true));
        state.tryStop(); other.tryStop();
    }

    @Test public void interactiveScreenKeepsHeldModifiersUntilTheirOwnRelease() {
        AndroidHostSessionState state = new AndroidHostSessionState();
        state.gestureState.clipboardKeys.key(0x11,0x1d,3,true);
        assertFalse(state.blocksInputForScreenState(false, false));
        assertEquals(0x43, state.gestureState.clipboardKeys.key(0x43,true));
        state.gestureState.clipboardKeys.key(0x11,0x1d,3,false);
        assertTrue(state.gestureState.clipboardKeys.permitsBareKey(0x0d));
        state.tryStop();
    }

    @Test public void stoppingOwnerClearsModifiersWithoutStoppingReplacement() {
        AndroidHostSessionState state = new AndroidHostSessionState();
        AndroidHostSessionState replacement = new AndroidHostSessionState();
        state.gestureState.clipboardKeys.key(0xa1,true);
        replacement.gestureState.clipboardKeys.key(0xa3,true);
        state.tryStop();
        assertTrue(state.gestureState.clipboardKeys.permitsBareKey(0x0d));
        assertFalse(state.running.get());
        assertTrue(replacement.running.get());
        assertEquals(0x43, replacement.gestureState.clipboardKeys.key(0x43,true));
        replacement.tryStop();
    }

    @Test public void accessibilityRevocationClearsSessionModifiersBeforePermissionReturns() {
        AndroidHostSessionState state = new AndroidHostSessionState();
        state.gestureState.clipboardKeys.key(0x11,0x1d,3,true);
        state.gestureState.clipboardKeys.key(0x5b,true);
        AndroidInputInjector.onAccessibilityServiceUnavailable();
        assertTrue(state.gestureState.clipboardKeys.permitsBareKey(0x0d));
        assertEquals(0, state.gestureState.clipboardKeys.key(0x56,true));
        assertTrue(state.running.get());
        state.tryStop();
    }
}
