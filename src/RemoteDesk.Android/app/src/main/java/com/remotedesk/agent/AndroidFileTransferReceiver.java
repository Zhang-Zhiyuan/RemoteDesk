package com.remotedesk.agent;

import android.content.ContentResolver;
import android.content.ContentValues;
import android.content.Context;
import android.net.Uri;
import android.os.Build;
import android.os.Environment;
import android.provider.MediaStore;

import java.io.File;
import java.io.FileInputStream;
import java.io.IOException;
import java.io.InterruptedIOException;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;
import java.nio.file.FileAlreadyExistsException;
import java.nio.file.Files;
import java.nio.file.StandardOpenOption;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.util.HashSet;
import java.util.Locale;
import java.util.Set;
import java.util.UUID;
import java.util.function.Supplier;

final class AndroidFileTransferReceiver {
    interface CancellationSignal {
        boolean isCancelled();
    }

    interface AvailableSpaceProvider {
        long getUsableSpace(File directory);
    }

    private static final CancellationSignal NEVER_CANCELLED = () -> false;
    static final String RECEIVE_FOLDER_NAME = "RemoteDeskReceived";
    private static final int MAX_SAFE_FILE_NAME_UTF8_BYTES = 180;
    private static final int MAX_SAFE_EXTENSION_UTF8_BYTES = 32;
    private static final int MAX_UNIQUE_FILE_ATTEMPTS = 10_000;
    private static final int MAX_CHUNK_BYTES = 128 * 1024;
    private static final long MAX_FILE_BYTES = 1024L * 1024L * 1024L;
    static final int MAX_FILES_PER_SESSION = 128;
    static final long MAX_DECLARED_BYTES_PER_SESSION = 2L * MAX_FILE_BYTES;
    static final long MINIMUM_FREE_SPACE_RESERVE_BYTES = 256L * 1024L * 1024L;
    private static final String TEMPORARY_FILE_PREFIX = ".remotedesk-";
    private static final String TEMPORARY_FILE_SUFFIX = ".rdtransfer";
    private static final int TEMPORARY_FILE_ID_LENGTH = 32;
    private static final String MIME_TYPE_BINARY = "application/octet-stream";
    private static final String CHECKSUM_ALGORITHM = "SHA256";
    private static final long STALE_TEMPORARY_FILE_MILLIS = 24L * 60L * 60L * 1000L;
    private static final Set<String> RESERVED_NAMES = new HashSet<>();

    static {
        RESERVED_NAMES.add("CON");
        RESERVED_NAMES.add("PRN");
        RESERVED_NAMES.add("AUX");
        RESERVED_NAMES.add("NUL");
        for (int index = 1; index <= 9; index++) {
            RESERVED_NAMES.add("COM" + index);
            RESERVED_NAMES.add("LPT" + index);
        }
    }

    private final Context context;
    private final Supplier<File> receiveDirectoryProvider;
    private final boolean requireChecksum;
    private final AvailableSpaceProvider availableSpaceProvider;
    private IncomingTransfer currentTransfer;
    private int acceptedTransferCount;
    private long acceptedDeclaredBytes;

    AndroidFileTransferReceiver(Context context) {
        this(context.getApplicationContext(), null, true, File::getUsableSpace);
    }

    AndroidFileTransferReceiver(File receiveDirectory) {
        this(null, () -> receiveDirectory, true, File::getUsableSpace);
    }

    AndroidFileTransferReceiver(Supplier<File> receiveDirectoryProvider) {
        this(null, receiveDirectoryProvider, true, File::getUsableSpace);
    }

    AndroidFileTransferReceiver(
        Supplier<File> receiveDirectoryProvider,
        AvailableSpaceProvider availableSpaceProvider) {
        this(null, receiveDirectoryProvider, true, availableSpaceProvider);
    }

