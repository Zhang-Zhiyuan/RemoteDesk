package com.remotedesk.agent;

/** Synthetic, private documents for exercising the real ContentResolver path. */
public final class ViewerFilesProbeProvider extends android.content.ContentProvider {
    @Override public boolean onCreate() { return true; }
    @Override public String getType(android.net.Uri uri) { return "application/octet-stream"; }
    private int index(android.net.Uri uri) {
        int value = Integer.parseInt(uri.getLastPathSegment());
        if (value < 0 || value > 2) throw new IllegalArgumentException("Unknown synthetic document");
        return value;
    }
    @Override public android.database.Cursor query(android.net.Uri uri, String[] projection, String selection, String[] args, String sort) {
        int index = index(uri);
        android.database.MatrixCursor cursor = new android.database.MatrixCursor(new String[] {
            android.provider.OpenableColumns.DISPLAY_NAME, android.provider.OpenableColumns.SIZE });
        cursor.addRow(new Object[] { "合成测试-" + index + ".bin", length(index) });
        return cursor;
    }
    private int length(int index) { return index == 2 ? 8 * 1024 * 1024 : (index + 1) * 1024; }
    @Override public android.os.ParcelFileDescriptor openFile(android.net.Uri uri, String mode) throws java.io.FileNotFoundException {
        int index = index(uri);
        java.io.File file = new java.io.File(getContext().getCacheDir(), "synthetic-upload-" + index);
        try (java.io.FileOutputStream output = new java.io.FileOutputStream(file)) {
            byte[] bytes = new byte[1024]; java.util.Arrays.fill(bytes, (byte)(index + 1));
            for (int count = 0; count < length(index); count += bytes.length) output.write(bytes);
        } catch (java.io.IOException error) { throw new java.io.FileNotFoundException(error.toString()); }
        return android.os.ParcelFileDescriptor.open(file, android.os.ParcelFileDescriptor.MODE_READ_ONLY);
    }
    @Override public android.net.Uri insert(android.net.Uri uri, android.content.ContentValues values) { throw new UnsupportedOperationException(); }
    @Override public int delete(android.net.Uri uri, String where, String[] args) { throw new UnsupportedOperationException(); }
    @Override public int update(android.net.Uri uri, android.content.ContentValues values, String where, String[] args) { throw new UnsupportedOperationException(); }
}
