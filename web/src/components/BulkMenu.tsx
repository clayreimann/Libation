import { useState } from "react";
import { normalizeTags } from "../tags";
import { Popover } from "./Popover";

export type BulkOp =
  | { kind: "liberate" }
  | { kind: "absUpload" }
  | { kind: "convertMp3" }
  | { kind: "removeAudible" }
  | { kind: "pushProgress"; force: boolean }
  | { kind: "trash" }
  | { kind: "purge" }
  | { kind: "pdfStatus"; status: "liberated" | "notLiberated" }
  | { kind: "status"; status: "liberated" | "notLiberated" }
  | { kind: "tags"; mode: "add" | "remove"; tags: string };

interface Props {
  count: number;
  trash: boolean;
  disabled: boolean;
  onRun: (op: BulkOp) => void;
}

export function BulkMenu({ count, trash, disabled, onRun }: Props) {
  const [tagMode, setTagMode] = useState<"add" | "remove" | null>(null);
  const [tags, setTags] = useState("");

  const parsed = normalizeTags(tags).join(" ");

  return (
    <>
      <Popover label={`Actions (${count})`} disabled={disabled} className="menu">
        {(close) => {
          const act = (op: BulkOp) => {
            close();
            onRun(op);
          };
          return (
            <ul className="plain menu-list">
              {!trash && (
                <li>
                  <button onClick={() => act({ kind: "liberate" })}>Liberate selected</button>
                </li>
              )}
              {!trash && (
                <>
                  <li>
                    <button onClick={() => act({ kind: "absUpload" })}>Upload to Audiobookshelf</button>
                  </li>
                  <li>
                    <button onClick={() => act({ kind: "pushProgress", force: false })} title="Queues a push of each liberated book's Audible position; Audiobookshelf's newer progress is kept">Push Audible progress to Audiobookshelf</button>
                  </li>
                  <li>
                    <button onClick={() => act({ kind: "pushProgress", force: true })} title="Replaces Audiobookshelf progress even where it is newer">Push progress, overwriting newer</button>
                  </li>
                  <li>
                    <button onClick={() => act({ kind: "convertMp3" })} title="Only liberated books with an m4b and no mp3 are queued">Convert to MP3</button>
                  </li>
                  <li>
                    <button onClick={() => act({ kind: "status", status: "liberated" })}>Set status: liberated</button>
                  </li>
                  <li>
                    <button onClick={() => act({ kind: "status", status: "notLiberated" })}>Set status: not liberated</button>
                  </li>
                  <li>
                    <button onClick={() => act({ kind: "pdfStatus", status: "liberated" })}>Set PDF status: downloaded</button>
                  </li>
                  <li>
                    <button onClick={() => act({ kind: "pdfStatus", status: "notLiberated" })}>Set PDF status: not downloaded</button>
                  </li>
                  <li>
                    <button onClick={() => act({ kind: "removeAudible" })} title="Audible Plus books only; irreversible">Remove from Audible...</button>
                  </li>
                  <li>
                    <button onClick={() => { close(); setTagMode("add"); }}>Add tags...</button>
                  </li>
                  <li>
                    <button onClick={() => { close(); setTagMode("remove"); }}>Remove tags...</button>
                  </li>
                </>
              )}
              <li>
                <button onClick={() => act({ kind: "trash" })}>{trash ? "Restore" : "Move to trash"}</button>
              </li>
              {trash && (
                <li>
                  <button onClick={() => act({ kind: "purge" })}>Delete permanently...</button>
                </li>
              )}
            </ul>
          );
        }}
      </Popover>
      {tagMode && (
        <form
          className="row"
          onSubmit={(e) => {
            e.preventDefault();
            if (!parsed) return;
            onRun({ kind: "tags", mode: tagMode, tags: parsed });
            setTagMode(null);
            setTags("");
          }}
        >
          <input
            autoFocus
            value={tags}
            onChange={(e) => setTags(e.target.value)}
            placeholder={tagMode === "add" ? "tags to add" : "tags to remove"}
            aria-label={tagMode === "add" ? "Tags to add" : "Tags to remove"}
          />
          <button type="submit" className="primary" disabled={!parsed || disabled}>
            {tagMode === "add" ? "Add" : "Remove"}
          </button>
          <button type="button" onClick={() => setTagMode(null)}>
            Cancel
          </button>
        </form>
      )}
    </>
  );
}