    private AndroidFileTransferReceiver(
        Context context,
        Supplier<File> receiveDirectoryProvider,
        boolean requireChecksum,
        AvailableSpaceProvider availableSpaceProvider) {
        this.context = context;
        if (receiveDirectoryProvider != null) {
            this.receiveDirectoryProvider = receiveDirectoryProvider;
        } else if (context != null) {
            this.receiveDirectoryProvider = () -> getAppSpecificReceiveDirectory(context);
        } else {
            throw new IllegalArgumentException("Android 接收目录提供器缺失。");
        }

        this.requireChecksum = requireChecksum;
        if (availableSpaceProvider == null) {
            throw new IllegalArgumentException("Android 可用空间提供器缺失。");
        }
        this.availableSpaceProvider = availableSpaceProvider;
    }

    synchronized String start(RemoteDeskTransport.ControlMessage control) throws IOException {
        String transferId = requireText(control.transferId, "文件传输编号缺失。");
        String fileName = sanitizeFileName(requireText(control.fileName, "文件名缺失。"));
        if (control.fileLength < 0 || control.fileLength > MAX_FILE_BYTES) {
            throw new IOException("文件大小超出 Android 接收上限。");
        }

        File receiveDirectory = getReceiveDirectory();
        if (receiveDirectory == null) {
            throw new IOException("Android 接收目录不可用。");
        }

        if (!receiveDirectory.exists() && !receiveDirectory.mkdirs()) {
            throw new IOException("无法创建 Android 接收目录。");
        }

        if (!receiveDirectory.isDirectory()) {
            throw new IOException("Android 接收路径不是目录。");
        }

        validateReceiveBudget(receiveDirectory, control.fileLength);

        IncomingTransfer previousTransfer = currentTransfer;
        cleanupStaleTemporaryFiles(
            receiveDirectory,
            System.currentTimeMillis(),
            previousTransfer == null ? null : previousTransfer.temporaryFile);
        MessageDigest hash = createSha256();
        TemporaryFileReservation reservation = reserveUniqueTemporaryFile(receiveDirectory, fileName);
        IncomingTransfer replacementTransfer = new IncomingTransfer(
            transferId,
            fileName,
            control.fileLength,
            reservation.finalFile,
            reservation.temporaryFile,
            reservation.stream,
            hash);
        String message = "开始接收文件到 Android：" + fileName + " (" +
            formatBytes(control.fileLength) + ")";
        currentTransfer = replacementTransfer;
        acceptedTransferCount++;
        acceptedDeclaredBytes += control.fileLength;
        discardTransfer(previousTransfer);
        return message;
    }

    synchronized void writeChunk(RemoteDeskTransport.ControlMessage control) throws IOException {
        IncomingTransfer transfer = getCurrentTransfer(control.transferId);
        try {
            byte[] bytes = control.fileBytes;
            if (bytes == null || bytes.length <= 0 || bytes.length > MAX_CHUNK_BYTES) {
                throw new IOException("文件分块大小异常。");
            }

            if (control.fileOffset != transfer.bytesReceived) {
                throw new IOException("文件分块顺序异常。");
            }

            if (transfer.bytesReceived + bytes.length > transfer.fileLength) {
                throw new IOException("文件分块超过声明大小。");
            }

            transfer.stream.write(bytes);
            transfer.hash.update(bytes);
            transfer.bytesReceived += bytes.length;
        } catch (IOException | RuntimeException ex) {
            abortTransferOnFailure(transfer);
            throw ex;
        }
    }

    synchronized void setExpectedChecksum(RemoteDeskTransport.ControlMessage control) throws IOException {
        IncomingTransfer transfer = getCurrentTransfer(control.transferId);
        try {
            String algorithm = requireText(control.checksumAlgorithm, "文件校验算法缺失。")
                .replace("-", "")
                .toUpperCase(Locale.ROOT);
            if (!CHECKSUM_ALGORITHM.equals(algorithm)) {
                throw new IOException("文件校验算法不受支持。");
            }

            transfer.expectedSha256 = parseSha256Hex(requireText(control.checksumHex, "文件校验值缺失。"));
        } catch (IOException | RuntimeException ex) {
            abortTransferOnFailure(transfer);
            throw ex;
        }
    }

    String complete(RemoteDeskTransport.ControlMessage control) throws IOException {
        return complete(control, NEVER_CANCELLED);
    }

