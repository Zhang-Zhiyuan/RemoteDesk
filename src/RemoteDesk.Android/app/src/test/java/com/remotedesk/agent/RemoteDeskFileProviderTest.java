package com.remotedesk.agent;

import static org.junit.Assert.assertEquals;

import org.junit.Test;

public final class RemoteDeskFileProviderTest {
    @Test
    public void extensionForMimeLookupHandlesPlainFileNames() {
        assertEquals("pdf", RemoteDeskFileProvider.extensionForMimeLookup("report final.PDF"));
        assertEquals("zip", RemoteDeskFileProvider.extensionForMimeLookup("接收 文件 (1).ZIP"));
        assertEquals("gz", RemoteDeskFileProvider.extensionForMimeLookup("archive.tar.gz"));
    }

    @Test
    public void extensionForMimeLookupIgnoresMissingOrUnsafeExtensions() {
        assertEquals("", RemoteDeskFileProvider.extensionForMimeLookup(".nomedia"));
        assertEquals("", RemoteDeskFileProvider.extensionForMimeLookup("filename."));
        assertEquals("", RemoteDeskFileProvider.extensionForMimeLookup("filename"));
        assertEquals("", RemoteDeskFileProvider.extensionForMimeLookup(null));
    }
}
