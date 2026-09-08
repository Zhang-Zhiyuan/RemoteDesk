package com.remotedesk.agent;

import static org.junit.Assert.assertArrayEquals;
import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertThrows;
import static org.junit.Assert.assertTrue;

import java.io.File;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.attribute.FileTime;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.util.Arrays;
import java.util.Locale;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicReference;

import org.junit.Test;

public final class AndroidFileTransferReceiverTest {
    @Test
    public void completeSavesFileUsingReceiveDirectoryOverride() throws IOException {
        File receiveDirectory = Files.createTempDirectory("RemoteDesk.AndroidFileTransferReceiverTest.").toFile();
        try {
            AndroidFileTransferReceiver receiver = new AndroidFileTransferReceiver(receiveDirectory);
            String transferId = "android-transfer-success";
            byte[] fileBytes = new byte[] {1, 2, 3, 4};

            receiver.start(startMessage(transferId, "saved.bin", fileBytes.length));
            receiver.writeChunk(chunkMessage(transferId, 0, fileBytes));
            receiver.setExpectedChecksum(checksumMessage(transferId, sha256Hex(fileBytes)));
            String message = receiver.complete(completeMessage(transferId));

            File savedFile = new File(receiveDirectory, "saved.bin");
            assertTrue(message.contains("SHA-256 已校验"));
            assertTrue(savedFile.exists());
            assertArrayEquals(fileBytes, Files.readAllBytes(savedFile.toPath()));
            assertNoOwnedTemporaryFiles(receiveDirectory);
        } finally {
            deleteRecursively(receiveDirectory);
        }
    }

    @Test
    public void cancelledPublicationDeletesPreparedTemporaryFile() throws IOException {
        File receiveDirectory = Files.createTempDirectory(
            "RemoteDesk.AndroidFileTransferReceiverTest.").toFile();
        try {
            AndroidFileTransferReceiver receiver =
                new AndroidFileTransferReceiver(receiveDirectory);
            String transferId = "android-transfer-cancelled-publication";
            byte[] fileBytes = new byte[] {9, 8, 7, 6};

            receiver.start(startMessage(
                transferId,
                "cancelled-publication.bin",
                fileBytes.length));
            receiver.writeChunk(chunkMessage(transferId, 0, fileBytes));
            receiver.setExpectedChecksum(checksumMessage(
                transferId,
                sha256Hex(fileBytes)));

            assertThrows(IOException.class, () -> receiver.complete(
                completeMessage(transferId),
                () -> true));

            assertFalse(new File(
                receiveDirectory,
                "cancelled-publication.bin").exists());
            assertNoOwnedTemporaryFiles(receiveDirectory);
        } finally {
            deleteRecursively(receiveDirectory);
        }
    }

    @Test
    public void preparedCompletionDoesNotRaceTheNextFileStart() throws IOException {
        File receiveDirectory = Files.createTempDirectory(
            "RemoteDesk.AndroidFileTransferReceiverTest.").toFile();
        try {
            AndroidFileTransferReceiver receiver =
                new AndroidFileTransferReceiver(receiveDirectory);
            byte[] firstBytes = new byte[] {1, 2, 3};
            byte[] secondBytes = new byte[] {4, 5, 6};

            receiver.start(startMessage("first", "first.bin", firstBytes.length));
            receiver.writeChunk(chunkMessage("first", 0, firstBytes));
            receiver.setExpectedChecksum(checksumMessage(
                "first",
                sha256Hex(firstBytes)));
            AndroidFileTransferReceiver.PreparedCompletion first =
                receiver.prepareCompletion(completeMessage("first"));

            // Desktop batch sends do not wait for the first publication
            // status before starting the next file.
            receiver.start(startMessage("second", "second.bin", secondBytes.length));
            receiver.publishCompletion(first, () -> false);
            receiver.writeChunk(chunkMessage("second", 0, secondBytes));
            receiver.setExpectedChecksum(checksumMessage(
                "second",
                sha256Hex(secondBytes)));
            receiver.complete(completeMessage("second"));

            assertArrayEquals(
                firstBytes,
                Files.readAllBytes(new File(
                    receiveDirectory,
                    "first.bin").toPath()));
            assertArrayEquals(
                secondBytes,
                Files.readAllBytes(new File(
                    receiveDirectory,
                    "second.bin").toPath()));
            assertNoOwnedTemporaryFiles(receiveDirectory);
        } finally {
            deleteRecursively(receiveDirectory);
        }
    }

