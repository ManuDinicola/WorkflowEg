// Syntax check of the scripts of the built-in UI (FR-UI-012). A typo in the page script blanks the whole UI and no C# test notices it,
// so CI runs this: it extracts the inline script of index.html and compiles it together with the vendored panel script.
import { readFileSync } from "node:fs";
import vm from "node:vm";

const root = new URL("../src/Flowd.Host/Ui/", import.meta.url);
const page = readFileSync(new URL("index.html", root), "utf8");
const inline = page.match(/<script>([\s\S]*)<\/script>/);
if (!inline) { console.error("index.html has no inline script"); process.exit(1); }

let failed = false;
for (const [name, source] of [["index.html (inline script)", inline[1]], ["vendor/flowd-panels.js", readFileSync(new URL("vendor/flowd-panels.js", root), "utf8")]]) {
  try { new vm.Script(source, { filename: name }); console.log(`ok  ${name}`); }
  catch (error) { failed = true; console.error(`FAIL ${name}: ${error.message}`); }
}
process.exit(failed ? 1 : 0);
