import { fieldGroups, syntaxNotes } from "../searchSyntax";
import { Popover } from "./Popover";

export function SyntaxHelp({ onPick }: { onPick: (text: string) => void }) {
  return (
    <Popover label="Syntax" title="Search syntax" className="wide">
      {(close) => (
        <div className="syntax">
          <ul className="plain small">
            {syntaxNotes.map((n) => (
              <li key={n}>{n}</li>
            ))}
          </ul>
          {fieldGroups.map((g) => (
            <section key={g.title}>
              <h4>{g.title}</h4>
              <pre>{g.usage}</pre>
              <div className="chips">
                {g.fields.map((f) => (
                  <button
                    key={f}
                    className="chip"
                    onClick={() => {
                      onPick(g.title === "Yes/no" ? f : `${f}:`);
                      close();
                    }}
                  >
                    {f}
                  </button>
                ))}
              </div>
            </section>
          ))}
        </div>
      )}
    </Popover>
  );
}
