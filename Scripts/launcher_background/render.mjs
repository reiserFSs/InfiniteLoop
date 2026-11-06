// Usage: node render.mjs SPINE_DIR OUT_DIR [FRAME_LIST e.g. 0-199 or 0,50,100] [HIDDEN_SLOTS comma list]
// SPINE_DIR is the `spine` output of extract_launcher_background.py (layer folders + report.json).
import http from "node:http";
import fs from "node:fs";
import path from "node:path";
import puppeteer from "puppeteer";

const here = path.dirname(new URL(import.meta.url).pathname);
const [skelArg, out = "frames", spec = "0-199", hiddenArg = ""] = process.argv.slice(2);
const skel = path.resolve(skelArg);
const report = JSON.parse(fs.readFileSync(path.join(skel, "report.json")));
const layers = report.layers_back_to_front.map((l) => l.name);
const scale = report.layers_back_to_front[0].composite_scale.x;
const hidden = hiddenArg ? hiddenArg.split(",") : [];
const frames = spec.split(",").flatMap((p) => {
  const [a, b] = p.split("-").map(Number);
  return b === undefined ? [a] : Array.from({ length: b - a + 1 }, (_, i) => a + i);
});
const FPS = 30;

const server = http.createServer((req, res) => {
  const url = decodeURIComponent(new URL(req.url, "http://x").pathname);
  const file = url.startsWith("/skel/") ? path.join(skel, url.slice(6)) : path.join(here, url === "/" ? "index.html" : url);
  fs.readFile(file, (err, data) => (err ? (res.writeHead(404), res.end()) : res.end(data)));
}).listen(0);
const port = server.address().port;

const browser = await puppeteer.launch({ headless: true, args: ["--use-angle=metal", "--enable-gpu", "--ignore-gpu-blocklist"] });
const page = await browser.newPage();
await page.setViewport({ width: 1920, height: 1080 });
page.on("console", (m) => console.log("page:", m.text()));
page.on("response", (r) => r.status() >= 400 && console.log("HTTP", r.status(), r.url()));
await page.goto(`http://127.0.0.1:${port}/`);
console.log(JSON.stringify(await page.evaluate((l, s) => window.setup(l, s), layers, scale)));
fs.mkdirSync(out, { recursive: true });
for (const f of frames) {
  const url = await page.evaluate((t, h) => window.draw(t, h), f / FPS, hidden);
  fs.writeFileSync(path.join(out, `frame-${String(f).padStart(3, "0")}.png`), Buffer.from(url.split(",")[1], "base64"));
}
console.log(`rendered ${frames.length} frames to ${out}`);
await browser.close();
server.close();
