package com.remotedesk.agent;

final class AndroidHostStopPolicy {
    interface Session {
        void signalStopped();

        void signalUdpClose();

        void closeWorkers();
    }

    private AndroidHostStopPolicy() {
    }

    static void signalFromMainThread(Session session) {
        if (session == null) {
            return;
        }
        session.signalStopped();
        session.signalUdpClose();
    }

    static void finishOnOwnerWorker(Session session) {
        if (session != null) {
            session.closeWorkers();
        }
    }
}
