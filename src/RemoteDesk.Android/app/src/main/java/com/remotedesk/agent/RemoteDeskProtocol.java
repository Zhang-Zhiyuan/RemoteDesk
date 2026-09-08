package com.remotedesk.agent;

final class RemoteDeskProtocol {
    static final int HOST_PORT = 56565;
    static final int DISCOVERY_PORT = 56566;
    static final String DISCOVERY_REQUEST = "RemoteDesk.Discover.v1";
    static final String DISCOVERY_RESPONSE_TYPE = "RemoteDesk.Discover.Response.v1";
    static final String PLATFORM_ANDROID = "Android";
    static final String CAPTURE_TARGET_ID = "android-screen";
    static final String CAPTURE_TARGET_NAME = "Android Screen";

    static final int CAPABILITY_REMOTE_DESKTOP = 1;
    static final int CAPABILITY_INPUT_CONTROL = 1 << 1;
    static final int CAPABILITY_CLIPBOARD_TEXT = 1 << 2;
    static final int CAPABILITY_FILE_RECEIVE = 1 << 3;
    static final int CAPABILITY_CAPTURE_TARGET_SELECTION = 1 << 4;
    static final int CAPABILITY_REMOTE_START = 1 << 5;
    static final int CAPABILITY_FILE_DROP_PASTE = 1 << 6;
    static final int CAPABILITY_FILE_SEND = 1 << 7;
    static final int CAPABILITY_FILE_CHECKSUM = 1 << 8;
    static final int CAPABILITY_FILE_TRANSFER_CANCEL = 1 << 9;
    static final int CAPABILITY_LOW_LATENCY_UDP_VIDEO = 1 << 13;
    static final int CAPABILITY_UDP_VIDEO_CONGESTION_FEEDBACK = 1 << 14;
    static final int CAPABILITY_LOW_LATENCY_UDP_VIDEO_XOR_FEC = 1 << 15;
    static final int CAPABILITY_LOW_LATENCY_UDP_MOUSE_INPUT = 1 << 16;
    static final int CAPABILITY_LOW_LATENCY_UDP_MOUSE_INPUT_APPLIED_ACK = 1 << 17;
    static final int CAPABILITY_SHORT_GOP_H264 = 1 << 18;
    static final int CAPABILITY_HIGH_FRAME_RATE_H264 = 1 << 19;
    static final int CAPABILITY_AUTHENTICATED_UDP_HEARTBEAT = 1 << 20;
    static final int CAPABILITY_HIGH_QUALITY_JPEG = 1 << 21;

    static final int VIDEO_CODEC_JPEG = 1;
    static final int VIDEO_CODEC_H264_ANNEX_B = 1 << 1;
    static final int MAX_CONTROL_ITEMS = 64;
    static final int MAX_FRAME_DIMENSION = 32_768;
    static final long MAX_FRAME_PIXELS = 16_777_216L;

    static final int INPUT_MOUSE_MOVE = 1;
    static final int INPUT_MOUSE_DOWN = 2;
    static final int INPUT_MOUSE_UP = 3;
    static final int INPUT_MOUSE_WHEEL = 4;
    static final int INPUT_KEY_DOWN = 5;
    static final int INPUT_KEY_UP = 6;
    static final int INPUT_TEXT = 7;
    static final int INPUT_PINCH_ZOOM = 8;

    static final int MOUSE_NONE = 0;
    static final int MOUSE_LEFT = 1;
    static final int MOUSE_RIGHT = 2;
    static final int MOUSE_MIDDLE = 3;

    static final int MESSAGE_FRAME = 1;
    static final int MESSAGE_INPUT = 2;
    static final int MESSAGE_CONTROL = 3;
    static final int MESSAGE_PING = 4;
    static final int MESSAGE_PONG = 5;
    static final int MESSAGE_VIDEO_FRAME = 6;

    static final int FRAME_ENCODING_JPEG = 1;
    static final int FRAME_ENCODING_H264_ANNEX_B = 2;
    static final int FRAME_FLAG_KEY_FRAME = 1;
    static final int FRAME_FLAG_CODEC_CONFIG = 1 << 1;

    static final int CONTROL_CAPTURE_TARGET_LIST = 1;
    static final int CONTROL_SELECT_CAPTURE_TARGET = 2;
    static final int CONTROL_CAPTURE_TARGET_CHANGED = 3;
    static final int CONTROL_CLIPBOARD_GET_TEXT = 4;
    static final int CONTROL_CLIPBOARD_SET_TEXT = 5;
    static final int CONTROL_CLIPBOARD_TEXT = 6;
    static final int CONTROL_CLIPBOARD_STATUS = 7;
    static final int CONTROL_FILE_TRANSFER_START = 8;
    static final int CONTROL_FILE_TRANSFER_CHUNK = 9;
    static final int CONTROL_FILE_TRANSFER_COMPLETE = 10;
    static final int CONTROL_FILE_TRANSFER_STATUS = 11;
    static final int CONTROL_DEVICE_INFO = 12;
    static final int CONTROL_VIEWER_INFO = 13;
    static final int CONTROL_VIDEO_KEY_FRAME_REQUEST = 14;
    static final int CONTROL_FILE_DROP_PASTE_BEGIN = 15;
    static final int CONTROL_FILE_DROP_PASTE_COMMIT = 16;
    static final int CONTROL_FILE_DROP_PASTE_CANCEL = 17;
    static final int CONTROL_FILE_TRANSFER_REQUEST_CLIPBOARD_FILES = 18;
    static final int CONTROL_FILE_TRANSFER_CHECKSUM = 19;
    static final int CONTROL_VIEWER_CAPABILITIES = 20;
    static final int CONTROL_FILE_TRANSFER_CANCEL = 21;
    static final int CONTROL_LOW_LATENCY_VIDEO_OFFER = 28;
    static final int CONTROL_LOW_LATENCY_VIDEO_READY = 29;
    static final int CONTROL_LOW_LATENCY_VIDEO_STOP = 30;
    static final int CONTROL_LOW_LATENCY_VIDEO_STOPPED = 31;
    static final int CONTROL_SESSION_REJECTED = 32;

    static final int LOW_LATENCY_FALLBACK_GENERIC = 1;
    static final int LOW_LATENCY_FALLBACK_BIND_TIMEOUT = 3;
    static final int LOW_LATENCY_FALLBACK_PRESERVE_UDP_INPUT = 4;

    private RemoteDeskProtocol() {
    }
}