    String complete(
        RemoteDeskTransport.ControlMessage control,
        CancellationSignal cancellationSignal) throws IOException {
        if (cancellationSignal == null) {
            throw new IllegalArgumentException("cancellationSignal is required");
        }

        PreparedCompletion completed = prepareCompletion(control);
        return publishCompletion(completed, cancellationSignal);
    }

    String publishCompletion(
        PreparedCompletion completed,
        CancellationSignal cancellationSignal) throws IOException {
        if (completed == null || cancellationSignal == null) {
            throw new IllegalArgumentException(
                "completed and cancellationSignal are required");
        }

        String savedPath;
        try {
            throwIfCancelled(cancellationSignal);
            savedPath = saveCompletedTransfer(
                completed.transfer,
                cancellationSignal);
        } catch (IOException | RuntimeException ex) {
            deleteQuietly(completed.transfer.temporaryFile);
            throw ex;
        }

        return completed.checksumVerified
            ? "文件已保存到 Android：" + savedPath + "（SHA-256 已校验）"
            : "文件已保存到 Android：" + savedPath;
    }

    synchronized PreparedCompletion prepareCompletion(
        RemoteDeskTransport.ControlMessage control) throws IOException {
        IncomingTransfer transfer = getCurrentTransfer(control.transferId);
        boolean checksumVerified;
        try {
            if (transfer.bytesReceived != transfer.fileLength) {
                throw new IOException("文件传输未完整完成。");
            }

            transfer.stream.flush();
            checksumVerified = verifyChecksumIfPresent(transfer);
            transfer.stream.close();
            currentTransfer = null;
        } catch (IOException | RuntimeException ex) {
            if (currentTransfer == transfer) {
                abortActiveTransfer();
            }

            throw ex;
        }
        return new PreparedCompletion(transfer, checksumVerified);
    }

    void discardCompletion(PreparedCompletion completed) {
        if (completed != null) {
            deleteQuietly(completed.transfer.temporaryFile);
        }
    }

    synchronized String cancel(RemoteDeskTransport.ControlMessage control) throws IOException {
        IncomingTransfer transfer = getCurrentTransfer(control.transferId);
        String fileName = transfer.fileName;
        String reason = control.text == null || control.text.trim().isEmpty()
            ? "对端取消了文件传输。"
            : control.text.trim();

        abortActiveTransfer();
        return "文件传输已取消：" + fileName + " - " + reason;
    }

    synchronized void abortActiveTransfer() {
        IncomingTransfer transfer = currentTransfer;
        currentTransfer = null;
        discardTransfer(transfer);
    }

    private void abortTransferOnFailure(IncomingTransfer transfer) {
        if (currentTransfer == transfer) {
            abortActiveTransfer();
            return;
        }

        discardTransfer(transfer);
    }

    private static void discardTransfer(IncomingTransfer transfer) {
        if (transfer == null) {
            return;
        }

        try {
            transfer.stream.close();
        } catch (IOException ignored) {
        }

        deleteQuietly(transfer.temporaryFile);
    }

    private IncomingTransfer getCurrentTransfer(String transferId) throws IOException {
        String requestedTransferId = requireText(transferId, "文件传输编号缺失。");
        IncomingTransfer transfer = currentTransfer;
        if (transfer == null || !transfer.transferId.equals(requestedTransferId)) {
            throw new IOException("没有匹配的 Android 文件接收会话。");
        }

        return transfer;
    }

    private boolean verifyChecksumIfPresent(IncomingTransfer transfer) throws IOException {
        byte[] expectedSha256 = transfer.expectedSha256;
        if (expectedSha256 == null) {
            if (requireChecksum) {
                throw new IOException("远端未发送文件 SHA-256 校验值，已拒绝保存。");
            }

            return false;
        }

        byte[] actualSha256 = transfer.hash.digest();
        if (!MessageDigest.isEqual(actualSha256, expectedSha256)) {
            throw new IOException("文件 SHA-256 校验失败，已拒绝保存。");
        }

        return true;
    }

    private static MessageDigest createSha256() throws IOException {
        try {
            return MessageDigest.getInstance("SHA-256");
        } catch (NoSuchAlgorithmException ex) {
            throw new IOException("Android 不支持 SHA-256 文件校验。", ex);
        }
    }

