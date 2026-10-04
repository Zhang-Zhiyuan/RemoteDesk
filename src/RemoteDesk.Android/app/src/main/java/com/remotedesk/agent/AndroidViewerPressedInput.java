package com.remotedesk.agent;

import java.util.ArrayList;
import java.util.LinkedHashSet;
import java.util.List;

/** Only records input actually written by the connection's single input sender. */
final class AndroidViewerPressedInput {
    private final LinkedHashSet<Integer> keys = new LinkedHashSet<>();
    private final LinkedHashSet<Integer> buttons = new LinkedHashSet<>();

    void sent(AndroidViewerInputQueue.Command command) {
        if (command.kind == RemoteDeskProtocol.INPUT_KEY_DOWN) keys.add(command.data);
        else if (command.kind == RemoteDeskProtocol.INPUT_KEY_UP) keys.remove(command.data);
        else if (command.kind == RemoteDeskProtocol.INPUT_MOUSE_DOWN) buttons.add(command.button);
        else if (command.kind == RemoteDeskProtocol.INPUT_MOUSE_UP) buttons.remove(command.button);
    }

    List<AndroidViewerInputQueue.Command> releases(int x, int y) {
        List<AndroidViewerInputQueue.Command> result = new ArrayList<>();
        List<Integer> reverseKeys = new ArrayList<>(keys);
        for (int i = reverseKeys.size() - 1; i >= 0; i--)
            result.add(new AndroidViewerInputQueue.Command(RemoteDeskProtocol.INPUT_KEY_UP, 0, 0, 0, reverseKeys.get(i)));
        for (int button : buttons)
            result.add(new AndroidViewerInputQueue.Command(RemoteDeskProtocol.INPUT_MOUSE_UP, button, x, y, 0));
        return result;
    }
}
