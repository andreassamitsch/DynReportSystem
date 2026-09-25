import fs from "node:fs";
import path from "node:path";

const target = path.resolve("src/DynReportSystem/wwwroot/vendor");
fs.mkdirSync(target, { recursive: true });

fs.copyFileSync(
  path.resolve("node_modules/echarts/dist/echarts.min.js"),
  path.join(target, "echarts.min.js")
);

const mod = await import("@svg-maps/world");
const map = mod.default ?? mod.world ?? Object.values(mod).find(v => v && Array.isArray(v.locations));

if (!map || !Array.isArray(map.locations)) {
  throw new Error("@svg-maps/world export format is not supported.");
}

const esc = value => String(value ?? "")
  .replaceAll("&", "&amp;")
  .replaceAll('"', "&quot;")
  .replaceAll("<", "&lt;")
  .replaceAll(">", "&gt;");

const paths = map.locations.map(location => {
  const id = String(location.id ?? location.name ?? "").toUpperCase();
  return '<path id="' + esc(id) + '" name="' + esc(id) + '" d="' + esc(location.path) + '"/>';
}).join("");

const svg =
  '<svg xmlns="http://www.w3.org/2000/svg" viewBox="' + esc(map.viewBox) + '">' +
  paths +
  '</svg>';

fs.writeFileSync(path.join(target, "world-map.svg"), svg, "utf8");
console.log("Vendored ECharts and world map assets.");
