package com.remotedesk.agent;

import org.junit.Test;
import static org.junit.Assert.*;

public class AndroidClipboardShortcutStateTest {
    @Test public void shiftedClipboardChordIsNotSilentlyReducedToPlainControlShortcut() {
        for (int shift : new int[]{0x10,0xa0,0xa1}) {
            AndroidClipboardShortcutState keys = new AndroidClipboardShortcutState();
            keys.key(0x11,true); keys.key(shift,true);
            for (int letter : new int[]{0x41,0x43,0x58,0x56})
                assertEquals("Shift-modified chord must not become plain Ctrl+letter",0,keys.key(letter,true));
            keys.key(shift,false);
            assertEquals(0x43,keys.key(0x43,true));
        }
    }

    @Test public void metaModifiedClipboardChordIsNotSilentlyReducedToPlainControlShortcut() {
        for (int meta : new int[]{0x5b,0x5c}) {
            AndroidClipboardShortcutState keys = new AndroidClipboardShortcutState();
            keys.key(0xa3,true); keys.key(meta,true);
            assertEquals(0,keys.key(0x56,true));
            keys.key(meta,false);
            assertEquals(0x56,keys.key(0x56,true));
        }
    }

    @Test public void plainControlClipboardChordsKeepBothSidedAndLegacySupport() {
        for (int control : new int[]{0x11,0xa2,0xa3}) {
            AndroidClipboardShortcutState keys = new AndroidClipboardShortcutState();
            keys.key(control,true);
            for (int letter : new int[]{0x41,0x43,0x58,0x56}) {
                assertEquals(letter,keys.key(letter,true));
                assertEquals(0,keys.key(letter,false));
            }
            keys.key(control,false);
            assertEquals(0,keys.key(0x56,true));
        }
    }

    @Test public void modifiedAndroidNavigationNeverBecomesABareGlobalAction() {
        for (int modifier : new int[]{0x10,0xa0,0xa1,0x11,0xa2,0xa3,0x12,0xa4,0xa5,0x5b,0x5c}) {
            AndroidClipboardShortcutState keys = new AndroidClipboardShortcutState();
            keys.key(modifier,true);
            for (int key : new int[]{8,9,27,36,122,123})
                assertFalse("Held modifier must not be ignored for Android navigation",keys.permitsBareKey(key));
            keys.key(modifier,false);
            for (int key : new int[]{8,9,27,36,122,123}) assertTrue(keys.permitsBareKey(key));
        }
    }

    @Test public void modifierKeysThemselvesAreNeverBareActions() {
        AndroidClipboardShortcutState keys = new AndroidClipboardShortcutState();
        for (int modifier : new int[]{0x10,0xa0,0xa1,0x11,0xa2,0xa3,0x12,0xa4,0xa5,0x5b,0x5c})
            assertFalse(keys.permitsBareKey(modifier));
    }

    @Test public void genericPhysicalControlSidesRemainHeldIndependently() {
        AndroidClipboardShortcutState keys = new AndroidClipboardShortcutState();
        keys.key(0x11,0x1d,1,true); // Left Ctrl via Windows WM_KEYDOWN.
        keys.key(0x11,0x1d,3,true); // Right Ctrl via E0 + 1D.
        keys.key(0x11,0x1d,1,false);
        assertEquals(0x43,keys.key(0x43,true));
        assertFalse(keys.permitsBareKey(36));
        keys.key(0x11,0x1d,3,false);
        assertEquals(0,keys.key(0x43,true));
        assertTrue(keys.permitsBareKey(36));
    }

    @Test public void genericPhysicalShiftSidesRemainHeldEvenForLegacyRightExtendedFlag() {
        for (int rightFlags : new int[]{1,3}) {
            AndroidClipboardShortcutState keys = new AndroidClipboardShortcutState();
            keys.key(0xa3,true);
            keys.key(0x10,0x2a,1,true);
            keys.key(0x10,0x36,rightFlags,true);
            keys.key(0x10,0x2a,1,false);
            assertEquals(0,keys.key(0x56,true));
            keys.key(0x10,0x36,1,false);
            assertEquals(0x56,keys.key(0x56,true));
        }
    }

    @Test public void bothAltSidesAndAltGrDoNotTriggerPlainClipboardCommands() {
        AndroidClipboardShortcutState keys = new AndroidClipboardShortcutState();
        keys.key(0x11,true);
        keys.key(0x12,0x38,1,true);
        keys.key(0x12,0x38,3,true);
        keys.key(0x12,0x38,1,false);
        assertEquals(0,keys.key(0x56,true));
        keys.key(0x12,0x38,3,false);
        assertEquals(0x56,keys.key(0x56,true));
    }

    @Test public void genericAndSidedLegacyShortcutsRemainIndependentOfPhysicalControl() {
        AndroidClipboardShortcutState keys = new AndroidClipboardShortcutState();
        keys.key(0x11,0x1d,3,true);
        keys.key(0x11,true);
        keys.key(0x11,false);
        assertEquals(0x58,keys.key(0x58,true));
        keys.key(0xa3,false);
        assertEquals(0,keys.key(0x58,true));
    }

