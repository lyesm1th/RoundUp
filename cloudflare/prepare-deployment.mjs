// Prepare a Cloudflare D1 binding idempotently without storing credentials in Git.
import { execFileSync } from "node:child_process";
import { readFileSync, writeFileSync, appendFileSync } from "node:fs";

const dbName = "roundup-shared";
const required = ["CLOUDFLARE_API_TOKEN", "CLOUDFLARE_ACCOUNT_ID"];
for (const key of required) {
  if (!process.env[key]) throw new Error("GitHub Actions secret is missing: " + key);
}
function wrangler(...args) {
  return execFileSync("npx", ["wrangler", ...args], {
    encoding: "utf8", env: { ...process.env, CI: "true", NO_COLOR: "1" },
    stdio: ["ignore", "pipe", "pipe"], maxBuffer: 6 * 1024 * 1024
  }).trim();
}
function listDatabases() {
  const result = JSON.parse(wrangler("d1", "list", "--json"));
  const rows = Array.isArray(result) ? result : result.result;
  if (!Array.isArray(rows)) throw new Error("Unexpected Wrangler D1 list response");
  return rows;
}
let matches = listDatabases().filter(x => x.name === dbName);
if (matches.length > 1) throw new Error("Multiple D1 databases have the same name: " + dbName);
let created = false;
if (!matches.length) {
  console.log("Creating an isolated RoundUp D1 database in Eastern Europe...");
  wrangler("d1", "create", dbName, "--location", "eeur");
  matches = listDatabases().filter(x => x.name === dbName);
  created = true;
}
const databaseId = matches[0]?.uuid;
if (!/^[0-9a-f-]{36}$/i.test(databaseId || "")) throw new Error("Cloudflare did not return a valid D1 UUID");
const template = JSON.parse(readFileSync("wrangler.jsonc.example", "utf8"));
if (template.name !== "roundup-workspace-api" || template.d1_databases?.length !== 1) {
  throw new Error("Unexpected RoundUp Worker configuration template");
}
template.d1_databases[0].database_id = databaseId;
writeFileSync("wrangler.jsonc", JSON.stringify(template, null, 2) + "\n");
if (process.env.GITHUB_OUTPUT) appendFileSync(process.env.GITHUB_OUTPUT, "database_created=" + created + "\n");
console.log("D1 binding configured for " + dbName + (created ? " (new database)" : " (existing database)") + ".");
