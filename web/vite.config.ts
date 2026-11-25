/// <reference types="vitest/config" />
import { defineConfig, loadEnv } from "vite";
import react from "@vitejs/plugin-react";

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), "");
  const target = env.LIBATION_API_URL || "http://localhost:8080";
  const headers = env.LIBATION_API_TOKEN ? { Authorization: `Bearer ${env.LIBATION_API_TOKEN}` } : undefined;
  return {
    plugins: [react()],
    server: { proxy: { "/api": { target, changeOrigin: true, headers } } },
    test: { environment: "node" },
  };
});