    @Test public void onlyKnownModifierScanMetadataIsNormalized() {
        assertEquals(0xa0,AndroidClipboardShortcutState.modifierIdentity(0x10,0x2a,1));
        assertEquals(0xa1,AndroidClipboardShortcutState.modifierIdentity(0x10,0x36,3));
        assertEquals(0xa2,AndroidClipboardShortcutState.modifierIdentity(0x11,0x1d,1));
        assertEquals(0xa3,AndroidClipboardShortcutState.modifierIdentity(0x11,0x1d,3));
        assertEquals(0xa4,AndroidClipboardShortcutState.modifierIdentity(0x12,0x38,1));
        assertEquals(0xa5,AndroidClipboardShortcutState.modifierIdentity(0x12,0x38,3));
        for (int flags : new int[]{0,2,5,-1})
            assertEquals(0x10,AndroidClipboardShortcutState.modifierIdentity(0x10,0x36,flags));
        assertEquals(0x11,AndroidClipboardShortcutState.modifierIdentity(0x11,0x36,1));
        assertEquals(0xa2,AndroidClipboardShortcutState.modifierIdentity(0xa3,0x1d,1));
        assertEquals(0x41,AndroidClipboardShortcutState.modifierIdentity(0x41,0x36,1));
    }

    @Test public void scanIdentityWinsWhenGenericAndSidedEventSourcesChange() {
        int[][] cases={{0x10,0xa0,0xa1,0x2a,1},{0x10,0xa1,0xa0,0x36,3},
            {0x11,0xa2,0xa3,0x1d,1},{0x11,0xa3,0xa2,0x1d,3},
            {0x12,0xa4,0xa5,0x38,1},{0x12,0xa5,0xa4,0x38,3}};
        for(int[] value:cases){
            assertEquals(value[1],AndroidClipboardShortcutState.modifierIdentity(value[2],value[3],value[4]));
            AndroidClipboardShortcutState keys=new AndroidClipboardShortcutState();
            keys.key(value[2],value[3],value[4],true);
            keys.key(value[0],value[3],value[4],false);
            assertTrue(keys.permitsBareKey(36));
            keys.key(value[0],value[3],value[4],true);
            keys.key(value[2],value[3],value[4],false);
            assertTrue(keys.permitsBareKey(36));
        }
    }

    @Test public void physicalReleaseAlsoClearsAnAmbiguousLegacyGenericPress() {
        int[][] cases={{0x10,0x36,1},{0x11,0x1d,3},{0x12,0x38,3}};
        for(int[] value:cases){
            AndroidClipboardShortcutState keys=new AndroidClipboardShortcutState();
            keys.key(value[0],true);
            keys.key(value[0],value[1],value[2],false);
            assertTrue(keys.permitsBareKey(36));
        }
    }

    @Test public void exactPhysicalReleaseRetainsAnIndependentLegacyGenericOwner() {
        int[][] cases={{0x10,0x36,1},{0x11,0x1d,3},{0x12,0x38,3}};
        for(int[] value:cases){
            AndroidClipboardShortcutState keys=new AndroidClipboardShortcutState();
            keys.key(value[0],true);
            keys.key(value[0],value[1],value[2],true);
            keys.key(value[0],value[1],value[2],false);
            assertFalse("The exact physical owner must not clear a separate legacy owner",keys.permitsBareKey(36));
            keys.key(value[0],false);
            assertTrue(keys.permitsBareKey(36));
        }
    }

    @Test public void unscopedLegacyReleaseCannotLeavePhysicalSidesLatched() {
        AndroidClipboardShortcutState keys=new AndroidClipboardShortcutState();
        keys.key(0x11,0x1d,1,true); keys.key(0x11,0x1d,3,true); keys.key(0xa1,true);
        keys.key(0x11,false);
        keys.key(0xa1,false);
        assertTrue(keys.permitsBareKey(36));
        assertEquals(0,keys.key(0x56,true));
    }

    @Test public void legacyReleaseWithItsOwnTrackedAliasDoesNotReleaseKnownOtherSide() {
        AndroidClipboardShortcutState keys=new AndroidClipboardShortcutState();
        keys.key(0x11,0x1d,3,true); keys.key(0x11,true);
        keys.key(0x11,false);
        assertEquals(0x43,keys.key(0x43,true));
        keys.key(0x11,0x1d,3,false);
        assertTrue(keys.permitsBareKey(36));
    }

    @Test public void resettingAuthorityClearsAllPhysicalAndLegacyModifiers() {
        AndroidClipboardShortcutState keys = new AndroidClipboardShortcutState();
        keys.key(0x11,0x1d,3,true); keys.key(0x10,0x36,1,true); keys.key(0xa5,true); keys.key(0x5b,true);
        keys.reset();
        assertEquals(0,keys.key(0x56,true));
        assertTrue(keys.permitsBareKey(9));
        assertTrue(keys.permitsBareKey(36));
    }

    @Test public void explicitUnicodeTextStillDeterminesCaseInsteadOfModifierGuessing() {
        java.util.List<AndroidViewerInputQueue.Command> commands=AndroidViewerKeyboard.text("aA!中文",0,0);
        assertEquals(5,commands.size());
        int[] points="aA!中文".codePoints().toArray();
        for(int i=0;i<points.length;i++){
            assertEquals(RemoteDeskProtocol.INPUT_TEXT,commands.get(i).kind);
            assertEquals(points[i],commands.get(i).data);
        }
    }
}