    private void validateReceiveBudget(File receiveDirectory, long fileLength) throws IOException {
        if (acceptedTransferCount >= MAX_FILES_PER_SESSION) {
            throw new IOException(
                "本次连接已达到 " + MAX_FILES_PER_SESSION + " 个 Android 接收文件上限。");
        }

        if (acceptedDeclaredBytes > MAX_DECLARED_BYTES_PER_SESSION - fileLength) {
            throw new IOException("本次连接累计接收声明大小超过 Android 安全配额。");
        }

        long usableSpace;
        try {
            usableSpace = availableSpaceProvider.getUsableSpace(receiveDirectory);
        } catch (RuntimeException ex) {
            throw new IOException("无法确认 Android 接收目录的剩余空间。", ex);
        }

        long copyMultiplier = context != null && Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q
            ? 2L
            : 1L;
        long requiredBytes = fileLength * copyMultiplier + MINIMUM_FREE_SPACE_RESERVE_BYTES;
        if (usableSpace < requiredBytes) {
            throw new IOException(
                "Android 接收空间不足；必须保留文件写入空间和 256 MiB 安全余量。");
        }
    }

    private static byte[] parseSha256Hex(String checksumHex) throws IOException {
        String normalized = checksumHex.trim();
        if (normalized.length() != 64) {
            throw new IOException("文件校验值长度异常。");
        }

        byte[] bytes = new byte[32];
        for (int index = 0; index < bytes.length; index++) {
            int high = hexValue(normalized.charAt(index * 2));
            int low = hexValue(normalized.charAt(index * 2 + 1));
            if (high < 0 || low < 0) {
                throw new IOException("文件校验值格式异常。");
            }

            bytes[index] = (byte) ((high << 4) | low);
        }

        return bytes;
    }

    private static int hexValue(char ch) {
        if (ch >= '0' && ch <= '9') {
            return ch - '0';
        }

        if (ch >= 'a' && ch <= 'f') {
            return ch - 'a' + 10;
        }

        if (ch >= 'A' && ch <= 'F') {
            return ch - 'A' + 10;
        }

        return -1;
    }

    static File getAppSpecificReceiveDirectory(Context context) {
        Context appContext = context.getApplicationContext();
        File downloads = appContext.getExternalFilesDir(Environment.DIRECTORY_DOWNLOADS);
        if (downloads == null) {
            downloads = appContext.getFilesDir();
        }

        return new File(downloads, RECEIVE_FOLDER_NAME);
    }

    private File getReceiveDirectory() {
        return receiveDirectoryProvider.get();
    }

    private String saveCompletedTransfer(
        IncomingTransfer transfer,
        CancellationSignal cancellationSignal) throws IOException {
        throwIfCancelled(cancellationSignal);
        if (context != null && Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            String publicPath = tryPublishToDownloads(
                transfer,
                cancellationSignal);
            if (publicPath != null) {
                return publicPath;
            }
        }

        throwIfCancelled(cancellationSignal);
        File savedFile = commitTemporaryToUniqueFinalFile(
            transfer,
            cancellationSignal);
        return savedFile.getAbsolutePath();
    }

    @androidx.annotation.RequiresApi(Build.VERSION_CODES.Q)
    private String tryPublishToDownloads(
        IncomingTransfer transfer,
        CancellationSignal cancellationSignal) throws IOException {
        throwIfCancelled(cancellationSignal);
        ContentResolver resolver = context.getContentResolver();
        ContentValues values = new ContentValues();
        values.put(MediaStore.MediaColumns.DISPLAY_NAME, transfer.fileName);
        values.put(MediaStore.MediaColumns.MIME_TYPE, MIME_TYPE_BINARY);
        values.put(MediaStore.MediaColumns.RELATIVE_PATH, Environment.DIRECTORY_DOWNLOADS + File.separator + RECEIVE_FOLDER_NAME);
        values.put(MediaStore.MediaColumns.IS_PENDING, 1);

        Uri uri = resolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values);
        if (uri == null) {
            return null;
        }

