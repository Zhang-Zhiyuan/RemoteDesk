package com.remotedesk.agent;

import org.json.JSONObject;
import org.junit.Rule;
import org.junit.Test;
import org.junit.rules.TemporaryFolder;
import java.io.ByteArrayInputStream;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.Arrays;
import static org.junit.Assert.*;

public final class AccountLoginPackageTest {
    @Rule public final TemporaryFolder destination = new TemporaryFolder();

    private static byte[] fixture() throws IOException {
        Path root = Path.of("").toAbsolutePath();
        while (root != null && !Files.isRegularFile(root.resolve("tests/data/account-login-v1.asxaccount"))) root = root.getParent();
        assertNotNull("shared API Switcher fixture", root);
        return Files.readAllBytes(root.resolve("tests/data/account-login-v1.asxaccount"));
    }

    @Test public void senderUsesSameBytesAsPickerEvenIfDocumentChanges() throws Exception {
        byte[] expected = fixture();
        byte[] document = expected.clone();
        String name = "演示账号.asxaccount";
        byte[] approved = AccountLoginPackage.read(name, new ByteArrayInputStream(document));
        Arrays.fill(document, (byte) 0);
        AndroidFileSender sender = new AndroidFileSender();
        AndroidFileTransferReceiver receiver = new AndroidFileTransferReceiver(destination.getRoot());
        String saved = sender.send(new ByteArrayInputStream(approved), name, approved.length,
            RemoteDeskProtocol.CAPABILITY_FILE_TRANSFER_RECEIPT | RemoteDeskProtocol.CAPABILITY_FILE_CHECKSUM, payload -> {
                var message = RemoteDeskTransport.decodeControl(payload);
                switch (message.kind) {
                    case RemoteDeskProtocol.CONTROL_FILE_TRANSFER_START: receiver.start(message); break;
                    case RemoteDeskProtocol.CONTROL_FILE_TRANSFER_CHUNK: receiver.writeChunk(message); break;
                    case RemoteDeskProtocol.CONTROL_FILE_TRANSFER_CHECKSUM: receiver.setExpectedChecksum(message); break;
                    case RemoteDeskProtocol.CONTROL_FILE_TRANSFER_COMPLETE:
                        String receipt = receiver.complete(message);
                        sender.receive(RemoteDeskTransport.decodeControl(RemoteDeskTransport.encodeFileTransferReceipt(message.transferId, true, receipt)));
                        break;
                }
            }, () -> true, status -> {});
        Path received = destination.getRoot().toPath().resolve(name);
        assertTrue(saved.contains(received.toString()));
        assertArrayEquals(expected, Files.readAllBytes(received));
    }

    @Test public void rejectsPlaintextProfilesDamagedOversizedAndRenamedFiles() throws Exception {
        byte[] source = fixture();
        JSONObject plain = new JSONObject(new String(source, StandardCharsets.UTF_8));
        plain.put("version", 2).put("cipher", new JSONObject().put("name", "none")).remove("kdf");
        IOException error = assertThrows(IOException.class, () -> AccountLoginPackage.read("demo.asxaccount",
            new ByteArrayInputStream(plain.toString().getBytes(StandardCharsets.UTF_8))));
        assertTrue(error.getMessage().contains("没有设置迁移密码"));
        JSONObject damaged = new JSONObject(new String(source, StandardCharsets.UTF_8));
        JSONObject kdf = damaged.getJSONObject("kdf");
        kdf.put("salt", kdf.getString("salt").replace("=", ""));
        assertThrows(IOException.class, () -> AccountLoginPackage.read("demo.asxaccount",
            new ByteArrayInputStream(damaged.toString().getBytes(StandardCharsets.UTF_8))));
        for (String field : new String[] {"version", "format", "payload"}) {
            JSONObject json = new JSONObject(new String(source, StandardCharsets.UTF_8));
            json.put(field, field.equals("version") ? 2 : "!");
            byte[] data = json.toString().getBytes(StandardCharsets.UTF_8);
            assertThrows(IOException.class, () -> AccountLoginPackage.read("demo.asxaccount", new ByteArrayInputStream(data)));
        }
        assertThrows(IOException.class, () -> AccountLoginPackage.read("demo.json", new ByteArrayInputStream(source)));
        byte[] oversized = new byte[AccountLoginPackage.LIMIT + 1];
        assertThrows(IOException.class, () -> AccountLoginPackage.read("demo.asxaccount", new ByteArrayInputStream(oversized)));
    }
}