    @Test
    public void completeSavesLongChineseNameWithinUtf8FileSystemLimit() throws IOException {
        File receiveDirectory = Files.createTempDirectory("RemoteDesk.AndroidFileTransferReceiverTest.").toFile();
        try {
            AndroidFileTransferReceiver receiver = new AndroidFileTransferReceiver(receiveDirectory);
            String transferId = "android-transfer-long-chinese-name";
            String fileName = "文".repeat(100) + "." + "扩".repeat(40);
            byte[] fileBytes = new byte[] {1, 3, 5, 7};

            receiveFile(receiver, transferId, fileName, fileBytes);

            File savedFile = onlySavedFile(receiveDirectory);
            String savedName = savedFile.getName();
            int extensionStart = savedName.lastIndexOf('.');
            assertTrue(savedName.getBytes(StandardCharsets.UTF_8).length <= 180);
            assertTrue(extensionStart > 0);
            assertTrue(savedName.substring(extensionStart).getBytes(StandardCharsets.UTF_8).length <= 32);
            assertArrayEquals(fileBytes, Files.readAllBytes(savedFile.toPath()));
            assertNoOwnedTemporaryFiles(receiveDirectory);
        } finally {
            deleteRecursively(receiveDirectory);
        }
    }

    @Test
    public void completeSavesLongEmojiNameWithoutSplittingSurrogatePair() throws IOException {
        File receiveDirectory = Files.createTempDirectory("RemoteDesk.AndroidFileTransferReceiverTest.").toFile();
        try {
            AndroidFileTransferReceiver receiver = new AndroidFileTransferReceiver(receiveDirectory);
            String transferId = "android-transfer-long-emoji-name";
            String fileName = "😀".repeat(100) + ".bin";
            byte[] fileBytes = new byte[] {2, 4, 6, 8};

            receiveFile(receiver, transferId, fileName, fileBytes);

            File savedFile = onlySavedFile(receiveDirectory);
            String savedName = savedFile.getName();
            String savedBaseName = savedName.substring(0, savedName.lastIndexOf('.'));
            assertTrue(savedName.getBytes(StandardCharsets.UTF_8).length <= 180);
            assertEquals(44, savedBaseName.codePointCount(0, savedBaseName.length()));
            assertWellFormedUtf16(savedName);
            assertArrayEquals(fileBytes, Files.readAllBytes(savedFile.toPath()));
            assertNoOwnedTemporaryFiles(receiveDirectory);
        } finally {
            deleteRecursively(receiveDirectory);
        }
    }

    @Test
    public void completeRejectsMissingChecksumAndDeletesTemporaryFile() throws IOException {
        File receiveDirectory = Files.createTempDirectory("RemoteDesk.AndroidFileTransferReceiverTest.").toFile();
        try {
            AndroidFileTransferReceiver receiver = new AndroidFileTransferReceiver(receiveDirectory);
            String transferId = "android-transfer-missing-checksum";
            byte[] fileBytes = new byte[] {1, 2, 3, 4};

            receiver.start(startMessage(transferId, "missing-checksum.bin", fileBytes.length));
            receiver.writeChunk(chunkMessage(transferId, 0, fileBytes));

            assertThrows(IOException.class, () ->
                receiver.complete(completeMessage(transferId)));

            assertThrows(IOException.class, () ->
                receiver.cancel(cancelMessage(transferId, "late cancel")));
            assertFalse(new File(receiveDirectory, "missing-checksum.bin").exists());
            assertNoOwnedTemporaryFiles(receiveDirectory);
        } finally {
            deleteRecursively(receiveDirectory);
        }
    }

    @Test
    public void completeRejectsMismatchedChecksumAndDeletesTemporaryFile() throws IOException {
        File receiveDirectory = Files.createTempDirectory("RemoteDesk.AndroidFileTransferReceiverTest.").toFile();
        try {
            AndroidFileTransferReceiver receiver = new AndroidFileTransferReceiver(receiveDirectory);
            String transferId = "android-transfer-bad-checksum";
            byte[] fileBytes = new byte[] {1, 2, 3, 4};

            receiver.start(startMessage(transferId, "bad-checksum.bin", fileBytes.length));
            receiver.writeChunk(chunkMessage(transferId, 0, fileBytes));
            receiver.setExpectedChecksum(checksumMessage(transferId, "0".repeat(64)));

            assertThrows(IOException.class, () ->
                receiver.complete(completeMessage(transferId)));

            assertThrows(IOException.class, () ->
                receiver.cancel(cancelMessage(transferId, "late cancel")));
            assertFalse(new File(receiveDirectory, "bad-checksum.bin").exists());
            assertNoOwnedTemporaryFiles(receiveDirectory);
        } finally {
            deleteRecursively(receiveDirectory);
        }
    }

