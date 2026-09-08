package com.remotedesk.agent;

final class AndroidViewerInputCapabilityPolicy {
    private AndroidViewerInputCapabilityPolicy() {
    }

    static boolean canSendInput(boolean deviceInfoReceived, int remoteCapabilities) {
        return deviceInfoReceived &&
            (remoteCapabilities & RemoteDeskProtocol.CAPABILITY_INPUT_CONTROL) != 0;
    }

    static boolean shouldConsumeTouch(boolean canSendInput) {
        return canSendInput;
    }

    static boolean shouldResetGesture(long appliedUiGeneration, long currentGeneration) {
        return appliedUiGeneration != currentGeneration;
    }

    static long nextGeneration(
        long currentGeneration,
        boolean previousAvailability,
        boolean currentAvailability) {
        if (previousAvailability == currentAvailability) {
            return currentGeneration;
        }
        return currentGeneration == Long.MAX_VALUE
            ? 1L
            : currentGeneration + 1L;
    }

    static boolean isCurrentCommand(
        boolean canSendInput,
        long commandGeneration,
        long currentGeneration) {
        return canSendInput && commandGeneration == currentGeneration;
    }
}
