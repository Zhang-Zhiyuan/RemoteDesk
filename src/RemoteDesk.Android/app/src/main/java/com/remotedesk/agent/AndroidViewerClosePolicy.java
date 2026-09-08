package com.remotedesk.agent;

final class AndroidViewerClosePolicy {
    interface Owner {
        boolean signalClose();

        void teardown();
    }

    interface Scheduler {
        boolean schedule(Runnable work);
    }

    private AndroidViewerClosePolicy() {
    }

    static boolean closeFromUi(Owner owner, Scheduler scheduler) {
        if (owner == null) {
            return false;
        }
        boolean newlyClosed = owner.signalClose();
        if (newlyClosed && scheduler != null) {
            scheduler.schedule(owner::teardown);
        }
        return newlyClosed;
    }
}