    @Test
    public void writeChunkRejectsOversizedChunkAndDeletesTemporaryFile() throws IOException {
        File receiveDirectory = Files.createTempDirectory("RemoteDesk.AndroidFileTransferReceiverTest.").toFile();
        try {
            AndroidFileTransferReceiver receiver = new AndroidFileTransferReceiver(receiveDirectory);
            String transferId = "android-transfer-oversized";
            byte[] oversizedBytes = new byte[] {1, 2, 3, 4, 5};

            receiver.start(startMessage(transferId, "oversized.bin", oversizedBytes.length - 1));

            assertThrows(IOException.class, () ->
                receiver.writeChunk(chunkMessage(transferId, 0, oversizedBytes)));

            assertThrows(IOException.class, () ->
                receiver.complete(completeMessage(transferId)));
            assertFalse(new File(receiveDirectory, "oversized.bin").exists());
            assertNoOwnedTemporaryFiles(receiveDirectory);
        } finally {
            deleteRecursively(receiveDirectory);
        }
    }

    @Test
    public void completeRejectsIncompleteTransferAndDeletesTemporaryFile() throws IOException {
        File receiveDirectory = Files.createTempDirectory("RemoteDesk.AndroidFileTransferReceiverTest.").toFile();
        try {
            AndroidFileTransferReceiver receiver = new AndroidFileTransferReceiver(receiveDirectory);
            String transferId = "android-transfer-incomplete";
            byte[] partialBytes = new byte[] {1, 2, 3};

            receiver.start(startMessage(transferId, "incomplete.bin", partialBytes.length + 1));
            receiver.writeChunk(chunkMessage(transferId, 0, partialBytes));

            assertThrows(IOException.class, () ->
                receiver.complete(completeMessage(transferId)));

            assertThrows(IOException.class, () ->
                receiver.writeChunk(chunkMessage(transferId, partialBytes.length, new byte[] {4})));
            assertFalse(new File(receiveDirectory, "incomplete.bin").exists());
            assertNoOwnedTemporaryFiles(receiveDirectory);
        } finally {
            deleteRecursively(receiveDirectory);
        }
    }

    @Test
    public void cancelDeletesTemporaryTransferFile() throws IOException {
        File receiveDirectory = Files.createTempDirectory("RemoteDesk.AndroidFileTransferReceiverTest.").toFile();
        try {
            AndroidFileTransferReceiver receiver = new AndroidFileTransferReceiver(receiveDirectory);
            String transferId = "android-transfer-cancel";
            byte[] partialBytes = new byte[] {1, 2, 3};

            receiver.start(startMessage(transferId, "cancel.bin", partialBytes.length + 1));
            receiver.writeChunk(chunkMessage(transferId, 0, partialBytes));

            String message = receiver.cancel(cancelMessage(transferId, "sender stopped"));

            assertTrue(message.contains("sender stopped"));
            assertThrows(IOException.class, () ->
                receiver.complete(completeMessage(transferId)));
            assertFalse(new File(receiveDirectory, "cancel.bin").exists());
            assertNoOwnedTemporaryFiles(receiveDirectory);
        } finally {
            deleteRecursively(receiveDirectory);
        }
    }

    @Test
    public void mismatchedCancelDoesNotAbortActiveTransfer() throws IOException {
        File receiveDirectory = Files.createTempDirectory("RemoteDesk.AndroidFileTransferReceiverTest.").toFile();
        try {
            AndroidFileTransferReceiver receiver = new AndroidFileTransferReceiver(receiveDirectory);
            String transferId = "android-transfer-active";
            byte[] firstChunk = new byte[] {1, 2};
            byte[] secondChunk = new byte[] {3, 4};
            byte[] fileBytes = new byte[] {1, 2, 3, 4};

            receiver.start(startMessage(transferId, "active.bin", fileBytes.length));
            receiver.writeChunk(chunkMessage(transferId, 0, firstChunk));

            assertThrows(IOException.class, () ->
                receiver.cancel(cancelMessage("stale-transfer", "late cancel")));

            receiver.writeChunk(chunkMessage(transferId, firstChunk.length, secondChunk));
            receiver.setExpectedChecksum(checksumMessage(transferId, sha256Hex(fileBytes)));
            receiver.complete(completeMessage(transferId));

            assertArrayEquals(fileBytes, Files.readAllBytes(new File(receiveDirectory, "active.bin").toPath()));
        } finally {
            deleteRecursively(receiveDirectory);
        }
    }

