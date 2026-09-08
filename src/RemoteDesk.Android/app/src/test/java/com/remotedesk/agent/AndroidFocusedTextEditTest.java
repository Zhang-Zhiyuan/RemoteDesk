package com.remotedesk.agent;

import static org.junit.Assert.assertEquals;
import org.junit.Test;

public final class AndroidFocusedTextEditTest {
    @Test public void emptyHintIsNotInsertedIntoTheField() {
        AndroidFocusedTextEdit edit = AndroidFocusedTextEdit.create("Remote text input target", true, -1, -1, "R", false);
        assertEquals("R", edit.text);
        assertEquals(1, edit.cursor);
    }
    @Test public void hintSelectionIsClampedToEmptyContent() {
        assertEquals("新", AndroidFocusedTextEdit.create("hint", true, 3, 4, "新", false).text);
    }
    @Test public void insertionPreservesTextAfterCursor() {
        AndroidFocusedTextEdit edit = AndroidFocusedTextEdit.create("abcd", false, 2, 2, "中", false);
        assertEquals("ab中cd", edit.text);
        assertEquals(3, edit.cursor);
    }
    @Test public void reversedSelectionIsReplaced() {
        AndroidFocusedTextEdit edit = AndroidFocusedTextEdit.create("abcd", false, 3, 1, "X", false);
        assertEquals("aXd", edit.text);
        assertEquals(2, edit.cursor);
    }
    @Test public void absentSelectionAppendsToEnd() {
        assertEquals("abc42", AndroidFocusedTextEdit.create("abc", false, -1, -1, "42", false).text);
    }
    @Test public void oversizedSelectionCannotCrash() {
        assertEquals("abc!", AndroidFocusedTextEdit.create("abc", false, 999, 999, "!", false).text);
    }
    @Test public void backspaceDeletesAWholeSupplementaryCharacter() {
        AndroidFocusedTextEdit edit = AndroidFocusedTextEdit.create("A\ud83d\ude00B", false, 3, 3, "", true);
        assertEquals("AB", edit.text);
        assertEquals(1, edit.cursor);
    }
    @Test public void backspaceAtStartDoesNothing() {
        assertEquals("abc", AndroidFocusedTextEdit.create("abc", false, 0, 0, "", true).text);
    }
    @Test public void nullTextCanBeEdited() {
        assertEquals("abc", AndroidFocusedTextEdit.create(null, false, -1, -1, "abc", false).text);
    }
    @Test public void refreshedSnapshotsRetainBurstCharacters() {
        String result = "";
        for (int codePoint : "RemoteDesk实测42".codePoints().toArray()) {
            result = AndroidFocusedTextEdit.create(result, false, result.length(), result.length(),
                new String(Character.toChars(codePoint)), false).text;
        }
        assertEquals("RemoteDesk实测42", result);
    }
}
