package com.remotedesk.agent;

import java.io.IOException;

/** An authenticated host intentionally ended this logical viewer session. */
final class AndroidSessionRejectedException extends IOException {
    AndroidSessionRejectedException(String message) {
        super(message);
    }
}