    @Test
    public void invalidReplacementStartDoesNotAbortActiveTransfer() throws IOException {
        File receiveDirectory = Files.createTempDirectory("RemoteDesk.AndroidFileTransferReceiverTest.").toFile();
        try {
            AndroidFileTransferReceiver receiver = new AndroidFileTransferReceiver(receiveDirectory);
            String transferId = "android-transfer-active-start";
            byte[] firstChunk = new byte[] {5, 6};
            byte[] secondChunk = new byte[] {7, 8};
            byte[] fileBytes = new byte[] {5, 6, 7, 8};

            receiver.start(startMessage(transferId, "active-start.bin", fileBytes.length));
            receiver.writeChunk(chunkMessage(transferId, 0, firstChunk));

            assertThrows(IOException.class, () -> receiver.start(startMessage(
                "invalid-replacement",
                "replacement.bin",
                1024L * 1024L * 1024L + 1)));

            receiver.writeChunk(chunkMessage(transferId, firstChunk.length, secondChunk));
            receiver.setExpectedChecksum(checksumMessage(transferId, sha256Hex(fileBytes)));
            receiver.complete(completeMessage(transferId));

            assertArrayEquals(fileBytes, Files.readAllBytes(new File(receiveDirectory, "active-start.bin").toPath()));
        } finally {
            deleteRecursively(receiveDirectory);
        }
    }

    @Test
    public void replacementDirectoryFailureDoesNotAbortActiveTransfer() throws IOException {
        File testRoot = Files.createTempDirectory("RemoteDesk.AndroidFileTransferReceiverTest.").toFile();
        try {
            File receiveDirectory = new File(testRoot, "received");
            assertTrue(receiveDirectory.mkdir());
            File unavailableDirectory = new File(testRoot, "not-a-directory");
            Files.write(unavailableDirectory.toPath(), new byte[] {9});
            AtomicReference<File> receiveDirectoryReference = new AtomicReference<>(receiveDirectory);
            AndroidFileTransferReceiver receiver = new AndroidFileTransferReceiver(receiveDirectoryReference::get);
            String transferId = "android-transfer-directory-failure";
            byte[] firstChunk = new byte[] {1, 2};
            byte[] secondChunk = new byte[] {3, 4};
            byte[] fileBytes = new byte[] {1, 2, 3, 4};

            receiver.start(startMessage(transferId, "directory-failure.bin", fileBytes.length));
            receiver.writeChunk(chunkMessage(transferId, 0, firstChunk));
            receiveDirectoryReference.set(unavailableDirectory);

            assertThrows(IOException.class, () -> receiver.start(startMessage(
                "replacement-directory-failure",
                "replacement.bin",
                1)));

            receiveDirectoryReference.set(receiveDirectory);
            receiver.writeChunk(chunkMessage(transferId, firstChunk.length, secondChunk));
            receiver.setExpectedChecksum(checksumMessage(transferId, sha256Hex(fileBytes)));
            receiver.complete(completeMessage(transferId));

            assertArrayEquals(
                fileBytes,
                Files.readAllBytes(new File(receiveDirectory, "directory-failure.bin").toPath()));
            assertNoOwnedTemporaryFiles(receiveDirectory);
        } finally {
            deleteRecursively(testRoot);
        }
    }

