package com.remotedesk.agent;
import java.util.ArrayList;
import java.util.List;
import org.junit.Test;
import static org.junit.Assert.*;

public class AndroidViewerGesturesTest {
    private final List<int[]> events = new ArrayList<>();
    private AndroidViewerGestures gestures() {
        AndroidViewerViewport v = new AndroidViewerViewport(); v.geometry(1000,1000,1920,1080);
        AndroidViewerGestures g = new AndroidViewerGestures(v,(k,b,x,y,d)->events.add(new int[]{k,b,x,y,d}),1);
        g.centerCursor(); return g;
    }
    private long count(int kind) { return events.stream().filter(e->e[0]==kind).count(); }
    @Test public void screenTransitionStillAllowsRelease() { assertTrue(AndroidViewerGestures.maySend(3,true,false)); }
    @Test public void screenTransitionBlocksNewInput() { for (int kind : new int[]{1,2,4,5,6,7}) assertFalse(AndroidViewerGestures.maySend(kind,true,false)); }
    @Test public void oldOrViewOnlyOwnerCannotSendEvenRelease() { assertFalse(AndroidViewerGestures.maySend(3,false,true)); assertFalse(AndroidViewerGestures.maySend(3,false,false)); }
    @Test public void touchingPadDoesNotPressMouse() { AndroidViewerGestures g=gestures(); g.down(400,500,0); assertTrue(events.isEmpty()); }
    @Test public void padMovesWithoutAccidentalDrag() { AndroidViewerGestures g=gestures(); g.down(100,100,0); g.move(200,120); g.up(200,120,100); assertEquals(0,count(2)); assertEquals(1,count(1)); assertTrue(g.cursorX>960); }
    @Test public void tapIsOneBalancedClick() { AndroidViewerGestures g=gestures(); g.down(100,100,0); g.up(100,100,100); assertEquals(1,count(2)); assertEquals(1,count(3)); }
    @Test public void longPressDragReleasesOnCancel() { AndroidViewerGestures g=gestures(); g.down(400,500,0); assertTrue(g.longPress()); g.move(450,520); g.cancel(); assertEquals(1,count(2)); assertEquals(1,count(3)); assertFalse(g.dragging); }
    @Test public void modeChangeReleasesLatchedDrag() { AndroidViewerGestures g=gestures(); g.toggleDrag(); g.mode(false); assertFalse(g.lockedDrag); assertEquals(1,count(3)); }
    @Test public void lockedDragSurvivesFingerLiftUntilExplicitRelease() { AndroidViewerGestures g=gestures(); g.toggleDrag(); g.down(200,200,0); g.move(250,250); g.up(250,250,100); assertEquals(0,count(3)); g.toggleDrag(); assertEquals(1,count(3)); }
    @Test public void directTapRejectsLetterbox() { AndroidViewerGestures g=gestures(); g.mode(false); g.down(10,10,0); g.up(10,10,100); assertTrue(events.isEmpty()); }
    @Test public void directDragWaitsForMovement() { AndroidViewerGestures g=gestures(); g.mode(false); g.down(500,500,0); assertEquals(0,count(2)); g.move(600,500); assertEquals(1,count(2)); g.up(620,500,100); assertEquals(1,count(3)); }
    @Test public void twoFingerTapIsRightClickWithoutLeftClick() { AndroidViewerGestures g=gestures(); g.down(300,500,0); g.secondDown(400,500,200); g.pointerUp(); g.up(500,500,150); assertEquals(1,count(2)); assertEquals(2,events.get(0)[1]); }
    @Test public void pinchOnlyChangesLocalViewport() { AndroidViewerGestures g=gestures(); g.down(300,500,0); g.secondDown(500,500,200); g.multiMove(500,500,400); g.pointerUp(); g.up(500,500,500); assertEquals(2,g.viewport.zoom,0.01); assertTrue(events.isEmpty()); }
    @Test public void twoFingerScrollDoesNotZoomOrClick() { AndroidViewerGestures g=gestures(); g.down(300,500,0); g.secondDown(500,500,200); g.multiMove(500,548,200); g.pointerUp(); g.up(500,548,200); assertEquals(1,count(4)); assertEquals(0,count(2)); assertEquals(1,g.viewport.zoom,0); }
    @Test public void liftingOneFingerCannotResumeSingleFingerDrag() { AndroidViewerGestures g=gestures(); g.down(300,500,0); g.secondDown(500,500,200); g.multiMove(500,550,200); g.pointerUp(); g.move(800,600); g.up(800,600,300); assertEquals(0,count(2)); }
    @Test public void secondFingerReleasesExistingDrag() { AndroidViewerGestures g=gestures(); g.down(300,500,0); g.longPress(); g.secondDown(500,500,200); assertFalse(g.dragging); assertEquals(1,count(3)); }
    @Test public void pendingLongPressCannotOutliveCancellation() { AndroidViewerGestures g=gestures(); g.down(300,500,0); g.cancel(); assertFalse(g.longPress()); assertTrue(events.isEmpty()); }
    @Test public void directLetterboxAllowsLocalPinchWithoutRemoteClick() {
        AndroidViewerGestures g=gestures(); g.mode(false); g.down(300,100,0);
        g.secondDown(500,100,200); g.multiMove(500,100,400); g.pointerUp(); g.up(600,100,200);
        assertEquals(2,g.viewport.zoom,.001); assertTrue(events.isEmpty());
    }
    @Test public void directLetterboxNeverStartsDragOrScroll() {
        AndroidViewerGestures g=gestures(); g.mode(false); g.down(300,100,0);
        assertFalse(g.longPress()); g.move(400,500);
        g.secondDown(500,500,200); g.multiMove(500,600,200); g.pointerUp(); g.up(500,600,250);
        assertTrue(events.isEmpty());
    }
    @Test public void gradualPinchUsesOriginalSpanWhenRecognitionStarts() {
        AndroidViewerGestures g=gestures(); g.down(300,500,0); g.secondDown(500,500,200);
        for(int span=202;span<=400;span+=2) g.multiMove(500,500,span);
        assertEquals(2,g.viewport.zoom,.001); assertTrue(events.isEmpty());
    }
    @Test public void addingSecondFingerAfterDragDoesNotBecomeRightClick() {
        AndroidViewerGestures g=gestures(); g.mode(false); g.down(300,500,0); g.move(350,500);
        g.secondDown(450,500,200); g.pointerUp(); g.up(450,500,200);
        assertEquals(1,count(2)); assertEquals(1,count(3));
    }
    @Test public void canceledLetterboxGestureCannotResumeAsPinch() {
        AndroidViewerGestures g=gestures(); g.mode(false); g.down(300,100,0); g.cancel();
        g.secondDown(500,100,200); g.multiMove(500,100,400);
        assertEquals(1,g.viewport.zoom,0); assertTrue(events.isEmpty());
    }

