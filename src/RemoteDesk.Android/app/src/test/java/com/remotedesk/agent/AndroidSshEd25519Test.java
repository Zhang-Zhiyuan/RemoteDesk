package com.remotedesk.agent;

import com.jcraft.jsch.JSch;
import com.jcraft.jsch.KeyPair;
import com.jcraft.jsch.bc.SignatureEd25519;
import java.io.ByteArrayOutputStream;
import java.nio.charset.StandardCharsets;
import java.security.Provider;
import java.security.Security;
import java.util.HexFormat;
import org.junit.Test;
import static org.junit.Assert.*;

/** Exercise the same lightweight BC classes selected by JSch on Android, not desktop JCA. */
public final class AndroidSshEd25519Test {
    @Test public void rfc8032SignatureUsesLightweightBcWithoutChangingSystemProviders() throws Exception {
        Provider[] providers = Security.getProviders();
        byte[] seed = HexFormat.of().parseHex("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60");
        byte[] publicKey = HexFormat.of().parseHex("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a");
        byte[] expected = HexFormat.of().parseHex("e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b");
        SignatureEd25519 signer = new SignatureEd25519();
        signer.init(); signer.setPrvKey(seed); signer.update(new byte[0]);
        assertArrayEquals(expected, signer.sign());
        SignatureEd25519 verifier = new SignatureEd25519();
        verifier.init(); verifier.setPubKey(publicKey); verifier.update(new byte[0]);
        assertTrue(verifier.verify(sshSignature(expected)));
        expected[0] ^= 1;
        verifier.init(); verifier.setPubKey(publicKey); verifier.update(new byte[0]);
        assertFalse(verifier.verify(sshSignature(expected)));
        assertArrayEquals(providers, Security.getProviders());
    }

    private static byte[] sshSignature(byte[] signature) throws Exception {
        ByteArrayOutputStream bytes = new ByteArrayOutputStream();
        java.io.DataOutputStream data = new java.io.DataOutputStream(bytes);
        byte[] type = "ssh-ed25519".getBytes(StandardCharsets.US_ASCII);
        data.writeInt(type.length); data.write(type);
        data.writeInt(signature.length); data.write(signature);
        return bytes.toByteArray();
    }

    @Test public void encryptedOpenSshEd25519KeyRoundTripsOnlyWithCorrectPassphrase() throws Exception {
        JSch client = new JSch();
        KeyPair original = KeyPair.genKeyPair(client, KeyPair.ED25519);
        byte[] passphrase = "synthetic-test-only-passphrase".getBytes(StandardCharsets.UTF_8);
        ByteArrayOutputStream bytes = new ByteArrayOutputStream();
        original.writeOpenSSHv1PrivateKey(bytes, passphrase);
        byte[] encoded = bytes.toByteArray();
        assertTrue(new String(encoded, StandardCharsets.US_ASCII).startsWith("-----BEGIN OPENSSH PRIVATE KEY-----"));
        KeyPair loaded = KeyPair.load(client, encoded, null);
        try {
            assertTrue(loaded.isEncrypted());
            assertFalse(loaded.decrypt("wrong-passphrase".getBytes(StandardCharsets.UTF_8)));
            assertTrue(loaded.decrypt(passphrase));
            assertEquals(KeyPair.ED25519, loaded.getKeyType());
            assertArrayEquals(original.getPublicKeyBlob(), loaded.getPublicKeyBlob());
        } finally {
            original.dispose(); loaded.dispose();
            java.util.Arrays.fill(passphrase, (byte) 0);
            java.util.Arrays.fill(encoded, (byte) 0);
        }
    }
}
