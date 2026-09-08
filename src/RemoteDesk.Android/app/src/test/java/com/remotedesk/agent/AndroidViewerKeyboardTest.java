package com.remotedesk.agent;
import java.util.List;
import org.junit.Test;
import static org.junit.Assert.*;

public class AndroidViewerKeyboardTest {
    @Test public void chineseAndEmojiAreCodePointsNotSurrogateKeystrokes() { List<AndroidViewerInputQueue.Command> b=AndroidViewerKeyboard.text("A中文😀",4,9); assertEquals(4,b.size()); assertEquals(0x1f600,b.get(3).data); assertEquals(9,b.get(0).inputCapabilityGeneration); }
    @Test public void shortcutReleasesKeysInReverseOrder() { List<AndroidViewerInputQueue.Command> b=AndroidViewerKeyboard.shortcut(1,2,0x11,0x43); assertEquals(4,b.size()); assertEquals(0x43,b.get(2).data); assertEquals(0x11,b.get(3).data); assertEquals(6,b.get(3).kind); }
    @Test public void newlineAndTabArePhysicalBalancedKeys() { List<AndroidViewerInputQueue.Command> b=AndroidViewerKeyboard.text("\r\n\t",0,0); assertEquals(4,b.size()); assertEquals(13,b.get(0).data); assertEquals(9,b.get(2).data); }
    @Test public void invalidTextIsNotSent() { assertTrue(AndroidViewerKeyboard.text(null,0,0).isEmpty()); assertTrue(AndroidViewerKeyboard.text("a".repeat(129),0,0).isEmpty()); assertTrue(AndroidViewerKeyboard.text("\uD800\u0001",0,0).isEmpty()); }
    @Test public void fullQueueRejectsEntireShortcutWithoutPartialCtrl() { AndroidViewerInputQueue q=new AndroidViewerInputQueue(4); q.offer(new AndroidViewerInputQueue.Command(7,0,0,0,65)); assertFalse(q.offerKeyboardBatch(AndroidViewerKeyboard.shortcut(0,0,17,67))); assertEquals(1,q.size()); }
    @Test public void oneLargeCommitIsBoundedAndStillReservesMouseRelease() { AndroidViewerInputQueue q=new AndroidViewerInputQueue(64); assertTrue(q.offerKeyboardBatch(AndroidViewerKeyboard.text("a".repeat(128),0,0))); assertFalse(q.offerKeyboardBatch(AndroidViewerKeyboard.text("b",0,0))); assertTrue(q.offer(new AndroidViewerInputQueue.Command(3,1,0,0,0))); assertFalse(q.offer(new AndroidViewerInputQueue.Command(2,1,0,0,0))); assertEquals(129,q.size()); }
    @Test public void drainingBatchRestoresNormalReleaseBound() { AndroidViewerInputQueue q=new AndroidViewerInputQueue(2); q.offerKeyboardBatch(AndroidViewerKeyboard.text("a".repeat(128),0,0)); while(q.poll()!=null) {} for(int i=0;i<18;i++) assertTrue(q.offer(new AndroidViewerInputQueue.Command(6,0,0,0,65))); assertFalse(q.offer(new AndroidViewerInputQueue.Command(6,0,0,0,65))); }
    @Test public void closedQueueNeverAcceptsComposition() { AndroidViewerInputQueue q=new AndroidViewerInputQueue(64); q.close(); assertFalse(q.offerKeyboardBatch(AndroidViewerKeyboard.text("hello",0,0))); }
    @Test public void pointerCommandsCannotUseKeyboardBatchAllowance() { AndroidViewerInputQueue q=new AndroidViewerInputQueue(64); assertFalse(q.offerKeyboardBatch(java.util.Collections.singletonList(new AndroidViewerInputQueue.Command(2,1,0,0,0)))); }
    @Test public void captureSelectionRoundTripsAndRejectsEmptyIds() throws Exception { RemoteDeskTransport.ControlMessage c=RemoteDeskTransport.decodeControl(RemoteDeskTransport.encodeSelectCaptureTarget("monitor:2")); assertEquals(2,c.kind); assertEquals("monitor:2",c.text); try {RemoteDeskTransport.encodeSelectCaptureTarget(""); fail();} catch(java.io.IOException expected) {} }
}
