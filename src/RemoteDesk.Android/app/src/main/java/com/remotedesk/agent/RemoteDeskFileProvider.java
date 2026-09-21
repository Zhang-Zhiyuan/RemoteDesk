package com.remotedesk.agent;

import android.content.ContentProvider;
import android.content.ContentValues;
import android.content.Context;
import android.database.Cursor;
import android.database.MatrixCursor;
import android.net.Uri;
import android.os.ParcelFileDescriptor;
import android.provider.OpenableColumns;
import android.webkit.MimeTypeMap;

import java.io.File;
import java.io.FileNotFoundException;
import java.io.IOException;
import java.util.List;

public final class RemoteDeskFileProvider extends ContentProvider {
    static final String AUTHORITY = "com.remotedesk.agent.files";

    private static final String RECEIVED_PATH = "received";
    private static final String DIAGNOSTICS_PATH = "diagnostics";

    static Uri uriForReceivedFile(Context context, File file) {
        return new Uri.Builder()
            .scheme("content")
            .authority(AUTHORITY)
            .appendPath(RECEIVED_PATH)
            .appendPath(file.getName())
            .build();
    }

    static Uri uriForDiagnosticFile(Context context, File file) {
        return new Uri.Builder()
            .scheme("content")
            .authority(AUTHORITY)
            .appendPath(DIAGNOSTICS_PATH)
            .appendPath(file.getName())
            .build();
    }

    @Override
    public boolean onCreate() {
        return true;
    }

    @Override
    public ParcelFileDescriptor openFile(Uri uri, String mode) throws FileNotFoundException {
        if (!"r".equals(mode)) {
            throw new FileNotFoundException("RemoteDesk files are read-only.");
        }

        return ParcelFileDescriptor.open(resolveFile(uri), ParcelFileDescriptor.MODE_READ_ONLY);
    }

    @Override
    public String getType(Uri uri) {
        File file;
        try {
            file = resolveFile(uri);
        } catch (FileNotFoundException ex) {
            return "application/octet-stream";
        }

        return AndroidFileMimeType.resolve(file.getName(),
            MimeTypeMap.getSingleton()::getMimeTypeFromExtension);
    }

    static String extensionForMimeLookup(String fileName) {
        return AndroidFileMimeType.extension(fileName);
    }

    @Override
    public Cursor query(
        Uri uri,
        String[] projection,
        String selection,
        String[] selectionArgs,
        String sortOrder) {
        File file;
        try {
            file = resolveFile(uri);
        } catch (FileNotFoundException ex) {
            return null;
        }

        String[] columns = projection == null || projection.length == 0
            ? new String[] { OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE }
            : projection;
        MatrixCursor cursor = new MatrixCursor(columns, 1);
        Object[] values = new Object[columns.length];
        for (int index = 0; index < columns.length; index++) {
            if (OpenableColumns.DISPLAY_NAME.equals(columns[index])) {
                values[index] = file.getName();
            } else if (OpenableColumns.SIZE.equals(columns[index])) {
                values[index] = file.length();
            }
        }

        cursor.addRow(values);
        return cursor;
    }

    @Override
    public Uri insert(Uri uri, ContentValues values) {
        throw new UnsupportedOperationException("RemoteDesk files are read-only.");
    }

    @Override
    public int delete(Uri uri, String selection, String[] selectionArgs) {
        return 0;
    }

    @Override
    public int update(Uri uri, ContentValues values, String selection, String[] selectionArgs) {
        return 0;
    }

    private File resolveFile(Uri uri) throws FileNotFoundException {
        Context context = getContext();
        if (context == null || !AUTHORITY.equals(uri.getAuthority())) {
            throw new FileNotFoundException("Invalid RemoteDesk file URI.");
        }

        List<String> segments = uri.getPathSegments();
        if (segments.size() != 2) {
            throw new FileNotFoundException("Invalid RemoteDesk file path.");
        }

        File directory = resolveDirectory(context, segments.get(0));
        File file = new File(directory, segments.get(1));
        try {
            String directoryPath = directory.getCanonicalPath();
            String filePath = file.getCanonicalPath();
            if (!filePath.startsWith(directoryPath + File.separator) || !file.isFile()) {
                throw new FileNotFoundException("RemoteDesk file is not available.");
            }

            return file;
        } catch (IOException ex) {
            throw new FileNotFoundException("RemoteDesk file is not available.");
        }
    }

    private static File resolveDirectory(Context context, String path) throws FileNotFoundException {
        if (RECEIVED_PATH.equals(path)) {
            return AndroidFileTransferReceiver.getAppSpecificReceiveDirectory(context);
        }

        if (DIAGNOSTICS_PATH.equals(path)) {
            return AndroidSessionLog.getDiagnosticDirectory(context);
        }

        throw new FileNotFoundException("Invalid RemoteDesk file path.");
    }
}