        try {
            try (OutputStream output = resolver.openOutputStream(uri);
                 FileInputStream input = new FileInputStream(transfer.temporaryFile)) {
                if (output == null) {
                    throw new IOException("无法打开 Android 公共下载目录输出流。");
                }

                copy(input, output, cancellationSignal);
            }

            throwIfCancelled(cancellationSignal);
            values.clear();
            values.put(MediaStore.MediaColumns.IS_PENDING, 0);
            int updated = resolver.update(uri, values, null, null);
            if (updated != 1) {
                throw new IOException("无法完成 Android 公共下载文件发布。");
            }

            // If ownership changed while MediaStore committed the row, roll
            // it back instead of leaving an unacknowledged cross-generation
            // file behind.
            throwIfCancelled(cancellationSignal);
            deleteQuietly(transfer.temporaryFile);
            return "Downloads/" + RECEIVE_FOLDER_NAME + "/" + transfer.fileName;
        } catch (TransferCancelledException ex) {
            try {
                resolver.delete(uri, null, null);
            } catch (Exception ignored) {
            }
            throw ex;
        } catch (Exception ex) {
            try {
                resolver.delete(uri, null, null);
            } catch (Exception ignored) {
            }

            return null;
        }
    }

    private static void copy(
        FileInputStream input,
        OutputStream output,
        CancellationSignal cancellationSignal) throws IOException {
        byte[] buffer = new byte[MAX_CHUNK_BYTES];
        int read;
        throwIfCancelled(cancellationSignal);
        while ((read = input.read(buffer)) != -1) {
            throwIfCancelled(cancellationSignal);
            output.write(buffer, 0, read);
        }
        throwIfCancelled(cancellationSignal);
    }

    private static TemporaryFileReservation reserveUniqueTemporaryFile(File directory, String fileName)
        throws IOException {
        File finalFile = new File(directory, fileName);
        for (int index = 0; index < MAX_UNIQUE_FILE_ATTEMPTS; index++) {
            File temporaryFile = new File(directory, createOwnedTemporaryFileName());
            try {
                OutputStream stream = Files.newOutputStream(
                    temporaryFile.toPath(),
                    StandardOpenOption.CREATE_NEW,
                    StandardOpenOption.WRITE);
                return new TemporaryFileReservation(finalFile, temporaryFile, stream);
            } catch (FileAlreadyExistsException ex) {
                // UUID collisions are extremely unlikely, but retry without truncating the owner.
            } catch (IOException ex) {
                if (!temporaryFile.exists()) {
                    throw ex;
                }
            }
        }

        throw new IOException("无法为 Android 接收文件保留唯一保存路径。");
    }

    private static File commitTemporaryToUniqueFinalFile(
        IncomingTransfer transfer,
        CancellationSignal cancellationSignal) throws IOException {
        File directory = transfer.finalFile.getParentFile();
        if (directory == null) {
            throw new IOException("Android 接收目录不可用。");
        }

        String baseName = baseName(transfer.fileName);
        String extension = extension(transfer.fileName);
        for (int index = 0; index < MAX_UNIQUE_FILE_ATTEMPTS; index++) {
            throwIfCancelled(cancellationSignal);
            File candidate = index == 0
                ? transfer.finalFile
                : new File(directory, baseName + " (" + index + ")" + extension);
            try {
                Files.createLink(candidate.toPath(), transfer.temporaryFile.toPath());
                try {
                    throwIfCancelled(cancellationSignal);
                } catch (TransferCancelledException ex) {
                    deleteQuietly(candidate);
                    throw ex;
                }
                deleteQuietly(transfer.temporaryFile);
                return candidate;
            } catch (FileAlreadyExistsException ex) {
                // Another writer already owns the final name. Try a suffix without replacing it.
            } catch (TransferCancelledException ex) {
                throw ex;
            } catch (UnsupportedOperationException ex) {
                if (copyTemporaryToNewFinalFile(
                        transfer.temporaryFile,
                        candidate,
                        cancellationSignal)) {
                    return candidate;
                }
            } catch (IOException ex) {
                try {
                    if (copyTemporaryToNewFinalFile(
                            transfer.temporaryFile,
                            candidate,
                            cancellationSignal)) {
                        return candidate;
                    }
                } catch (IOException copyException) {
                    copyException.addSuppressed(ex);
                    throw copyException;
                }
            }
        }

        throw new IOException("无法为 Android 接收文件保留唯一保存路径。");
    }

    private static boolean copyTemporaryToNewFinalFile(
        File temporaryFile,
        File finalFile,
        CancellationSignal cancellationSignal)
        throws IOException {
        boolean finalFileCreated = false;
        try {
            OutputStream output = Files.newOutputStream(
                finalFile.toPath(),
                StandardOpenOption.CREATE_NEW,
                StandardOpenOption.WRITE);
            finalFileCreated = true;
            try (OutputStream ownedOutput = output;
                 FileInputStream input = new FileInputStream(temporaryFile)) {
                copy(input, ownedOutput, cancellationSignal);
                ownedOutput.flush();
                throwIfCancelled(cancellationSignal);
            }
        } catch (FileAlreadyExistsException ex) {
            return false;
        } catch (IOException | RuntimeException ex) {
            if (finalFileCreated) {
                deleteQuietly(finalFile);
            }

            throw ex;
        }

        deleteQuietly(temporaryFile);
        return true;
    }

    private static void throwIfCancelled(
        CancellationSignal cancellationSignal) throws TransferCancelledException {
        if (Thread.currentThread().isInterrupted() || cancellationSignal.isCancelled()) {
            throw new TransferCancelledException();
        }
    }

    private static String createOwnedTemporaryFileName() {
        return TEMPORARY_FILE_PREFIX +
            UUID.randomUUID().toString().replace("-", "") +
            TEMPORARY_FILE_SUFFIX;
    }

    static boolean isOwnedTemporaryFileName(String fileName) {
        if (fileName == null ||
            !fileName.startsWith(TEMPORARY_FILE_PREFIX) ||
            !fileName.endsWith(TEMPORARY_FILE_SUFFIX)) {
            return false;
        }

        int idStart = TEMPORARY_FILE_PREFIX.length();
        int idEnd = fileName.length() - TEMPORARY_FILE_SUFFIX.length();
        if (idEnd - idStart != TEMPORARY_FILE_ID_LENGTH) {
            return false;
        }

        for (int index = idStart; index < idEnd; index++) {
            char ch = fileName.charAt(index);
            boolean hexadecimal = (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f');
            if (!hexadecimal) {
                return false;
            }
        }

        return true;
    }

    private static String sanitizeFileName(String fileName) {
        String sanitized = fileName.trim();
        int slash = Math.max(sanitized.lastIndexOf('/'), sanitized.lastIndexOf('\\'));
        if (slash >= 0) {
            sanitized = sanitized.substring(slash + 1);
        }

        sanitized = sanitized.replaceAll("[\\p{Cntrl}<>:\"/\\\\|?*]", "_").trim();
        while (sanitized.endsWith(".") || sanitized.endsWith(" ")) {
            sanitized = sanitized.substring(0, sanitized.length() - 1);
        }

        if (sanitized.isEmpty()) {
            sanitized = "remote-file";
        }

        String extension = extension(sanitized);
        extension = truncateUtf8ToBytes(extension, MAX_SAFE_EXTENSION_UTF8_BYTES);

        String baseName = baseName(sanitized);
        if (baseName.isEmpty()) {
            baseName = "remote-file";
        }

        if (RESERVED_NAMES.contains(baseName.toUpperCase(Locale.ROOT))) {
            baseName = "_" + baseName;
        }

        int extensionBytes = extension.getBytes(StandardCharsets.UTF_8).length;
        int maxBaseBytes = Math.max(1, MAX_SAFE_FILE_NAME_UTF8_BYTES - extensionBytes);
        baseName = truncateUtf8ToBytes(baseName, maxBaseBytes);
        if (baseName.isEmpty()) {
            baseName = truncateUtf8ToBytes("remote-file", maxBaseBytes);
        }

        return baseName + extension;
    }

    private static String truncateUtf8ToBytes(String value, int maxBytes) {
        if (value == null || value.isEmpty() || maxBytes <= 0) {
            return "";
        }

        int usedBytes = 0;
        int endIndex = 0;
        while (endIndex < value.length()) {
            int codePoint = value.codePointAt(endIndex);
            int codePointBytes = utf8BytesForCodePoint(codePoint);
            if (usedBytes + codePointBytes > maxBytes) {
                break;
            }

            usedBytes += codePointBytes;
            endIndex += Character.charCount(codePoint);
        }

        return value.substring(0, endIndex);
    }

    private static int utf8BytesForCodePoint(int codePoint) {
        if (codePoint <= 0x7F) {
            return 1;
        }

        if (codePoint <= 0x7FF) {
            return 2;
        }

        if (codePoint <= 0xFFFF) {
            return 3;
        }

        return 4;
    }

    private static String baseName(String fileName) {
        int dot = fileName.lastIndexOf('.');
        return dot <= 0 ? fileName : fileName.substring(0, dot);
    }

    private static String extension(String fileName) {
        int dot = fileName.lastIndexOf('.');
        return dot <= 0 ? "" : fileName.substring(dot);
    }

    private static String requireText(String value, String message) throws IOException {
        if (value == null || value.trim().isEmpty()) {
            throw new IOException(message);
        }

        return value;
    }

    private static String formatBytes(long bytes) {
        String[] units = {"B", "KB", "MB", "GB"};
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.length - 1) {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? bytes + " " + units[unit] : String.format(Locale.ROOT, "%.1f %s", value, units[unit]);
    }

    private static void deleteQuietly(File file) {
        if (file != null && file.exists()) {
            //noinspection ResultOfMethodCallIgnored
            file.delete();
        }
    }

    static int cleanupStaleTemporaryFiles(File directory, long nowMillis) {
        return cleanupStaleTemporaryFiles(directory, nowMillis, null);
    }

    private static int cleanupStaleTemporaryFiles(
        File directory,
        long nowMillis,
        File protectedTemporaryFile) {
        File normalizedProtectedFile = protectedTemporaryFile == null
            ? null
            : protectedTemporaryFile.getAbsoluteFile();
        File[] files = directory.listFiles(file ->
            file.isFile() &&
                isOwnedTemporaryFileName(file.getName()) &&
                (normalizedProtectedFile == null || !file.getAbsoluteFile().equals(normalizedProtectedFile)));
        if (files == null) {
            return 0;
        }

        int deleted = 0;
        long cutoff = nowMillis - STALE_TEMPORARY_FILE_MILLIS;
        for (File file : files) {
            if (file.lastModified() > cutoff) {
                continue;
            }

            if (file.delete()) {
                deleted++;
            }
        }

        return deleted;
    }

    private static final class IncomingTransfer {
        final String transferId;
        final String fileName;
        final long fileLength;
        final File finalFile;
        final File temporaryFile;
        final OutputStream stream;
        final MessageDigest hash;
        byte[] expectedSha256;
        long bytesReceived;

        IncomingTransfer(
            String transferId,
            String fileName,
            long fileLength,
            File finalFile,
            File temporaryFile,
            OutputStream stream,
            MessageDigest hash) {
            this.transferId = transferId;
            this.fileName = fileName;
            this.fileLength = fileLength;
            this.finalFile = finalFile;
            this.temporaryFile = temporaryFile;
            this.stream = stream;
            this.hash = hash;
        }
    }

    static final class PreparedCompletion {
        final IncomingTransfer transfer;
        final boolean checksumVerified;

        PreparedCompletion(IncomingTransfer transfer, boolean checksumVerified) {
            this.transfer = transfer;
            this.checksumVerified = checksumVerified;
        }
    }

    private static final class TransferCancelledException
        extends InterruptedIOException {
        TransferCancelledException() {
            super("Android file publication was cancelled.");
        }
    }

    private static final class TemporaryFileReservation {
        final File finalFile;
        final File temporaryFile;
        final OutputStream stream;

        TemporaryFileReservation(File finalFile, File temporaryFile, OutputStream stream) {
            this.finalFile = finalFile;
            this.temporaryFile = temporaryFile;
            this.stream = stream;
        }
    }
}
