import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode, type RefObject } from "react";
import { api } from "../api/client";
import type { AudioFile, Book, Chapter } from "../api/types";
import {
  SKIP_SEC,
  SPEEDS,
  bookTarget,
  chapterAt,
  chapterTarget,
  clampTime,
  formatClock,
  loadPosition,
  loadSavedAt,
  loadSpeed,
  nextTarget,
  preferAudible,
  previousTarget,
  resumeTime,
  saveSpeed,
  savePosition,
  shouldSave,
  type SeekTarget,
} from "../player";
import { useMedia } from "../useMedia";
import { Cover } from "./Cover";
import { Icon } from "./Icon";

interface PlayerApi {
  playing: Book | null;
  play: (book: Book) => void;
}

const Ctx = createContext<PlayerApi>({ playing: null, play: () => undefined });
export const usePlayer = () => useContext(Ctx);

const srcFor = (asin: string, file: number, time: number) => `${api.audioUrl(asin, file)}${time > 0 ? `#t=${time.toFixed(1)}` : ""}`;

/**
 * Owns one <audio> element for the whole app. play() sets its source and starts it inside the click,
 * so phones that only allow playback from a user gesture start at once; the file list, chapters and
 * Audible position load afterwards.
 */
export function PlayerProvider({ children }: { children: ReactNode }) {
  const audio = useRef<HTMLAudioElement>(null);
  const [session, setSession] = useState<{ book: Book; file: number; time: number } | null>(null);

  const play = useCallback((b: Book) => {
    const a = audio.current;
    if (!a) return;
    if (session?.book.asin === b.asin) {
      void a.play().catch(() => undefined);
      return;
    }
    const saved = loadPosition(b.asin, Number.MAX_SAFE_INTEGER);
    a.src = srcFor(b.asin, saved.file, saved.time);
    a.playbackRate = loadSpeed();
    void a.play().catch(() => undefined);
    setSession({ book: b, file: saved.file, time: saved.time });
  }, [session?.book.asin]);

  const close = useCallback(() => {
    const a = audio.current;
    if (a) {
      a.pause();
      a.removeAttribute("src");
      a.load();
    }
    setSession(null);
  }, []);

  const value = useMemo(() => ({ playing: session?.book ?? null, play }), [session, play]);
  return (
    <Ctx.Provider value={value}>
      {children}
      <audio ref={audio} preload="auto" data-testid="audio" />
      {session && <PlayerUI key={session.book.asin} book={session.book} startFile={session.file} startTime={session.time} audio={audio} onClose={close} />}
    </Ctx.Provider>
  );
}

interface UIProps {
  book: Book;
  startFile: number;
  startTime: number;
  audio: RefObject<HTMLAudioElement | null>;
  onClose: () => void;
}