    @Test
    public void concurrentReceiversReserveAndCommitWithoutTruncationOrOverwrite() throws Exception {
        File receiveDirectory = Files.createTempDirectory("RemoteDesk.AndroidFileTransferReceiverTest.").toFile();
        try {
            AndroidFileTransferReceiver firstReceiver = new AndroidFileTransferReceiver(receiveDirectory);
            AndroidFileTransferReceiver secondReceiver = new AndroidFileTransferReceiver(receiveDirectory);
            byte[] firstBytes = new byte[] {1, 2, 3};
            byte[] secondBytes = new byte[] {7, 8, 9, 10};

            firstReceiver.start(startMessage("first-transfer", "shared.bin", firstBytes.length));
            firstReceiver.writeChunk(chunkMessage("first-transfer", 0, firstBytes));
            secondReceiver.start(startMessage("second-transfer", "shared.bin", secondBytes.length));
            secondReceiver.writeChunk(chunkMessage("second-transfer", 0, secondBytes));

            firstReceiver.setExpectedChecksum(checksumMessage("first-transfer", sha256Hex(firstBytes)));
            secondReceiver.setExpectedChecksum(checksumMessage("second-transfer", sha256Hex(secondBytes)));
            CountDownLatch startGate = new CountDownLatch(1);
            ExecutorService executor = Executors.newFixedThreadPool(2);
            try {
                Future<String> firstCompletion = executor.submit(() -> {
                    startGate.await();
                    return firstReceiver.complete(completeMessage("first-transfer"));
                });
                Future<String> secondCompletion = executor.submit(() -> {
                    startGate.await();
                    return secondReceiver.complete(completeMessage("second-transfer"));
                });
                startGate.countDown();
                firstCompletion.get(5, TimeUnit.SECONDS);
                secondCompletion.get(5, TimeUnit.SECONDS);
            } finally {
                executor.shutdownNow();
            }

            byte[] firstSavedFile = Files.readAllBytes(new File(receiveDirectory, "shared.bin").toPath());
            byte[] secondSavedFile = Files.readAllBytes(new File(receiveDirectory, "shared (1).bin").toPath());
            boolean bothTransfersPreserved =
                (Arrays.equals(firstSavedFile, firstBytes) && Arrays.equals(secondSavedFile, secondBytes)) ||
                (Arrays.equals(firstSavedFile, secondBytes) && Arrays.equals(secondSavedFile, firstBytes));
            assertTrue(bothTransfersPreserved);
            assertNoOwnedTemporaryFiles(receiveDirectory);
        } finally {
            deleteRecursively(receiveDirectory);
        }
    }

    @Test
    public void completeUsesNewUniqueNameWhenFinalNameAppearsDuringTransfer() throws IOException {
        File receiveDirectory = Files.createTempDirectory("RemoteDesk.AndroidFileTransferReceiverTest.").toFile();
        try {
            AndroidFileTransferReceiver receiver = new AndroidFileTransferReceiver(receiveDirectory);
            String transferId = "android-transfer-late-collision";
            byte[] receivedBytes = new byte[] {1, 2, 3, 4};
            byte[] existingBytes = new byte[] {9, 8, 7};

            receiver.start(startMessage(transferId, "collision.bin", receivedBytes.length));
            receiver.writeChunk(chunkMessage(transferId, 0, receivedBytes));
            receiver.setExpectedChecksum(checksumMessage(transferId, sha256Hex(receivedBytes)));
            File collidingFile = new File(receiveDirectory, "collision.bin");
            Files.write(collidingFile.toPath(), existingBytes);

            String message = receiver.complete(completeMessage(transferId));

            File renamedFile = new File(receiveDirectory, "collision (1).bin");
            assertTrue(message.contains("collision (1).bin"));
            assertArrayEquals(existingBytes, Files.readAllBytes(collidingFile.toPath()));
            assertArrayEquals(receivedBytes, Files.readAllBytes(renamedFile.toPath()));
            assertNoOwnedTemporaryFiles(receiveDirectory);
        } finally {
            deleteRecursively(receiveDirectory);
        }
    }

    @Test
    public void abortActiveTransferIsIdempotentAndDeletesTemporaryFile() throws IOException {
        File receiveDirectory = Files.createTempDirectory("RemoteDesk.AndroidFileTransferReceiverTest.").toFile();
        try {
            AndroidFileTransferReceiver receiver = new AndroidFileTransferReceiver(receiveDirectory);
            String transferId = "android-transfer-disconnect";

            receiver.start(startMessage(transferId, "disconnect.bin", 4));
            receiver.writeChunk(chunkMessage(transferId, 0, new byte[] {1, 2}));
            receiver.abortActiveTransfer();
            receiver.abortActiveTransfer();

            assertThrows(IOException.class, () ->
                receiver.writeChunk(chunkMessage(transferId, 2, new byte[] {3, 4})));
            assertFalse(new File(receiveDirectory, "disconnect.bin").exists());
            assertNoOwnedTemporaryFiles(receiveDirectory);
        } finally {
            deleteRecursively(receiveDirectory);
        }
    }

