import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { api } from "../api/client";
import type { AudioFile, Book, Chapter } from "../api/types";
import {
  SKIP_SEC,
  SPEEDS,
  chapterAt,
  chapterTarget,
  clampTime,
  formatClock,
  loadPosition,
  loadSpeed,
  nextTarget,
  previousTarget,
  resumeTime,
  saveSpeed,
  savePosition,
  shouldSave,
  type SeekTarget,
} from "../player";
import { Cover } from "./Cover";

interface PlayerApi {
  playing: Book | null;
  play: (book: Book) => void;
}

const Ctx = createContext<PlayerApi>({ playing: null, play: () => undefined });
export const usePlayer = () => useContext(Ctx);

export function PlayerProvider({ children }: { children: ReactNode }) {
  const [playing, setPlaying] = useState<Book | null>(null);
  const play = useCallback((b: Book) => setPlaying(b), []);
  const value = useMemo(() => ({ playing, play }), [playing, play]);
  return (
    <Ctx.Provider value={value}>
      {children}
      {playing && <PlayerBar key={playing.asin} book={playing} onClose={() => setPlaying(null)} />}
    </Ctx.Provider>
  );
}

function PlayerBar({ book, onClose }: { book: Book; onClose: () => void }) {
  const audio = useRef<HTMLAudioElement>(null);
  const [files, setFiles] = useState<AudioFile[] | null>(null);
  const [chapters, setChapters] = useState<Chapter[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [fileIndex, setFileIndex] = useState(0);
  const [time, setTime] = useState(0);
  const [duration, setDuration] = useState(0);
  const [paused, setPaused] = useState(true);
  const [speed, setSpeed] = useState(loadSpeed);
  const [open, setOpen] = useState(false);
  const pendingSeek = useRef(0);
  const autoplay = useRef(false);
  const lastSave = useRef({ at: 0, time: 0 });
  const fileRef = useRef(0);
  fileRef.current = fileIndex;

  useEffect(() => {
    const ctl = new AbortController();
    Promise.all([api.audioFiles(book.asin, ctl.signal), api.chapters(book.asin, ctl.signal).catch(() => [] as Chapter[])]).then(
      ([f, c]) => {
        if (f.length === 0) {
          setError("No audio files found.");
          return;
        }
        const saved = loadPosition(book.asin, f.length);
        pendingSeek.current = saved.time;
        setFileIndex(saved.file);
        setChapters(c);
        setFiles(f);
      },
      (e: Error) => !ctl.signal.aborted && setError(e.message),
    );
    return () => ctl.abort();
  }, [book.asin]);

  const persist = useCallback(
    (force: boolean) => {
      const a = audio.current;
      if (!a || !files || a.readyState === 0) return;
      const now = Date.now();
      if (!force && !shouldSave(lastSave.current.at, now, lastSave.current.time, a.currentTime)) return;
      lastSave.current = { at: now, time: a.currentTime };
      savePosition(book.asin, fileRef.current, a.currentTime);
    },
    [book.asin, files],
  );

  useEffect(() => {
    const flush = () => persist(true);
    window.addEventListener("pagehide", flush);
    return () => {
      window.removeEventListener("pagehide", flush);
      flush();
    };
  }, [persist]);

  useEffect(() => {
    if (audio.current) audio.current.playbackRate = speed;
  }, [speed, files, fileIndex]);

  const seekTo = useCallback((t: SeekTarget, play = false) => {
    const a = audio.current;
    if (!a) return;
    if (t.fileIndex === fileRef.current) {
      a.currentTime = clampTime(t.time, a.duration);
      if (play) void a.play().catch(() => undefined);
      return;
    }
    pendingSeek.current = t.time;
    savePosition(book.asin, t.fileIndex, t.time);
    setFileIndex(t.fileIndex);
    autoplay.current = play || !a.paused;
  }, [book.asin]);

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

  useEffect(() => {
    if (!("mediaSession" in navigator)) return;
    const a = () => audio.current;
    const skip = (d: number) => {
      if (a()) a()!.currentTime = clampTime(a()!.currentTime + d, a()!.duration);
    };
    const handlers: [MediaSessionAction, MediaSessionActionHandler][] = [
      ["play", () => void a()?.play()],
      ["pause", () => a()?.pause()],
      ["seekforward", () => skip(SKIP_SEC)],
      ["seekbackward", () => skip(-SKIP_SEC)],
      ["nexttrack", () => { const t = nextTarget(chapters, fileRef.current, a()?.currentTime ?? 0); if (t) seekTo(t); }],
      ["previoustrack", () => { const t = previousTarget(chapters, fileRef.current, a()?.currentTime ?? 0); if (t) seekTo(t); }],
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
  }, [chapters, seekTo]);

  const skip = (d: number) => {
    const a = audio.current;
    if (a) a.currentTime = clampTime(a.currentTime + d, a.duration);
  };
  const toggle = () => {
    const a = audio.current;
    if (!a) return;
    if (a.paused) void a.play().catch((e: Error) => setError(e.message));
    else a.pause();
  };
  const prev = () => {
    const t = previousTarget(chapters, fileIndex, audio.current?.currentTime ?? 0);
    if (t) seekTo(t);
  };
  const next = () => {
    const t = nextTarget(chapters, fileIndex, audio.current?.currentTime ?? 0);
    if (t) seekTo(t);
  };

  const onLoaded = () => {
    const a = audio.current!;
    setDuration(a.duration);
    a.playbackRate = speed;
    autoplay.current = false;
    const t = resumeTime(pendingSeek.current, a.duration);
    pendingSeek.current = 0;
    if (t > 0) a.currentTime = clampTime(t, a.duration);
    setTime(a.currentTime);
  };

  const onEnded = () => {
    if (files && fileIndex + 1 < files.length) {
      pendingSeek.current = 0;
      savePosition(book.asin, fileIndex + 1, 0);
      setFileIndex(fileIndex + 1);
      autoplay.current = true;
    } else {
      savePosition(book.asin, fileIndex, 0);
    }
  };

  const fileDuration = duration || files?.[fileIndex]?.durationSec || 0;
  const label = files && files.length > 1 ? `Part ${fileIndex + 1} of ${files.length}` : null;

  return (
    <div className="player" role="region" aria-label="Audio player">
      {files && (
        <audio
          ref={audio}
          key={fileIndex}
          src={api.audioUrl(book.asin, fileIndex)}
          preload="metadata"
          autoPlay={autoplay.current}
          onLoadedMetadata={onLoaded}
          onTimeUpdate={(e) => {
            setTime(e.currentTarget.currentTime);
            persist(false);
          }}
          onPlay={() => setPaused(false)}
          onPause={() => {
            setPaused(true);
            persist(true);
          }}
          onSeeked={(e) => {
            setTime(e.currentTarget.currentTime);
            persist(true);
          }}
          onEnded={onEnded}
          onError={() => setError(`Cannot play this file${audio.current?.error?.message ? `: ${audio.current.error.message}` : ""}`)}
          data-testid="audio"
        />
      )}
      <div className="player-main">
        <Cover asin={book.asin} pictureId={book.pictureId} size="small" />
        <div className="player-title">
          <strong>{book.title}</strong>
          <div className="muted small">{[chapter?.title, label].filter(Boolean).join(" · ") || book.authors.join(", ")}</div>
        </div>
        <div className="player-controls">
          <button onClick={prev} disabled={!chapters.length} aria-label="Previous chapter">
            Prev
          </button>
          <button onClick={() => skip(-SKIP_SEC)} aria-label={`Back ${SKIP_SEC} seconds`}>
            -{SKIP_SEC}s
          </button>
          <button className="primary" onClick={toggle} disabled={!files} aria-label={paused ? "Play" : "Pause"}>
            {paused ? "Play" : "Pause"}
          </button>
          <button onClick={() => skip(SKIP_SEC)} aria-label={`Forward ${SKIP_SEC} seconds`}>
            +{SKIP_SEC}s
          </button>
          <button onClick={next} disabled={!chapters.length} aria-label="Next chapter">
            Next
          </button>
        </div>
        <div className="player-side">
          <select
            value={speed}
            onChange={(e) => {
              const v = Number(e.target.value);
              setSpeed(v);
              saveSpeed(v);
            }}
            aria-label="Playback speed"
          >
            {SPEEDS.map((s) => (
              <option key={s} value={s}>
                {s}x
              </option>
            ))}
          </select>
          {chapters.length > 0 && (
            <button onClick={() => setOpen((o) => !o)} aria-expanded={open}>
              Chapters
            </button>
          )}
          <button onClick={onClose} aria-label="Close player">
            Close
          </button>
        </div>
      </div>
      <div className="player-seek">
        <span className="small" data-testid="player-time">{formatClock(time)}</span>
        <input
          type="range"
          min={0}
          max={fileDuration || 0}
          step={1}
          value={Math.min(time, fileDuration || 0)}
          disabled={!fileDuration}
          onChange={(e) => {
            if (audio.current) audio.current.currentTime = Number(e.target.value);
          }}
          aria-label="Position"
        />
        <span className="small">{formatClock(fileDuration)}</span>
      </div>
      {error && <p className="error small">{error}</p>}
      {open && (
        <ol className="player-chapters" aria-label="Chapters">
          {chapters.map((c, i) => (
            <li key={c.index}>
              <button className={`linklike ${i === current ? "current" : ""}`} aria-current={i === current ? "true" : undefined} onClick={() => seekTo(chapterTarget(c), true)}>
                <span>{c.title}</span> <span className="muted small">{formatClock(c.fileStartSec)}</span>
              </button>
            </li>
          ))}
        </ol>
      )}
    </div>
  );
}
