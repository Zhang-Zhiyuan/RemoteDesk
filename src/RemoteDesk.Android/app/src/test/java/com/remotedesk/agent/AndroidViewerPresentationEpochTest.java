package com.remotedesk.agent;
import org.junit.Test;
import static org.junit.Assert.*;

public class AndroidViewerPresentationEpochTest {
    @Test public void sameCodecKeepsVersionStable() { AndroidViewerPresentationEpoch p=new AndroidViewerPresentationEpoch(); assertEquals(p.accept(1),p.accept(1)); }
    @Test public void jpegPreviewCannotCoverNewH264() { AndroidViewerPresentationEpoch p=new AndroidViewerPresentationEpoch(); long jpeg=p.accept(1); p.accept(2); assertFalse(p.current(jpeg,1)); }
    @Test public void fallbackThenReturnDoesNotReuseOldJpegJob() { AndroidViewerPresentationEpoch p=new AndroidViewerPresentationEpoch(); long old=p.accept(1); p.accept(2); long current=p.accept(1); assertNotEquals(old,current); assertFalse(p.current(old,1)); assertTrue(p.current(current,1)); }
    @Test public void screenChangeRejectsOldFramesWithSameDimensionsAndCodec() { AndroidViewerPresentationEpoch p=new AndroidViewerPresentationEpoch(); long old=p.accept(2); p.invalidate(); long next=p.accept(2); assertFalse(p.current(old,2)); assertTrue(p.current(next,2)); }
    @Test public void invalidationHasNoReadyCodec() { AndroidViewerPresentationEpoch p=new AndroidViewerPresentationEpoch(); p.accept(1); p.invalidate(); assertEquals(0,p.encoding()); }
}
