import { defineConfig } from "vite";

export default defineConfig({
  base: "./",
  build: {
    outDir: "../DashboardAssets",
    emptyOutDir: true,
    sourcemap: false,
    cssCodeSplit: false,
    rollupOptions: {
      output: {
        entryFileNames: "dashboard.js",
        chunkFileNames: "dashboard.js",
        assetFileNames: assetInfo => {
          if (assetInfo.name?.endsWith(".css")) {
            return "dashboard.css";
          }
          return "asset-[name][extname]";
        }
      }
    }
  }
});