    @Test public void directTapPlacesCursorAndClickAtTheTouchedDesktopPoint() {
        AndroidViewerGestures g=gestures(); g.mode(false);
        int[] expected=g.viewport.point(700,600,true);
        assertNotNull(expected);
        g.down(700,600,0); g.up(700,600,100);
        assertEquals(1,count(RemoteDeskProtocol.INPUT_MOUSE_MOVE));
        assertEquals(1,count(RemoteDeskProtocol.INPUT_MOUSE_DOWN));
        assertEquals(1,count(RemoteDeskProtocol.INPUT_MOUSE_UP));
        for(int[] event : events) {
            assertEquals(expected[0],event[2]); assertEquals(expected[1],event[3]);
        }
    }

    @Test public void directTapRemainsAccurateAfterLocalZoomAndPan() {
        AndroidViewerGestures g=gestures(); g.mode(false);
        g.viewport.zoomAt(2,500,500); g.viewport.pan(-100,50);
        int[] expected=g.viewport.point(700,600,true);
        assertNotNull(expected);
        g.down(700,600,0); g.up(700,600,100);
        for(int[] event : events) {
            assertEquals(expected[0],event[2]); assertEquals(expected[1],event[3]);
        }
    }

    @Test public void twoFingerVerticalScrollWorksInBothInputModesAndDirections() {
        for(boolean trackpad : new boolean[]{true,false}) {
            for(int direction : new int[]{-1,1}) {
                events.clear();
                AndroidViewerGestures g=gestures(); g.mode(trackpad);
                g.down(400,500,0); g.secondDown(500,500,200);
                g.multiMove(500,500+direction*48,200);
                g.pointerUp(); g.up(500,500+direction*48,200);
                assertEquals(1,count(RemoteDeskProtocol.INPUT_MOUSE_WHEEL));
                int[] wheel=events.stream().filter(e->e[0]==RemoteDeskProtocol.INPUT_MOUSE_WHEEL).findFirst().get();
                assertEquals(direction*120,wheel[4]);
                assertEquals(0,count(RemoteDeskProtocol.INPUT_MOUSE_DOWN));
                assertEquals(1,g.viewport.zoom,0);
            }
        }
    }

