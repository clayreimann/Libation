import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { App } from "./App";
import { PlayerProvider } from "./components/Player";
import { LiveProvider } from "./live";
import "./styles.css";

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <LiveProvider>
      <PlayerProvider>
        <App />
      </PlayerProvider>
    </LiveProvider>
  </StrictMode>,
);
