package com.remotedesk.agent;

import android.content.ContentUris;
import android.content.ContentValues;
import android.database.Cursor;
import android.net.Uri;
import android.provider.MediaStore;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import java.util.Locale;
import java.util.UUID;
import org.json.JSONArray;
import org.json.JSONObject;

/** Test-only APK: real scoped-storage duplicate publication; no user file access. */
public final class FileReceiveProbeActivity extends android.app.Activity {
    private final String prefix = "RemoteDesk-owned-mime-" + UUID.randomUUID();
    private final List<Uri> owned = new ArrayList<>();
    private final JSONArray checks = new JSONArray();
    private boolean failed;

    @Override public void onCreate(android.os.Bundle state) {
        super.onCreate(state);
        android.widget.TextView label = new android.widget.TextView(this);
        label.setText("RemoteDesk MediaStore regression\nOwned synthetic files only");
        setContentView(label);
        new Thread(this::runProbe, "RemoteDeskOwnedFileProbe").start();
    }

    private void check(String name, boolean passed, Object details) throws Exception {
        checks.put(new JSONObject().put("name", name).put("passed", passed).put("details", details));
        failed |= !passed;
    }

    private void runProbe() {
        try {
            // Reproduce the original generic-MIME behavior on the same OS.
            String baseline = prefix + "-baseline.txt";
            for (int i = 0; i < 2; i++) {
                ContentValues values = new ContentValues();
                values.put(MediaStore.MediaColumns.DISPLAY_NAME, baseline);
                values.put(MediaStore.MediaColumns.MIME_TYPE, "application/octet-stream");
                values.put(MediaStore.MediaColumns.RELATIVE_PATH, "Download/RemoteDeskReceived");
                values.put(MediaStore.MediaColumns.IS_PENDING, 1);
                Uri uri = getContentResolver().insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values);
                if (uri == null) throw new IllegalStateException("Baseline insert failed");
                owned.add(uri);
                try (java.io.OutputStream stream = getContentResolver().openOutputStream(uri)) {
                    if (stream == null) throw new IllegalStateException("Baseline output unavailable");
                    stream.write(new byte[] { (byte) (i + 1) });
                }
                values.clear();
                values.put(MediaStore.MediaColumns.IS_PENDING, 0);
                if (getContentResolver().update(uri, values, null, null) != 1)
                    throw new IllegalStateException("Baseline publication failed");
            }
            List<Row> baselineRows = rows();
            check("OS reproduces old misplaced extension with generic MIME", baselineRows.stream()
                .anyMatch(row -> row.name.equals(prefix + "-baseline.txt (1)")), baselineRows.toString());

            AndroidFileTransferReceiver receiver = new AndroidFileTransferReceiver(this);
            for (String extension : new String[] {"txt", "TXT", "zip", "rdkunknown"}) {
                String label = extension.equals("TXT") ? "upper" : extension;
                String name = prefix + "-" + label + "." + extension;
                byte[][] contents = {
                    ("RemoteDesk first " + extension + " 中文😀").getBytes(StandardCharsets.UTF_8),
                    ("RemoteDesk second " + extension + " 中文😀").getBytes(StandardCharsets.UTF_8)
                };
                String firstReceipt = receive(receiver, name, contents[0]);
                String secondReceipt = receive(receiver, name, contents[1]);
                List<Row> published = rows();
                Row first = published.stream().filter(row -> row.name.equals(name)).findFirst().orElse(null);
                String duplicateName = prefix + "-" + label + " (1)." + extension;
                Row second = published.stream().filter(row -> row.name.equals(duplicateName)).findFirst().orElse(null);
                check("Duplicate preserves ." + extension + " and original content", first != null && second != null
                    && Arrays.equals(read(first.uri), contents[0]) && Arrays.equals(read(second.uri), contents[1]), published.toString());
                check("Receipt reports actual ." + extension + " filenames", firstReceipt.contains(name)
                    && secondReceipt.contains(duplicateName), secondReceipt);
                String expectedMime = extension.equalsIgnoreCase("txt") ? "text/plain"
                    : extension.equals("zip") ? "application/zip" : "application/octet-stream";
                check("Stored MIME for ." + extension, first != null && second != null
                    && expectedMime.equals(first.mime) && expectedMime.equals(second.mime), expectedMime);
            }
        } catch (Exception error) {
            failed = true;
            try { check(error.getClass().getSimpleName(), false, error.getMessage()); } catch (Exception ignored) { }
        } finally {
            try {
                rows(); // Adopt only UUID-prefixed rows created by this run, including a failed publication.
                for (Uri uri : owned) getContentResolver().delete(uri, null, null);
                check("All owned public test files removed", rows().isEmpty(), owned.size());
            } catch (Exception cleanup) { failed = true; }
            try {
                JSONObject report = new JSONObject().put("complete", true).put("passed", !failed)
                    .put("sdk", android.os.Build.VERSION.SDK_INT).put("checks", checks);
                java.nio.file.Files.write(new java.io.File(getFilesDir(), "file-receive-probe.json").toPath(),
                    report.toString(2).getBytes(StandardCharsets.UTF_8));
            } catch (Exception error) { throw new IllegalStateException(error); }
        }
    }

    private List<Row> rows() throws Exception {
        List<Row> rows = new ArrayList<>();
        try (Cursor cursor = getContentResolver().query(MediaStore.Downloads.EXTERNAL_CONTENT_URI,
            new String[] {"_id", "_display_name", "mime_type"}, "_display_name LIKE ?",
            new String[] {prefix + "%"}, null)) {
            if (cursor == null) throw new IllegalStateException("MediaStore query failed");
            while (cursor.moveToNext()) {
                String name = cursor.getString(1);
                if (!name.startsWith(prefix)) throw new IllegalStateException("Wrong cleanup scope");
                Uri uri = ContentUris.withAppendedId(MediaStore.Downloads.EXTERNAL_CONTENT_URI, cursor.getLong(0));
                if (!owned.contains(uri)) owned.add(uri);
                rows.add(new Row(uri, name, cursor.getString(2)));
            }
        }
        return rows;
    }

    private byte[] read(Uri uri) throws Exception {
        try (java.io.InputStream input = getContentResolver().openInputStream(uri);
             java.io.ByteArrayOutputStream output = new java.io.ByteArrayOutputStream()) {
            if (input == null) throw new IllegalStateException("Cannot read owned file");
            byte[] buffer = new byte[1024];
            int count;
            while ((count = input.read(buffer)) != -1) output.write(buffer, 0, count);
            return output.toByteArray();
        }
    }

    private static String receive(AndroidFileTransferReceiver receiver, String name, byte[] bytes) throws Exception {
        String id = UUID.randomUUID().toString();
        receiver.start(new RemoteDeskTransport.ControlMessage(RemoteDeskProtocol.CONTROL_FILE_TRANSFER_START,
            null, id, name, bytes.length, 0, null, 0));
        receiver.writeChunk(new RemoteDeskTransport.ControlMessage(RemoteDeskProtocol.CONTROL_FILE_TRANSFER_CHUNK,
            null, id, null, 0, 0, bytes, 0));
        StringBuilder hash = new StringBuilder();
        for (byte value : MessageDigest.getInstance("SHA-256").digest(bytes))
            hash.append(String.format(Locale.ROOT, "%02x", value & 255));
        receiver.setExpectedChecksum(new RemoteDeskTransport.ControlMessage(RemoteDeskProtocol.CONTROL_FILE_TRANSFER_CHECKSUM,
            null, id, null, 0, 0, null, 0, "SHA256", hash.toString()));
        return receiver.complete(new RemoteDeskTransport.ControlMessage(RemoteDeskProtocol.CONTROL_FILE_TRANSFER_COMPLETE,
            null, id, null, 0, 0, null, 0));
    }

    private static final class Row {
        final Uri uri;
        final String name, mime;
        Row(Uri uri, String name, String mime) { this.uri = uri; this.name = name; this.mime = mime; }
        @Override public String toString() { return name + ":" + mime; }
    }
}
