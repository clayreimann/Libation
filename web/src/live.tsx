import { createContext, useCallback, useContext, useEffect, useMemo, useReducer, useRef, useState, type ReactNode } from "react";
import { api } from "./api/client";
import { eventTypes, type Status } from "./api/types";
import {
  applyQueueEvent,
  applySnapshot,
  applyStatusEvent,
  emptyQueue,
  needsQueueRefetch,
  needsStatusRefetch,
  parseEvent,
  type QueueView,
} from "./events";

interface Live {
  connected: boolean;
  status: Status | null;
  statusError: string | null;
  queue: QueueView;
  refreshQueue: () => void;
  refreshStatus: () => void;
  setStatus: (s: Status) => void;
  onBookEvent: (fn: () => void) => () => void;
}

const Ctx = createContext<Live | null>(null);

export function useLive(): Live {
  const v = useContext(Ctx);
  if (!v) throw new Error("LiveProvider missing");
  return v;
}

type Action = { kind: "snapshot"; snap: Parameters<typeof applySnapshot>[1] } | { kind: "event"; evt: Parameters<typeof applyQueueEvent>[1] };

function reduce(view: QueueView, a: Action): QueueView {
  return a.kind === "snapshot" ? applySnapshot(view, a.snap) : applyQueueEvent(view, a.evt);
}

export function LiveProvider({ children }: { children: ReactNode }) {
  const [connected, setConnected] = useState(false);
  const [status, setStatus] = useState<Status | null>(null);
  const [statusError, setStatusError] = useState<string | null>(null);
  const [queue, dispatch] = useReducer(reduce, emptyQueue);
  const bookListeners = useRef(new Set<() => void>());

  const refreshStatus = useCallback(() => {
    api.status().then(
      (s) => {
        setStatus(s);
        setStatusError(null);
      },
      (e: Error) => setStatusError(e.message),
    );
  }, []);

  const refreshQueue = useCallback(() => {
    api.queue().then(
      (snap) => dispatch({ kind: "snapshot", snap }),
      () => undefined,
    );
  }, []);

  useEffect(() => {
    refreshStatus();
    refreshQueue();

    let queueTimer: number | undefined;
    const scheduleQueueRefetch = () => {
      window.clearTimeout(queueTimer);
      queueTimer = window.setTimeout(refreshQueue, 150);
    };

    const es = new EventSource("/api/events");
    es.onopen = () => {
      setConnected(true);
      refreshStatus();
      refreshQueue();
    };
    es.onerror = () => setConnected(false);

    for (const name of eventTypes) {
      es.addEventListener(name, (msg) => {
        const evt = parseEvent(name, (msg as MessageEvent<string>).data);
        if (!evt) return;
        setStatus((s) => applyStatusEvent(s, evt));
        dispatch({ kind: "event", evt });
        if (needsQueueRefetch(evt)) scheduleQueueRefetch();
        if (needsStatusRefetch(evt)) refreshStatus();
        if (evt.type === "BookFinished" || evt.type === "CycleFinished") bookListeners.current.forEach((fn) => fn());
      });
    }

    return () => {
      window.clearTimeout(queueTimer);
      es.close();
    };
  }, [refreshQueue, refreshStatus]);

  const onBookEvent = useCallback((fn: () => void) => {
    bookListeners.current.add(fn);
    return () => void bookListeners.current.delete(fn);
  }, []);

  const value = useMemo(
    () => ({ connected, status, statusError, queue, refreshQueue, refreshStatus, setStatus, onBookEvent }),
    [connected, status, statusError, queue, refreshQueue, refreshStatus, onBookEvent],
  );
  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}
