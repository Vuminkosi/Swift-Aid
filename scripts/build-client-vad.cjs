const { buildSync } = require("esbuild");
const fs = require("node:fs");
const crypto = require("node:crypto");
const output = "src/SwiftAid.UI/wwwroot/vendor/vad/";
fs.mkdirSync(output, { recursive: true });
buildSync({ stdin: { contents: 'export { Silero } from "@ricky0123/vad-web/dist/models/silero";', resolveDir: process.cwd() },
  bundle: true, minify: true, format: "iife", globalName: "ellaiVad", outfile: output + "vad.bundle.min.js" });
for (const [source, name] of [
  ["@ricky0123/vad-web/dist/silero_vad_v5.onnx", "silero_vad_v5.onnx"],
  ["onnxruntime-web/dist/ort.wasm.min.js", "ort.wasm.min.js"],
  ["onnxruntime-web/dist/ort-wasm-simd-threaded.mjs", "ort-wasm-simd-threaded.mjs"],
  ["onnxruntime-web/dist/ort-wasm-simd-threaded.wasm", "ort-wasm-simd-threaded.wasm"]]) fs.copyFileSync("node_modules/" + source, output + name);
const files = fs.readdirSync(output).filter(x => /\.(js|mjs|wasm|onnx)$/.test(x));
fs.writeFileSync(output + "ASSETS.md", "Pinned @ricky0123/vad-web 0.0.31 (ISC), onnxruntime-web 1.22.0 (MIT), Silero v5 (MIT).\nRegenerate: npm ci --ignore-scripts && node scripts/build-client-vad.cjs\nThe adapter uses the pinned library Silero frame API with existing enhanced capture.\n\nSHA256:\n" +
 files.map(x => x + ": " + crypto.createHash("sha256").update(fs.readFileSync(output + x)).digest("hex")).join("\n") + "\n");