    @Test public void replacingAFingerAfterScrollingMustNotBecomeARightClick() {
        AndroidViewerGestures g=gestures();
        g.down(400,500,0); g.secondDown(500,500,200);
        g.multiMove(500,548,200); g.pointerUp();
        g.secondDown(500,548,200); g.pointerUp(); g.up(500,548,250);
        assertEquals(1,count(RemoteDeskProtocol.INPUT_MOUSE_WHEEL));
        assertEquals(0,count(RemoteDeskProtocol.INPUT_MOUSE_DOWN));
        assertEquals(0,count(RemoteDeskProtocol.INPUT_MOUSE_UP));
    }

    @Test public void replacingAFingerDuringTwoFingerTapCancelsTheTap() {
        AndroidViewerGestures g=gestures();
        g.down(400,500,0); g.secondDown(500,500,200); g.pointerUp();
        g.secondDown(500,500,200); g.pointerUp(); g.up(500,500,250);
        assertTrue(events.isEmpty());
    }

    private int wheelTotal() { return events.stream().filter(e -> e[0] == 4).mapToInt(e -> e[4]).sum(); }

    @Test public void unevenFingerMotionScrollsInsteadOfZooming() {
        AndroidViewerGestures g = gestures(); g.down(400,500,0); g.secondDown(500,500,200);
        g.multiMove(501,510,216); // one finger reaches its sample before the other
        for (int dy = 12; dy <= 108; dy += 4) g.multiMove(500 + dy % 3,500 + dy,200 + dy % 19);
        assertEquals(1,g.viewport.zoom,0); assertTrue(wheelTotal() > 0); assertEquals(0,count(2));
    }
    @Test public void scrollingStaysScrollingDespiteLaterSpanChanges() {
        AndroidViewerGestures g = gestures(); g.down(400,500,0); g.secondDown(500,500,200);
        g.multiMove(500,548,200); g.multiMove(500,590,280);
        assertEquals(1,g.viewport.zoom,0); assertTrue(wheelTotal() >= 240);
    }
    @Test public void intentionalPinchWithSmallCentroidDriftStillZooms() {
        AndroidViewerGestures g = gestures(); g.down(400,500,0); g.secondDown(500,500,200);
        g.multiMove(504,510,250); g.multiMove(506,512,400);
        assertEquals(2,g.viewport.zoom,.001); assertEquals(0,count(4));
    }
    @Test public void horizontalMovementIsNotVerticalScrollOrRightClick() {
        AndroidViewerGestures g = gestures(); g.down(400,500,0); g.secondDown(500,500,200);
        g.multiMove(560,511,206); g.pointerUp(); g.up(560,511,200);
        assertEquals(0,count(4)); assertEquals(0,count(2)); assertEquals(1,g.viewport.zoom,0);
    }
    @Test public void replacingFingerResumesScrollWithoutJumpOrClick() {
        AndroidViewerGestures g = gestures(); g.down(400,500,0); g.secondDown(500,500,200);
        g.multiMove(500,548,200); g.pointerUp(); g.move(800,700);
        int before = wheelTotal(); g.secondDown(700,700,350); g.multiMove(700,700,350);
        assertEquals(before,wheelTotal());
        g.multiMove(700,748,350); g.pointerUp(); g.up(700,748,300);
        assertEquals(before + 120,wheelTotal()); assertEquals(0,count(2)); assertEquals(1,g.viewport.zoom,0);
    }
    @Test public void replacingFingerResumesPinchFromNewSpanWithoutJump() {
        AndroidViewerGestures g = gestures(); g.down(400,500,0); g.secondDown(500,500,200);
        g.multiMove(500,500,400); g.pointerUp(); g.secondDown(500,500,100);
        g.multiMove(500,500,100); assertEquals(2,g.viewport.zoom,.001);
        g.multiMove(500,500,150); assertEquals(3,g.viewport.zoom,.001); assertEquals(0,count(4));
    }
    @Test public void reversalDoesNotSpendNewMotionCancelingOldWheelResidue() {
        AndroidViewerGestures g = gestures(); g.down(400,500,0); g.secondDown(500,500,200);
        g.multiMove(500,560,200); assertEquals(120,wheelTotal());
        g.multiMove(500,518,200); assertEquals(0,wheelTotal());
    }
    @Test public void subPixelJitterDoesNotAccumulateSpuriousWheelTicks() {
        AndroidViewerGestures g = gestures(); g.down(400,500,0); g.secondDown(500,500,200);
        g.multiMove(500,548,200); int before = wheelTotal();
        for (int i = 0; i < 500; i++) g.multiMove(500,548 + (i % 2 == 0 ? .6f : -.6f),200);
        assertEquals(before,wheelTotal());
    }
    @Test public void tinyForwardStepsAccumulateRatherThanBeingDropped() {
        AndroidViewerGestures g = gestures(); g.down(400,500,0); g.secondDown(500,500,200);
        for (int i = 1; i <= 200; i++) g.multiMove(500,500 + i * .5f,200);
        assertEquals(360,wheelTotal());
    }
    @Test public void directScrollTargetsTheMiddleOfTwoFingers() {
        AndroidViewerGestures g = gestures(); g.mode(false);
        int[] expected = g.viewport.point(600,500,true);
        g.down(400,500,0); g.secondDown(600,500,400); g.multiMove(600,548,400);
        int[] wheel = events.stream().filter(e -> e[0] == 4).findFirst().get();
        assertEquals(expected[0],wheel[2]); assertEquals(expected[1],wheel[3]);
    }
    @Test public void trackpadScrollPreservesTheMouseTarget() {
        AndroidViewerGestures g = gestures(); g.cursorX=100; g.cursorY=200;
        g.down(400,500,0); g.secondDown(600,500,400); g.multiMove(600,548,400);
        int[] wheel = events.stream().filter(e -> e[0] == 4).findFirst().get();
        assertEquals(100,wheel[2]); assertEquals(200,wheel[3]);
    }
    @Test public void scrollDistanceIsIndependentOfPhoneDensityAndDesktopZoom() {
        for (float density : new float[]{1,2,3.5f}) for (float zoom : new float[]{1,2}) {
            events.clear(); AndroidViewerViewport viewport = new AndroidViewerViewport();
            viewport.geometry(Math.round(1000*density),Math.round(1000*density),1920,1080); viewport.zoomAt(zoom,500*density,500*density);
            AndroidViewerGestures g = new AndroidViewerGestures(viewport,(k,b,x,y,d)->events.add(new int[]{k,b,x,y,d}),density);
            g.centerCursor(); g.down(400*density,500*density,0); g.secondDown(500*density,500*density,200*density);
            g.multiMove(500*density,600*density,200*density);
            assertEquals(360,wheelTotal());
        }
    }
}