    @Test
    public void cleanupStaleTemporaryFilesDeletesOnlyOldOwnedTransferFiles() throws IOException {
        File receiveDirectory = Files.createTempDirectory("RemoteDesk.AndroidFileTransferReceiverTest.").toFile();
        try {
            long now = 1_800_000_000_000L;
            File staleTransfer = new File(
                receiveDirectory,
                ".remotedesk-0123456789abcdef0123456789abcdef.rdtransfer");
            File freshTransfer = new File(
                receiveDirectory,
                ".remotedesk-fedcba9876543210fedcba9876543210.rdtransfer");
            File ordinaryTransferSuffixFile = new File(receiveDirectory, "user-document.rdtransfer");
            File normalFile = new File(receiveDirectory, "normal.bin");
            Files.write(staleTransfer.toPath(), new byte[] {1});
            Files.write(freshTransfer.toPath(), new byte[] {2});
            Files.write(ordinaryTransferSuffixFile.toPath(), new byte[] {3});
            Files.write(normalFile.toPath(), new byte[] {4});
            Files.setLastModifiedTime(staleTransfer.toPath(), FileTime.fromMillis(now - (2L * 24L * 60L * 60L * 1000L)));
            Files.setLastModifiedTime(freshTransfer.toPath(), FileTime.fromMillis(now - (10L * 60L * 1000L)));
            Files.setLastModifiedTime(ordinaryTransferSuffixFile.toPath(), FileTime.fromMillis(now - (2L * 24L * 60L * 60L * 1000L)));
            Files.setLastModifiedTime(normalFile.toPath(), FileTime.fromMillis(now - (2L * 24L * 60L * 60L * 1000L)));

            int deleted = AndroidFileTransferReceiver.cleanupStaleTemporaryFiles(receiveDirectory, now);

            assertEquals(1, deleted);
            assertFalse(staleTransfer.exists());
            assertTrue(freshTransfer.exists());
            assertTrue(ordinaryTransferSuffixFile.exists());
            assertTrue(normalFile.exists());
        } finally {
            deleteRecursively(receiveDirectory);
        }
    }

    @Test
    public void ownedTemporaryFileNameRequiresStrictPrivateFormat() {
        assertTrue(AndroidFileTransferReceiver.isOwnedTemporaryFileName(
            ".remotedesk-0123456789abcdef0123456789abcdef.rdtransfer"));
        assertFalse(AndroidFileTransferReceiver.isOwnedTemporaryFileName("old.bin.rdtransfer"));
        assertFalse(AndroidFileTransferReceiver.isOwnedTemporaryFileName(
            ".remotedesk-0123456789abcdef0123456789abcdeg.rdtransfer"));
        assertFalse(AndroidFileTransferReceiver.isOwnedTemporaryFileName(
            ".remotedesk-0123456789abcdef.rdtransfer"));
    }

    @Test
    public void startRejectsWhenDiskSafetyReserveWouldBeConsumed() throws IOException {
        File receiveDirectory = Files.createTempDirectory(
            "RemoteDesk.AndroidFileTransferReceiverLowSpaceTest.").toFile();
        try {
            AndroidFileTransferReceiver receiver = new AndroidFileTransferReceiver(
                () -> receiveDirectory,
                ignored -> AndroidFileTransferReceiver.MINIMUM_FREE_SPACE_RESERVE_BYTES);

            IOException error = assertThrows(IOException.class, () ->
                receiver.start(startMessage("low-space", "blocked.bin", 1)));

            assertTrue(error.getMessage().contains("空间不足"));
            assertNoOwnedTemporaryFiles(receiveDirectory);
        } finally {
            deleteRecursively(receiveDirectory);
        }
    }

    @Test
    public void startRejectsDeclaredBytesBeyondSessionQuota() throws IOException {
        File receiveDirectory = Files.createTempDirectory(
            "RemoteDesk.AndroidFileTransferReceiverQuotaTest.").toFile();
        try {
            AndroidFileTransferReceiver receiver = new AndroidFileTransferReceiver(
                () -> receiveDirectory,
                ignored -> Long.MAX_VALUE);
            long oneFileLimit = 1024L * 1024L * 1024L;
            receiver.start(startMessage("quota-one", "one.bin", oneFileLimit));
            receiver.start(startMessage("quota-two", "two.bin", oneFileLimit));

            IOException error = assertThrows(IOException.class, () ->
                receiver.start(startMessage("quota-three", "three.bin", 1)));

            assertTrue(error.getMessage().contains("累计"));
            receiver.abortActiveTransfer();
        } finally {
            deleteRecursively(receiveDirectory);
        }
    }

