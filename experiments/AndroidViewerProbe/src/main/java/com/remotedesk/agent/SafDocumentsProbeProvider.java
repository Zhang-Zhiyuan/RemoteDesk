package com.remotedesk.agent;

import android.database.Cursor;
import android.database.MatrixCursor;
import android.os.CancellationSignal;
import android.os.ParcelFileDescriptor;
import android.provider.DocumentsContract.Document;
import android.provider.DocumentsContract.Root;
import android.provider.DocumentsProvider;
import java.io.File;
import java.io.FileNotFoundException;
import java.io.FileOutputStream;
import java.io.IOException;

/** Isolated APK's synthetic SAF provider; no real user files or network access. */
public final class SafDocumentsProbeProvider extends DocumentsProvider {
    private static final String[] DOCUMENT_COLUMNS = {Document.COLUMN_DOCUMENT_ID,
        Document.COLUMN_DISPLAY_NAME, Document.COLUMN_MIME_TYPE, Document.COLUMN_FLAGS, Document.COLUMN_SIZE};
    private static final String[] IDS = {"size-fallback", "unknown-size", "unreadable"};

    @Override public boolean onCreate() { return true; }

    @Override public Cursor queryRoots(String[] projection) {
        MatrixCursor cursor = new MatrixCursor(projection == null ? new String[] {Root.COLUMN_ROOT_ID,
            Root.COLUMN_DOCUMENT_ID, Root.COLUMN_TITLE, Root.COLUMN_FLAGS, Root.COLUMN_MIME_TYPES} : projection);
        MatrixCursor.RowBuilder row = cursor.newRow();
        row.add(Root.COLUMN_ROOT_ID, "synthetic").add(Root.COLUMN_DOCUMENT_ID, "root")
            .add(Root.COLUMN_TITLE, "RemoteDesk SAF fixtures").add(Root.COLUMN_FLAGS, 0)
            .add(Root.COLUMN_MIME_TYPES, "*/*");
        return cursor;
    }

    @Override public Cursor queryDocument(String documentId, String[] projection) throws FileNotFoundException {
        MatrixCursor cursor = new MatrixCursor(projection == null ? DOCUMENT_COLUMNS : projection);
        add(cursor, documentId);
        return cursor;
    }

    @Override public Cursor queryChildDocuments(String parentId, String[] projection, String sortOrder)
            throws FileNotFoundException {
        if (!"root".equals(parentId)) throw new FileNotFoundException("Unknown synthetic directory");
        MatrixCursor cursor = new MatrixCursor(projection == null ? DOCUMENT_COLUMNS : projection);
        for (String id : IDS) add(cursor, id);
        if (new File(getContext().getFilesDir(), "ssh-key.pem").isFile()) {
            add(cursor, "ssh-key"); add(cursor, "ssh-key-slow");
        }
        return cursor;
    }

    private void add(MatrixCursor cursor, String id) throws FileNotFoundException {
        if (!"root".equals(id) && !id.startsWith("ssh-key") && !java.util.Arrays.asList(IDS).contains(id))
            throw new FileNotFoundException("Unknown synthetic document");
        MatrixCursor.RowBuilder row = cursor.newRow();
        row.add(Document.COLUMN_DOCUMENT_ID, id).add(Document.COLUMN_DISPLAY_NAME,
            "root".equals(id) ? "RemoteDesk SAF fixtures" : "saf-" + id + ".bin")
            .add(Document.COLUMN_MIME_TYPE, "root".equals(id) ? Document.MIME_TYPE_DIR : "application/octet-stream")
            .add(Document.COLUMN_FLAGS, 0).add(Document.COLUMN_SIZE, "unreadable".equals(id) ? 4096 : null);
    }

    @Override public ParcelFileDescriptor openDocument(String id, String mode, CancellationSignal signal)
            throws FileNotFoundException {
        if (!"r".equals(mode)) throw new FileNotFoundException("Read-only synthetic provider");
        if ("unreadable".equals(id)) throw new FileNotFoundException("Synthetic provider cannot open this document");
        try {
            if ("ssh-key".equals(id) || "ssh-key-slow".equals(id)) {
                if ("ssh-key-slow".equals(id)) try { Thread.sleep(3500); }
                    catch (InterruptedException ex) { Thread.currentThread().interrupt(); throw new IOException(ex); }
                return ParcelFileDescriptor.open(new File(getContext().getFilesDir(), "ssh-key.pem"), ParcelFileDescriptor.MODE_READ_ONLY);
            }
            if ("unknown-size".equals(id)) {
                ParcelFileDescriptor[] pipe = ParcelFileDescriptor.createPipe();
                pipe[1].close();
                return pipe[0]; // statSize remains unknown, just like a streamed provider.
            }
            if (!"size-fallback".equals(id)) throw new FileNotFoundException("Unknown synthetic document");
            File file = new File(getContext().getCacheDir(), "saf-size-fallback.bin");
            try (FileOutputStream output = new FileOutputStream(file)) {
                byte[] bytes = new byte[3073];
                for (int i = 0; i < bytes.length; i++) bytes[i] = (byte) (i % 251);
                output.write(bytes);
            }
            return ParcelFileDescriptor.open(file, ParcelFileDescriptor.MODE_READ_ONLY);
        } catch (IOException failure) {
            throw new FileNotFoundException(failure.getMessage());
        }
    }
}
