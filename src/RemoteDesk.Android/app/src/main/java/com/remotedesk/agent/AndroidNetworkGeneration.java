package com.remotedesk.agent;

import java.util.Objects;

/** Tracks default-network replacement without binding the process to a network. */
final class AndroidNetworkGeneration {
    private Object activeNetwork;
    private long generation;

    synchronized void setInitialNetwork(Object network) {
        activeNetwork = network;
    }

    synchronized boolean onAvailable(Object network) {
        if (network == null || Objects.equals(activeNetwork, network)) {
            return false;
        }

        boolean replacedLiveRoute = activeNetwork != null;
        activeNetwork = network;
        generation++;
        return replacedLiveRoute;
    }

    synchronized boolean onLost(Object network) {
        if (network == null || !Objects.equals(activeNetwork, network)) {
            return false;
        }

        activeNetwork = null;
        generation++;
        return true;
    }

    synchronized long getGeneration() {
        return generation;
    }
}