function PlayerUI({ book, startFile, startTime, audio, onClose }: UIProps) {
  const phone = useMedia("(max-width: 720px)");
  const [files, setFiles] = useState<AudioFile[] | null>(null);
  const [chapters, setChapters] = useState<Chapter[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [note, setNote] = useState<string | null>(null);
  const [fileIndex, setFileIndex] = useState(startFile);
  const [time, setTime] = useState(startTime);
  const [duration, setDuration] = useState(0);
  const [paused, setPaused] = useState(() => audio.current?.paused ?? true);
  const [waiting, setWaiting] = useState(true);
  const [speed, setSpeed] = useState(loadSpeed);
  const [expanded, setExpanded] = useState(false);
  const [showChapters, setShowChapters] = useState(false);
  const pendingSeek = useRef(startTime);
  const firstLoad = useRef(true);
  const userMoved = useRef(false);
  const lastSave = useRef({ at: 0, time: 0 });
  const fileRef = useRef(startFile);
  fileRef.current = fileIndex;

  const a = () => audio.current;

  // Load the file list and chapters while the first file is already playing.
  useEffect(() => {
    const ctl = new AbortController();
    Promise.all([api.audioFiles(book.asin, ctl.signal), api.chapters(book.asin, ctl.signal).catch(() => [] as Chapter[])]).then(
      ([f, c]) => {
        if (f.length === 0) return setError("No audio files found.");
        setChapters(c);
        setFiles(f);
        if (fileRef.current >= f.length) switchFile(0, 0, true);
      },
      (e: Error) => !ctl.signal.aborted && setError(e.message),
    );
    return () => ctl.abort();
  }, [book.asin]); // eslint-disable-line react-hooks/exhaustive-deps

  const switchFile = useCallback((index: number, t: number, autoplay: boolean) => {
    const el = audio.current;
    if (!el) return;
    pendingSeek.current = t;
    fileRef.current = index;
    setFileIndex(index);
    setTime(t);
    el.src = srcFor(book.asin, index, t);
    el.playbackRate = loadSpeed();
    if (autoplay) void el.play().catch(() => undefined);
    savePosition(book.asin, index, t);
  }, [audio, book.asin]);

  const seekTo = useCallback((t: SeekTarget, play = false) => {
    const el = audio.current;
    if (!el) return;
    if (t.fileIndex === fileRef.current) {
      el.currentTime = clampTime(t.time, el.duration);
      if (play) void el.play().catch(() => undefined);
      return;
    }
    switchFile(t.fileIndex, t.time, play || !el.paused);
  }, [audio, switchFile]);

  // Start from Audible's position when it is newer than this browser's.
  useEffect(() => {
    if (!files) return;
    const ctl = new AbortController();
    const timer = window.setTimeout(() => ctl.abort(), 10_000);
    const localAt = loadSavedAt(book.asin);
    api.progress(book.asin, ctl.signal).then(
      (p) => {
        if (userMoved.current) return;
        const durations = files.map((f) => f.durationSec);
        const before = durations.slice(0, fileRef.current).reduce<number>((s, d) => s + (d ?? 0), 0);
        const here = before + (audio.current?.currentTime ?? 0);
        if (!preferAudible(p.audible, localAt, here)) return;
        if (files.length > 1 && durations.some((d) => d == null)) return;
        const sec = p.audible!.positionMs / 1000;
        seekTo(bookTarget(durations, sec));
        setNote(`Picked up where Audible left off, ${formatClock(sec)} in.`);
      },
      () => undefined,
    ).finally(() => window.clearTimeout(timer));
    return () => {
      window.clearTimeout(timer);
      ctl.abort();
    };
  }, [files, book.asin, audio, seekTo]);

  const persist = useCallback((force: boolean) => {
    const el = audio.current;
    if (!el || el.readyState === 0 || !el.src.includes(`/library/${encodeURIComponent(book.asin)}/`)) return;
    const now = Date.now();
    if (!force && !shouldSave(lastSave.current.at, now, lastSave.current.time, el.currentTime)) return;
    lastSave.current = { at: now, time: el.currentTime };
    savePosition(book.asin, fileRef.current, el.currentTime);
  }, [audio, book.asin]);

  // Wire the shared element's events to this book.
  useEffect(() => {
    const el = audio.current;
    if (!el) return;
    const onLoaded = () => {
      setDuration(el.duration);
      el.playbackRate = loadSpeed();
      let t = pendingSeek.current;
      if (firstLoad.current) t = resumeTime(t, el.duration);
      firstLoad.current = false;
      pendingSeek.current = 0;
      if (Math.abs(el.currentTime - t) > 1) el.currentTime = clampTime(t, el.duration);
      setTime(el.currentTime);
    };
    const onTime = () => {
      setTime(el.currentTime);
      persist(false);
    };
    const onPlay = () => setPaused(false);
    const onPause = () => {
      setPaused(true);
      persist(true);
    };
    const onSeeked = () => {
      setTime(el.currentTime);
      persist(true);
    };
    const onWaiting = () => setWaiting(true);
    const onPlaying = () => {
      setWaiting(false);
      setError(null);
    };
    const onCanPlay = () => setWaiting(false);
    const onEnded = () => {
      const count = files?.length ?? 1;
      if (fileRef.current + 1 < count) switchFile(fileRef.current + 1, 0, true);
      else savePosition(book.asin, fileRef.current, 0);
    };
    const onError = () => setError(`Cannot play this file${el.error?.message ? `: ${el.error.message}` : ""}`);
    const events: [string, () => void][] = [
      ["loadedmetadata", onLoaded], ["timeupdate", onTime], ["play", onPlay], ["pause", onPause], ["seeked", onSeeked],
      ["waiting", onWaiting], ["playing", onPlaying], ["canplay", onCanPlay], ["ended", onEnded], ["error", onError],
    ];
    for (const [n, h] of events) el.addEventListener(n, h);
    if (el.readyState >= 1) onLoaded();
    return () => {
      for (const [n, h] of events) el.removeEventListener(n, h);
    };
  }, [audio, book.asin, files, persist, switchFile]);

  useEffect(() => {
    const flush = () => persist(true);
    window.addEventListener("pagehide", flush);
    return () => {
      window.removeEventListener("pagehide", flush);
      flush();
    };
  }, [persist]);

  const current = chapterAt(chapters, fileIndex, time);
  const chapter = current >= 0 ? chapters[current] : undefined;

  useEffect(() => {
    if (!("mediaSession" in navigator)) return;
    const art = new URL(api.coverUrl(book.asin, "large"), window.location.href).href;
    navigator.mediaSession.metadata = new MediaMetadata({
      title: chapter?.title ?? book.title,
      artist: book.authors.join(", "),
      album: book.title,
      artwork: book.pictureId ? [{ src: art, sizes: "500x500", type: "image/jpeg" }] : [],
    });
  }, [book, chapter?.title]);

  const skip = useCallback((d: number) => {
    const el = audio.current;
    if (!el) return;
    userMoved.current = true;
    el.currentTime = clampTime(el.currentTime + d, el.duration);
  }, [audio]);
  const prev = useCallback(() => {
    userMoved.current = true;
    const t = previousTarget(chapters, fileRef.current, audio.current?.currentTime ?? 0);
    if (t) seekTo(t);
  }, [audio, chapters, seekTo]);
  const next = useCallback(() => {
    userMoved.current = true;
    const t = nextTarget(chapters, fileRef.current, audio.current?.currentTime ?? 0);
    if (t) seekTo(t);
  }, [audio, chapters, seekTo]);

  useEffect(() => {
    if (!("mediaSession" in navigator)) return;
    const handlers: [MediaSessionAction, MediaSessionActionHandler][] = [
      ["play", () => void audio.current?.play()],
      ["pause", () => audio.current?.pause()],
      ["seekforward", () => skip(SKIP_SEC)],
      ["seekbackward", () => skip(-SKIP_SEC)],
      ["nexttrack", next],
      ["previoustrack", prev],
    ];
    for (const [n, h] of handlers) {
      try {
        navigator.mediaSession.setActionHandler(n, h);
      } catch {
        // action unsupported by this browser
      }
    }
    return () => {
      for (const [n] of handlers) {
        try {
          navigator.mediaSession.setActionHandler(n, null);
        } catch {
          // action unsupported by this browser
        }
      }
    };
  }, [audio, skip, next, prev]);

  const toggle = () => {
    const el = a();
    if (!el) return;
    if (el.paused) void el.play().catch((e: Error) => setError(e.message));
    else el.pause();
  };

  const setRate = (v: number) => {
    setSpeed(v);
    saveSpeed(v);
    if (a()) a()!.playbackRate = v;
  };
  const cycleSpeed = () => setRate(SPEEDS[(SPEEDS.indexOf(speed as (typeof SPEEDS)[number]) + 1) % SPEEDS.length] ?? 1);

  const fileDuration = duration || files?.[fileIndex]?.durationSec || 0;
  const pct = fileDuration ? Math.min(100, (time / fileDuration) * 100) : 0;
  const part = files && files.length > 1 ? `Part ${fileIndex + 1} of ${files.length}` : null;
  const subtitle = [chapter?.title, part].filter(Boolean).join(" · ") || book.authors.join(", ");
  const loading = waiting && !paused;

  const playButton = (big = false) => (
    <button className={`play-toggle ${big ? "big" : ""}`} onClick={toggle} aria-label={paused ? "Play" : "Pause"} disabled={!!error && !files}>
      {loading ? <span className="spinner" aria-hidden="true" /> : <Icon name={paused ? "play" : "pause"} size={big ? 30 : 20} />}
    </button>
  );

  const scrubber = (
    <div className="player-seek">
      <span className="time" data-testid="player-time">{formatClock(time)}</span>
      <input
        type="range"
        min={0}
        max={fileDuration || 0}
        step={1}
        value={Math.min(time, fileDuration || 0)}
        disabled={!fileDuration}
        style={{ "--pct": `${pct}%` } as React.CSSProperties}
        onChange={(e) => {
          userMoved.current = true;
          if (a()) a()!.currentTime = Number(e.target.value);
        }}
        aria-label="Position"
      />
      <span className="time">-{formatClock(Math.max(0, fileDuration - time))}</span>
    </div>
  );

  const transport = (big: boolean) => (
    <div className={`player-controls ${big ? "big" : ""}`}>
      <button className="ctl" onClick={prev} disabled={!chapters.length} aria-label="Previous chapter" title="Previous chapter">
        <Icon name="prevTrack" size={big ? 24 : 16} />
      </button>
      <button className="ctl skip" onClick={() => skip(-SKIP_SEC)} aria-label={`Back ${SKIP_SEC} seconds`} title={`Back ${SKIP_SEC}s`}>
        <Icon name="back30" size={big ? 34 : 22} />
        <span>{SKIP_SEC}</span>
      </button>
      {playButton(big)}
      <button className="ctl skip" onClick={() => skip(SKIP_SEC)} aria-label={`Forward ${SKIP_SEC} seconds`} title={`Forward ${SKIP_SEC}s`}>
        <Icon name="fwd30" size={big ? 34 : 22} />
        <span>{SKIP_SEC}</span>
      </button>
      <button className="ctl" onClick={next} disabled={!chapters.length} aria-label="Next chapter" title="Next chapter">
        <Icon name="nextTrack" size={big ? 24 : 16} />
      </button>
    </div>
  );

  const chapterList = showChapters && chapters.length > 0 && (
    <ol className="player-chapters" aria-label="Chapters">
      {chapters.map((c, i) => (
        <li key={c.index}>
          <button
            className={i === current ? "current" : ""}
            aria-current={i === current ? "true" : undefined}
            onClick={() => {
              userMoved.current = true;
              seekTo(chapterTarget(c), true);
            }}
          >
            <span className="ch-title">{c.title}</span>
            <span className="ch-time">{formatClock(c.startSec)}</span>
          </button>
        </li>
      ))}
    </ol>
  );

  const messages = (
    <>
      {error && <p className="player-msg error">{error}</p>}
      {note && !error && <p className="player-msg muted">{note}</p>}
    </>
  );

  if (phone && expanded) {
    return (
      <div className="player-full" role="dialog" aria-label="Now playing">
        <div className="pf-head">
          <button className="icon-btn" onClick={() => setExpanded(false)} aria-label="Minimize player">
            <Icon name="chevronDown" size={22} />
          </button>
          <span className="pf-label">Now playing</span>
          <button className="btn subtle" onClick={onClose}>Stop</button>
        </div>
        {showChapters ? (
          <div className="pf-chapters">{chapterList}</div>
        ) : (
          <div className="pf-art">
            <Cover asin={book.asin} pictureId={book.pictureId} size="large" />
          </div>
        )}
        <div className="pf-info">
          <strong>{book.title}</strong>
          <span>{subtitle}</span>
        </div>
        {scrubber}
        {transport(true)}
        <div className="pf-foot">
          <button className="btn subtle" onClick={cycleSpeed} aria-label={`Speed ${speed}x, change`}>{speed}x</button>
          {chapters.length > 0 && (
            <button className={`btn subtle ${showChapters ? "on" : ""}`} onClick={() => setShowChapters((s) => !s)} aria-pressed={showChapters}>
              <Icon name="list" size={18} /> Chapters
            </button>
          )}
        </div>
        {messages}
      </div>
    );
  }

  if (phone) {
    return (
      <div className="player mini" role="region" aria-label="Audio player">
        <div className="mini-progress" style={{ width: `${pct}%` }} />
        <button className="mini-open" onClick={() => setExpanded(true)} aria-label="Open player">
          <Cover asin={book.asin} pictureId={book.pictureId} size="small" />
          <span className="mini-text">
            <strong>{book.title}</strong>
            <span>{error ?? note ?? subtitle}</span>
          </span>
        </button>
        {playButton()}
        <button className="ctl skip" onClick={() => skip(SKIP_SEC)} aria-label={`Forward ${SKIP_SEC} seconds`}>
          <Icon name="fwd30" size={24} />
          <span>{SKIP_SEC}</span>
        </button>
      </div>
    );
  }

  return (
    <div className="player dock" role="region" aria-label="Audio player">
      <div className="player-main">
        <div className="player-now">
          <Cover asin={book.asin} pictureId={book.pictureId} size="small" />
          <div className="player-title">
            <strong>{book.title}</strong>
            <span>{subtitle}</span>
          </div>
        </div>
        <div className="player-center">
          {transport(false)}
          {scrubber}
        </div>
        <div className="player-side">
          <select value={speed} onChange={(e) => setRate(Number(e.target.value))} aria-label="Playback speed">
            {SPEEDS.map((s) => (
              <option key={s} value={s}>
                {s}x
              </option>
            ))}
          </select>
          {chapters.length > 0 && (
            <button className={`icon-btn ${showChapters ? "on" : ""}`} onClick={() => setShowChapters((o) => !o)} aria-expanded={showChapters} aria-label="Chapters" title="Chapters">
              <Icon name="list" />
            </button>
          )}
          <button className="icon-btn" onClick={onClose} aria-label="Close player" title="Close player">
            <Icon name="close" />
          </button>
        </div>
      </div>
      {messages}
      {chapterList}
    </div>
  );
}