    private static RemoteDeskTransport.ControlMessage startMessage(
        String transferId,
        String fileName,
        long fileLength) {
        return new RemoteDeskTransport.ControlMessage(
            RemoteDeskProtocol.CONTROL_FILE_TRANSFER_START,
            null,
            transferId,
            fileName,
            fileLength,
            0,
            null,
            0);
    }

    private static RemoteDeskTransport.ControlMessage chunkMessage(
        String transferId,
        long fileOffset,
        byte[] bytes) {
        return new RemoteDeskTransport.ControlMessage(
            RemoteDeskProtocol.CONTROL_FILE_TRANSFER_CHUNK,
            null,
            transferId,
            null,
            0,
            fileOffset,
            bytes,
            0);
    }

    private static RemoteDeskTransport.ControlMessage completeMessage(String transferId) {
        return new RemoteDeskTransport.ControlMessage(
            RemoteDeskProtocol.CONTROL_FILE_TRANSFER_COMPLETE,
            null,
            transferId,
            null,
            0,
            0,
            null,
            0);
    }

    private static RemoteDeskTransport.ControlMessage checksumMessage(String transferId, String checksumHex) {
        return new RemoteDeskTransport.ControlMessage(
            RemoteDeskProtocol.CONTROL_FILE_TRANSFER_CHECKSUM,
            null,
            transferId,
            null,
            0,
            0,
            null,
            0,
            "SHA256",
            checksumHex);
    }

    private static RemoteDeskTransport.ControlMessage cancelMessage(String transferId, String reason) {
        return new RemoteDeskTransport.ControlMessage(
            RemoteDeskProtocol.CONTROL_FILE_TRANSFER_CANCEL,
            reason,
            transferId,
            null,
            0,
            0,
            null,
            0);
    }

    private static String sha256Hex(byte[] bytes) {
        try {
            byte[] hash = MessageDigest.getInstance("SHA-256").digest(bytes);
            StringBuilder builder = new StringBuilder(hash.length * 2);
            for (byte value : hash) {
                builder.append(String.format(Locale.ROOT, "%02x", value & 0xFF));
            }

            return builder.toString();
        } catch (NoSuchAlgorithmException ex) {
            throw new AssertionError(ex);
        }
    }

    private static void receiveFile(
        AndroidFileTransferReceiver receiver,
        String transferId,
        String fileName,
        byte[] bytes) throws IOException {
        receiver.start(startMessage(transferId, fileName, bytes.length));
        if (bytes.length > 0) {
            receiver.writeChunk(chunkMessage(transferId, 0, bytes));
        }

        receiver.setExpectedChecksum(checksumMessage(transferId, sha256Hex(bytes)));
        receiver.complete(completeMessage(transferId));
    }

    private static File onlySavedFile(File directory) {
        File[] files = directory.listFiles(file ->
            file.isFile() && !AndroidFileTransferReceiver.isOwnedTemporaryFileName(file.getName()));
        assertTrue(files != null);
        assertEquals(1, files.length);
        return files[0];
    }

    private static void assertWellFormedUtf16(String value) {
        for (int index = 0; index < value.length(); index++) {
            char ch = value.charAt(index);
            if (Character.isHighSurrogate(ch)) {
                assertTrue(index + 1 < value.length());
                assertTrue(Character.isLowSurrogate(value.charAt(index + 1)));
                index++;
            } else {
                assertFalse(Character.isLowSurrogate(ch));
            }
        }
    }

    private static void assertNoOwnedTemporaryFiles(File directory) {
        File[] files = directory.listFiles(file ->
            file.isFile() && AndroidFileTransferReceiver.isOwnedTemporaryFileName(file.getName()));
        assertTrue(files == null || files.length == 0);
    }

    private static void deleteRecursively(File file) {
        if (file == null || !file.exists()) {
            return;
        }

        File[] children = file.listFiles();
        if (children != null) {
            for (File child : children) {
                deleteRecursively(child);
            }
        }

        //noinspection ResultOfMethodCallIgnored
        file.delete();
    }
}
